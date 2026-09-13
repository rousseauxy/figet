using FiGet.Domain.Entities;

namespace FiGet.Application.Ports;

/// <summary>The rows of asset directories: which files and folders exist, and what is known about each.</summary>
public interface IAssetStore
{
    Task<AssetItem?> FindAsync(int feedKey, string pathLower, CancellationToken cancellationToken);

    /// <summary>
    /// The items directly inside a folder, or everything below it when <paramref name="recursive"/> is set.
    /// Folders first, then by name. A folder that does not exist has no items, which is not an error.
    /// </summary>
    Task<IReadOnlyList<AssetItem>> ListAsync(int feedKey, string folderLower, bool recursive, CancellationToken cancellationToken);

    /// <summary>Adds a row. Returns false when the path was taken in the meantime, by another request or replica.</summary>
    Task<bool> AddAsync(AssetItem item, CancellationToken cancellationToken);

    /// <summary>Saves changes to a row previously read from this store.</summary>
    Task UpdateAsync(AssetItem item, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the item and, for a folder, everything below it. Returns the blob ids of the files that
    /// went, so their bytes can be removed too.
    /// </summary>
    Task<IReadOnlyList<string>> DeleteAsync(int feedKey, string pathLower, CancellationToken cancellationToken);

    /// <summary>Whether a folder has anything in it.</summary>
    Task<bool> HasChildrenAsync(int feedKey, string folderLower, CancellationToken cancellationToken);
}
