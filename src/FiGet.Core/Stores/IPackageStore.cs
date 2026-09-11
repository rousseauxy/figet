using FiGet.Core.Entities;

namespace FiGet.Core.Stores;

/// <summary>Query and command surface over package metadata.</summary>
public interface IPackageStore
{
    /// <summary>The package (with original-case id) and all its versions, including unlisted.</summary>
    Task<Package?> GetPackageAsync(int feedKey, string idLower, bool includeDependencies, CancellationToken cancellationToken);

    /// <summary>Several packages with all their versions, without dependencies.</summary>
    Task<IReadOnlyList<Package>> GetPackagesAsync(IReadOnlyCollection<long> packageKeys, CancellationToken cancellationToken);

    Task<PackageVersion?> GetVersionAsync(int feedKey, string idLower, string normalizedVersionLower, CancellationToken cancellationToken);

    Task<SearchPage> SearchAsync(int feedKey, PackageSearchFilter filter, int skip, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> AutocompleteIdsAsync(int feedKey, string query, bool includePrerelease, bool includeSemVer2, int skip, int take, CancellationToken cancellationToken);

    /// <summary>
    /// Adds the version, creating the package row when needed. When <paramref name="replaceExisting"/> is
    /// true an existing row for the same version is replaced. Returns false when the version exists and
    /// replacing is not allowed.
    /// </summary>
    Task<bool> AddVersionAsync(int feedKey, string id, PackageVersion version, bool replaceExisting, CancellationToken cancellationToken);

    Task<bool> SetListedAsync(int feedKey, string idLower, string normalizedVersionLower, bool listed, CancellationToken cancellationToken);

    /// <summary>Deletes the version row with its dependencies and symbol rows; removes the package row when it was the last version.</summary>
    Task<bool> DeleteVersionAsync(int feedKey, string idLower, string normalizedVersionLower, CancellationToken cancellationToken);

    Task IncrementDownloadsAsync(long packageVersionKey, CancellationToken cancellationToken);

    /// <summary>Replaces the symbol rows of a package version.</summary>
    Task ReplaceSymbolFilesAsync(long packageVersionKey, IReadOnlyList<SymbolFile> files, CancellationToken cancellationToken);

    Task<IReadOnlyList<SymbolFile>> GetSymbolFilesAsync(long packageVersionKey, CancellationToken cancellationToken);

    Task<SymbolFile?> FindSymbolFileAsync(int feedKey, string fileNameLower, string symbolKeyLower, CancellationToken cancellationToken);
}

/// <summary>A page of package keys matching a search, in result order, plus the total number of matching packages.</summary>
public sealed record SearchPage(IReadOnlyList<long> PackageKeys, int TotalHits);

public sealed record PackageSearchFilter(
    IReadOnlyList<SearchTerm> Terms,
    bool IncludePrerelease,
    bool IncludeSemVer2,
    string? PackageTypeLower);
