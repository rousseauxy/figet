using FiGet.Core.Entities;

namespace FiGet.Core.Stores;

public interface IFeedStore
{
    Task<Feed?> FindAsync(string name, CancellationToken cancellationToken);

    Task<IReadOnlyList<Feed>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Creates the feed. Returns false when a feed with that name already exists.</summary>
    Task<bool> CreateAsync(Feed feed, CancellationToken cancellationToken);

    /// <summary>Changes the feed's settings. Returns false when the feed no longer exists.</summary>
    Task<bool> UpdateSettingsAsync(int key, bool anonymousRead, bool allowOverwrite, PackageDeletionBehavior deletionBehavior, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the feed with every package, version, dependency, symbol row and feed-scoped token it owns.
    /// Package files are not touched: the caller removes them through <c>IPackageStorage</c> first, because
    /// a leftover row is recoverable while a leftover file is not discoverable.
    /// Returns false when the feed no longer exists.
    /// </summary>
    Task<bool> DeleteAsync(int key, CancellationToken cancellationToken);

    /// <summary>How many package versions the feed holds, listed or not.</summary>
    Task<int> CountVersionsAsync(int key, CancellationToken cancellationToken);

    /// <summary>
    /// Adds an upstream to the feed, last in the order it is queried. A feed that gains its first
    /// upstream becomes a proxy feed. Returns false when the feed is gone or the name is already used.
    /// </summary>
    Task<bool> AddUpstreamAsync(int feedKey, FeedUpstream upstream, CancellationToken cancellationToken);

    /// <summary>
    /// Removes one upstream with the listings cached from it. Packages already cached stay: they were
    /// downloaded and are still what a client asked for. A feed that loses its last upstream becomes a
    /// curated feed again. Returns false when the upstream no longer exists.
    /// </summary>
    Task<bool> RemoveUpstreamAsync(int feedKey, int upstreamKey, CancellationToken cancellationToken);
}
