using FiGet.Core.Connectors;
using FiGet.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace FiGet.Persistence.Stores;

/// <summary>
/// Stores the version lists upstreams reported, so every replica answers the same thing and a new upstream
/// release becomes visible everywhere within one time-to-live.
/// </summary>
public sealed class EfUpstreamIndexStore(FiGetDbContext db) : IUpstreamIndexStore
{
    public Task<CachedUpstreamIndex?> FindAsync(int feedUpstreamKey, string idLower, CancellationToken cancellationToken) =>
        db.CachedUpstreamIndexes
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.FeedUpstreamKey == feedUpstreamKey && c.IdLower == idLower, cancellationToken);

    public async Task SaveAsync(
        int feedUpstreamKey,
        string idLower,
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
}
