using FiGet.Application.Assets;
using FiGet.Domain.Assets;
using FiGet.Domain.Entities;

namespace FiGet.Application.Ports;

/// <summary>
/// An asset directory whose content is a folder on the server (<see cref="Feed.FolderRoot"/>): a mounted share written
/// directly by applications and people. Read as it is, with no copy and no row per file, so a file placed on the share
/// is served at once and a deleted one is gone. Items come back as <see cref="AssetItem"/> rows that were never stored:
/// no key, no blob, no hashes, and no content type, which the caller takes from the extension.
/// </summary>
public interface IFolderAssets
{
    Task<AssetItem?> FindAsync(Feed feed, AssetPath path, CancellationToken cancellationToken);

    Task<IReadOnlyList<AssetItem>> ListAsync(Feed feed, AssetPath folder, bool recursive, CancellationToken cancellationToken);

    Task<Stream?> OpenAsync(Feed feed, AssetPath path, CancellationToken cancellationToken);

    /// <summary>Stores a file on the folder, with the verb's rule about existing files, under the size limit.</summary>
    Task<AssetOutcome> WriteAsync(Feed feed, AssetPath path, Stream content, AssetWriteMode mode, long maxBytes, CancellationToken cancellationToken);

    Task<AssetOutcome> CreateFolderAsync(Feed feed, AssetPath path, CancellationToken cancellationToken);

    Task<AssetOutcome> DeleteAsync(Feed feed, AssetPath path, bool recursive, CancellationToken cancellationToken);
}
