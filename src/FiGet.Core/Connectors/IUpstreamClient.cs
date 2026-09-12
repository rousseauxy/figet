using FiGet.Core.Entities;
using NuGet.Versioning;

namespace FiGet.Core.Connectors;

/// <summary>One version an upstream reports for a package id.</summary>
public readonly record struct UpstreamVersion(NuGetVersion Version, bool IsSemVer2);

/// <summary>
/// One package an upstream's search returned. Enough to list it before anything has been downloaded.
/// </summary>
public sealed record UpstreamSearchHit(string Id, NuGetVersion Version, string Description, string Authors, string Tags, long Downloads);

/// <summary>
/// What an upstream publishes about one version, without downloading the package. The tags matter more
/// than they look: a PowerShell client reads PSEdition_Desktop and PSEdition_Core from them to decide
/// whether a version can run at all, so a feed that drops them makes that choice impossible.
/// </summary>
public sealed record UpstreamMetadata(
    NuGetVersion Version,
    string Description,
    string Summary,
    string Title,
    string Authors,
    string Tags,
    string ProjectUrl,
    string IconUrl,
    string LicenseUrl,
    DateTime? Published,
    long Downloads);

/// <summary>
/// Talks to one upstream feed. Implemented over NuGet's own client library, so both v2 and v3 upstreams
/// work without FiGet re-implementing either protocol as a client.
/// </summary>
public interface IUpstreamClient
{
    /// <summary>
    /// Every version the upstream knows for the id, or an empty list when it knows none.
    /// Throws when the upstream cannot be reached, so the caller can serve the last known list instead.
    /// </summary>
    Task<IReadOnlyList<UpstreamVersion>> GetVersionsAsync(FeedUpstream upstream, string idLower, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the nupkg for one exact version, or null when the upstream does not have it. Exact versions
    /// matter: a meta-package pins its dependencies, so "only the latest" would break installs.
    /// </summary>
    Task<Stream?> OpenPackageAsync(FeedUpstream upstream, string idLower, NuGetVersion version, CancellationToken cancellationToken);

    /// <summary>
    /// What the upstream publishes about every version of one id. Used to describe versions nobody has
    /// downloaded yet, so a listing shows the real description, authors and tags rather than blanks.
    /// </summary>
    Task<IReadOnlyList<UpstreamMetadata>> GetMetadataAsync(FeedUpstream upstream, string idLower, CancellationToken cancellationToken);

    /// <summary>
    /// Searches the upstream, so a package nobody has cached yet can still be found. Throws when the
    /// upstream cannot be reached, which the caller turns into "local results only" rather than an error.
    /// </summary>
    Task<IReadOnlyList<UpstreamSearchHit>> SearchAsync(FeedUpstream upstream, string query, bool includePrerelease, int skip, int take, CancellationToken cancellationToken);
}

/// <summary>
/// Reads and writes the cached upstream version lists. Kept behind an interface so the connector can be
/// tested without a database and without the network.
/// </summary>
public interface IUpstreamIndexStore
{
    /// <summary>The cached list for one upstream and id, whatever its age; null when nothing is cached.</summary>
    Task<CachedUpstreamIndex?> FindAsync(int feedUpstreamKey, string idLower, CancellationToken cancellationToken);

    /// <summary>Writes or replaces the cached list.</summary>
    Task SaveAsync(int feedUpstreamKey, string idLower, IReadOnlyList<UpstreamVersion> versions, bool stale, DateTime fetchedUtc, CancellationToken cancellationToken);

    /// <summary>Drops every cached list of one upstream, used when its configuration changes.</summary>
    Task ClearAsync(int feedUpstreamKey, CancellationToken cancellationToken);
}
