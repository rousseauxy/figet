namespace FiGet.Application.Ports;

/// <summary>Addresses the bytes of one asset file: the directory by its key, so a rename moves nothing.</summary>
public readonly record struct AssetBlobKey(int Feed, string BlobId);

/// <summary>
/// Addresses one unfinished multipart upload. The id is derived from the one the client chose, so it is
/// always 32 lower-case hex characters whatever the client sent.
/// </summary>
public readonly record struct AssetUploadKey(int Feed, string UploadId);

/// <summary>One stored part of a multipart upload.</summary>
public sealed record AssetUploadPart(int Index, long Offset, long Size);

/// <summary>Blob storage for asset files. Implementations must be safe across replicas.</summary>
public interface IAssetStorage
{
    /// <summary>Stores the bytes, reading <paramref name="content"/> to its end. A reader never sees a partial file.</summary>
    Task SaveAsync(AssetBlobKey key, Stream content, CancellationToken cancellationToken);

    /// <summary>A seekable stream over the file, or null when it is not there.</summary>
    Task<Stream?> OpenAsync(AssetBlobKey key, CancellationToken cancellationToken);

    /// <summary>Removes the file. Not an error when it is already gone.</summary>
    Task DeleteAsync(AssetBlobKey key, CancellationToken cancellationToken);

    /// <summary>Removes every asset file of one directory, finished or not.</summary>
    Task DeleteFeedAsync(int feedKey, CancellationToken cancellationToken);

    /// <summary>
    /// Stores one part of a multipart upload, replacing a part already stored at that index. On shared
    /// storage, not in local temp: the parts of one upload may arrive at different replicas.
    /// </summary>
    Task SaveUploadPartAsync(AssetUploadKey key, int index, long offset, Stream content, CancellationToken cancellationToken);

    /// <summary>Records what the whole upload will be, so the request that completes it can check the parts.</summary>
    Task SaveUploadManifestAsync(AssetUploadKey key, long totalSize, int totalParts, CancellationToken cancellationToken);

    /// <summary>The recorded total size and part count, or null when no part of this upload was ever stored.</summary>
    Task<(long TotalSize, int TotalParts)?> ReadUploadManifestAsync(AssetUploadKey key, CancellationToken cancellationToken);

    Task<IReadOnlyList<AssetUploadPart>> ListUploadPartsAsync(AssetUploadKey key, CancellationToken cancellationToken);

    Task<Stream?> OpenUploadPartAsync(AssetUploadKey key, AssetUploadPart part, CancellationToken cancellationToken);

    /// <summary>Removes an upload's parts, completed or abandoned.</summary>
    Task DeleteUploadAsync(AssetUploadKey key, CancellationToken cancellationToken);

    /// <summary>Removes uploads nothing was added to since <paramref name="olderThanUtc"/>. Returns how many went.</summary>
    Task<int> PruneUploadsAsync(DateTime olderThanUtc, CancellationToken cancellationToken);
}
