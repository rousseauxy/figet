using FiGet.Application.Packages;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FiGet.Infrastructure.Persistence.Stores;

public sealed class EfFeedStore(FiGetDbContext db) : IFeedStore
{
    public async Task<Feed?> FindAsync(string name, CancellationToken cancellationToken)
    {
        // Upstreams come along: every protocol request resolves its feed here, and a proxy feed needs them
        // to answer at all. A curated feed has none, so this costs nothing there.
        var lower = name.ToLowerInvariant();
        var feeds = db.Feeds.AsNoTracking().Include(f => f.Upstreams.OrderBy(u => u.Ordinal));

        // The alternate names only when the name itself found nothing: a second query for requests by an old name and for
        // names that do not exist, none for the rest.
        return await feeds.FirstOrDefaultAsync(f => f.NameLower == lower, cancellationToken)
            ?? await feeds.FirstOrDefaultAsync(f => db.FeedAliases.Any(a => a.FeedKey == f.Key && a.NameLower == lower), cancellationToken);
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
        if (await NameTakenAsync(feed.NameLower, cancellationToken))
        {
            return false;
        }

        // The feed and its name row in one transaction: the name row's primary key is what refuses a name another feed,
        // or an alternate name of one, took in the same instant.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Feeds.Add(feed);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            db.Names.Add(new FeedName { NameLower = feed.NameLower, FeedKey = feed.Key });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            feed.Key = 0;
            return false;
        }
    }

    public async Task<bool> UpdateFolderAsync(int key, string? folderRoot, bool folderWritable, CancellationToken cancellationToken)
    {
        var root = string.IsNullOrWhiteSpace(folderRoot) ? null : folderRoot.Trim();
        var writable = root is not null && folderWritable;
        return await db.Feeds
            .Where(f => f.Key == key && (f.FolderRoot != root || f.FolderWritable != writable))
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.FolderRoot, root).SetProperty(f => f.FolderWritable, writable), cancellationToken) > 0;
    }

    public async Task<bool> UpdateAllowedNetworksAsync(int key, string? allowedNetworks, CancellationToken cancellationToken)
    {
        var networks = string.IsNullOrWhiteSpace(allowedNetworks) ? null : allowedNetworks;
        return await db.Feeds
            .Where(f => f.Key == key)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.AllowedNetworks, networks), cancellationToken) > 0;
    }

    public async Task<bool> UpdateSettingsAsync(int key, bool anonymousRead, bool anonymousList, bool allowOverwrite, PackageDeletionBehavior deletionBehavior, bool mergePushedIdsWithUpstreams, int? chartColor, FeedPurpose purpose, CancellationToken cancellationToken)
    {
        var feed = await db.Feeds.FirstOrDefaultAsync(f => f.Key == key, cancellationToken);
        if (feed is null)
        {
            return false;
        }

        feed.AnonymousRead = anonymousRead;
        feed.AnonymousList = feed.Kind == FeedKind.Assets && anonymousList;
        feed.AllowOverwrite = allowOverwrite;
        feed.DeletionBehavior = deletionBehavior;
        feed.MergePushedIdsWithUpstreams = mergePushedIdsWithUpstreams;
        feed.ChartColor = chartColor is >= 1 and <= FeedColors.Count ? chartColor : null;
        feed.Purpose = feed.Kind == FeedKind.Assets || !Enum.IsDefined(purpose) ? FeedPurpose.Any : purpose;
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
        await db.FeedAliases.Where(a => a.FeedKey == key).ExecuteDeleteAsync(cancellationToken);
        await db.Names.Where(n => n.FeedKey == key).ExecuteDeleteAsync(cancellationToken);
        await db.FeedUsage.Where(u => u.FeedKey == key).ExecuteDeleteAsync(cancellationToken);
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

    public async Task<FeedNameChange> RenameAsync(int key, string name, bool keepOldName, DateTime nowUtc, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var lower = name.ToLowerInvariant();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var feed = await db.Feeds.FirstOrDefaultAsync(f => f.Key == key, cancellationToken);
        if (feed is null)
        {
            return FeedNameChange.NotFound;
        }

        if (string.Equals(feed.Name, name, StringComparison.Ordinal))
        {
            return FeedNameChange.Unchanged;
        }

        if (lower != feed.NameLower)
        {
            if (await db.Feeds.AnyAsync(f => f.NameLower == lower, cancellationToken))
            {
                return FeedNameChange.NameTaken;
            }

            // One of this feed's own alternate names is the name it goes back to, and stops being an alternate.
            var ownAlias = false;
            if (await db.FeedAliases.FirstOrDefaultAsync(a => a.NameLower == lower, cancellationToken) is { } alias)
            {
                if (alias.FeedKey != key)
                {
                    return FeedNameChange.NameTaken;
                }

                db.FeedAliases.Remove(alias);
                ownAlias = true;
            }

            // The name rows move with the names: the new name claims its row (or turns its alternate row back into the
            // feed's own), and the old name's row stays as an alternate or goes.
            var oldRow = await db.Names.FirstOrDefaultAsync(n => n.NameLower == feed.NameLower, cancellationToken);
            if (keepOldName)
            {
                db.FeedAliases.Add(new FeedAlias { FeedKey = key, Name = feed.Name, NameLower = feed.NameLower, CreatedUtc = nowUtc });
                if (oldRow is not null)
                {
                    oldRow.IsAlias = true;
                }
                else
                {
                    db.Names.Add(new FeedName { NameLower = feed.NameLower, FeedKey = key, IsAlias = true });
                }
            }
            else if (oldRow is not null)
            {
                db.Names.Remove(oldRow);
            }

            if (ownAlias && await db.Names.FirstOrDefaultAsync(n => n.NameLower == lower, cancellationToken) is { } newRow)
            {
                newRow.IsAlias = false;
            }
            else
            {
                db.Names.Add(new FeedName { NameLower = lower, FeedKey = key });
            }
        }

        feed.Name = name;
        feed.NameLower = lower;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Taken by a request that got there between the check and the save: the name row's primary key refused it.
            db.ChangeTracker.Clear();
            return FeedNameChange.NameTaken;
        }

        await transaction.CommitAsync(cancellationToken);
        return FeedNameChange.Done;
    }

    public async Task<IReadOnlyList<FeedAlias>> ListAliasesAsync(int key, CancellationToken cancellationToken) =>
        await db.FeedAliases.AsNoTracking().Where(a => a.FeedKey == key).OrderBy(a => a.NameLower).ToListAsync(cancellationToken);

    public async Task<string?> RemoveAliasAsync(int key, int aliasKey, CancellationToken cancellationToken)
    {
        var alias = await db.FeedAliases.FirstOrDefaultAsync(a => a.Key == aliasKey && a.FeedKey == key, cancellationToken);
        if (alias is null)
        {
            return null;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.FeedAliases.Remove(alias);
        await db.SaveChangesAsync(cancellationToken);
        await db.Names.Where(n => n.NameLower == alias.NameLower && n.FeedKey == key).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return alias.Name;
    }

    public Task TouchAliasAsync(string nameLower, DateTime nowUtc, CancellationToken cancellationToken) =>
        db.FeedAliases.Where(a => a.NameLower == nameLower).ExecuteUpdateAsync(s => s.SetProperty(a => a.LastUsedUtc, nowUtc), cancellationToken);

    /// <summary>
    /// Whether a feed, a directory or an alternate name has the name, for a friendly refusal before the write; the
    /// <c>Names</c> table's primary key is what makes the answer hold when two writes race.
    /// </summary>
    private Task<bool> NameTakenAsync(string lower, CancellationToken cancellationToken) =>
        db.Names.AnyAsync(n => n.NameLower == lower, cancellationToken);

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

    public async Task<UpstreamChange> UpdateUpstreamAsync(int feedKey, FeedUpstream changed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changed);
        var upstreams = await db.FeedUpstreams.Where(u => u.FeedKey == feedKey).ToListAsync(cancellationToken);
        var upstream = upstreams.Find(u => u.Key == changed.Key);
        if (upstream is null)
        {
            return UpstreamChange.NotFound;
        }

        if (upstreams.Exists(u => u.Key != changed.Key && u.Name.Equals(changed.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return UpstreamChange.NameTaken;
        }

        var sourceChanged = upstream.Url != changed.Url || upstream.Kind != changed.Kind || upstream.CredentialRef != changed.CredentialRef;
        upstream.Name = changed.Name;
        upstream.Url = changed.Url;
        upstream.Kind = changed.Kind;
        upstream.Allow = changed.Allow;
        upstream.Deny = changed.Deny;
        upstream.CredentialRef = changed.CredentialRef;
        upstream.Enabled = changed.Enabled;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return UpstreamChange.NameTaken;
        }

        if (sourceChanged)
        {
            await db.CachedUpstreamIndexes.Where(c => c.FeedUpstreamKey == changed.Key).ExecuteDeleteAsync(cancellationToken);
            await db.CachedUpstreamDescriptions.Where(c => c.FeedUpstreamKey == changed.Key).ExecuteDeleteAsync(cancellationToken);
            await EfUpstreamDescriptionStore.SweepOrphanedTagSetsAsync(db, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return UpstreamChange.Done;
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
