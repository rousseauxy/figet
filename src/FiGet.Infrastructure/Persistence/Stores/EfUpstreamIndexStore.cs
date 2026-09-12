using System.Text.Json;
using System.Text.Json.Serialization;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using NuGet.Versioning;

namespace FiGet.Infrastructure.Persistence.Stores;

/// <summary>
/// Stores what upstreams reported, so every replica answers the same thing and a restart does not throw
/// the work away. Both halves of a catalogue live on one row: asking for the versions without their
/// descriptions would only mean fetching the whole walk again to fill in the blanks.
///
/// The encoding is this adapter's business. Versions stay a space-separated list, which is what they were;
/// descriptions are JSON, because they are records rather than words and nothing else needs to read them.
/// </summary>
public sealed class EfUpstreamIndexStore(FiGetDbContext db) : IUpstreamIndexStore
{
    /// <summary>
    /// Short names and omitted empties, deliberately. These rows are large - a package with two thousand
    /// versions carries two thousand of these - and a property name written out in full is paid once per
    /// version. "Description" alone would cost more than twenty kilobytes on such a row.
    /// </summary>
    private sealed record Described(
        [property: JsonPropertyName("v")] string Version,
        [property: JsonPropertyName("d")] string? Description,
        [property: JsonPropertyName("s")] string? Summary,
        [property: JsonPropertyName("t")] string? Title,
        [property: JsonPropertyName("a")] string? Authors,
        [property: JsonPropertyName("g")] string? Tags,
        [property: JsonPropertyName("p")] string? ProjectUrl,
        [property: JsonPropertyName("i")] string? IconUrl,
        [property: JsonPropertyName("l")] string? LicenseUrl,
        [property: JsonPropertyName("u")] DateTime? Published,
        [property: JsonPropertyName("n")] long Downloads,
        [property: JsonPropertyName("k")] bool Listed);

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<CachedUpstreamCatalog?> FindAsync(int feedUpstreamKey, string idLower, CancellationToken cancellationToken)
    {
        var row = await db.CachedUpstreamIndexes
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.FeedUpstreamKey == feedUpstreamKey && c.IdLower == idLower, cancellationToken);

        return row is null
            ? null
            : new CachedUpstreamCatalog(ParseVersions(row), ParseDescribed(row), row.FetchedUtc, row.Stale);
    }

    public async Task SaveAsync(
        int feedUpstreamKey,
        string idLower,
        UpstreamCatalog catalog,
        bool stale,
        DateTime fetchedUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var all = string.Join(' ', catalog.Versions.Select(v => v.Version.ToNormalizedString()));
        var semVer2 = string.Join(' ', catalog.Versions.Where(v => v.IsSemVer2).Select(v => v.Version.ToNormalizedString()));
        var described = JsonSerializer.Serialize(
            catalog.Described.Select(m => new Described(
                m.Version.ToNormalizedString(),
                Empty(m.Description),
                Empty(m.Summary),
                Empty(m.Title),
                Empty(m.Authors),
                Empty(m.Tags),
                Empty(m.ProjectUrl),
                Empty(m.IconUrl),
                Empty(m.LicenseUrl),
                m.Published,
                m.Downloads,
                m.Listed)).ToList(),
            Json);

        var existing = await db.CachedUpstreamIndexes
            .FirstOrDefaultAsync(c => c.FeedUpstreamKey == feedUpstreamKey && c.IdLower == idLower, cancellationToken);

        if (existing is not null)
        {
            existing.Versions = all;
            existing.SemVer2Versions = semVer2;
            existing.Metadata = described;
            existing.FetchedUtc = fetchedUtc;
            existing.Stale = stale;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var row = new CachedUpstreamIndex
        {
            FeedUpstreamKey = feedUpstreamKey,
            IdLower = idLower,
            Versions = all,
            SemVer2Versions = semVer2,
            Metadata = described,
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

    private static string? Empty(string? value) => string.IsNullOrEmpty(value) ? null : value;

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

    /// <summary>
    /// The stored descriptions, or none. A row written before this column existed has nothing here, and a
    /// row written by a newer shape might not parse: both mean "described nothing", which costs a listing
    /// its details until the next refresh rather than costing the request an exception.
    /// </summary>
    private static List<UpstreamMetadata> ParseDescribed(CachedUpstreamIndex cached)
    {
        if (string.IsNullOrWhiteSpace(cached.Metadata))
        {
            return [];
        }

        List<Described>? rows;
        try
        {
            rows = JsonSerializer.Deserialize<List<Described>>(cached.Metadata, Json);
        }
        catch (JsonException)
        {
            return [];
        }

        var described = new List<UpstreamMetadata>();
        foreach (var row in rows ?? [])
        {
            if (NuGetVersion.TryParse(row.Version, out var version))
            {
                described.Add(new UpstreamMetadata(
                    version,
                    row.Description ?? "",
                    row.Summary ?? "",
                    row.Title ?? "",
                    row.Authors ?? "",
                    row.Tags ?? "",
                    row.ProjectUrl ?? "",
                    row.IconUrl ?? "",
                    row.LicenseUrl ?? "",
                    row.Published,
                    row.Downloads,
                    row.Listed));
            }
        }

        return described;
    }
}
