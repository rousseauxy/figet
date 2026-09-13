using FiGet.Application.Packages;
using FiGet.Domain.Entities;
using FiGet.Domain.Search;

namespace FiGet.Application.Ports;

/// <summary>Query and command surface over package metadata.</summary>
public interface IPackageStore
{
    /// <summary>The package (with original-case id) and all its versions, including unlisted.</summary>
    Task<Package?> GetPackageAsync(int feedKey, string idLower, bool includeDependencies, CancellationToken cancellationToken);

    /// <summary>Several packages with all their versions, without dependencies.</summary>
    Task<IReadOnlyList<Package>> GetPackagesAsync(IReadOnlyCollection<long> packageKeys, CancellationToken cancellationToken);

    /// <summary>Which of these lower-cased ids the feed holds a package for.</summary>
    Task<IReadOnlySet<string>> HeldIdsAsync(int feedKey, IReadOnlyCollection<string> idsLower, CancellationToken cancellationToken);

    /// <summary>
    /// Every package the feed stores, with every version, listed or not, and no dependencies. What a
    /// management listing of a whole feed needs; a feed of a few thousand versions is one query per table.
    /// </summary>
    Task<IReadOnlyList<Package>> ListPackagesAsync(int feedKey, CancellationToken cancellationToken);

    Task<PackageVersion?> GetVersionAsync(int feedKey, string idLower, string normalizedVersionLower, CancellationToken cancellationToken);

    Task<SearchPage> SearchAsync(int feedKey, PackageSearchFilter filter, int skip, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> AutocompleteIdsAsync(int feedKey, string query, bool includePrerelease, bool includeSemVer2, int skip, int take, CancellationToken cancellationToken);

    /// <summary>
    /// Adds the version, creating the package row when needed. When <paramref name="replaceExisting"/> is
    /// true an existing row for the same version is replaced. Returns false when the version exists and
    /// replacing is not allowed.
    /// </summary>
    Task<bool> AddVersionAsync(int feedKey, string id, PackageVersion version, bool replaceExisting, CancellationToken cancellationToken);

    /// <summary>
    /// Versions this feed holds that it does not advertise, newest first, each with its package so the id
    /// can be shown. Every other query here answers "what does this feed offer"; this is the opposite view,
    /// and it is the only way to find a version again once it has been unlisted.
    /// </summary>
    Task<IReadOnlyList<PackageVersion>> ListUnlistedAsync(int feedKey, int skip, int take, CancellationToken cancellationToken);

    Task<bool> SetListedAsync(int feedKey, string idLower, string normalizedVersionLower, bool listed, CancellationToken cancellationToken);

    /// <summary>Sets when a version was published, for a cached copy whose upstream says so.</summary>
    Task<bool> SetPublishedAsync(int feedKey, string idLower, string normalizedVersionLower, DateTime publishedUtc, CancellationToken cancellationToken);

    /// <summary>Deletes the version row with its dependencies and symbol rows; removes the package row when it was the last version.</summary>
    Task<bool> DeleteVersionAsync(int feedKey, string idLower, string normalizedVersionLower, CancellationToken cancellationToken);

    /// <summary>Counts a download and marks the version used now, which is what retention and cache pruning look at.</summary>
    Task IncrementDownloadsAsync(long packageVersionKey, DateTime utcNow, CancellationToken cancellationToken);

    /// <summary>Every version of every package in the feed, with what retention decides on and nothing else.</summary>
    Task<IReadOnlyList<RetentionCandidate>> ListRetentionCandidatesAsync(int feedKey, CancellationToken cancellationToken);

    /// <summary>Replaces the symbol rows of a package version.</summary>
    Task ReplaceSymbolFilesAsync(long packageVersionKey, IReadOnlyList<SymbolFile> files, CancellationToken cancellationToken);

    Task<IReadOnlyList<SymbolFile>> GetSymbolFilesAsync(long packageVersionKey, CancellationToken cancellationToken);

    Task<SymbolFile?> FindSymbolFileAsync(int feedKey, string fileNameLower, string symbolKeyLower, CancellationToken cancellationToken);
}

/// <summary>A page of package keys matching a search, in result order, plus the total number of matching packages.</summary>
public sealed record SearchPage(IReadOnlyList<long> PackageKeys, int TotalHits);

/// <param name="Sort">How the matching packages are ordered; null is relevance, as the protocols expect.</param>
public sealed record PackageSearchFilter(
    IReadOnlyList<SearchTerm> Terms,
    bool IncludePrerelease,
    bool IncludeSemVer2,
    string? PackageTypeLower,
    PackageSort? Sort = null);

/// <summary>What a package list can be ordered by. Only what the database can order by: a package's latest version
/// is a NuGet version comparison, not a column, so it is not one of them.</summary>
public enum PackageSortField
{
    /// <summary>An exact id match first, then by id - what a search answers with.</summary>
    Relevance,

    Id,

    /// <summary>How many versions the feed holds, listed or not.</summary>
    Versions,

    /// <summary>Downloads summed over every version.</summary>
    Downloads,

    /// <summary>When the newest version was published.</summary>
    LastPublished,
}

public sealed record PackageSort(PackageSortField Field, bool Descending)
{
    /// <summary>
    /// Reads a sort from a query string: the field's name in any case, and <c>desc</c> for descending. Anything else is
    /// relevance, so a hand-edited URL can at worst lose its order, never fail the page.
    /// </summary>
    public static PackageSort? Parse(string? field, string? direction) =>
        Enum.TryParse<PackageSortField>(field, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) && parsed != PackageSortField.Relevance
            ? new PackageSort(parsed, string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase))
            : null;
}
