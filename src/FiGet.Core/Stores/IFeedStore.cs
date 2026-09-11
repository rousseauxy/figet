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
}
