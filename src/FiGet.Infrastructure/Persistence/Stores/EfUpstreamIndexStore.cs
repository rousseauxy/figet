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

        // Coalesced here, at the boundary that reads the database: the column was added nullable, so
        // every row written before it exists comes back null however the property is declared, and one
        // `.Length` on it took out every registration index for a cached package.
        return row is null ? null : new CachedUpstreamCatalog(ParseVersions(row), row.FetchedUtc, row.Stale, row.Id ?? "");
    }

    public async Task SaveAsync(
        int feedUpstreamKey,
        string idLower,
        string casedId,
        IReadOnlyList<UpstreamVersion> versions,
        bool stale,
        DateTime fetchedUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(versions);
        var all = string.Join(' ', versions.Select(v => v.Version.ToNormalizedString()));
        var semVer2 = string.Join(' ', versions.Where(v => v.IsSemVer2).Select(v => v.Version.ToNormalizedString()));

        var existing = await db.CachedUpstreamIndexes
            .FirstOrDefaultAsync(c => c.FeedUpstreamKey == feedUpstreamKey && c.IdLower == idLower, cancellationToken);

        if (existing is not null)
        {
            existing.Versions = all;
            existing.Id = casedId ?? "";
            existing.SemVer2Versions = semVer2;
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
