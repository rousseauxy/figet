using FiGet.Domain.Entities;
using NuGet.Versioning;

namespace FiGet.Application.Ports;

/// <summary>One version an upstream reports for a package id.</summary>
public readonly record struct UpstreamVersion(NuGetVersion Version, bool IsSemVer2);

/// <summary>
/// One package an upstream's search returned. Enough to list it before anything has been downloaded.
/// </summary>
public sealed record UpstreamSearchHit(string Id, NuGetVersion Version, string Description, string Authors, string Tags, long Downloads)
{
    /// <summary>The name of the feed's upstream that returned it, filled in by the connector.</summary>
    public string Upstream { get; init; } = "";
}

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
    bool Listed = true,
    IReadOnlyList<UpstreamDependency>? Dependencies = null);

/// <summary>
/// One dependency an upstream declares for a version, in the shape the indexer produces for a package
/// pushed here: the group's short framework name (empty for "any"), the dependency id (null for a group
/// that declares none), and a normalised range (empty for "any version").
///
/// Carried because a version nobody has cached was otherwise described with everything except this, so a
/// client read "no dependencies" and installed the module alone. The second attempt worked, because by
/// then the first had cached the package and its dependencies came from the nuspec.
/// </summary>
public sealed record UpstreamDependency(string TargetFramework, string? Id, string VersionRange);

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
    /// What one version's release notes say, or null when this upstream does not report them. One small request for
    /// one version, not a walk of the catalogue: a change report wants these for the handful of versions that moved.
    ///
    /// Only a v2 gallery answers. A v3 registration leaf carries no release notes, and FiGet does not read the catalog
    /// resource that would, so nuget.org rows simply have none.
    /// </summary>
    Task<string?> GetReleaseNotesAsync(FeedUpstream upstream, string idLower, NuGetVersion version, CancellationToken cancellationToken);

    /// <summary>
    /// Only the versions of one id, when the upstream can answer that more cheaply than the full catalogue - a v3
    /// source's flat container, 0.17 s where describing the same versions took 3.2 s. Null when it cannot: on a v2
    /// gallery the versions come from the same paged walk as the descriptions, and asking for them alone costs more.
    /// Throws when the upstream cannot be reached.
    /// </summary>
    Task<IReadOnlyList<UpstreamVersion>?> GetVersionsAsync(FeedUpstream upstream, string idLower, CancellationToken cancellationToken);

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
    string Id = "",
    IReadOnlySet<string>? Unlisted = null,
    IReadOnlyDictionary<string, IReadOnlyList<UpstreamDependency>>? Dependencies = null);

/// <summary>
/// The text an upstream wrote about each version of a package, kept so it survives a restart. Separate from
/// <see cref="IUpstreamIndexStore"/> because it is display, never correctness, and because its size is a different
/// order: it is written only for versions not stored yet, and read only when memory has nothing.
/// </summary>
/// <summary>One version an upstream published, as a report of recent activity reads it.</summary>
public readonly record struct UpstreamPublished(string IdLower, string NormalizedVersion, DateTime PublishedUtc, string Authors, string ReleaseNotes);

public interface IUpstreamDescriptionStore
{
    /// <summary>
    /// The stored descriptions of one package, or an empty list. <paramref name="unlisted"/> and
    /// <paramref name="dependencies"/> come from the cached catalogue, which is where those facts are kept; null means
    /// the catalogue has no news, and every version then reads as listed with no dependencies known.
    /// </summary>
    Task<IReadOnlyList<UpstreamMetadata>> LoadAsync(
        int feedUpstreamKey,
        string idLower,
        IReadOnlySet<string>? unlisted,
        IReadOnlyDictionary<string, IReadOnlyList<UpstreamDependency>>? dependencies,
        CancellationToken cancellationToken);

    /// <summary>
    /// When this upstream published each version of these ids, by lower-cased id and then by normalised version.
    /// Three columns of a stored description and no more: the description text and the tag sets are the hundred
    /// megabytes that must stay out of memory, and a listing of a version nobody has cached needs only the date -
    /// without it that version is served as published in the year 1, which a client sorting on the date reads as the
    /// oldest thing in the feed and a report filtering on it drops.
    /// </summary>
    /// <summary>
    /// What this upstream published in <c>[fromUtc, toUtc)</c>, whatever the id, newest first, at most
    /// <paramref name="take"/>. The window comes first on purpose: a gallery's catalogue holds every version of every
    /// id anyone here ever asked about, and a report wants the few that are new.
    /// </summary>
    Task<IReadOnlyList<UpstreamPublished>> PublishedBetweenAsync(
        int feedUpstreamKey,
        DateTime fromUtc,
        DateTime toUtc,
        int take,
        CancellationToken cancellationToken);

    /// <summary>
    /// Keeps one version's release notes, fetched after the fact because a version list does not carry them. Stored
    /// only for versions a report mentioned, so this stays a handful of rows rather than every version of every id.
    /// </summary>
    Task SetReleaseNotesAsync(int feedUpstreamKey, string idLower, string normalizedVersion, string releaseNotes, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, DateTime>>> PublishedDatesAsync(
        int feedUpstreamKey,
        IReadOnlyCollection<string> idsLower,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stores what was not stored yet, and forgets versions the upstream no longer describes. An empty list changes
    /// nothing: an upstream that described nothing has not said the descriptions are gone.
    /// </summary>
    Task SaveAsync(int feedUpstreamKey, string idLower, IReadOnlyList<UpstreamMetadata> described, CancellationToken cancellationToken);
}

public interface IUpstreamIndexStore
{
    /// <summary>
    /// The cached catalogue for one upstream and id, whatever its age; null when nothing is cached. It
    /// carries the version list and the two facts that must outlive a restart - which versions the
    /// upstream hides, and what each depends on - but never the descriptions, which stay in memory.
    /// </summary>
    Task<CachedUpstreamCatalog?> FindAsync(int feedUpstreamKey, string idLower, CancellationToken cancellationToken);

    /// <summary>The cached catalogues of several ids at once, keyed by lower-cased id; ids with nothing cached are absent.</summary>
    Task<IReadOnlyDictionary<string, CachedUpstreamCatalog>> FindManyAsync(int feedUpstreamKey, IReadOnlyCollection<string> idsLower, CancellationToken cancellationToken);

    /// <summary>
    /// Writes or replaces the cached version list, and the two facts about those versions that must
    /// survive a restart: which of them the upstream does not advertise, and what each one depends on.
    ///
    /// Still not the descriptions. Those are two orders of magnitude larger - a hundred megabytes against
    /// tens of kilobytes here - and the reason is recorded on <c>UpstreamMetadataCache</c>. What is stored
    /// is what changes an answer: a hidden version must not look listed after a restart, and a package
    /// must not report that it depends on nothing.
    ///
    /// <paramref name="described"/> empty leaves both of those columns as they are, rather than erasing
    /// them: an upstream that answered without describing anything has told us nothing new, not that the
    /// package suddenly has no dependencies.
    /// </summary>
    Task SaveAsync(
        int feedUpstreamKey,
        string idLower,
        string casedId,
        IReadOnlyList<UpstreamVersion> versions,
        IReadOnlyList<UpstreamMetadata> described,
        bool stale,
        DateTime fetchedUtc,
        CancellationToken cancellationToken);

    /// <summary>Drops every cached list of one upstream, used when its configuration changes.</summary>
    Task ClearAsync(int feedUpstreamKey, CancellationToken cancellationToken);
}
