using FiGet.Application.Ports;
using FiGet.Domain.Entities;

namespace FiGet.Application.Accounts;

/// <summary>
/// Who may do what with a feed: the one place that answers it, for pages and protocols alike (docs/auth-plan.md,
/// "Effective permission"). Evaluated on every request rather than cached, so removing a grant or a group membership
/// takes effect on the next request.
/// </summary>
public sealed class FeedAccessService(IFeedPermissionStore permissions)
{
    /// <summary>
    /// The level of a signed-in account, or of nobody when <paramref name="actor"/> is null. Admins and super admins
    /// manage every feed; otherwise the higher of anonymous read and the account's own and its groups' grants.
    /// </summary>
    public async Task<FeedAccessLevel> LevelAsync(Feed feed, AccountActor? actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        if (actor?.Role >= UserRole.Admin)
        {
            return FeedAccessLevel.Manage;
        }

        var level = feed.AnonymousRead ? FeedAccessLevel.Read : FeedAccessLevel.None;
        if (actor is null)
        {
            return level;
        }

        var granted = await permissions.GrantedAsync(feed.Key, actor.Key, cancellationToken);
        return granted > level ? granted : level;
    }

    /// <summary>The same for a list of feeds, in one query for the grants rather than one per feed.</summary>
    public async Task<IReadOnlyDictionary<int, FeedAccessLevel>> LevelsAsync(IEnumerable<Feed> feeds, AccountActor? actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feeds);
        var granted = actor is null || actor.Role >= UserRole.Admin
            ? new Dictionary<int, FeedAccessLevel>()
            : await permissions.GrantedOnAllAsync(actor.Key, cancellationToken);

        var levels = new Dictionary<int, FeedAccessLevel>();
        foreach (var feed in feeds)
        {
            if (actor?.Role >= UserRole.Admin)
            {
                levels[feed.Key] = FeedAccessLevel.Manage;
                continue;
            }

            var level = feed.AnonymousRead ? FeedAccessLevel.Read : FeedAccessLevel.None;
            if (granted.TryGetValue(feed.Key, out var grant) && grant > level)
            {
                level = grant;
            }

            levels[feed.Key] = level;
        }

        return levels;
    }
}
