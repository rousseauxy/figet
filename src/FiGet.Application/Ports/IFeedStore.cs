using FiGet.Application.Packages;
using FiGet.Domain.Entities;

namespace FiGet.Application.Ports;

public interface IFeedStore
{
    Task<Feed?> FindAsync(string name, CancellationToken cancellationToken);

    Task<IReadOnlyList<Feed>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Creates the feed. Returns false when a feed with that name already exists.</summary>
    Task<bool> CreateAsync(Feed feed, CancellationToken cancellationToken);

    /// <summary>Changes the feed's settings. Returns false when the feed no longer exists.</summary>
    Task<bool> UpdateSettingsAsync(int key, bool anonymousRead, bool allowOverwrite, PackageDeletionBehavior deletionBehavior, bool mergePushedIdsWithUpstreams, CancellationToken cancellationToken);

    /// <summary>Changes the feed's retention and cache pruning. Returns false when the feed no longer exists.</summary>
    Task<bool> UpdateRetentionAsync(int key, RetentionRules rules, CancellationToken cancellationToken);

    /// <summary>Changes the client address and the instruction templates; nulls restore the defaults. False when the feed no longer exists.</summary>
    Task<bool> UpdateInstructionsAsync(int key, string? clientBaseUrl, string? packageInstructions, string? feedInstructions, string? fileInstructions, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the feed with every package, version, dependency, symbol row, asset row and feed-scoped token it owns.
    /// Files are not touched: the caller removes them through <c>IPackageStorage</c> and <c>IAssetStorage</c> first, because
    /// a leftover row is recoverable while a leftover file is not discoverable.
    /// Returns false when the feed no longer exists.
    /// </summary>
    Task<bool> DeleteAsync(int key, CancellationToken cancellationToken);

    /// <summary>How many files an asset directory holds, folders not counted.</summary>
    Task<int> CountAssetsAsync(int key, CancellationToken cancellationToken);

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

    /// <summary>
    /// Moves one upstream a place up (towards first) or down in the feed's priority order. The first upstream in
    /// that order that holds an id owns it. Returns false when the upstream does not exist or is already at that end.
    /// </summary>
    Task<bool> MoveUpstreamAsync(int feedKey, int upstreamKey, bool up, CancellationToken cancellationToken);
}
