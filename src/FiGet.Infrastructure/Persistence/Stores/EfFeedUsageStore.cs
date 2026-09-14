using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FiGet.Infrastructure.Persistence.Stores;

/// <summary>
/// Adds each count in the database (<c>Count = Count + n</c>), so two replicas flushing the same hour both land. The row is
/// created the first time; a replica that created it a moment earlier makes the insert fail, and the increment is retried.
/// </summary>
public sealed class EfFeedUsageStore(FiGetDbContext db) : IFeedUsageStore
{
    public async Task AddAsync(IReadOnlyCollection<FeedUsageCount> counts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(counts);
        foreach (var count in counts)
        {
            if (await IncrementAsync(count, cancellationToken))
            {
                continue;
            }

            db.FeedUsage.Add(new FeedUsage { FeedKey = count.FeedKey, HourUtc = count.HourUtc, Kind = count.Kind, Count = count.Count });
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                if (!await IncrementAsync(count, cancellationToken))
                {
                    // Neither there nor insertable: the feed was deleted meanwhile, and its counts go with it.
                    continue;
                }
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    public async Task<IReadOnlyList<FeedUsageCount>> ListAsync(IReadOnlyCollection<int> feedKeys, FeedUsageKind kind, DateTime fromUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feedKeys);
        if (feedKeys.Count == 0)
        {
            return [];
        }

        return await db.FeedUsage.AsNoTracking()
            .Where(u => feedKeys.Contains(u.FeedKey) && u.Kind == kind && u.HourUtc >= fromUtc)
            .Select(u => new FeedUsageCount(u.FeedKey, u.HourUtc, u.Kind, u.Count))
            .ToListAsync(cancellationToken);
    }

    public Task<int> PruneAsync(DateTime beforeUtc, CancellationToken cancellationToken) =>
        db.FeedUsage.Where(u => u.HourUtc < beforeUtc).ExecuteDeleteAsync(cancellationToken);

    private async Task<bool> IncrementAsync(FeedUsageCount count, CancellationToken cancellationToken) =>
        await db.FeedUsage
            .Where(u => u.FeedKey == count.FeedKey && u.HourUtc == count.HourUtc && u.Kind == count.Kind)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Count, u => u.Count + count.Count), cancellationToken) > 0;
}
