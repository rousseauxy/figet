using System.Globalization;
using FiGet.Application.Connectors;
using FiGet.Application.Packages;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Domain.Search;
using FiGet.Domain.Versions;
using FiGet.Http;
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
    public const int DefaultTop = 40;

    /// <summary>PSResourceGet asks for 6000 at a time; it pages with $skip when it gets fewer.</summary>
    public const int MaxTop = 1000;

    /// <summary>How many packages a listing may scan when the filter does not name one id.</summary>
    private const int MaxPackagesScanned = 2000;

    public static IEndpointRouteBuilder MapNuGetV2(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Both roots carry the same operations. PSResourceGet decides the protocol from the URL suffix,
        // so /api/v2 must exist and behave identically; PowerShellGet uses the bare feed root.
        Map(app.MapGroup("/nuget/{feed}"));
        Map(app.MapGroup("/nuget/{feed}/api/v2"));
        return app;
    }

    private static void Map(RouteGroupBuilder group)
    {
        // Only the slashless form is mapped: the router ignores a trailing slash, so mapping both would
        // make "/nuget/{feed}/", the URL PowerShellGet registers, an ambiguous match.
        group.MapGet("", ServiceDocumentAsync);
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
        var prerelease = Bool(query["includePrerelease"]) || (filter?.LatestOnly != true);
        var rows = await SearchRowsAsync(store, connector, request!.Feed, term, prerelease, SemVer2(query), cancellationToken);
        return Page(http, request.Feed.Name, rows, filter, order, query);
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
        var rows = filter?.RequiredId is { Length: > 0 } id
            ? await RowsForIdAsync(store, connector, request!.Feed, id, semVer2, cancellationToken)
            : await SearchRowsAsync(store, connector, request!.Feed, "", includePrerelease: true, semVer2, cancellationToken);

        return Page(http, request.Feed.Name, rows, filter, order, query);
    }

    private static async Task<IResult> PackageByKeyAsync(HttpContext http, string feed, string id, string version, IPackageStore store, ConnectorService connector, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, TokenScopes.Read, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var rows = await RowsForIdAsync(store, connector, request!.Feed, id, includeSemVer2: true, cancellationToken);
        var row = rows.FirstOrDefault(r =>
            r.OriginalVersion.Equals(version, StringComparison.OrdinalIgnoreCase)
            || r.NormalizedVersion.Equals(version, StringComparison.OrdinalIgnoreCase));

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

        var stream = await storage.OpenPackageAsync(new PackageStorageKey(request.Feed.NameLower, idLower, versionLower), cancellationToken);
        if (stream is null)
        {
            return Results.NotFound();
        }

        await store.IncrementDownloadsAsync(row.Key, cancellationToken);
        var fileName = string.Create(CultureInfo.InvariantCulture, $"{idLower}.{versionLower}.nupkg");
        return Results.Stream(stream, "application/zip", fileName, enableRangeProcessing: true);
    }

    private static async Task<IResult> PushAsync(HttpContext http, string feed, PackageIngestionService ingestion, IOptions<UploadOptions> upload, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, TokenScopes.Push, cancellationToken);
        if (error is not null)
        {
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
            return result.Outcome switch
            {
                PushOutcome.Created or PushOutcome.Replaced => Status(http, StatusCodes.Status201Created, result.Message),
                PushOutcome.Conflict => Status(http, StatusCodes.Status409Conflict, result.Message),
                PushOutcome.NotFound => Status(http, StatusCodes.Status404NotFound, result.Message),
                _ => Status(http, StatusCodes.Status400BadRequest, result.Message),
            };
        }
    }

    private static async Task<IResult> DeleteAsync(HttpContext http, string feed, string id, string version, PackageIngestionService ingestion, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, TokenScopes.Delete, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        return await ingestion.DeleteAsync(request!.Feed, id, version, cancellationToken) ? Results.NoContent() : Results.NotFound();
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
        if (local.Count == 0 && upstream.Count == 0)
        {
            return [];
        }

        var merged = VersionListBuilder.Build(local.Select(VersionListBuilder.ToCandidate).Concat(upstream), includeSemVer2);
        var displayId = package?.Id ?? id;
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
        bool includePrerelease,
        bool includeSemVer2,
        CancellationToken cancellationToken)
    {
        var search = new PackageSearchFilter(SearchQueryParser.Parse(term), includePrerelease, includeSemVer2, null);
        var page = await store.SearchAsync(feed.Key, search, 0, MaxPackagesScanned, cancellationToken);
        var packages = await store.GetPackagesAsync(page.PackageKeys, cancellationToken);
        var byKey = packages.ToDictionary(p => p.Key);

        var rows = new List<V2Row>();
        foreach (var key in page.PackageKeys)
        {
            if (byKey.TryGetValue(key, out var package))
            {
                rows.AddRange(V2Row.ForPackage(package, includeSemVer2));
            }
        }

        // On a proxy feed a search also reaches the upstreams, so Find-Module finds a module that nobody
        // has cached here yet. Ids already listed locally keep their local rows.
        if (feed.Upstreams.Count > 0 && !string.IsNullOrWhiteSpace(term))
        {
            var known = rows.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, placeholder) in await connector.SearchPlaceholdersAsync(feed, term, includePrerelease, DefaultTop, cancellationToken))
            {
                if (known.Contains(id))
                {
                    continue;
                }

                var merged = VersionListBuilder.Build(
                    [VersionListBuilder.ToCandidate(placeholder) with { Source = VersionSource.Upstream }],
                    includeSemVer2);
                if (merged.Count > 0)
                {
                    rows.Add(new V2Row(id, merged[0]));
                }
            }
        }

        return rows;
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
