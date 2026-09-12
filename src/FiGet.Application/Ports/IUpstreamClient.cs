using FiGet.Domain.Entities;
using NuGet.Versioning;

namespace FiGet.Application.Ports;

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
/// <param name="Listed">
/// Whether the upstream still advertises this version. Unlisted does not mean gone: the version stays
/// downloadable by exact version, because a pinned dependency asks for one and does not care whether the
/// gallery still shows it. It means undiscoverable — out of search, out of a version listing's visible
/// rows, and never the latest.
///
/// It rides with the metadata rather than with the version because that is where it is learnt, and
/// because the version list is cached in the database as bare strings while the metadata is cached beside
/// it; a catalogue is only ever answered from the cache when both halves are present, so a version that
/// has a description also has this flag. Defaults to listed, which is the safe answer when an upstream
/// reports a version it says nothing else about.
/// </param>
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
    long Downloads,
    bool Listed = true);

/// <summary>
/// One upstream's answer for one package id: every version it holds, and what it published about the
/// versions it described in the same breath.
///
/// The two travel together because on a v2 gallery they come from the same paged walk of
/// <c>FindPackagesById()</c>, so asking for them separately pays for that walk twice. A v3 source answers
/// versions from a cheap index and descriptions from a dearer one. Which of those happened is the
/// adapter's business; the connector only needs both answers.
/// </summary>
/// <param name="Versions">Every version the upstream holds, including ones it does not advertise.</param>
/// <param name="Described">
/// What the upstream published about them. May cover fewer versions than <paramref name="Versions"/>, or
/// none at all: a version with nothing said about it is listed with blanks rather than not listed.
/// </param>
/// <param name="Id">
/// The id as the upstream spells it. Ids are compared case-insensitively, so this changes nothing about
/// what resolves - but a v3 registration URL is lower-cased by convention, and echoing that back made an
/// uncached package read as "powershellget" until somebody downloaded it and the real nuspec replaced it.
/// Empty when the upstream described nothing, in which case the caller keeps whatever it already had.
/// </param>
public sealed record UpstreamCatalog(
    IReadOnlyList<UpstreamVersion> Versions,
    IReadOnlyList<UpstreamMetadata> Described,
    string Id = "");

/// <summary>
/// Talks to one upstream feed. Implemented over NuGet's own client library, so both v2 and v3 upstreams
/// work without FiGet re-implementing either protocol as a client.
/// </summary>
public interface IUpstreamClient
{
    /// <summary>
    /// Everything the upstream knows about one id: its versions, and what it publishes about them. An
    /// empty catalogue means the upstream answered and knows no such package. Throws when it cannot be
    /// reached, so the caller can serve the last known catalogue instead.
    /// </summary>
    Task<UpstreamCatalog> GetCatalogAsync(FeedUpstream upstream, string idLower, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the nupkg for one exact version, or null when the upstream does not have it. Exact versions
    /// matter: a meta-package pins its dependencies, so "only the latest" would break installs.
    /// </summary>
    Task<Stream?> OpenPackageAsync(FeedUpstream upstream, string idLower, NuGetVersion version, CancellationToken cancellationToken);

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
/// <summary>
/// A catalogue as it was last stored, with the moment it was fetched so the caller can decide whether to
/// use it, refresh it behind the request, or both.
/// </summary>
public sealed record CachedUpstreamCatalog(
    IReadOnlyList<UpstreamVersion> Versions,
    DateTime FetchedUtc,
    bool Stale,
    string Id = "");

public interface IUpstreamIndexStore
{
    /// <summary>The cached catalogue for one upstream and id, whatever its age; null when nothing is cached.</summary>
    Task<CachedUpstreamCatalog?> FindAsync(int feedUpstreamKey, string idLower, CancellationToken cancellationToken);

    /// <summary>
    /// Writes or replaces the cached version list. Only the versions: the descriptions of those versions
    /// are two orders of magnitude larger and live in memory, for the reason recorded on
    /// <c>UpstreamMetadataCache</c>.
    /// </summary>
    Task SaveAsync(int feedUpstreamKey, string idLower, string casedId, IReadOnlyList<UpstreamVersion> versions, bool stale, DateTime fetchedUtc, CancellationToken cancellationToken);

    /// <summary>Drops every cached list of one upstream, used when its configuration changes.</summary>
    Task ClearAsync(int feedUpstreamKey, CancellationToken cancellationToken);
}
