using FiGet.Application.Packages;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FiGet.Infrastructure.Persistence.Stores;

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

    public async Task<bool> UpdateSettingsAsync(int key, bool anonymousRead, bool allowOverwrite, PackageDeletionBehavior deletionBehavior, bool mergePushedIdsWithUpstreams, CancellationToken cancellationToken)
    {
        var feed = await db.Feeds.FirstOrDefaultAsync(f => f.Key == key, cancellationToken);
        if (feed is null)
        {
            return false;
        }

        feed.AnonymousRead = anonymousRead;
        feed.AllowOverwrite = allowOverwrite;
        feed.DeletionBehavior = deletionBehavior;
        feed.MergePushedIdsWithUpstreams = mergePushedIdsWithUpstreams;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> UpdateRetentionAsync(int key, RetentionRules rules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return await db.Feeds
            .Where(f => f.Key == key)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(f => f.RetainStableVersions, rules.KeepStable)
                    .SetProperty(f => f.RetainPrereleaseVersions, rules.KeepPrerelease)
                    .SetProperty(f => f.RetainPerMajorVersion, rules.PerMajorVersion)
                    .SetProperty(f => f.RetainIfUsedWithinDays, rules.KeepIfUsedWithinDays)
                    .SetProperty(f => f.PruneCachedAfterDays, rules.PruneCachedAfterDays),
                cancellationToken) > 0;
    }

    public async Task<bool> UpdateInstructionsAsync(int key, string? clientBaseUrl, string? packageInstructions, string? feedInstructions, string? fileInstructions, CancellationToken cancellationToken) =>
        await db.Feeds
            .Where(f => f.Key == key)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(f => f.ClientBaseUrl, clientBaseUrl)
                    .SetProperty(f => f.PackageInstructions, packageInstructions)
                    .SetProperty(f => f.FeedInstructions, feedInstructions)
                    .SetProperty(f => f.FileInstructions, fileInstructions),
                cancellationToken) > 0;

    public async Task<bool> DeleteAsync(int key, CancellationToken cancellationToken)
    {
        if (!await db.Feeds.AnyAsync(f => f.Key == key, cancellationToken))
        {
            return false;
        }

        // Deleted explicitly and in dependency order rather than by database cascade, so both providers
        // behave the same and no row survives because a connection had foreign keys switched off.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.AssetItems.Where(a => a.FeedKey == key).ExecuteDeleteAsync(cancellationToken);
        await db.SymbolFiles.Where(s => s.FeedKey == key).ExecuteDeleteAsync(cancellationToken);
        await db.PackageDependencies
            .Where(d => db.PackageVersions.Any(v => v.Key == d.PackageVersionKey && db.Packages.Any(p => p.Key == v.PackageKey && p.FeedKey == key)))
            .ExecuteDeleteAsync(cancellationToken);
        await db.PackageVersions
            .Where(v => db.Packages.Any(p => p.Key == v.PackageKey && p.FeedKey == key))
            .ExecuteDeleteAsync(cancellationToken);
        await db.Packages.Where(p => p.FeedKey == key).ExecuteDeleteAsync(cancellationToken);
        await db.AccessTokens.Where(t => t.FeedKey == key).ExecuteDeleteAsync(cancellationToken);
        await db.FeedPermissions.Where(p => p.FeedKey == key).ExecuteDeleteAsync(cancellationToken);
        await db.CachedUpstreamDescriptions
            .Where(d => db.FeedUpstreams.Any(u => u.Key == d.FeedUpstreamKey && u.FeedKey == key))
            .ExecuteDeleteAsync(cancellationToken);
        await db.Feeds.Where(f => f.Key == key).ExecuteDeleteAsync(cancellationToken);
        await EfUpstreamDescriptionStore.SweepOrphanedTagSetsAsync(db, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public Task<int> CountAssetsAsync(int key, CancellationToken cancellationToken) =>
        db.AssetItems.CountAsync(a => a.FeedKey == key && !a.IsDirectory, cancellationToken);

    public Task<int> CountVersionsAsync(int key, CancellationToken cancellationToken) =>
        db.PackageVersions.CountAsync(v => db.Packages.Any(p => p.Key == v.PackageKey && p.FeedKey == key), cancellationToken);

    public async Task<bool> AddUpstreamAsync(int feedKey, FeedUpstream upstream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        var feed = await db.Feeds.FirstOrDefaultAsync(f => f.Key == feedKey, cancellationToken);

        // An asset directory has files, not packages, so there is nothing an upstream could offer it; and
        // gaining one would silently turn it into a proxy feed.
        if (feed is null || feed.Kind == FeedKind.Assets)
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
        await db.CachedUpstreamDescriptions.Where(c => c.FeedUpstreamKey == upstreamKey).ExecuteDeleteAsync(cancellationToken);
        db.FeedUpstreams.Remove(upstream);
        await db.SaveChangesAsync(cancellationToken);
        await EfUpstreamDescriptionStore.SweepOrphanedTagSetsAsync(db, cancellationToken);

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

    public async Task<bool> MoveUpstreamAsync(int feedKey, int upstreamKey, bool up, CancellationToken cancellationToken)
    {
        var upstreams = await db.FeedUpstreams
            .Where(u => u.FeedKey == feedKey)
            .OrderBy(u => u.Ordinal)
            .ThenBy(u => u.Key)
            .ToListAsync(cancellationToken);

        var index = upstreams.FindIndex(u => u.Key == upstreamKey);
        var other = up ? index - 1 : index + 1;
        if (index < 0 || other < 0 || other >= upstreams.Count)
        {
            return false;
        }

        (upstreams[index], upstreams[other]) = (upstreams[other], upstreams[index]);

        // Renumbered from zero rather than two ordinals swapped: rows seeded or added over time can share an
        // ordinal or leave gaps, and a swap of equal numbers would change nothing.
        for (var i = 0; i < upstreams.Count; i++)
        {
            upstreams[i].Ordinal = i;
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
