namespace FiGet.Application.Ports;

/// <summary>Addresses one package version in storage. Every part is lower-cased.</summary>
public readonly record struct PackageStorageKey(string Feed, string Id, string Version);

/// <summary>Addresses one symbol file in storage. Every part is lower-cased.</summary>
public readonly record struct SymbolStorageKey(string Feed, string FileName, string SymbolKey);

/// <summary>Blob storage for package and symbol files. Implementations must be safe across replicas.</summary>
public interface IPackageStorage
{
    /// <summary>Stores the nupkg and its nuspec. Replaces existing files only when <paramref name="overwrite"/> is true.</summary>
    Task SavePackageAsync(PackageStorageKey key, Stream nupkg, ReadOnlyMemory<byte> nuspec, bool overwrite, CancellationToken cancellationToken);

    Task<Stream?> OpenPackageAsync(PackageStorageKey key, CancellationToken cancellationToken);

    Task<Stream?> OpenNuspecAsync(PackageStorageKey key, CancellationToken cancellationToken);

    Task DeletePackageAsync(PackageStorageKey key, CancellationToken cancellationToken);

    Task SaveSymbolAsync(SymbolStorageKey key, Stream pdb, CancellationToken cancellationToken);

    Task<Stream?> OpenSymbolAsync(SymbolStorageKey key, CancellationToken cancellationToken);

    Task DeleteSymbolAsync(SymbolStorageKey key, CancellationToken cancellationToken);

    /// <summary>Removes every package and symbol file of one feed. The name must be lower-cased.</summary>
    Task DeleteFeedAsync(string feedLower, CancellationToken cancellationToken);
}
