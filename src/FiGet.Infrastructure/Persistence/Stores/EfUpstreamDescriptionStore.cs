using System.Security.Cryptography;
using System.Text;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using NuGet.Versioning;

namespace FiGet.Infrastructure.Persistence.Stores;

/// <summary>
/// Upstream descriptions, per version, with tag lists stored once each. Everything here is bounded by design, because
/// the unbounded version of this - one serialised value per package - is what ran the server out of memory.
///
/// Writes use a context of their own. Saving in batches means clearing the change tracker between them, and the
/// request's context belongs to every store in the request: clearing it, or saving it early, is not this store's call.
/// </summary>
public sealed class EfUpstreamDescriptionStore(FiGetDbContext db, DbContextOptions<FiGetDbContext> options) : IUpstreamDescriptionStore
{
    /// <summary>Rows added before the context is saved and cleared. A PowerShell module's tags make a row tens of kilobytes.</summary>
    private const int WriteBatch = 100;

    /// <summary>Values per <c>IN</c> list, kept well under SQL Server's 2100 parameters and SQLite's variable limit.</summary>
    private const int LookupBatch = 500;

    public async Task<IReadOnlyList<UpstreamMetadata>> LoadAsync(
        int feedUpstreamKey,
        string idLower,
        IReadOnlySet<string>? unlisted,
        IReadOnlyDictionary<string, IReadOnlyList<UpstreamDependency>>? dependencies,
        CancellationToken cancellationToken)
    {
        var rows = await db.CachedUpstreamDescriptions
            .AsNoTracking()
            .Where(d => d.FeedUpstreamKey == feedUpstreamKey && d.IdLower == idLower)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return [];
        }

        // Each distinct tag list read once and shared by every version that carries it, so memory holds it once too.
        var hashes = rows.Select(r => r.TagSetHash).Distinct().ToList();
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chunk in hashes.Chunk(LookupBatch))
        {
            foreach (var set in await db.CachedUpstreamTagSets.AsNoTracking().Where(t => chunk.Contains(t.Hash)).ToListAsync(cancellationToken))
            {
                tags[set.Hash] = set.Tags;
            }
        }

        var described = new List<UpstreamMetadata>(rows.Count);
        foreach (var row in rows)
        {
            if (!NuGetVersion.TryParse(row.NormalizedVersion, out var version))
            {
                continue;
            }

            described.Add(new UpstreamMetadata(
                version,
                row.Description,
                row.Summary,
                row.Title,
                row.Authors,
                tags.GetValueOrDefault(row.TagSetHash, ""),
                row.ProjectUrl,
                row.IconUrl,
                row.LicenseUrl,
                row.PublishedUtc is { } published ? DateTime.SpecifyKind(published, DateTimeKind.Utc) : null,
                row.Downloads,
                unlisted?.Contains(row.NormalizedVersion) != true,
                dependencies is not null && dependencies.TryGetValue(row.NormalizedVersion, out var declared) ? declared : null));
        }

        return described;
    }

    public async Task SetReleaseNotesAsync(int feedUpstreamKey, string idLower, string normalizedVersion, string releaseNotes, CancellationToken cancellationToken)
    {
        // Capped at the column, and by the same reasoning: a release note is a paragraph, and a gallery that sends a
        // novel is not worth the row it would grow into.
        var notes = releaseNotes.Length <= 8000 ? releaseNotes : releaseNotes[..8000];
        await db.CachedUpstreamDescriptions
            .Where(d => d.FeedUpstreamKey == feedUpstreamKey && d.IdLower == idLower && d.NormalizedVersion == normalizedVersion)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ReleaseNotes, notes), cancellationToken);
    }

    public async Task<IReadOnlyList<UpstreamPublished>> PublishedBetweenAsync(
        int feedUpstreamKey,
        DateTime fromUtc,
        DateTime toUtc,
        int take,
        CancellationToken cancellationToken)
    {
        if (take <= 0)
        {
            return [];
        }

        var rows = await db.CachedUpstreamDescriptions
            .AsNoTracking()
            .Where(d => d.FeedUpstreamKey == feedUpstreamKey && d.PublishedUtc >= fromUtc && d.PublishedUtc < toUtc)
            .OrderByDescending(d => d.PublishedUtc)
            .Take(take)
            .Select(d => new { d.IdLower, d.NormalizedVersion, d.PublishedUtc, d.Authors, d.ReleaseNotes })
            .ToListAsync(cancellationToken);

        return [.. rows.Select(r => new UpstreamPublished(r.IdLower, r.NormalizedVersion, r.PublishedUtc!.Value, r.Authors, r.ReleaseNotes ?? ""))];
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, DateTime>>> PublishedDatesAsync(
        int feedUpstreamKey,
        IReadOnlyCollection<string> idsLower,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(idsLower);
        var dates = new Dictionary<string, IReadOnlyDictionary<string, DateTime>>(StringComparer.Ordinal);
        foreach (var chunk in idsLower.Distinct(StringComparer.Ordinal).Chunk(LookupBatch))
        {
            // Three columns. Reading the rows themselves would pull every description and every tag list of every
            // version of every id on the page, which is exactly what this store exists to avoid.
            var rows = await db.CachedUpstreamDescriptions
                .AsNoTracking()
                .Where(d => d.FeedUpstreamKey == feedUpstreamKey && chunk.Contains(d.IdLower) && d.PublishedUtc != null)
                .Select(d => new { d.IdLower, d.NormalizedVersion, d.PublishedUtc })
                .ToListAsync(cancellationToken);

            foreach (var group in rows.GroupBy(r => r.IdLower, StringComparer.Ordinal))
            {
                var byVersion = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in group)
                {
                    byVersion[row.NormalizedVersion] = row.PublishedUtc!.Value;
                }

                dates[group.Key] = byVersion;
            }
        }

        return dates;
    }

    public async Task SaveAsync(int feedUpstreamKey, string idLower, IReadOnlyList<UpstreamMetadata> described, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(described);
        if (described.Count == 0)
        {
            return;
        }

        var byVersion = new Dictionary<string, UpstreamMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in described)
        {
            byVersion[item.Version.ToNormalizedString()] = item;
        }

        await using var writer = new FiGetDbContext(options);
        var stored = await writer.CachedUpstreamDescriptions
            .AsNoTracking()
            .Where(d => d.FeedUpstreamKey == feedUpstreamKey && d.IdLower == idLower)
            .Select(d => new { d.Key, d.NormalizedVersion })
            .ToListAsync(cancellationToken);
        var storedVersions = stored.Select(s => s.NormalizedVersion).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Versions the upstream stopped describing are dropped, so a stored list cannot outgrow what the gallery holds.
        var gone = stored.Where(s => !byVersion.ContainsKey(s.NormalizedVersion)).Select(s => s.Key).ToList();
        foreach (var chunk in gone.Chunk(LookupBatch))
        {
            await writer.CachedUpstreamDescriptions.Where(d => chunk.Contains(d.Key)).ExecuteDeleteAsync(cancellationToken);
        }

        var added = byVersion.Where(pair => !storedVersions.Contains(pair.Key)).ToList();
        if (added.Count > 0)
        {
            await AddAsync(writer, feedUpstreamKey, idLower, added, cancellationToken);
        }

        if (gone.Count > 0)
        {
            await SweepOrphanedTagSetsAsync(writer, cancellationToken);
        }
    }

    /// <summary>Tag lists no description refers to any more. Run after descriptions are removed, never on a read.</summary>
    public static Task SweepOrphanedTagSetsAsync(FiGetDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.CachedUpstreamTagSets
            .Where(t => !db.CachedUpstreamDescriptions.Any(d => d.TagSetHash == t.Hash))
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static async Task AddAsync(FiGetDbContext writer, int feedUpstreamKey, string idLower, List<KeyValuePair<string, UpstreamMetadata>> added, CancellationToken cancellationToken)
    {
        var withHash = added.Select(pair => (Version: pair.Key, Item: pair.Value, Hash: Hash(pair.Value.Tags))).ToList();

        // The tag lists first, each distinct one once, skipping those another package or an earlier write stored.
        var distinct = withHash.GroupBy(x => x.Hash, StringComparer.Ordinal).Select(g => (g.Key, g.First().Item.Tags)).ToList();
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in distinct.Select(d => d.Key).Chunk(LookupBatch))
        {
            known.UnionWith(await writer.CachedUpstreamTagSets.AsNoTracking().Where(t => chunk.Contains(t.Hash)).Select(t => t.Hash).ToListAsync(cancellationToken));
        }

        foreach (var chunk in distinct.Where(d => !known.Contains(d.Key)).Chunk(WriteBatch))
        {
            writer.CachedUpstreamTagSets.AddRange(chunk.Select(d => new CachedUpstreamTagSet { Hash = d.Key, Tags = d.Tags }));
            await SaveBatchAsync(writer, cancellationToken);
        }

        foreach (var chunk in withHash.Chunk(WriteBatch))
        {
            writer.CachedUpstreamDescriptions.AddRange(chunk.Select(x => new CachedUpstreamDescription
            {
                FeedUpstreamKey = feedUpstreamKey,
                IdLower = idLower,
                NormalizedVersion = x.Version,
                Title = Clip(x.Item.Title, 512),
                Summary = x.Item.Summary,
                Description = x.Item.Description,
                Authors = Clip(x.Item.Authors, 1024),
                ProjectUrl = Clip(x.Item.ProjectUrl, 2048),
                IconUrl = Clip(x.Item.IconUrl, 2048),
                LicenseUrl = Clip(x.Item.LicenseUrl, 2048),
                PublishedUtc = x.Item.Published,
                Downloads = x.Item.Downloads,
                TagSetHash = x.Hash,
            }));
            await SaveBatchAsync(writer, cancellationToken);
        }
    }

    /// <summary>
    /// Saves and lets go of one batch. Another replica writing the same package at the same moment loses nothing: its
    /// rows are the same rows, so a conflict is dropped and the next batch goes on.
    /// </summary>
    private static async Task SaveBatchAsync(FiGetDbContext writer, CancellationToken cancellationToken)
    {
        try
        {
            await writer.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Duplicate key from a concurrent writer; see above.
        }
        finally
        {
            writer.ChangeTracker.Clear();
        }
    }

    private static string Hash(string tags) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tags ?? "")));

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];
}
