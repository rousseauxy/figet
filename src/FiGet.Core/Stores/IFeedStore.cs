using FiGet.Core.Entities;

namespace FiGet.Core.Stores;

public interface IFeedStore
{
    Task<Feed?> FindAsync(string name, CancellationToken cancellationToken);

    Task<IReadOnlyList<Feed>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Creates the feed. Returns false when a feed with that name already exists.</summary>
    Task<bool> CreateAsync(Feed feed, CancellationToken cancellationToken);
}
