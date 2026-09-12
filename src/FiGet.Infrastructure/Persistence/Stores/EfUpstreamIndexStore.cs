using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using NuGet.Versioning;

namespace FiGet.Infrastructure.Persistence.Stores;

/// <summary>
/// Stores the version lists upstreams reported, so every replica answers the same thing and a restart does
/// not throw them away. Versions only, and cheaply: PnP.PowerShell's 2098 versions are 31 KB of normalised
/// strings here, while what the gallery says *about* those versions is 101 MB. That belongs in memory, and
/// <c>UpstreamMetadataCache</c> records what happened when it briefly did not.
/// </summary>
public sealed class EfUpstreamIndexStore(FiGetDbContext db) : IUpstreamIndexStore
{
    public async Task<CachedUpstreamCatalog?> FindAsync(int feedUpstreamKey, string idLower, CancellationToken cancellationToken)
    {
        var row = await db.CachedUpstreamIndexes
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.FeedUpstreamKey == feedUpstreamKey && c.IdLower == idLower, cancellationToken);

        if (row is null)
        {
            return null;
        }

        // Empty columns are ambiguous: a row written before these existed looks exactly like a row whose
        // upstream hides nothing and declares nothing. They must mean opposite things - the first is "no
        // news", the second is "we asked, and the answer was none" - so absence is reported as null and
        // only content counts as having been told.
        //
        // Getting this wrong is not theoretical. The migration defaults these to empty, and reading an
        // empty set as "nothing is hidden" re-listed a version the gallery hides three seconds after a
        // restart, which is the exact defect the reconciliation rules exist to prevent.
        //
        // The cost is a package that genuinely hides nothing and depends on nothing: it reads as "no
        // news" until a refresh describes it, so nothing is re-listed on its behalf. That is the safe
        // direction - it changes no flag - and one refresh later it is moot.
        var told = row.UnlistedVersions.Length > 0 || row.Dependencies.Length > 0;

        return new CachedUpstreamCatalog(
            ParseVersions(row),
            row.FetchedUtc,
            row.Stale,
            // Coalesced at the boundary that reads the database: this column was added nullable, so a row
            // written before it existed comes back null however the property is declared, and one
            // `.Length` on it took out every registration index for a cached package.
            row.Id ?? "",
            told ? ParseUnlisted(row.UnlistedVersions) : null,
            told ? ParseDependencies(row.Dependencies) : null);
    }

    public async Task SaveAsync(
        int feedUpstreamKey,
        string idLower,
        string casedId,
        IReadOnlyList<UpstreamVersion> versions,
        IReadOnlyList<UpstreamMetadata> described,
        bool stale,
        DateTime fetchedUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(described);
        var all = string.Join(' ', versions.Select(v => v.Version.ToNormalizedString()));
        var semVer2 = string.Join(' ', versions.Where(v => v.IsSemVer2).Select(v => v.Version.ToNormalizedString()));

        var existing = await db.CachedUpstreamIndexes
            .FirstOrDefaultAsync(c => c.FeedUpstreamKey == feedUpstreamKey && c.IdLower == idLower, cancellationToken);

        if (existing is not null)
        {
            existing.Versions = all;
            existing.Id = casedId ?? "";
            existing.SemVer2Versions = semVer2;

            // Only when there is something to say. An upstream that answered without describing anything
            // has told us nothing new, and blanking these would re-create the defects they exist to stop.
            if (described.Count > 0)
            {
                existing.UnlistedVersions = EncodeUnlisted(described);
                existing.Dependencies = EncodeDependencies(described);
            }

            existing.FetchedUtc = fetchedUtc;
            existing.Stale = stale;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var row = new CachedUpstreamIndex
        {
            FeedUpstreamKey = feedUpstreamKey,
            IdLower = idLower,
            Id = casedId ?? "",
            Versions = all,
            SemVer2Versions = semVer2,
            UnlistedVersions = EncodeUnlisted(described),
            Dependencies = EncodeDependencies(described),
            FetchedUtc = fetchedUtc,
            Stale = stale,
        };

        db.CachedUpstreamIndexes.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another replica cached the same id at the same moment; its row is as good as this one.
            db.Entry(row).State = EntityState.Detached;
        }
    }

    public async Task ClearAsync(int feedUpstreamKey, CancellationToken cancellationToken) =>
        await db.CachedUpstreamIndexes.Where(c => c.FeedUpstreamKey == feedUpstreamKey).ExecuteDeleteAsync(cancellationToken);

    /// <summary>
    /// How the dependency text is split up. Chosen because none of the four things it separates - a
    /// version, a dependency id, a normalised range or a framework name - can contain either character,
    /// so the encoding needs no escaping and cannot be broken by a package naming itself something odd.
    /// </summary>
    private const char VersionSeparator = '\n';

    private const char FieldSeparator = '\t';

    /// <summary>Versions the upstream holds but does not advertise, separated by a single space.</summary>
    private static string EncodeUnlisted(IReadOnlyList<UpstreamMetadata> described) =>
        string.Join(' ', described.Where(m => !m.Listed).Select(m => m.Version.ToNormalizedString()));

    /// <summary>
    /// One line per version that declares anything: the version, a tab, then <c>id:range:framework</c>
    /// joined by pipes - the same triple the v2 protocol puts on the wire. A range carries spaces and
    /// commas but never a tab, colon or pipe, so this needs no escaping.
    /// </summary>
    private static string EncodeDependencies(IReadOnlyList<UpstreamMetadata> described) =>
        string.Join(
            VersionSeparator,
            described
                .Where(m => m.Dependencies is { Count: > 0 })
                .Select(m => m.Version.ToNormalizedString()
                    + FieldSeparator
                    + string.Join('|', m.Dependencies!.Select(d => $"{d.Id}:{d.VersionRange}:{d.TargetFramework}"))));

    private static IReadOnlySet<string> ParseUnlisted(string? text) =>
        (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, IReadOnlyList<UpstreamDependency>> ParseDependencies(string? text)
    {
        var map = new Dictionary<string, IReadOnlyList<UpstreamDependency>>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (text ?? "").Split(VersionSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf(FieldSeparator);
            if (tab <= 0)
            {
                continue;
            }

            var declared = new List<UpstreamDependency>();
            foreach (var part in line[(tab + 1)..].Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                // Exactly three, because none of the three can contain the separator.
                var bits = part.Split(':');
                if (bits.Length == 3)
                {
                    declared.Add(new UpstreamDependency(bits[2], bits[0].Length == 0 ? null : bits[0], bits[1]));
                }
            }

            map[line[..tab]] = declared;
        }

        return map;
    }

    private static List<UpstreamVersion> ParseVersions(CachedUpstreamIndex cached)
    {
        var semVer2 = cached.SemVer2Versions.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var versions = new List<UpstreamVersion>();
        foreach (var text in cached.Versions.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (NuGetVersion.TryParse(text, out var version))
            {
                versions.Add(new UpstreamVersion(version, semVer2.Contains(text)));
            }
        }

        return versions;
    }
}
