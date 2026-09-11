using FiGet.Core.Entities;
using FiGet.Core.Stores;
using Microsoft.EntityFrameworkCore;

namespace FiGet.Persistence.Stores;

public sealed class EfFeedStore(FiGetDbContext db) : IFeedStore
{
    public Task<Feed?> FindAsync(string name, CancellationToken cancellationToken)
    {
        var lower = name.ToLowerInvariant();
        return db.Feeds.AsNoTracking().FirstOrDefaultAsync(f => f.NameLower == lower, cancellationToken);
    }

    public async Task<IReadOnlyList<Feed>> ListAsync(CancellationToken cancellationToken) =>
        await db.Feeds.AsNoTracking().OrderBy(f => f.NameLower).ToListAsync(cancellationToken);

    public async Task<bool> CreateAsync(Feed feed, CancellationToken cancellationToken)
    {
        feed.NameLower = feed.Name.ToLowerInvariant();
        if (await db.Feeds.AnyAsync(f => f.NameLower == feed.NameLower, cancellationToken))
        {
            return false;
        }

        db.Feeds.Add(feed);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            db.Entry(feed).State = EntityState.Detached;
            return false;
        }
    }
}
