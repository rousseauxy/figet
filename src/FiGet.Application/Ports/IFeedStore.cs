using FiGet.Application.Packages;
using FiGet.Domain.Entities;

namespace FiGet.Application.Ports;

/// <summary>What became of a change to a feed's names.</summary>
public enum FeedNameChange
{
    Done,
    NotFound,

    /// <summary>A feed, an asset directory or an alternate name of one already has the name.</summary>
    NameTaken,

    /// <summary>The name is not a valid feed name.</summary>
    Invalid,

    /// <summary>The feed already has exactly that name.</summary>
    Unchanged,
}

/// <summary>What became of a change to an upstream.</summary>
public enum UpstreamChange
{
    Done,
    NotFound,

    /// <summary>Another upstream of the same feed has the name.</summary>
    NameTaken,
}

public interface IFeedStore
{
    /// <summary>The feed with this name, or with this as one of its alternate names. Case-insensitive.</summary>
    Task<Feed?> FindAsync(string name, CancellationToken cancellationToken);

    Task<IReadOnlyList<Feed>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Creates the feed. Returns false when a feed, or an alternate name of one, already has the name.</summary>
    Task<bool> CreateAsync(Feed feed, CancellationToken cancellationToken);

    /// <summary>Changes the feed's settings. Returns false when the feed no longer exists.</summary>
    Task<bool> UpdateSettingsAsync(int key, bool anonymousRead, bool anonymousList, bool allowOverwrite, PackageDeletionBehavior deletionBehavior, bool mergePushedIdsWithUpstreams, int? chartColor, FeedPurpose purpose, CancellationToken cancellationToken);

    /// <summary>
    /// Points an asset directory at a folder on the server, or away from one, as configuration says on start. Returns
    /// whether anything changed.
    /// </summary>
    Task<bool> UpdateFolderAsync(int key, string? folderRoot, bool folderWritable, CancellationToken cancellationToken);

    /// <summary>The addresses the feed may be reached from, as <c>FeedNetworks.Normalise</c> left them; null for any.</summary>
    Task<bool> UpdateAllowedNetworksAsync(int key, string? allowedNetworks, CancellationToken cancellationToken);

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

    /// <summary>
    /// Renames the feed. With <paramref name="keepOldName"/> the old name becomes an alternate name, so clients using it keep
    /// working. Renaming to one of the feed's own alternate names takes that name back from the list. A change of case only
    /// renames and never adds an alternate name, because names are case-insensitive. Files are not touched: they are stored
    /// by the feed's key.
    /// </summary>
    Task<FeedNameChange> RenameAsync(int key, string name, bool keepOldName, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>The names a rename left behind. Only a rename makes one; there is no other way to add a name.</summary>
    Task<IReadOnlyList<FeedAlias>> ListAliasesAsync(int key, CancellationToken cancellationToken);

    /// <summary>Removes one alternate name of the feed. Returns the name removed, or null when there was no such name.</summary>
    Task<string?> RemoveAliasAsync(int key, int aliasKey, CancellationToken cancellationToken);

    /// <summary>Notes that a client reached a feed by this alternate name.</summary>
    Task TouchAliasAsync(string nameLower, DateTime nowUtc, CancellationToken cancellationToken);

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
    /// Changes an upstream in place - name, URL, protocol, id patterns, credential reference, whether it is asked - keeping
    /// its place in the order. <paramref name="changed"/> carries the upstream's key and every new value. A different URL,
    /// protocol or credential makes what was stored about the old source wrong, so the version lists and descriptions cached
    /// from it are dropped and fetched again; packages already cached stay, as they do when an upstream is removed.
    /// </summary>
    Task<UpstreamChange> UpdateUpstreamAsync(int feedKey, FeedUpstream changed, CancellationToken cancellationToken);

    /// <summary>
    /// Moves one upstream a place up (towards first) or down in the feed's priority order. The first upstream in
    /// that order that holds an id owns it. Returns false when the upstream does not exist or is already at that end.
    /// </summary>
    Task<bool> MoveUpstreamAsync(int feedKey, int upstreamKey, bool up, CancellationToken cancellationToken);
}
