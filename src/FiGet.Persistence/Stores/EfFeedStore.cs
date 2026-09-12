using FiGet.Core.Entities;
using FiGet.Core.Stores;
using Microsoft.EntityFrameworkCore;

namespace FiGet.Persistence.Stores;

public sealed class EfFeedStore(FiGetDbContext db) : IFeedStore
{
    public Task<Feed?> FindAsync(string name, CancellationToken cancellationToken)
    {
        // Upstreams come along: every protocol request resolves its feed here, and a proxy feed needs them
        // to answer at all. A curated feed has none, so this costs nothing there.
        var lower = name.ToLowerInvariant();
        return db.Feeds
            .AsNoTracking()
            .Include(f => f.Upstreams.OrderBy(u => u.Ordinal))
            .FirstOrDefaultAsync(f => f.NameLower == lower, cancellationToken);
    }

    public async Task<IReadOnlyList<Feed>> ListAsync(CancellationToken cancellationToken) =>
        await db.Feeds
            .AsNoTracking()
            .Include(f => f.Upstreams.OrderBy(u => u.Ordinal))
            .OrderBy(f => f.NameLower)
            .ToListAsync(cancellationToken);

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

    public async Task<bool> UpdateSettingsAsync(int key, bool anonymousRead, bool allowOverwrite, PackageDeletionBehavior deletionBehavior, CancellationToken cancellationToken)
    {
        var feed = await db.Feeds.FirstOrDefaultAsync(f => f.Key == key, cancellationToken);
        if (feed is null)
        {
            return false;
        }

        feed.AnonymousRead = anonymousRead;
        feed.AllowOverwrite = allowOverwrite;
        feed.DeletionBehavior = deletionBehavior;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(int key, CancellationToken cancellationToken)
    {
        if (!await db.Feeds.AnyAsync(f => f.Key == key, cancellationToken))
        {
            return false;
        }

        // Deleted explicitly and in dependency order rather than by database cascade, so both providers
        // behave the same and no row survives because a connection had foreign keys switched off.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.SymbolFiles.Where(s => s.FeedKey == key).ExecuteDeleteAsync(cancellationToken);
        await db.PackageDependencies
            .Where(d => db.PackageVersions.Any(v => v.Key == d.PackageVersionKey && db.Packages.Any(p => p.Key == v.PackageKey && p.FeedKey == key)))
            .ExecuteDeleteAsync(cancellationToken);
        await db.PackageVersions
            .Where(v => db.Packages.Any(p => p.Key == v.PackageKey && p.FeedKey == key))
            .ExecuteDeleteAsync(cancellationToken);
        await db.Packages.Where(p => p.FeedKey == key).ExecuteDeleteAsync(cancellationToken);
        await db.AccessTokens.Where(t => t.FeedKey == key).ExecuteDeleteAsync(cancellationToken);
        await db.Feeds.Where(f => f.Key == key).ExecuteDeleteAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public Task<int> CountVersionsAsync(int key, CancellationToken cancellationToken) =>
        db.PackageVersions.CountAsync(v => db.Packages.Any(p => p.Key == v.PackageKey && p.FeedKey == key), cancellationToken);

    public async Task<bool> AddUpstreamAsync(int feedKey, FeedUpstream upstream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        var feed = await db.Feeds.FirstOrDefaultAsync(f => f.Key == feedKey, cancellationToken);
        if (feed is null)
        {
            return false;
        }

        var existing = await db.FeedUpstreams.Where(u => u.FeedKey == feedKey).ToListAsync(cancellationToken);
        if (existing.Exists(u => u.Name.Equals(upstream.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        upstream.FeedKey = feedKey;
        upstream.Ordinal = existing.Count == 0 ? 0 : existing.Max(u => u.Ordinal) + 1;
        db.FeedUpstreams.Add(upstream);

        // A feed with an upstream behaves like a proxy feed, so that is what it is called.
        feed.Kind = FeedKind.Proxy;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RemoveUpstreamAsync(int feedKey, int upstreamKey, CancellationToken cancellationToken)
    {
        var upstream = await db.FeedUpstreams.FirstOrDefaultAsync(u => u.Key == upstreamKey && u.FeedKey == feedKey, cancellationToken);
        if (upstream is null)
        {
            return false;
        }

        await db.CachedUpstreamIndexes.Where(c => c.FeedUpstreamKey == upstreamKey).ExecuteDeleteAsync(cancellationToken);
        db.FeedUpstreams.Remove(upstream);
        await db.SaveChangesAsync(cancellationToken);

        if (!await db.FeedUpstreams.AnyAsync(u => u.FeedKey == feedKey, cancellationToken))
        {
            var feed = await db.Feeds.FirstOrDefaultAsync(f => f.Key == feedKey, cancellationToken);
            if (feed is not null)
            {
                feed.Kind = FeedKind.Curated;
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        return true;
    }
}
