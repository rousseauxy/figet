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
    IUpstreamDescriptionStore descriptions,
    IPackageStore packages,
    IPackageStorage storage,
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
    /// <param name="versionsOnly">
    /// For a caller that serves version numbers and nothing else - the v3 flat container. It never reads whether a
    /// version is listed or what it depends on, so the upstream need not describe anything for it: a v3 upstream is
    /// asked for versions alone, and stored descriptions are not loaded. Every other caller reads those facts and must
    /// leave this false (docs/backlog.md has why it is not the default).
    /// </param>
    public async Task<UpstreamCandidates> UpstreamCandidatesAsync(Feed feed, string idLower, CancellationToken cancellationToken, bool versionsOnly = false)
    {
        ArgumentNullException.ThrowIfNull(feed);
        if (await ServedLocallyAsync(feed, idLower, cancellationToken))
        {
            return new UpstreamCandidates([], "");
        }

        var candidates = new List<VersionCandidate<PackageVersion>>();
        var offered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Versions an upstream still advertises, as opposed to still holds. Null while nothing has been
        // described - after a restart, say - because "we were not told" must not read as "withdrawn".
        HashSet<string>? advertised = null;

        // When each version was published upstream, from the first upstream that says. A cached copy is stored
        // with the date it was fetched unless something corrects it, and the version page then showed the day
        // somebody installed it rather than the day its author published it.
        var published = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var casedId = "";
        var authoritative = false;

        // Who serves the id is decided once, by the same rule the cache fill uses (OwnerAsync), so a client can never list
        // one upstream's package and download another's. No owner and not undecided: every upstream was asked and none
        // holds the id, and walking them all still tells the reconciliation below that the answer was authoritative.
        var (ownership, read) = await DecideOwnerAsync(feed, idLower, versionsOnly, cancellationToken);
        IEnumerable<FeedUpstream> asked = ownership.Owner is { } owner
            ? [owner]
            : ownership.Undecided
                ? []
                : feed.Upstreams.Where(u => u.Enabled && Allows(u, idLower)).OrderBy(u => u.Ordinal);

        foreach (var upstream in asked)
        {

            // One read for both: the versions and what the upstream says about them, so an uncached version is listed with
            // its real description, authors and tags instead of blanks. The read the ownership decision made, not a second
            // one: a second read of a catalogue just stored without descriptions would load older stored descriptions that
            // count every version as listed, and re-list a version the upstream hides.
            var (catalog, answered, cached) = read.TryGetValue(upstream.Key, out var earlier)
                ? earlier
                : await CatalogAsync(upstream, idLower, cancellationToken, versionsOnly);
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
                    if (RealDate(metadata.Published) is { } date)
                    {
                        published.TryAdd(normalized, date);
                    }

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
            await ReconcileWithdrawnAsync(feed, idLower, offered, advertised, published, cancellationToken);
        }

        return new UpstreamCandidates(candidates, casedId, ownership.Owner?.Name ?? "");
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
    /// <param name="published">
    /// Publish dates the upstreams reported. A cached copy that was stored with its fetch date - every copy
    /// cached before the fetch learnt to carry the upstream's date - takes the upstream's instead.
    /// </param>
    private async Task ReconcileWithdrawnAsync(
        Feed feed,
        string idLower,
        HashSet<string> offered,
        HashSet<string>? advertised,
        IReadOnlyDictionary<string, DateTime> published,
        CancellationToken cancellationToken)
    {
        var package = await packages.GetPackageAsync(feed.Key, idLower, includeDependencies: false, cancellationToken);
        if (package is null)
        {
            return;
        }

        foreach (var version in package.Versions.Where(v => v.Origin == PackageOrigin.Cached))
        {
            // Within a second: a database may round what it stores, and rewriting the row on every read over
            // a sub-second difference would turn each listing into a write.
            if (published.TryGetValue(version.NormalizedVersion, out var upstreamDate)
                && Math.Abs((version.PublishedUtc - upstreamDate).TotalSeconds) >= 1)
            {
                await packages.SetPublishedAsync(feed.Key, idLower, version.NormalizedVersionLower, upstreamDate, cancellationToken);
            }

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
    /// <summary>
    /// The upstream versions of an id as far as this server already knows them, for a listing: from the stored catalogue
    /// only, never a request, so a page of packages costs a database read each rather than a round trip to a gallery. The
    /// first upstream in priority order that holds the id owns it, as everywhere else, and an id pushed to the feed has
    /// none. A stale catalogue is still used and queued for refresh, so the next listing is current.
    /// </summary>
    public async Task<UpstreamCandidates> StoredUpstreamCandidatesAsync(Feed feed, Package? local, string idLower, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        var found = await StoredUpstreamCandidatesAsync(feed, [(idLower, local)], cancellationToken);
        return found.TryGetValue(idLower, out var candidates) ? candidates : new UpstreamCandidates([], "");
    }

    /// <summary>
    /// The same for a page of packages - a search, a listing - with one stored-catalogue query per upstream rather than one
    /// per package, so a v2 search over thousands of packages stays a handful of reads. Ids with nothing upstream are absent.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, UpstreamCandidates>> StoredUpstreamCandidatesAsync(Feed feed, IReadOnlyList<Package> locals, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(locals);
        return await StoredUpstreamCandidatesAsync(feed, [.. locals.Select(p => (p.IdLower, (Package?)p))], cancellationToken);
    }

    private async Task<IReadOnlyDictionary<string, UpstreamCandidates>> StoredUpstreamCandidatesAsync(Feed feed, IReadOnlyList<(string IdLower, Package? Local)> ids, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, UpstreamCandidates>(StringComparer.Ordinal);
        if (feed.Upstreams.Count == 0)
        {
            return result;
        }

        // An id pushed to the feed has no upstream, unless the feed merges pushed ids with its upstreams.
        var open = ids
            .Where(i => feed.MergePushedIdsWithUpstreams || i.Local?.Versions.Any(v => v.Origin == PackageOrigin.Pushed) != true)
            .GroupBy(i => i.IdLower, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Local, StringComparer.Ordinal);

        // The same ownership rule as a live listing, from stored catalogues: the first upstream in priority order whose
        // catalogue lists something owns the id; a catalogue holding only unlisted versions gives way to a lower one that
        // lists something, and is used only when none does.
        var holdsOnlyUnlisted = new Dictionary<string, UpstreamCandidates>(StringComparer.Ordinal);
        foreach (var upstream in feed.Upstreams.Where(u => u.Enabled).OrderBy(u => u.Ordinal))
        {
            var asked = open.Keys.Where(id => !result.ContainsKey(id) && Allows(upstream, id)).ToList();
            if (asked.Count == 0)
            {
                continue;
            }

            foreach (var (idLower, cached) in await index.FindManyAsync(upstream.Key, asked, cancellationToken))
            {
                if (cached.Versions.Count == 0)
                {
                    continue;
                }

                if (cached.Stale)
                {
                    refreshes.Enqueue(upstream, idLower);
                }

                var newestLocal = open[idLower]?.Versions.OrderByDescending(v => NuGetVersion.Parse(v.NormalizedVersion), VersionComparer.Default).FirstOrDefault();
                var candidates = new UpstreamCandidates(
                    cached.Versions
                        .Select(v => new VersionCandidate<PackageVersion>(
                            v.Version,
                            cached.Unlisted?.Contains(v.Version.ToNormalizedString()) != true,
                            v.IsSemVer2,
                            VersionSource.Upstream,
                            DescribedLike(Placeholder(idLower, v), newestLocal)))
                        .ToList(),
                    cached.Id,
                    upstream.Name);
                if (candidates.Versions.Any(c => c.Listed))
                {
                    result[idLower] = candidates;
                }
                else
                {
                    holdsOnlyUnlisted.TryAdd(idLower, candidates);
                }
            }
        }

        foreach (var (idLower, candidates) in holdsOnlyUnlisted)
        {
            result.TryAdd(idLower, candidates);
        }

        return result;
    }

    /// <summary>
    /// A stored catalogue has version numbers and nothing about them: the descriptions live in memory. A listing that shows
    /// or filters on an upstream version - the latest one, in a search - takes what it says from the newest copy cached
    /// here, so a search by tag or by words from the description still finds the package, flagged at the version the
    /// upstream now has. The real text replaces it when that version is cached.
    /// </summary>
    private static PackageVersion DescribedLike(PackageVersion placeholder, PackageVersion? local)
    {
        if (local is null)
        {
            return placeholder;
        }

        placeholder.Description = local.Description;
        placeholder.Summary = local.Summary;
        placeholder.Title = local.Title;
        placeholder.Authors = local.Authors;
        placeholder.Tags = local.Tags;
        placeholder.TagsLower = local.TagsLower;
        placeholder.SearchTextLower = local.SearchTextLower;
        placeholder.ProjectUrl = local.ProjectUrl;
        placeholder.IconUrl = local.IconUrl;
        placeholder.LicenseUrl = local.LicenseUrl;
        placeholder.PackageTypes = local.PackageTypes;
        placeholder.PackageTypesLower = local.PackageTypesLower;
        return placeholder;
    }

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

        // A version of a pushed id that is not here does not exist, whatever an upstream holds under that name.
        if (await ServedLocallyAsync(feed, idLower, cancellationToken))
        {
            return null;
        }

        var ownership = await OwnerAsync(feed, idLower, cancellationToken);
        if (ownership.Undecided)
        {
            logger.LogWarning("Not fetching {Id} {Version}: a higher-priority upstream could not be asked whether it holds the id.", id, version.ToNormalizedString());
            return null;
        }

        // Only the owner, when there is one. No owner means no upstream lists the id - a listing may simply be out of
        // date - so every upstream that allows it is tried in priority order, as before.
        IEnumerable<FeedUpstream> sources = ownership.Owner is { } owner
            ? [owner]
            : feed.Upstreams.Where(u => u.Enabled).OrderBy(u => u.Ordinal);

        foreach (var upstream in sources)
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
                var result = await ingestion.PushAsync(
                    feed,
                    nupkg,
                    PackageOrigin.Cached,
                    cancellationToken,
                    KnownPublished(upstream, idLower, version),
                    listed: await KnownListedAsync(upstream, idLower, version, cancellationToken));
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
    /// A version row whose package file is gone: a process stopped between writing the row and the file, or someone removed
    /// the file. A copy cached from an upstream is dropped and fetched again, so the download that noticed still succeeds;
    /// a version pushed here cannot be fetched from anywhere, so it is logged with the path to restore, and the caller
    /// answers 404 as before. Returns the row to serve, or null.
    /// </summary>
    public async Task<PackageVersion?> RepairMissingFileAsync(Feed feed, string id, PackageVersion row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(row);
        var idLower = id.ToLowerInvariant();
        var key = new PackageStorageKey(feed.Key, idLower, row.NormalizedVersionLower);
        if (await storage.OpenPackageAsync(key, cancellationToken) is { } present)
        {
            // Another request repaired it meanwhile.
            await present.DisposeAsync();
            return row;
        }

        if (row.Origin != PackageOrigin.Cached || feed.Upstreams.Count == 0)
        {
            logger.LogWarning(
                "{Id} {Version} in feed {Feed} is listed but its package file is missing from storage (feed key {FeedKey}, {IdLower}/{VersionLower}); restore the file or delete the version.",
                id,
                row.NormalizedVersion,
                feed.Name,
                feed.Key,
                idLower,
                row.NormalizedVersionLower);
            return null;
        }

        logger.LogWarning("The cached copy of {Id} {Version} in feed {Feed} had no package file; caching it again from its upstream.", id, row.NormalizedVersion, feed.Name);
        await packages.DeleteVersionAsync(feed.Key, idLower, row.NormalizedVersionLower, cancellationToken);
        return await EnsureCachedAsync(feed, id, NuGetVersion.Parse(row.NormalizedVersion), cancellationToken);
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
    /// Searches every upstream of the feed, de-duplicated by id with the first upstream winning, for the first
    /// <paramref name="take"/> hits. A package nobody has cached must still be findable, which is the whole point of
    /// putting a proxy feed in front of a gallery. An upstream that fails is logged and skipped, so the search still
    /// answers with what the other upstreams and the local feed hold.
    ///
    /// Asked in chunks, because a gallery caps what one request returns: asking the PowerShell Gallery for 150 at once
    /// is not a promise of 150. An upstream stops being asked when it returns less than a chunk, or a chunk that adds
    /// nothing new - one that ignores the offset would otherwise be asked for ever.
    /// </summary>
    /// <summary>Hits asked of an upstream per request: what galleries answer in one page without capping it.</summary>
    public const int SearchChunk = 100;

    /// <summary>
    /// The most upstream hits one search collects, and the most pages it asks one upstream for. Any client may search an
    /// anonymous proxy feed, and a v3 search may ask for a thousand results; found by the 2026-09-14 review, that was ten
    /// requests to every upstream per search, and an allow list that filtered out every hit kept paging to the end.
    /// </summary>
    public const int MaxUpstreamSearchHits = 500;

    public const int MaxSearchChunksPerUpstream = 5;

    public async Task<IReadOnlyList<UpstreamSearchHit>> SearchUpstreamsAsync(
        Feed feed,
        string query,
        bool includePrerelease,
        int take,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        take = Math.Min(take, MaxUpstreamSearchHits);
        var hits = new List<UpstreamSearchHit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var upstream in feed.Upstreams.Where(u => u.Enabled).OrderBy(u => u.Ordinal))
        {
            var offset = 0;
            for (var chunk = 0; hits.Count < take && chunk < MaxSearchChunksPerUpstream; chunk++)
            {
                var size = Math.Min(SearchChunk, take - hits.Count);
                IReadOnlyList<UpstreamSearchHit> found;
                try
                {
                    found = await client.SearchAsync(upstream, query, includePrerelease, offset, size, cancellationToken);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Upstream {Upstream} did not answer a search for {Query}.", upstream.Name, query);
                    break;
                }

                var before = hits.Count;
                foreach (var hit in found)
                {
                    if (hits.Count < take && Allows(upstream, hit.Id.ToLowerInvariant()) && seen.Add(hit.Id))
                    {
                        hits.Add(hit with { Upstream = upstream.Name });
                    }
                }

                if (found.Count < size || (hits.Count == before && found.Count > 0 && found.All(h => seen.Contains(h.Id))))
                {
                    break;
                }

                offset += found.Count;
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

        // An upstream that declares a dependency with an empty id would otherwise put a registration URL ending in "//" into
        // the answer. Such an entry is dropped; a framework group left with no ids keeps one empty row, as for a pushed package.
        var kept = new List<UpstreamDependency>();
        foreach (var group in dependencies.GroupBy(d => d.TargetFramework))
        {
            var named = group.Where(d => !string.IsNullOrWhiteSpace(d.Id)).ToList();
            kept.AddRange(named.Count > 0 ? named : [new UpstreamDependency(group.Key, null, "")]);
        }

        var ordinal = 0;
        return [.. kept.Select(d => new PackageDependency
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

    /// <summary>
    /// Whether this feed serves the id only from what it holds: a version of it was pushed here, and the feed has
    /// not opted into merging pushed ids with its upstreams.
    ///
    /// Copies of the id that were cached from an upstream before anything was pushed are a different package that
    /// happens to share the name. They are unlisted here rather than deleted, so a deployment pinned to one of them
    /// can still fetch it by exact version while it moves to the pushed package. Done on read, not on push, so that
    /// feeds holding both before this rule existed are put right the first time the id is asked for.
    /// </summary>
    private async Task<bool> ServedLocallyAsync(Feed feed, string idLower, CancellationToken cancellationToken)
    {
        if (feed.MergePushedIdsWithUpstreams)
        {
            return false;
        }

        var package = await packages.GetPackageAsync(feed.Key, idLower, includeDependencies: false, cancellationToken);
        if (package is null || !package.Versions.Any(v => v.Origin == PackageOrigin.Pushed))
        {
            return false;
        }

        foreach (var shadowed in package.Versions.Where(v => v.Origin == PackageOrigin.Cached && v.Listed))
        {
            await packages.SetListedAsync(feed.Key, idLower, shadowed.NormalizedVersionLower, listed: false, cancellationToken);
            logger.LogWarning(
                "{Id} {Version} was cached from an upstream, but {Id} is pushed to feed {Feed}; the cached copy is now unlisted.",
                package.Id,
                shadowed.NormalizedVersion,
                package.Id,
                feed.Name);
        }

        return true;
    }

    /// <summary>How long a push waits to learn whether an upstream holds the id it pushed. A warning is not worth a slow CI job.</summary>
    private static readonly TimeSpan PushWarningBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A warning for the client that just pushed an id an upstream of this feed also holds, or null. Sent back as
    /// <c>X-NuGet-Warning</c>, which nuget, dotnet and PowerShell print, so the person publishing learns about the
    /// name clash when it happens rather than when an install picks the wrong package.
    ///
    /// Best effort within a few seconds: an upstream that is slow or down yields no warning, never a failed push.
    /// </summary>
    public async Task<string?> PushWarningAsync(Feed feed, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        if (!feed.Upstreams.Any(u => u.Enabled))
        {
            return null;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(PushWarningBudget);
        UpstreamOwnership ownership;
        try
        {
            ownership = await OwnerAsync(feed, id.ToLowerInvariant(), budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        if (ownership.Owner is not { } owner)
        {
            return null;
        }

        return feed.MergePushedIdsWithUpstreams
            ? $"{id} also exists on upstream '{owner.Name}'. Feed '{feed.Name}' merges both into one version list, so the higher version of either package is served as the latest."
            : $"{id} also exists on upstream '{owner.Name}'. Feed '{feed.Name}' now serves {id} only from what is pushed to it; the upstream package of that name is no longer offered.";
    }

    /// <summary>
    /// The upstream that owns an id for this feed: the first enabled upstream, in priority order, that allows the id
    /// and lists at least one version of it. Undecided when an upstream ahead of any owner could not be asked and
    /// has nothing remembered, because then nobody can say it does not hold the id.
    /// </summary>
    public async Task<UpstreamOwnership> OwnerAsync(Feed feed, string idLower, CancellationToken cancellationToken, bool versionsOnly = false) =>
        (await DecideOwnerAsync(feed, idLower, versionsOnly, cancellationToken)).Ownership;

    /// <summary>The ownership decision, with every catalogue read to make it, so a caller that lists can reuse them.</summary>
    private async Task<(UpstreamOwnership Ownership, Dictionary<int, (UpstreamCatalog Catalog, bool Answered, CachedUpstreamCatalog? Cached)> Read)> DecideOwnerAsync(
        Feed feed,
        string idLower,
        bool versionsOnly,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        var read = new Dictionary<int, (UpstreamCatalog Catalog, bool Answered, CachedUpstreamCatalog? Cached)>();

        // The first upstream in priority order that offers the id owns it: holds a version it has not unlisted. One that
        // holds only unlisted versions does not - a gallery keeps withdrawn packages under their name, invisible to its
        // own search, and letting that shadow a lower upstream's real package made the id unfindable (DscTestModule: two
        // unlisted versions on the PowerShell Gallery, the package itself on another gallery). Such an upstream owns the
        // id only when no upstream offers it at all, so a package hidden everywhere still behaves as before.
        FeedUpstream? holdsOnlyUnlisted = null;
        foreach (var upstream in feed.Upstreams.Where(u => u.Enabled).OrderBy(u => u.Ordinal))
        {
            if (!Allows(upstream, idLower))
            {
                continue;
            }

            var (catalog, answered, cached) = await CatalogAsync(upstream, idLower, cancellationToken, versionsOnly);
            read[upstream.Key] = (catalog, answered, cached);
            if (OffersListedVersion(catalog, cached))
            {
                return (new UpstreamOwnership(upstream, Undecided: false), read);
            }

            if (catalog.Versions.Count > 0)
            {
                holdsOnlyUnlisted ??= upstream;
            }
            else if (!answered && cached is null)
            {
                // Whether it holds the id is unknown, and serving a lower upstream's package in the meantime would cache
                // that package here for good.
                return (new UpstreamOwnership(null, Undecided: true), read);
            }
        }

        return (new UpstreamOwnership(holdsOnlyUnlisted, Undecided: false), read);
    }

    /// <summary>
    /// Whether an upstream's answer includes a version it still lists. What it described decides; failing that, what is
    /// remembered about its unlisted versions; with neither, a version counts as listed, because "not told" must not read
    /// as "withdrawn".
    /// </summary>
    private static bool OffersListedVersion(UpstreamCatalog catalog, CachedUpstreamCatalog? remembered)
    {
        var described = ByVersion(catalog.Described);
        foreach (var version in catalog.Versions)
        {
            var normalized = version.Version.ToNormalizedString();
            var listed = described.TryGetValue(normalized, out var metadata)
                ? metadata.Listed
                : remembered?.Unlisted?.Contains(normalized) != true;
            if (listed)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// When the upstream published this version, if the connector has heard. Only what is already in memory: a
    /// client lists versions before it downloads one, so the description is nearly always there, and a cache fill
    /// that misses it is corrected the next time the upstream describes the package.
    /// </summary>
    /// <summary>
    /// Whether the upstream advertises the version: from what it last described, else from the stored catalogue's hidden
    /// versions, else listed - "not told" never reads as hidden.
    /// </summary>
    private async Task<bool> KnownListedAsync(FeedUpstream upstream, string idLower, NuGetVersion version, CancellationToken cancellationToken)
    {
        var described = metadataCache.Get(upstream.Key, idLower, time.GetUtcNow().UtcDateTime, TimeSpan.MaxValue);
        if (described?.FirstOrDefault(m => m.Version == version) is { } metadata)
        {
            return metadata.Listed;
        }

        var cached = await index.FindAsync(upstream.Key, idLower, cancellationToken);
        return cached?.Unlisted?.Contains(version.ToNormalizedString()) != true;
    }

    private DateTime? KnownPublished(FeedUpstream upstream, string idLower, NuGetVersion version)
    {
        var described = metadataCache.Get(upstream.Key, idLower, time.GetUtcNow().UtcDateTime, TimeSpan.MaxValue);
        return described?.FirstOrDefault(m => m.Version == version) is { } metadata ? RealDate(metadata.Published) : null;
    }

    /// <summary>
    /// A publish date worth storing. A gallery reports 1900-01-01 for a version it has unlisted, which is a
    /// marker, not a date; the protocols write that marker themselves for unlisted rows.
    /// </summary>
    private static DateTime? RealDate(DateTime? published) =>
        published is { Year: > 1900 } date ? DateTime.SpecifyKind(date, DateTimeKind.Utc) : null;

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
        CancellationToken cancellationToken,
        bool versionsOnly = false)
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

            // Memory is empty after every restart and after eviction; what was stored is the description a listing
            // shows until the refresh below lands, instead of blank rows. Put back in memory under the catalogue's own
            // age, so it reads as exactly as old as it is.
            //
            // The facts go with it, and they may only be read as "nothing hidden, nothing declared" because of the
            // order things are written in: descriptions are saved right after the catalogue row's facts, in the same
            // call, so a package with stored descriptions had its facts written too. A catalogue row the facts
            // migration left blank has no descriptions stored, loads nothing here, and keeps its "no news" meaning.
            if (described.Count == 0 && !versionsOnly)
            {
                described = await descriptions.LoadAsync(
                    upstream.Key,
                    idLower,
                    cached.Unlisted ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    cached.Dependencies ?? new Dictionary<string, IReadOnlyList<UpstreamDependency>>(StringComparer.OrdinalIgnoreCase),
                    cancellationToken);
                if (described.Count > 0)
                {
                    metadataCache.Set(upstream.Key, idLower, described, cached.FetchedUtc);
                }
            }

            // Worth asking again once the list is past its window, or whenever nothing describes it -
            // which is what a restart leaves behind. Never worth waiting for: enqueue collapses
            // duplicates, so a popular package refreshes once however many readers it has.
            if (described.Count == 0 || now - cached.FetchedUtc >= settings.UpstreamIndexTtl)
            {
                refreshes.Enqueue(upstream, idLower);
            }

            return (new UpstreamCatalog(cached.Versions, described, cached.Id), !cached.Stale, cached);
        }

        // An id the upstream said it does not hold, a moment ago: the same authoritative "nothing" again, without asking.
        if (metadataCache.IsMissing(upstream.Key, idLower, now, settings.UpstreamIndexTtl))
        {
            return (new UpstreamCatalog([], []), true, null);
        }

        try
        {
            // Versions alone where that is cheaper, with the full description queued behind the request rather than
            // waited for. Stored exactly as a catalogue that described nothing is - the facts stay "no news" - so
            // nothing downstream can mistake the missing descriptions for "nothing hidden".
            if (versionsOnly && await client.GetVersionsAsync(upstream, idLower, cancellationToken) is { } versions)
            {
                if (versions.Count == 0)
                {
                    metadataCache.SetMissing(upstream.Key, idLower, now);
                    return (new UpstreamCatalog([], []), true, null);
                }

                await index.SaveAsync(upstream.Key, idLower, "", versions, [], stale: false, now, cancellationToken);
                refreshes.Enqueue(upstream, idLower);
                return (new UpstreamCatalog(versions, []), true, null);
            }

            var catalog = await client.GetCatalogAsync(upstream, idLower, cancellationToken);

            // Nothing under this id: remembered in memory for the refresh window, never stored. Any client can ask about any
            // id, and each answer used to become a row.
            if (catalog.Versions.Count == 0)
            {
                metadataCache.SetMissing(upstream.Key, idLower, now);
                return (catalog, true, null);
            }

            await index.SaveAsync(upstream.Key, idLower, catalog.Id, catalog.Versions, catalog.Described, stale: false, now, cancellationToken);
            await descriptions.SaveAsync(upstream.Key, idLower, catalog.Described, cancellationToken);
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

/// <summary>Which upstream owns an id for a feed, if any, and whether that could be decided at all.</summary>
public sealed record UpstreamOwnership(FeedUpstream? Owner, bool Undecided);

/// <summary>
/// What the upstreams hold for one id, and the id as they spell it. The spelling travels with the
/// candidates because a v3 registration URL is lower-cased by convention: without it an uncached package
/// reads as "powershellget" until somebody downloads it and the real nuspec replaces it.
/// </summary>
/// <param name="Upstream">The name of the upstream that owns the id and served these versions; empty when none does.</param>
public sealed record UpstreamCandidates(IReadOnlyList<VersionCandidate<PackageVersion>> Versions, string Id, string Upstream = "")
{
    /// <summary>The upstream's spelling when there is one, otherwise whatever the caller already had.</summary>
    public string Spell(string fallback) => string.IsNullOrEmpty(Id) ? fallback : Id;
}
