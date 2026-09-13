namespace FiGet.Application.Ports;

/// <summary>Addresses the bytes of one asset file. Both parts are lower-case.</summary>
public readonly record struct AssetBlobKey(string Feed, string BlobId);

/// <summary>Blob storage for asset files. Implementations must be safe across replicas.</summary>
public interface IAssetStorage
{
    /// <summary>Stores the bytes, reading <paramref name="content"/> to its end. A reader never sees a partial file.</summary>
    Task SaveAsync(AssetBlobKey key, Stream content, CancellationToken cancellationToken);

    /// <summary>A seekable stream over the file, or null when it is not there.</summary>
    Task<Stream?> OpenAsync(AssetBlobKey key, CancellationToken cancellationToken);

    /// <summary>Removes the file. Not an error when it is already gone.</summary>
    Task DeleteAsync(AssetBlobKey key, CancellationToken cancellationToken);

    /// <summary>Removes every asset file of one directory. The name must be lower-cased.</summary>
    Task DeleteFeedAsync(string feedLower, CancellationToken cancellationToken);
}
