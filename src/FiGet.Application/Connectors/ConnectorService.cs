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
    IPackageStore packages,
    PackageIngestionService ingestion,
    UpstreamMetadataCache metadataCache,
    IUpstreamRefreshQueue refreshes,
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
    public async Task<UpstreamCandidates> UpstreamCandidatesAsync(Feed feed, string idLower, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        var candidates = new List<VersionCandidate<PackageVersion>>();
        var offered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Versions an upstream still advertises, as opposed to still holds. Null while nothing has been
        // described - after a restart, say - because "we were not told" must not read as "withdrawn".
        HashSet<string>? advertised = null;
        var casedId = "";
        var authoritative = false;

        foreach (var upstream in feed.Upstreams.Where(u => u.Enabled).OrderBy(u => u.Ordinal))
        {
            if (!Allows(upstream, idLower))
            {
                continue;
            }

            // One call for both: the versions and what the upstream says about them, so an uncached
            // version is listed with its real description, authors and tags instead of blanks.
            var (catalog, answered, cached) = await CatalogAsync(upstream, idLower, cancellationToken);
            authoritative |= answered;
            var described = ByVersion(catalog.Described);

            // What the database remembers, consulted only when memory has nothing to say - which is what
            // every restart leaves behind. Two facts, and deliberately only two.
            var remembered = described.Count == 0 ? cached : null;
            if (string.IsNullOrEmpty(casedId) && !string.IsNullOrEmpty(catalog.Id))
            {
                casedId = catalog.Id;
            }

            if (described.Count > 0 || remembered?.Unlisted is not null)
            {
                advertised ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            foreach (var version in catalog.Versions)
            {
                var normalized = version.Version.ToNormalizedString();
                offered.Add(normalized);
                var row = Placeholder(idLower, version);
                if (described.TryGetValue(normalized, out var metadata))
                {
                    Describe(row, metadata);
                    if (metadata.Listed)
                    {
                        advertised!.Add(normalized);
                    }
                }
                else if (remembered is not null)
                {
                    // These two and nothing else. Description, authors and tags stay empty until the
                    // refresh lands, because keeping those in the database is the hundred megabytes that
                    // took the server down once already.
                    row.Listed = remembered.Unlisted?.Contains(normalized) != true;
                    if (remembered.Dependencies is not null
                        && remembered.Dependencies.TryGetValue(normalized, out var declared))
                    {
                        row.Dependencies = ToDependencies(declared);
                    }

                    if (row.Listed)
                    {
                        advertised?.Add(normalized);
                    }
                }

                // Listed as the upstream lists it. Claiming otherwise made every version the gallery
                // hides look current: of PnP.PowerShell's 2098 versions the gallery advertises 36, and
                // this listed all of them, so the newest nightly the gallery had withdrawn was offered as
                // the latest version of the module.
                candidates.Add(new VersionCandidate<PackageVersion>(
                    version.Version,
                    row.Listed,
                    version.IsSemVer2,
                    VersionSource.Upstream,
                    row));
            }
        }

        // Only when an upstream actually answered: an outage must never look like a mass withdrawal.
        if (authoritative)
        {
            await ReconcileWithdrawnAsync(feed, idLower, offered, advertised, cancellationToken);
        }

        return new UpstreamCandidates(candidates, casedId);
    }

    /// <summary>
    /// Keeps cached copies in step with what the upstreams still offer. A version withdrawn upstream, for
    /// instance a module pulled from the gallery, is unlisted here too, so it stops being found, stops
    /// being "latest" and stops being installed by anyone asking for the newest version. It is unlisted
    /// rather than deleted, so a deployment already pinned to that exact version can still fetch it while
    /// it is being moved off; a feed that wants it gone entirely deletes it.
    /// Only cached copies are touched. What was pushed to this feed is nobody else's to withdraw.
    /// </summary>
    /// <param name="advertised">
    /// Versions the upstream still lists, or null when it described nothing this time. A cached copy of a
    /// version the gallery has hidden should stop being offered here too - PowerShellGet 2.2.5.1 is
    /// unlisted on the gallery, was cached here by a look-through install, and then went on winning
    /// "latest" over the 2.2.5 the gallery actually advertises.
    ///
    /// Null means "no news", and nothing is changed on that basis. It cannot unlist a feed wholesale, and
    /// - since 2026-09-12 - it cannot re-list either: the description cache is empty after every restart,
    /// and treating that as "still offered" put a hidden version back in the running for "latest" each
    /// time the container came up.
    /// </param>
    private async Task ReconcileWithdrawnAsync(Feed feed, string idLower, HashSet<string> offered, HashSet<string>? advertised, CancellationToken cancellationToken)
    {
        var package = await packages.GetPackageAsync(feed.Key, idLower, includeDependencies: false, cancellationToken);
        if (package is null)
        {
            return;
        }

        foreach (var version in package.Versions.Where(v => v.Origin == PackageOrigin.Cached))
        {
            // Three answers, not two: withdrawn, advertised, or no news. A cold description cache used to
            // count as "still offered", so every restart re-listed a copy the gallery hides - and while it
            // was listed it could win "latest" again, which is the one thing this method exists to prevent.
            // Re-listing now needs positive evidence; absence from the version list alone still withdraws.
            bool? offeredNow = !offered.Contains(version.NormalizedVersion)
                ? false
                : advertised is null
                    ? null
                    : advertised.Contains(version.NormalizedVersion);

            if (offeredNow is not { } stillOffered || version.Listed == stillOffered)
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

    /// <summary>
    /// The upstream's dependencies as rows, numbered the way the indexer numbers a pushed package's: one
    /// running ordinal across every group, and a row with no id for a group that declares none. Matching
    /// that exactly is the point - a package must read the same before and after it is cached.
    /// </summary>
    private static List<PackageDependency> ToDependencies(IReadOnlyList<UpstreamDependency>? dependencies)
    {
        if (dependencies is null || dependencies.Count == 0)
        {
            return [];
        }

        var ordinal = 0;
        return [.. dependencies.Select(d => new PackageDependency
        {
            Ordinal = ordinal++,
            TargetFramework = d.TargetFramework,
            Id = d.Id,
            VersionRange = d.VersionRange,
        })];
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

    /// <summary>The upstream's metadata by normalised version, so a placeholder row can be filled in.</summary>
    private static Dictionary<string, UpstreamMetadata> ByVersion(IReadOnlyList<UpstreamMetadata> items)
    {
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
        row.Listed = metadata.Listed;
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
        row.Dependencies = ToDependencies(metadata.Dependencies);
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
    /// The upstream's catalogue for one id, and whether it is a real answer. A catalogue served after a
    /// failure is the last known one, which is good enough to answer a client but not good enough to
    /// conclude that anything missing from it was withdrawn.
    ///
    /// Versions and descriptions are fetched and cached together because on a v2 gallery they are the same
    /// paged walk: fetching them separately paid for it twice, which was most of the twenty-three seconds
    /// a two-thousand-version package took to render (docs/status.md, 2026-09-12).
    /// </summary>
    /// <summary>
    /// The upstream's catalogue for one id, and whether it is a real answer.
    ///
    /// Anything cached is served at once, however old. Only a package nothing is known about waits, which
    /// is the first view of it and never again: a stale catalogue is refreshed behind the request instead
    /// of in front of it. The difference is not small - a cold two-thousand-version package cost 13 to 15
    /// seconds, and under the old rule every reader who arrived more than five minutes after the last one
    /// paid it again (docs/status.md, 2026-09-12).
    ///
    /// What the time-to-live now means is "refresh after this", not "expire after this". Nothing is ever
    /// thrown away for being old, because the only thing worse than a five-minute-old version list is no
    /// version list while somebody waits for a walk of several megabytes.
    /// </summary>
    private async Task<(UpstreamCatalog Catalog, bool Authoritative, CachedUpstreamCatalog? Remembered)> CatalogAsync(
        FeedUpstream upstream,
        string idLower,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var cached = await index.FindAsync(upstream.Key, idLower, cancellationToken);

        if (cached is not null)
        {
            // Whatever has been said about these versions, however old. The list is in the database and
            // survives a restart; the descriptions are in memory and do not, so after one they are empty
            // and the rows are listed plainly until the refresh below fills them in. A plain listing in
            // milliseconds beats a described one in fifteen seconds.
            var described = metadataCache.Get(upstream.Key, idLower, now, TimeSpan.MaxValue) ?? [];

            // Worth asking again once the list is past its window, or whenever nothing describes it -
            // which is what a restart leaves behind. Never worth waiting for: enqueue collapses
            // duplicates, so a popular package refreshes once however many readers it has.
            if (described.Count == 0 || now - cached.FetchedUtc >= settings.UpstreamIndexTtl)
            {
                refreshes.Enqueue(upstream, idLower);
            }

            return (new UpstreamCatalog(cached.Versions, described, cached.Id), !cached.Stale, cached);
        }

        try
        {
            var catalog = await client.GetCatalogAsync(upstream, idLower, cancellationToken);
            await index.SaveAsync(upstream.Key, idLower, catalog.Id, catalog.Versions, catalog.Described, stale: false, now, cancellationToken);
            metadataCache.Set(upstream.Key, idLower, catalog.Described, now);
            return (catalog, true, null);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // A timeout lands here too, which is why the answer is "not authoritative": a version missing
            // from a list we never received must not be mistaken for a version withdrawn upstream.
            logger.LogWarning(ex, "Upstream {Upstream} did not answer for {Id}, and nothing was cached.", upstream.Name, idLower);
            return (new UpstreamCatalog([], []), false, null);
        }
    }

}

/// <summary>
/// What the upstreams hold for one id, and the id as they spell it. The spelling travels with the
/// candidates because a v3 registration URL is lower-cased by convention: without it an uncached package
/// reads as "powershellget" until somebody downloads it and the real nuspec replaces it.
/// </summary>
public sealed record UpstreamCandidates(IReadOnlyList<VersionCandidate<PackageVersion>> Versions, string Id)
{
    /// <summary>The upstream's spelling when there is one, otherwise whatever the caller already had.</summary>
    public string Spell(string fallback) => string.IsNullOrEmpty(Id) ? fallback : Id;
}
