using FiGet.Domain.Entities;

namespace FiGet.Application.Ports;

/// <summary>The cache modes set on an asset directory's folders (<see cref="AssetCachePolicy"/>).</summary>
public interface IAssetCachePolicyStore
{
    /// <summary>Every policy of the directory, so the nearest folder above a file can be found in memory.</summary>
    Task<IReadOnlyList<AssetCachePolicy>> ListAsync(int feedKey, CancellationToken cancellationToken);

    /// <summary>Sets the mode of one folder (empty path: the directory itself), replacing what was there.</summary>
    Task SetAsync(int feedKey, string pathLower, AssetCacheMode mode, int? maxAgeSeconds, CancellationToken cancellationToken);

    /// <summary>Removes the folder's own mode, so it inherits again. False when it had none.</summary>
    Task<bool> RemoveAsync(int feedKey, string pathLower, CancellationToken cancellationToken);
}
