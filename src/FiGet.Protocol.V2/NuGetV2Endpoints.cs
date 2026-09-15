using System.Globalization;
using FiGet.Application.Connectors;
using FiGet.Application.Packages;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Domain.Search;
using FiGet.Domain.Versions;
using FiGet.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NuGet.Versioning;

namespace FiGet.Protocol.V2;

/// <summary>
/// NuGet v2 OData under <c>/nuget/{feed}</c>, plus the <c>/api/v2</c> alias PSResourceGet needs to detect
/// the protocol at all (build plan section 4.3). The surface is what the clients were recorded sending in
/// phase 0, not a specification.
/// </summary>
public static class NuGetV2Endpoints
{
    /// <summary>What the clients ask for when they do not say: PowerShellGet uses 40, nuget.exe 30.</summary>
    /// <summary>
    /// Entries returned when a client does not ask for a page size. A hundred, because that is what the
    /// PowerShell gallery answers and a client that does not page should see the same amount from either.
    /// Every client this server exists for sends $top explicitly, so this is about matching the gallery
    /// rather than about what those clients receive.
    /// </summary>
    public const int DefaultTop = 100;

    /// <summary>PSResourceGet asks for 6000 at a time; it pages with $skip when it gets fewer.</summary>
    public const int MaxTop = 1000;

    /// <summary>How many packages a listing ordered other than by id may read; an id-first order is read in chunks, uncapped.</summary>
    private const int MaxPackagesScanned = 2000;

    /// <summary>Packages read at a time by a listing ordered by id.</summary>
    private const int ScanChunk = 500;

    public static IEndpointRouteBuilder MapNuGetV2(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Both roots carry the same operations. PSResourceGet decides the protocol from the URL suffix,
        // so /api/v2 must exist and behave identically; PowerShellGet uses the bare feed root.
        Map(app.MapGroup("/nuget/{feed}"), serviceDocument: true);

        // Except the service document. Register-PSRepository probes {source}/api/v2/ and, when that answers,
        // stores it as the repository's SourceLocation instead of the URL it was given. The server being
        // replaced answers the probe with 404, so a fleet registered against it keeps its plain URL - and
        // Ansible's win_psrepository compares that URL as a string, so a rewritten one reports "changed" and
        // re-registers on every run. Found by running PowerShellGet 2.2.5 against FiGet. PSResourceGet never
        // asks /api/v2 for a service document: it goes straight to the operations below.
        Map(app.MapGroup("/nuget/{feed}/api/v2"), serviceDocument: false);
        return app;
    }

    private static void Map(RouteGroupBuilder group, bool serviceDocument)
    {
        // Only the slashless form is mapped: the router ignores a trailing slash, so mapping both would
        // make "/nuget/{feed}/", the URL PowerShellGet registers, an ambiguous match.
        if (serviceDocument)
        {
            group.MapGet("", ServiceDocumentAsync);
        }
        else
        {
            // Mapped, not merely left out. Without a GET here the path still matches this group's PUT and the
            // plain root's DELETE /{id}/{version} (id "api", version "v2"), and a Release build answers such a
            // request 405 - which is not the 404 the probe needs. A Debug build answers 404 instead, so the
            // tests and every local client run saw the right status while the deployed image did not.
            group.MapGet("", () => Results.NotFound());
        }

        group.MapGet("/$metadata", MetadataAsync);

        group.MapGet("/FindPackagesById()", FindPackagesByIdAsync);
        group.MapGet("/FindPackagesById()/$count", FindPackagesByIdAsync);
        group.MapGet("/Search()", SearchAsync);
        group.MapGet("/Search()/$count", SearchAsync);
        group.MapGet("/Packages", PackagesAsync);
        group.MapGet("/Packages()", PackagesAsync);
        group.MapGet("/Packages()/$count", PackagesAsync);
        group.MapGet("/Packages(Id='{id}',Version='{version}')", PackageByKeyAsync);
        group.MapGet("/GetUpdates()", GetUpdatesAsync);

        group.MapGet("/package/{id}/{version}", DownloadAsync);
        group.MapGet("/package/{id}", LatestDownloadAsync);

        // nuget.exe pushes to the source URL itself, or to {source}/package when the source ends in /api/v2.
        group.MapPut("", PushAsync).DisableAntiforgery();
        group.MapPut("/package", PushAsync).DisableAntiforgery();

        group.MapDelete("/package/{id}/{version}", DeleteAsync);
        group.MapDelete("/{id}/{version}", DeleteAsync);
    }

    private static async Task<IResult> ServiceDocumentAsync(HttpContext http, string feed, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, TokenScopes.Read, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        return Xml(AtomWriter.ServiceDocument(Root(http, request!.Feed.Name)), "application/xml");
    }

    private static async Task<IResult> MetadataAsync(HttpContext http, string feed, CancellationToken cancellationToken)
    {
        var (_, error) = await FeedAccess.ResolveAsync(http, feed, TokenScopes.Read, cancellationToken);
        return error ?? Xml(AtomWriter.Metadata(), "application/xml");
    }

    /// <summary>
    /// The workhorse: every recorded client calls this to find a package by name. An unknown id answers an
    /// empty feed with 200, which is also how the NuGet providers validate that a source is a v2 source.
    /// </summary>
    private static async Task<IResult> FindPackagesByIdAsync(HttpContext http, string feed, IPackageStore store, ConnectorService connector, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, TokenScopes.Read, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var query = http.Request.Query;
        ODataFilter? filter;
        ODataOrderBy order;
        try
        {
            filter = ParseFilter(query["$filter"]);
            order = ODataOrderBy.Parse(query["$orderby"], ODataOrderBy.VersionAscending);
        }
        catch (ODataFilterException ex)
        {
            return BadExpression(http, loggers, ex);
        }

        var id = Unquote(query["id"]) ?? filter?.RequiredId ?? "";
        var rows = id.Length == 0
            ? []
            : await RowsForIdAsync(store, connector, request!.Feed, id, SemVer2(query), cancellationToken);

        return Page(http, request!.Feed.Name, rows, filter, order, query);
    }

    /// <summary>
    /// Free-text search. PowerShellGet puts its own syntax in searchTerm, including the leading space of
    /// " tag:x", which the shared search parser already understands.
    /// </summary>
    private static async Task<IResult> SearchAsync(HttpContext http, string feed, IPackageStore store, ConnectorService connector, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, TokenScopes.Read, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var query = http.Request.Query;
        ODataFilter? filter;
        ODataOrderBy order;
        try
        {
            filter = ParseFilter(query["$filter"]);
            order = ODataOrderBy.Parse(query["$orderby"], ODataOrderBy.Default);
        }
        catch (ODataFilterException ex)
        {
            return BadExpression(http, loggers, ex);
        }

        var term = Unquote(query["searchTerm"]) ?? "";

        // includePrerelease is honoured unless a filter other than a latest-only one asks about versions itself,
        // in which case that filter decides. With no filter at all it applies too: `nuget list -AllVersions`
        // sends none, and got prerelease versions it had not asked for until a recorded answer showed otherwise.
        var prerelease = Bool(query["includePrerelease"]) || (filter is not null && !filter.LatestOnly);

        // A search lists what a client may choose from: never an unlisted version, and a prerelease one only when
        // asked. The package search picks packages; this picks among each package's versions.
        bool Choosable(V2Row r) => r.Entry.Listed && (prerelease || !r.Version.IsPrerelease);
        if (order.LeadsWithIdAscending)
        {
            return await ScanAsync(http, store, connector, request!.Feed, term, prerelease, SemVer2(query), Choosable, filter, order, query, cancellationToken);
        }

        var rows = await SearchRowsAsync(store, connector, request!.Feed, term, filter?.StoreTerms ?? [], prerelease, SemVer2(query), loggers, cancellationToken);
        return Page(http, request.Feed.Name, rows.Where(Choosable).ToList(), filter, order, query);
    }

    private static async Task<IResult> PackagesAsync(HttpContext http, string feed, IPackageStore store, ConnectorService connector, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, TokenScopes.Read, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var query = http.Request.Query;
        ODataFilter? filter;
        ODataOrderBy order;
        try
        {
            filter = ParseFilter(query["$filter"]);
            order = ODataOrderBy.Parse(query["$orderby"], ODataOrderBy.Default);
        }
        catch (ODataFilterException ex)
        {
            return BadExpression(http, loggers, ex);
        }

        var semVer2 = SemVer2(query);
        if (filter?.RequiredId is { Length: > 0 } id)
        {
            return Page(http, request!.Feed.Name, await RowsForIdAsync(store, connector, request.Feed, id, semVer2, cancellationToken), filter, order, query);
        }

        if (order.LeadsWithIdAscending)
        {
            return await ScanAsync(http, store, connector, request!.Feed, "", includePrerelease: true, semVer2, _ => true, filter, order, query, cancellationToken);
        }

        var rows = await SearchRowsAsync(store, connector, request!.Feed, "", filter?.StoreTerms ?? [], includePrerelease: true, semVer2, loggers, cancellationToken);
        return Page(http, request.Feed.Name, rows, filter, order, query);
    }

    /// <summary>
    /// The package without a version: a redirect to the latest stable version, or to the newest prerelease when there is
    /// no stable one. nuget.org's v2 answers this, and so did the server being replaced, so a script that fetched "the
    /// latest" that way keeps working. On a proxy feed the latest is taken from the merged list, upstream versions included.
    /// </summary>
    private static async Task<IResult> LatestDownloadAsync(HttpContext http, string feed, string id, IPackageStore store, ConnectorService connector, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, TokenScopes.Read, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var rows = await RowsForIdAsync(store, connector, request!.Feed, id, includeSemVer2: true, cancellationToken);
        var latest = rows.LastOrDefault(r => r.Entry.IsLatestVersion) ?? rows.LastOrDefault(r => r.Entry.IsAbsoluteLatestVersion);
        return latest is null
            ? Results.NotFound()
            : Results.Redirect($"{Root(http, request.Feed.Name)}/package/{Uri.EscapeDataString(latest.Id)}/{Uri.EscapeDataString(latest.NormalizedVersion)}");
    }

    private static async Task<IResult> PackageByKeyAsync(HttpContext http, string feed, string id, string version, IPackageStore store, ConnectorService connector, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, TokenScopes.Read, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var rows = await RowsForIdAsync(store, connector, request!.Feed, id, includeSemVer2: true, cancellationToken);

        // The version as the client typed it: PackageManagement's v2 provider and nuget install -Version send "1.0" for a
        // stored 1.0.0, which every download route already accepts. The spelling as published is tried first.
        var normalized = PackageIngestionService.NormalizeLower(version);
        var row = rows.FirstOrDefault(r => r.OriginalVersion.Equals(version, StringComparison.OrdinalIgnoreCase))
            ?? rows.FirstOrDefault(r => r.NormalizedVersion.Equals(version, StringComparison.OrdinalIgnoreCase)
                || (normalized is not null && r.NormalizedVersion.Equals(normalized, StringComparison.OrdinalIgnoreCase)));

        return row is null
            ? Results.NotFound()
            : Xml(AtomWriter.Entry(row, Root(http, request.Feed.Name)), "application/atom+xml;type=entry;charset=utf-8");
    }

    /// <summary>
    /// The update check of the old clients: for each id and version pair, the newer versions. Not sent by
    /// any client recorded in phase 0, but cheap to support and part of the documented surface.
    /// </summary>
    private static async Task<IResult> GetUpdatesAsync(HttpContext http, string feed, IPackageStore store, ConnectorService connector, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, TokenScopes.Read, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var query = http.Request.Query;
        var ids = (Unquote(query["packageIds"]) ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries);
        var versions = (Unquote(query["versions"]) ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries);
        var includePrerelease = Bool(query["includePrerelease"]);
        var includeAll = Bool(query["includeAllVersions"]);
        var semVer2 = SemVer2(query);

        var results = new List<V2Row>();
        for (var i = 0; i < ids.Length; i++)
        {
            if (!NuGetVersion.TryParse(i < versions.Length ? versions[i] : "", out var current))
            {
                continue;
            }

            var rows = await RowsForIdAsync(store, connector, request!.Feed, ids[i], semVer2, cancellationToken);
            var newer = rows
                .Where(r => r.Listed && VersionComparer.Default.Compare(r.Version, current) > 0)
                .Where(r => includePrerelease || !r.Version.IsPrerelease)
                .ToList();

            if (newer.Count == 0)
            {
                continue;
            }

            results.AddRange(includeAll ? newer : [newer[^1]]);
        }

        return Xml(
            AtomWriter.Feed(results, Root(http, request!.Feed.Name), SelfUrl(http), null, null),
            "application/atom+xml;type=feed;charset=utf-8");
    }

    private static async Task<IResult> DownloadAsync(HttpContext http, string feed, string id, string version, IPackageStore store, IPackageStorage storage, ConnectorService connector, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, TokenScopes.Read, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var idLower = id.ToLowerInvariant();
        var versionLower = PackageIngestionService.NormalizeLower(version);
        if (versionLower is null)
        {
            return Results.NotFound();
        }

        var row = await store.GetVersionAsync(request!.Feed.Key, idLower, versionLower, cancellationToken);
        if (row is null && request.Feed.Upstreams.Count > 0 && NuGetVersion.TryParse(version, out var wanted))
        {
            // Look-through: any exact version an upstream has is fetchable, not only the latest, because a
            // meta-package pins its dependencies to exact versions.
            row = await connector.EnsureCachedAsync(request.Feed, id, wanted, cancellationToken);
        }

        if (row is null)
        {
            return Results.NotFound();
        }

        var stream = await storage.OpenPackageAsync(new PackageStorageKey(request.Feed.Key, idLower, versionLower), cancellationToken);
        if (stream is null
            && await connector.RepairMissingFileAsync(request.Feed, id, row, cancellationToken) is { } repaired)
        {
            row = repaired;
            stream = await storage.OpenPackageAsync(new PackageStorageKey(request.Feed.Key, idLower, versionLower), cancellationToken);
        }

        if (stream is null)
        {
            return Results.NotFound();
        }

        await store.IncrementDownloadsAsync(row.Key, http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime, cancellationToken);
        var fileName = string.Create(CultureInfo.InvariantCulture, $"{idLower}.{versionLower}.nupkg");
        return Results.Stream(stream, "application/zip", fileName, enableRangeProcessing: true);
    }

    private static async Task<IResult> PushAsync(HttpContext http, string feed, PackageIngestionService ingestion, ConnectorService connector, IOptions<UploadOptions> upload, AuditLog audit, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, TokenScopes.Push, cancellationToken);
        if (error is not null)
        {
            await PackageUpload.DiscardRefusedBodyAsync(http.Request, error, upload.Value, cancellationToken);
            return error;
        }

        FileStream? file;
        try
        {
            file = await PackageUpload.ReadAsync(http.Request, upload.Value, cancellationToken);
        }
        catch (UploadTooLargeException ex)
        {
            return Status(http, StatusCodes.Status413PayloadTooLarge, ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return Status(http, StatusCodes.Status400BadRequest, ex.Message);
        }

        if (file is null)
        {
            return Status(http, StatusCodes.Status400BadRequest, "The request does not contain a package.");
        }

        await using (file)
        {
            var result = await ingestion.PushAsync(request!.Feed, file, cancellationToken);
            if (result.Outcome is PushOutcome.Created or PushOutcome.Replaced)
            {
                audit.Record(http, "package.push", result.Id ?? "", $"feed={feed} version={result.Version} outcome={result.Outcome}");
                await PushWarning.AddAsync(http, connector, request.Feed, result.Id, cancellationToken);
            }

            return result.Outcome switch
            {
                PushOutcome.Created or PushOutcome.Replaced => Status(http, StatusCodes.Status201Created, result.Message),
                PushOutcome.Conflict => Status(http, StatusCodes.Status409Conflict, result.Message),
                PushOutcome.NotFound => Status(http, StatusCodes.Status404NotFound, result.Message),
                _ => Status(http, StatusCodes.Status400BadRequest, result.Message),
            };
        }
    }

    private static async Task<IResult> DeleteAsync(HttpContext http, string feed, string id, string version, PackageIngestionService ingestion, AuditLog audit, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, TokenScopes.Delete, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (!await ingestion.DeleteAsync(request!.Feed, id, version, cancellationToken))
        {
            return Results.NotFound();
        }

        audit.Record(http, "package.delete", id, $"feed={feed} version={version}");
        return Results.Ok();
    }

    /// <summary>
    /// The rows for one id: local versions on a curated feed, and on a proxy feed the merged list of local
    /// and upstream versions. The merge is what keeps exactly one version flagged latest, which is the
    /// failure this whole server exists to avoid.
    /// </summary>
    private static async Task<IReadOnlyList<V2Row>> RowsForIdAsync(
        IPackageStore store,
        ConnectorService connector,
        Feed feed,
        string id,
        bool includeSemVer2,
        CancellationToken cancellationToken)
    {
        var idLower = id.ToLowerInvariant();
        if (feed.Upstreams.Count == 0)
        {
            var curated = await store.GetPackageAsync(feed.Key, idLower, includeDependencies: true, cancellationToken);
            return curated is null ? [] : V2Row.ForPackage(curated, includeSemVer2);
        }

        // The upstreams are asked first: that refresh is what unlists a cached copy the upstream has
        // withdrawn, and the local rows must be read after it, not before.
        var upstream = await connector.UpstreamCandidatesAsync(feed, idLower, cancellationToken);
        var package = await store.GetPackageAsync(feed.Key, idLower, includeDependencies: true, cancellationToken);
        var local = package?.Versions ?? [];
        if (local.Count == 0 && upstream.Versions.Count == 0)
        {
            return [];
        }

        var merged = VersionListBuilder.Build(local.Select(VersionListBuilder.ToCandidate).Concat(upstream.Versions), includeSemVer2);
        var displayId = package?.Id ?? upstream.Spell(id);
        return merged.Where(e => e.Payload is not null).Select(e => new V2Row(displayId, e)).ToList();
    }

    /// <summary>
    /// Rows for a search. When the filter keeps only the latest version, one package yields one row and
    /// package paging is row paging; otherwise a bounded window of packages is flattened, because the
    /// client pages over entries and the two do not line up.
    /// </summary>
    private static async Task<IReadOnlyList<V2Row>> SearchRowsAsync(
        IPackageStore store,
        ConnectorService connector,
        Feed feed,
        string term,
        IReadOnlyList<SearchTerm> filterTerms,
        bool includePrerelease,
        bool includeSemVer2,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var search = new PackageSearchFilter([.. SearchQueryParser.Parse(term), .. filterTerms], includePrerelease, includeSemVer2, null);
        var page = await store.SearchAsync(feed.Key, search, 0, MaxPackagesScanned, cancellationToken);
        if (page.TotalHits > MaxPackagesScanned)
        {
            // Only orders that do not start with the id get here; the answer covers the first packages alone, so say so.
            loggers.CreateLogger("FiGet.Protocol.V2").LogWarning(
                "A v2 listing of feed {Feed} ordered other than by id matched {Packages} packages and read the first {Scanned}; results beyond them are missing.",
                feed.Name,
                page.TotalHits,
                MaxPackagesScanned);
        }

        var packages = await store.GetPackagesAsync(page.PackageKeys, cancellationToken);
        var byKey = packages.ToDictionary(p => p.Key);
        var upstream = await StoredUpstreamAsync(connector, feed, packages, cancellationToken);

        var rows = new List<V2Row>();
        foreach (var key in page.PackageKeys)
        {
            if (byKey.TryGetValue(key, out var package))
            {
                rows.AddRange(PackageRows(package, upstream, includeSemVer2));
            }
        }

        var known = rows.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        rows.AddRange((await UpstreamHitRowsAsync(connector, feed, term, includePrerelease, includeSemVer2, cancellationToken)).Where(r => !known.Contains(r.Id)));
        return rows;
    }

    /// <summary>
    /// A listing ordered by id first, read a chunk of packages at a time in the database's id order, keeping only the rows of
    /// the page asked for. No cap on how many packages match: the 2,000-package window this replaces answered an empty 200
    /// for a startswith filter on a larger feed when the first 2,000 ids held no match. Memory stays one chunk. The scan
    /// stops once a row past the page is found, unless a count is asked for.
    /// </summary>
    private static async Task<IResult> ScanAsync(
        HttpContext http,
        IPackageStore store,
        ConnectorService connector,
        Feed feed,
        string term,
        bool includePrerelease,
        bool includeSemVer2,
        Func<V2Row, bool> keep,
        ODataFilter? filter,
        ODataOrderBy order,
        IQueryCollection query,
        CancellationToken cancellationToken)
    {
        var skip = Math.Max(0, Int(query["$skip"], 0));
        var top = Math.Clamp(Int(query["$top"], DefaultTop), 0, MaxTop);
        var countOnly = http.Request.Path.Value?.EndsWith("/$count", StringComparison.Ordinal) == true;
        var counting = countOnly || query["$inlinecount"].ToString().Equals("allpages", StringComparison.OrdinalIgnoreCase);

        var search = new PackageSearchFilter(
            [.. SearchQueryParser.Parse(term), .. filter?.StoreTerms ?? []],
            includePrerelease,
            includeSemVer2,
            null,
            new PackageSort(PackageSortField.Id, Descending: false));

        // A proxy feed's hits from its upstreams for ids this feed does not hold, merged into the id order as the scan goes.
        var hits = new Queue<V2Row>();
        var upstreamHits = await UpstreamHitRowsAsync(connector, feed, term, includePrerelease, includeSemVer2, cancellationToken);
        if (upstreamHits.Count > 0)
        {
            var held = await store.HeldIdsAsync(feed.Key, [.. upstreamHits.Select(r => r.Id.ToLowerInvariant())], cancellationToken);
            foreach (var row in upstreamHits.Where(r => !held.Contains(r.Id.ToLowerInvariant())).OrderBy(r => r.Id.ToLowerInvariant(), StringComparer.Ordinal))
            {
                hits.Enqueue(row);
            }
        }

        var window = new List<V2Row>();
        var matched = 0;
        var more = false;

        // True when the scan may stop: a row past the page was found and nobody asked for the total.
        bool Take(V2Row row)
        {
            if (!keep(row) || (filter is not null && !filter.Matches(row)))
            {
                return false;
            }

            if (matched >= skip && window.Count < top)
            {
                window.Add(row);
            }
            else if (matched >= skip + top)
            {
                more = true;
            }

            matched++;
            return more && !counting;
        }

        try
        {
            var done = false;
            for (var offset = 0; !done; offset += ScanChunk)
            {
                var page = await store.SearchAsync(feed.Key, search, offset, ScanChunk, cancellationToken);
                var packages = await store.GetPackagesAsync(page.PackageKeys, cancellationToken);
                var byKey = packages.ToDictionary(p => p.Key);
                var upstream = await StoredUpstreamAsync(connector, feed, packages, cancellationToken);
                foreach (var key in page.PackageKeys)
                {
                    if (done || !byKey.TryGetValue(key, out var package))
                    {
                        continue;
                    }

                    while (!done && hits.Count > 0 && string.CompareOrdinal(hits.Peek().Id.ToLowerInvariant(), package.IdLower) < 0)
                    {
                        done = Take(hits.Dequeue());
                    }

                    foreach (var row in order.Sort(PackageRows(package, upstream, includeSemVer2)))
                    {
                        if (done)
                        {
                            break;
                        }

                        done = Take(row);
                    }
                }

                if (page.PackageKeys.Count < ScanChunk)
                {
                    break;
                }
            }

            while (!(more && !counting) && hits.Count > 0)
            {
                Take(hits.Dequeue());
            }
        }
        catch (ODataFilterException ex)
        {
            return Status(http, StatusCodes.Status400BadRequest, $"{ex.Message} Expression: {ex.Expression}");
        }

        if (countOnly)
        {
            return Results.Text(matched.ToString(CultureInfo.InvariantCulture), "text/plain");
        }

        return Xml(
            AtomWriter.Feed(window, Root(http, feed.Name), SelfUrl(http), counting ? matched : null, more && window.Count > 0 ? NextUrl(http, skip + window.Count) : null),
            "application/atom+xml;type=feed;charset=utf-8");
    }

    /// <summary>
    /// A package's rows: on a proxy feed its merged version list from what is stored about the upstreams. Without that a
    /// search flagged the newest cached copy as the latest while the gallery had a newer one, which a wildcard Find-Module
    /// then offered as current - the failure the merged version list exists to prevent.
    /// </summary>
    private static IEnumerable<V2Row> PackageRows(Package package, IReadOnlyDictionary<string, UpstreamCandidates> upstream, bool includeSemVer2)
    {
        if (!upstream.TryGetValue(package.IdLower, out var candidates))
        {
            return V2Row.ForPackage(package, includeSemVer2);
        }

        return VersionListBuilder.Build(package.Versions.Select(VersionListBuilder.ToCandidate).Concat(candidates.Versions), includeSemVer2)
            .Where(e => e.Payload is not null)
            .Select(e => new V2Row(package.Id, e));
    }

    private static async Task<IReadOnlyDictionary<string, UpstreamCandidates>> StoredUpstreamAsync(ConnectorService connector, Feed feed, IReadOnlyList<Package> packages, CancellationToken cancellationToken) =>
        feed.Upstreams.Count > 0 && packages.Count > 0
            ? await connector.StoredUpstreamCandidatesAsync(feed, packages, cancellationToken)
            : new Dictionary<string, UpstreamCandidates>();

    /// <summary>
    /// On a proxy feed a search also reaches the upstreams, so Find-Module finds a module that nobody has cached here yet:
    /// one row per hit. The caller leaves out ids the feed lists itself.
    /// </summary>
    private static async Task<IReadOnlyList<V2Row>> UpstreamHitRowsAsync(ConnectorService connector, Feed feed, string term, bool includePrerelease, bool includeSemVer2, CancellationToken cancellationToken)
    {
        if (feed.Upstreams.Count == 0 || string.IsNullOrWhiteSpace(term))
        {
            return [];
        }

        var rows = new List<V2Row>();
        foreach (var (id, placeholder) in await connector.SearchPlaceholdersAsync(feed, term, includePrerelease, DefaultTop, cancellationToken))
        {
            var merged = VersionListBuilder.Build([VersionListBuilder.ToCandidate(placeholder) with { Source = VersionSource.Upstream }], includeSemVer2);
            if (merged.Count > 0)
            {
                rows.Add(new V2Row(id, merged[0]));
            }
        }

        return rows.DistinctBy(r => r.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Applies the filter, the ordering and the paging, then writes the feed or the count.</summary>
    private static IResult Page(HttpContext http, string feedName, IReadOnlyList<V2Row> rows, ODataFilter? filter, ODataOrderBy order, IQueryCollection query)
    {
        IReadOnlyList<V2Row> ordered;
        try
        {
            // Evaluating the filter and ordering can still fail here: a property name is only known to be
            // unsupported once a row is asked for it. That stays a 400, like a parse failure.
            var matched = filter is null ? rows : rows.Where(filter.Matches).ToList();
            ordered = order.Sort(matched);
        }
        catch (ODataFilterException ex)
        {
            return Status(http, StatusCodes.Status400BadRequest, $"{ex.Message} Expression: {ex.Expression}");
        }

        var skip = Math.Max(0, Int(query["$skip"], 0));
        var top = Math.Clamp(Int(query["$top"], DefaultTop), 0, MaxTop);
        var window = ordered.Skip(skip).Take(top).ToList();

        if (http.Request.Path.Value?.EndsWith("/$count", StringComparison.Ordinal) == true)
        {
            return Results.Text(ordered.Count.ToString(CultureInfo.InvariantCulture), "text/plain");
        }

        var count = query["$inlinecount"].ToString().Equals("allpages", StringComparison.OrdinalIgnoreCase)
            ? ordered.Count
            : (int?)null;

        string? next = null;
        if (skip + window.Count < ordered.Count && window.Count > 0)
        {
            next = NextUrl(http, skip + window.Count);
        }

        return Xml(
            AtomWriter.Feed(window, Root(http, feedName), SelfUrl(http), count, next),
            "application/atom+xml;type=feed;charset=utf-8");
    }

    private static ODataFilter? ParseFilter(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : ODataFilter.Parse(text);

    /// <summary>
    /// An expression outside the supported grammar is a 400 naming it, never an empty 200: that is the
    /// difference between "this server cannot do that" and "the package does not exist".
    /// </summary>
    private static IResult BadExpression(HttpContext http, ILoggerFactory loggers, ODataFilterException exception)
    {
        loggers.CreateLogger("FiGet.Protocol.V2").LogWarning(
            "Unsupported OData expression on {Path}: {Expression} ({Message})",
            http.Request.Path.Value,
            exception.Expression,
            exception.Message);

        return Status(http, StatusCodes.Status400BadRequest, $"{exception.Message} Expression: {exception.Expression}");
    }

    private static string Root(HttpContext http, string feedName)
    {
        var root = PublicUrls.Feed(http, feedName);
        // Keep the alias the client used, so every URL in the answer stays under the root it registered.
        return http.Request.Path.Value?.Contains("/api/v2", StringComparison.OrdinalIgnoreCase) == true
            ? root + "/api/v2"
            : root;
    }

    private static string SelfUrl(HttpContext http) =>
        PublicUrls.Base(http) + http.Request.Path + http.Request.QueryString;

    private static string NextUrl(HttpContext http, int skip)
    {
        var parameters = http.Request.Query
            .Where(p => !p.Key.Equals("$skip", StringComparison.OrdinalIgnoreCase))
            .SelectMany(p => p.Value.Select(v => (p.Key, Value: v ?? "")))
            .ToList();
        parameters.Add(("$skip", skip.ToString(CultureInfo.InvariantCulture)));

        var queryString = string.Join('&', parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        return PublicUrls.Base(http) + http.Request.Path + "?" + queryString;
    }

    /// <summary>Strips the single quotes OData puts around a string parameter.</summary>
    private static string? Unquote(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[^1] == '\'')
        {
            trimmed = trimmed[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        return trimmed;
    }

    private static bool Bool(string? value) =>
        value is not null && value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

    private static bool SemVer2(IQueryCollection query) =>
        query["semVerLevel"].ToString().Trim().StartsWith("2.", StringComparison.Ordinal);

    private static int Int(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static IResult Xml(string body, string contentType) => Results.Text(body, contentType);

    /// <summary>Puts the message in the reason phrase, which nuget.exe prints, and in a JSON body.</summary>
    private static IResult Status(HttpContext http, int statusCode, string message)
    {
        var feature = http.Features.Get<IHttpResponseFeature>();
        if (feature is not null)
        {
            var phrase = new string(message.Where(c => c is >= ' ' and <= '~').ToArray());
            feature.ReasonPhrase = phrase.Length > 200 ? phrase[..200] : phrase;
        }

        return Results.Json(new { message }, statusCode: statusCode);
    }
}
