using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FiGet.Infrastructure.Persistence.Stores;

public sealed class EfAssetStore(FiGetDbContext db) : IAssetStore
{
    public Task<AssetItem?> FindAsync(int feedKey, string pathLower, CancellationToken cancellationToken) =>
        db.AssetItems.AsNoTracking().FirstOrDefaultAsync(a => a.FeedKey == feedKey && a.PathLower == pathLower, cancellationToken);

    public async Task<IReadOnlyList<AssetItem>> ListAsync(int feedKey, string folderLower, bool recursive, CancellationToken cancellationToken)
    {
        var items = db.AssetItems.AsNoTracking().Where(a => a.FeedKey == feedKey);
        if (!recursive)
        {
            items = items.Where(a => a.ParentLower == folderLower);
        }
        else if (folderLower.Length > 0)
        {
            // StartsWith on a parameter is translated with its wildcards escaped by both providers, so a
            // folder named "50%" does not match "50x" as well.
            var prefix = folderLower + "/";
            items = items.Where(a => a.PathLower.StartsWith(prefix));
        }

        return await items
            .OrderByDescending(a => a.IsDirectory)
            .ThenBy(a => a.PathLower)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> AddAsync(AssetItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        db.AssetItems.Add(item);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
        finally
        {
            db.Entry(item).State = EntityState.Detached;
        }
    }

    public async Task UpdateAsync(AssetItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        db.AssetItems.Update(item);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            db.Entry(item).State = EntityState.Detached;
        }
    }

    public async Task<IReadOnlyList<string>> DeleteAsync(int feedKey, string pathLower, CancellationToken cancellationToken)
    {
        var prefix = pathLower + "/";
        var doomed = db.AssetItems.Where(a => a.FeedKey == feedKey && (a.PathLower == pathLower || a.PathLower.StartsWith(prefix)));

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var blobs = await doomed.Where(a => a.BlobId != null).Select(a => a.BlobId!).ToListAsync(cancellationToken);
        await doomed.ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return blobs;
    }

    public Task<bool> HasChildrenAsync(int feedKey, string folderLower, CancellationToken cancellationToken) =>
        db.AssetItems.AnyAsync(a => a.FeedKey == feedKey && a.ParentLower == folderLower, cancellationToken);
}
