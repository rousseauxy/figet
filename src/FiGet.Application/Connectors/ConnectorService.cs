using System.Text.RegularExpressions;
using FiGet.Application.Packages;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Domain.Versions;
using Microsoft.Extensions.Logging;
using NuGet.Versioning;

namespace FiGet.Application.Connectors;

/// <summary>
/// Proxy-feed behaviour (build plan section 5). Two jobs: report what the upstreams hold so the merged
/// version list covers them, and fetch an exact version on demand so a pinned dependency resolves.
/// The merge itself stays in <see cref="VersionListBuilder"/>, which is what keeps exactly one version
/// flagged latest no matter how many sources answered.
/// </summary>
public sealed class ConnectorService(
    IUpstreamClient client,
    IUpstreamIndexStore index,
    UpstreamMetadataCache metadataCache,
    IPackageStore packages,
    PackageIngestionService ingestion,
    ConnectorSettings settings,
    TimeProvider time,
    ILogger<ConnectorService> logger)
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Every version the feed's upstreams report for the id, as merge candidates with no local payload.
    /// A cached list is used while it is fresh; a failing upstream falls back to its last known list and
    /// is logged, because answering from a slightly old list beats failing a client's install.
    /// </summary>
    public async Task<IReadOnlyList<VersionCandidate<PackageVersion>>> UpstreamCandidatesAsync(Feed feed, string idLower, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        var candidates = new List<VersionCandidate<PackageVersion>>();
        var offered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var authoritative = false;

        foreach (var upstream in feed.Upstreams.Where(u => u.Enabled).OrderBy(u => u.Ordinal))
        {
            if (!Allows(upstream, idLower))
            {
                continue;
            }

            var (versions, answered) = await VersionsAsync(upstream, idLower, cancellationToken);
            authoritative |= answered;

            // What the upstream says about each version, so an uncached one is listed with its real
            // description, authors and tags instead of blanks.
            var described = await DescribeAsync(upstream, idLower, cancellationToken);

            foreach (var version in versions)
            {
                offered.Add(version.Version.ToNormalizedString());
                var row = Placeholder(idLower, version);
                if (described.TryGetValue(version.Version.ToNormalizedString(), out var metadata))
                {
                    Describe(row, metadata);
                }

                candidates.Add(new VersionCandidate<PackageVersion>(
                    version.Version,
                    Listed: true,
                    version.IsSemVer2,
                    VersionSource.Upstream,
                    row));
            }
        }

        // Only when an upstream actually answered: an outage must never look like a mass withdrawal.
        if (authoritative)
        {
            await ReconcileWithdrawnAsync(feed, idLower, offered, cancellationToken);
        }

        return candidates;
    }

    /// <summary>
    /// Keeps cached copies in step with what the upstreams still offer. A version withdrawn upstream, for
    /// instance a module pulled from the gallery, is unlisted here too, so it stops being found, stops
    /// being "latest" and stops being installed by anyone asking for the newest version. It is unlisted
    /// rather than deleted, so a deployment already pinned to that exact version can still fetch it while
    /// it is being moved off; a feed that wants it gone entirely deletes it.
    /// Only cached copies are touched. What was pushed to this feed is nobody else's to withdraw.
    /// </summary>
    private async Task ReconcileWithdrawnAsync(Feed feed, string idLower, HashSet<string> offered, CancellationToken cancellationToken)
    {
        var package = await packages.GetPackageAsync(feed.Key, idLower, includeDependencies: false, cancellationToken);
        if (package is null)
        {
            return;
        }

        foreach (var version in package.Versions.Where(v => v.Origin == PackageOrigin.Cached))
        {
            var stillOffered = offered.Contains(version.NormalizedVersion);
            if (version.Listed == stillOffered)
            {
                continue;
            }

            await packages.SetListedAsync(feed.Key, idLower, version.NormalizedVersionLower, stillOffered, cancellationToken);
            if (stillOffered)
            {
                logger.LogInformation("{Id} {Version} is offered upstream again; the cached copy is listed once more.", package.Id, version.NormalizedVersion);
            }
            else
            {
                logger.LogWarning("{Id} {Version} was withdrawn upstream; the cached copy is now unlisted.", package.Id, version.NormalizedVersion);
            }
        }
    }

    /// <summary>
    /// Makes sure one exact version exists locally, fetching it from the first upstream that has it and
    /// storing it as a cached package. Returns the local row, or null when no upstream has that version.
    /// </summary>
    public async Task<PackageVersion?> EnsureCachedAsync(Feed feed, string id, NuGetVersion version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(version);

        var idLower = id.ToLowerInvariant();
        var versionLower = version.ToNormalizedString().ToLowerInvariant();
        var existing = await packages.GetVersionAsync(feed.Key, idLower, versionLower, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        foreach (var upstream in feed.Upstreams.Where(u => u.Enabled).OrderBy(u => u.Ordinal))
        {
            if (!Allows(upstream, idLower))
            {
                continue;
            }

            Stream? nupkg;
            try
            {
                nupkg = await client.OpenPackageAsync(upstream, idLower, version, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Includes this connector's own timeout: a slow upstream is an upstream that did not
                // answer, never an error thrown back at the client.
                logger.LogWarning(ex, "Upstream {Upstream} could not serve {Id} {Version}.", upstream.Name, id, version.ToNormalizedString());
                continue;
            }

            if (nupkg is null)
            {
                continue;
            }

            await using (nupkg)
            {
                var result = await ingestion.PushAsync(feed, nupkg, PackageOrigin.Cached, cancellationToken);
                if (result.Outcome is PushOutcome.Created or PushOutcome.Replaced)
                {
                    logger.LogInformation("Cached {Id} {Version} from upstream {Upstream}.", id, version.ToNormalizedString(), upstream.Name);
                    return await packages.GetVersionAsync(feed.Key, idLower, versionLower, cancellationToken);
                }

                if (result.Outcome == PushOutcome.Conflict)
                {
                    // Another request cached it while this one was downloading.
                    return await packages.GetVersionAsync(feed.Key, idLower, versionLower, cancellationToken);
                }

                logger.LogWarning("Upstream {Upstream} served an unusable {Id} {Version}: {Message}", upstream.Name, id, version.ToNormalizedString(), result.Message);
            }
        }

        return null;
    }

    /// <summary>
    /// Upstream search hits as listable versions: the same placeholder rows a version listing uses, so a
    /// protocol search can show a package that nobody has cached yet. Ids already known locally are the
    /// caller's to filter out, because only the caller knows what it has already listed.
    /// </summary>
    public async Task<IReadOnlyList<(string Id, PackageVersion Version)>> SearchPlaceholdersAsync(
        Feed feed,
        string query,
        bool includePrerelease,
        int take,
        CancellationToken cancellationToken)
    {
        var hits = await SearchUpstreamsAsync(feed, query, includePrerelease, take, cancellationToken);
        return hits.Select(hit =>
        {
            var row = Placeholder(hit.Id.ToLowerInvariant(), new UpstreamVersion(hit.Version, IsSemVer2: false));
            row.Description = hit.Description;
            row.Authors = hit.Authors;
            row.Tags = hit.Tags;
            row.Downloads = hit.Downloads;

            // The id keeps the upstream's casing: a listing showing "microsoft.powershell.secretstore"
            // where the gallery says "Microsoft.PowerShell.SecretStore" reads as a different package.
            return (hit.Id, row);
        }).ToList();
    }

    /// <summary>
    /// Metadata for a version that exists only upstream. A listing must be able to show it before anything
    /// has been downloaded, so the fields nobody can know yet stay empty and the real nuspec replaces this
    /// row the moment the package is cached. Marked as cached because that is what it will become.
    /// </summary>
    /// <summary>
    /// Searches every upstream of the feed, de-duplicated by id with the first upstream winning. A package
    /// nobody has cached must still be findable, which is the whole point of putting a proxy feed in front
    /// of a gallery. An upstream that fails is logged and skipped, so the search still answers with what
    /// the other upstreams and the local feed hold.
    /// </summary>
    public async Task<IReadOnlyList<UpstreamSearchHit>> SearchUpstreamsAsync(
        Feed feed,
        string query,
        bool includePrerelease,
        int take,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        var hits = new List<UpstreamSearchHit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var upstream in feed.Upstreams.Where(u => u.Enabled).OrderBy(u => u.Ordinal))
        {
            if (hits.Count >= take)
            {
                break;
            }

            IReadOnlyList<UpstreamSearchHit> found;
            try
            {
                found = await client.SearchAsync(upstream, query, includePrerelease, 0, take, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Upstream {Upstream} did not answer a search for {Query}.", upstream.Name, query);
                continue;
            }

            foreach (var hit in found)
            {
                if (Allows(upstream, hit.Id.ToLowerInvariant()) && seen.Add(hit.Id))
                {
                    hits.Add(hit);
                }
            }
        }

        return hits;
    }

    private static PackageVersion Placeholder(string idLower, UpstreamVersion version)
    {
        var normalized = version.Version.ToNormalizedString();
        return new PackageVersion
        {
            OriginalVersion = normalized,
            NormalizedVersion = normalized,
            NormalizedVersionLower = normalized.ToLowerInvariant(),
            IsPrerelease = version.Version.IsPrerelease,
            IsSemVer2 = version.IsSemVer2,
            Listed = true,
            Origin = PackageOrigin.Cached,
            SearchTextLower = idLower,
        };
    }

    /// <summary>
    /// The upstream's metadata for one id, by normalised version. Cached for the same time as the version
    /// list, and never allowed to fail a request: a listing with plain rows beats no listing at all.
    /// </summary>
    private async Task<Dictionary<string, UpstreamMetadata>> DescribeAsync(FeedUpstream upstream, string idLower, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var items = metadataCache.Get(upstream.Key, idLower, now, settings.UpstreamIndexTtl);
        if (items is null)
        {
            try
            {
                items = await client.GetMetadataAsync(upstream, idLower, cancellationToken);
                metadataCache.Set(upstream.Key, idLower, items, now);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Upstream {Upstream} did not describe {Id}; versions are listed without details.", upstream.Name, idLower);
                items = [];
            }
        }

        var byVersion = new Dictionary<string, UpstreamMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            byVersion[item.Version.ToNormalizedString()] = item;
        }

        return byVersion;
    }

    /// <summary>Copies what the upstream published onto a placeholder row.</summary>
    private static void Describe(PackageVersion row, UpstreamMetadata metadata)
    {
        row.Description = metadata.Description;
        row.Summary = metadata.Summary;
        row.Title = metadata.Title;
        row.Authors = metadata.Authors;
        row.Tags = metadata.Tags;
        row.TagsLower = " " + metadata.Tags.ToLowerInvariant() + " ";
        row.ProjectUrl = metadata.ProjectUrl;
        row.IconUrl = metadata.IconUrl;
        row.LicenseUrl = metadata.LicenseUrl;
        row.Downloads = metadata.Downloads;
        if (metadata.Published is { } published)
        {
            row.PublishedUtc = published;
            row.LastUpdatedUtc = published;
        }
    }

    /// <summary>Deny wins over allow, and an empty allow list means every id is allowed.</summary>
    public static bool Allows(FeedUpstream upstream, string idLower)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        foreach (var pattern in FeedUpstream.Patterns(upstream.Deny))
        {
            if (Matches(pattern, idLower))
            {
                return false;
            }
        }

        var allow = FeedUpstream.Patterns(upstream.Allow);
        return allow.Length == 0 || allow.Any(pattern => Matches(pattern, idLower));
    }

    private static bool Matches(string pattern, string idLower)
    {
        try
        {
            return Regex.IsMatch(idLower, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout);
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            // A bad pattern must not take the feed down: it simply matches nothing.
            return false;
        }
    }

    /// <summary>
    /// The upstream's versions, and whether they are a real answer. A list served after a failure is the
    /// last known one, which is good enough to answer a client but not good enough to conclude that
    /// anything missing from it was withdrawn.
    /// </summary>
    private async Task<(IReadOnlyList<UpstreamVersion> Versions, bool Authoritative)> VersionsAsync(
        FeedUpstream upstream,
        string idLower,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var cached = await index.FindAsync(upstream.Key, idLower, cancellationToken);
        if (cached is not null && now - cached.FetchedUtc < settings.UpstreamIndexTtl)
        {
            return (Parse(cached), !cached.Stale);
        }

        try
        {
            var versions = await client.GetVersionsAsync(upstream, idLower, cancellationToken);
            await index.SaveAsync(upstream.Key, idLower, versions, stale: false, now, cancellationToken);
            return (versions, true);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // A timeout lands here too, which is why the answer is "not authoritative": a version missing
            // from a list we never received must not be mistaken for a version withdrawn upstream.
            logger.LogWarning(ex, "Upstream {Upstream} did not answer for {Id}; serving the last known list.", upstream.Name, idLower);
            return (cached is null ? [] : Parse(cached), false);
        }
    }

    private static List<UpstreamVersion> Parse(CachedUpstreamIndex cached)
    {
        var semVer2 = cached.SemVer2Versions.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var versions = new List<UpstreamVersion>();
        foreach (var text in cached.Versions.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (NuGetVersion.TryParse(text, out var version))
            {
                versions.Add(new UpstreamVersion(version, semVer2.Contains(text)));
            }
        }

        return versions;
    }
}
