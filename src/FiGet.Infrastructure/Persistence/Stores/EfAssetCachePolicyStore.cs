using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FiGet.Infrastructure.Persistence.Stores;

public sealed class EfAssetCachePolicyStore(FiGetDbContext db) : IAssetCachePolicyStore
{
    public async Task<IReadOnlyList<AssetCachePolicy>> ListAsync(int feedKey, CancellationToken cancellationToken) =>
        await db.AssetCachePolicies.AsNoTracking().Where(p => p.FeedKey == feedKey).ToListAsync(cancellationToken);

    public async Task SetAsync(int feedKey, string pathLower, AssetCacheMode mode, int? maxAgeSeconds, CancellationToken cancellationToken)
    {
        var seconds = mode == AssetCacheMode.MaxAge ? maxAgeSeconds : null;
        var updated = await db.AssetCachePolicies
            .Where(p => p.FeedKey == feedKey && p.PathLower == pathLower)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Mode, mode).SetProperty(p => p.MaxAgeSeconds, seconds), cancellationToken);
        if (updated > 0)
        {
            return;
        }

        var policy = new AssetCachePolicy { FeedKey = feedKey, PathLower = pathLower, Mode = mode, MaxAgeSeconds = seconds };
        db.AssetCachePolicies.Add(policy);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Set for the same folder by another request at the same moment; its value stands.
        }
        finally
        {
            db.Entry(policy).State = EntityState.Detached;
        }
    }

    public async Task<bool> RemoveAsync(int feedKey, string pathLower, CancellationToken cancellationToken) =>
        await db.AssetCachePolicies.Where(p => p.FeedKey == feedKey && p.PathLower == pathLower).ExecuteDeleteAsync(cancellationToken) > 0;
}
