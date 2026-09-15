using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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
using Microsoft.Extensions.Options;
using NuGet.Versioning;

namespace FiGet.Protocol.V3;

/// <summary>NuGet v3 server API under <c>/nuget/{feed}/v3</c> (build plan §4.2).</summary>
public static class NuGetV3Endpoints
{
    /// <summary>Leaves per registration page, as on nuget.org.</summary>
    public const int RegistrationPageSize = 64;

    /// <summary>Registration indexes with at most this many versions inline their pages.</summary>
    public const int MaxInlinedLeaves = 128;

    public const int DefaultTake = 20;
    public const int MaxTake = 1000;

    private const string UnlistedPublished = "1900-01-01T00:00:00+00:00";

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly RegistrationContext RegistrationJsonContext = new(
        "http://schema.nuget.org/schema#",
        "http://schema.nuget.org/catalog#",
        "http://www.w3.org/2001/XMLSchema#");

    public static IEndpointRouteBuilder MapNuGetV3(this IEndpointRouteBuilder app)
    {
        var v3 = app.MapGroup("/nuget/{feed}/v3");
        v3.MapGet("/index.json", ServiceIndexAsync);
        v3.MapGet("/registration/{id}/index.json", RegistrationIndexAsync);
        v3.MapGet("/registration/{id}/page/{lower}/{upper}.json", RegistrationPageAsync);
        v3.MapGet("/registration/{id}/{version}.json", RegistrationLeafAsync);
        v3.MapGet("/catalog/{id}/{version}.json", CatalogEntryAsync);
        v3.MapGet("/flatcontainer/{id}/index.json", FlatContainerVersionsAsync);
        v3.MapGet("/flatcontainer/{id}/{version}/{file}", FlatContainerFileAsync);
        v3.MapGet("/query", SearchAsync);
        v3.MapGet("/autocomplete", AutocompleteAsync);
        v3.MapPut("/publish", PushAsync).DisableAntiforgery();
        v3.MapDelete("/publish/{id}/{version}", DeleteAsync);
        v3.MapPost("/publish/{id}/{version}", RelistAsync).DisableAntiforgery();
        v3.MapPut("/symbolpublish", PushSymbolsAsync).DisableAntiforgery();

        app.MapGet("/nuget/{feed}/symbols/{file}/{key}/{file2}", SymbolFileAsync);
        return app;
    }

    private static async Task<IResult> ServiceIndexAsync(HttpContext http, string feed, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveServiceIndexAsync(http, feed, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var v3 = PublicUrls.Feed(http, request!.Feed.Name) + "/v3";
        var resources = new List<ServiceResource>();
        void Add(string path, string comment, params string[] types) =>
            resources.AddRange(types.Select(t => new ServiceResource(v3 + path, t, comment)));

        Add("/query", "Query endpoint of the search service.", "SearchQueryService", "SearchQueryService/3.0.0-beta", "SearchQueryService/3.0.0-rc", "SearchQueryService/3.5.0");
        Add("/autocomplete", "Autocomplete endpoint of the search service.", "SearchAutocompleteService", "SearchAutocompleteService/3.0.0-beta", "SearchAutocompleteService/3.0.0-rc", "SearchAutocompleteService/3.5.0");
        Add("/registration/", "Base URL of the package metadata.", "RegistrationsBaseUrl", "RegistrationsBaseUrl/3.0.0-beta", "RegistrationsBaseUrl/3.0.0-rc", "RegistrationsBaseUrl/3.4.0", "RegistrationsBaseUrl/3.6.0");
        Add("/flatcontainer/", "Base URL of the package content.", "PackageBaseAddress/3.0.0");
        Add("/publish", "Push and delete packages.", "PackagePublish/2.0.0");
        Add("/symbolpublish", "Push symbol packages.", "SymbolPackagePublish/4.9.0");

        return Results.Json(
            new ServiceIndex("3.0.0", resources, new ServiceIndexContext("http://schema.nuget.org/services#", "http://www.w3.org/2000/01/rdf-schema#comment")),
            Json);
    }

    private static async Task<IResult> RegistrationIndexAsync(HttpContext http, string feed, string id, IPackageStore store, ConnectorService connector, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, Domain.Entities.TokenScopes.Read, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var (package, list) = await MergedAsync(store, connector, request!.Feed, id, includeDependencies: true, cancellationToken);
        if (list.Count == 0)
        {
            return Results.NotFound();
        }

        var urls = new UrlSet(PublicUrls.Feed(http, request.Feed.Name), package.IdLower);
        var inline = list.Count <= MaxInlinedLeaves;
        var pages = list
            .Chunk(RegistrationPageSize)
            .Select(chunk => BuildPage(urls, package, chunk, inline))
            .ToList();

        var latest = list.Max(e => e.Payload!.LastUpdatedUtc);
        return Results.Json(
            new RegistrationIndex(
                urls.RegistrationIndex,
                ["catalog:CatalogRoot", "PackageRegistration", "catalog:Permalink"],
                CommitId(package.IdLower, latest),
                Timestamp(latest),
                pages.Count,
                pages,
                RegistrationJsonContext),
            Json);
    }

    /// <summary>
    /// One page of a registration index, for a package with more versions than the index inlines.
    ///
    /// Merged, like the index that links here. It used to read only what this feed holds, which on a proxy
    /// feed is a different list from the one the index paged: the index advertised ranges spanning upstream
    /// versions, and this answered 404 for every one of them. A client asking for a package by name then
    /// reports that it does not exist - dbatools, 16 pages, none of them inlined, none of them fetchable.
    /// </summary>
    private static async Task<IResult> RegistrationPageAsync(HttpContext http, string feed, string id, string lower, string upper, IPackageStore store, ConnectorService connector, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, Domain.Entities.TokenScopes.Read, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var (package, list) = await MergedAsync(store, connector, request!.Feed, id, includeDependencies: true, cancellationToken);
        if (list.Count == 0)
        {
            return Results.NotFound();
        }

        var urls = new UrlSet(PublicUrls.Feed(http, request.Feed.Name), package.IdLower);
        var chunk = list
            .Chunk(RegistrationPageSize)
            .FirstOrDefault(c =>
                c[0].Version.ToNormalizedString().Equals(lower, StringComparison.OrdinalIgnoreCase)
                && c[^1].Version.ToNormalizedString().Equals(upper, StringComparison.OrdinalIgnoreCase));
        if (chunk is null)
        {
            return Results.NotFound();
        }

        var page = BuildPage(urls, package, chunk, inline: true);
        return Results.Json(
            new RegistrationPageDocument(urls.Page(chunk), page.Type, page.CommitId, page.CommitTimeStamp, page.Count, page.Items!, urls.RegistrationIndex, page.Lower, page.Upper, RegistrationJsonContext),
            Json);
    }

    /// <summary>
    /// The leaf for one version. Merged for the same reason the page is: a version this feed has not
    /// cached still appears in the index that links here, so answering 404 for it makes the index lie.
    /// </summary>
    private static async Task<IResult> RegistrationLeafAsync(HttpContext http, string feed, string id, string version, IPackageStore store, ConnectorService connector, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, Domain.Entities.TokenScopes.Read, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var versionLower = PackageIngestionService.NormalizeLower(version);
        if (versionLower is null)
        {
            return Results.NotFound();
        }

        var (package, list) = await MergedAsync(store, connector, request!.Feed, id, includeDependencies: false, cancellationToken);
        var row = list.FirstOrDefault(e => e.Payload!.NormalizedVersionLower == versionLower)?.Payload;
        if (row is null)
        {
            return Results.NotFound();
        }

        var urls = new UrlSet(PublicUrls.Feed(http, request.Feed.Name), package.IdLower);
        return Results.Json(
            new RegistrationLeaf(
                urls.Leaf(row.NormalizedVersionLower),
                ["Package", "http://schema.nuget.org/catalog#Permalink"],
                urls.Catalog(row.NormalizedVersionLower),
                row.Listed,
                urls.Content(row.NormalizedVersion),
                row.Listed ? Timestamp(row.PublishedUtc) : UnlistedPublished,
                urls.RegistrationIndex,
                RegistrationJsonContext),
            Json);
    }

    /// <summary>
    /// The package details of one version, the document a registration leaf's <c>catalogEntry</c> points to.
    /// PackageManagement's NuGet provider 3.x resolves a version by following that URL and reading <c>version</c>
    /// and the metadata from it; pointing it anywhere else makes every version silently not match. This is a
    /// per-version document only, not the catalog resource (no pages, no commit log).
    /// </summary>
    private static async Task<IResult> CatalogEntryAsync(HttpContext http, string feed, string id, string version, IPackageStore store, ConnectorService connector, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, Domain.Entities.TokenScopes.Read, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var versionLower = PackageIngestionService.NormalizeLower(version);
        if (versionLower is null)
        {
            return Results.NotFound();
        }

        // Merged: the comment above is why. A version the provider cannot read here is a version it
        // silently refuses to install, and on a proxy feed most versions are not cached yet.
        var (package, list) = await MergedAsync(store, connector, request!.Feed, id, includeDependencies: true, cancellationToken);
        var row = list.FirstOrDefault(e => e.Payload!.NormalizedVersionLower == versionLower)?.Payload;
        if (row is null)
        {
            return Results.NotFound();
        }

        var urls = new UrlSet(PublicUrls.Feed(http, request.Feed.Name), package.IdLower);
        return Results.Json(BuildLeafItem(urls, package, row).CatalogEntry, Json);
    }

    private static async Task<IResult> FlatContainerVersionsAsync(HttpContext http, string feed, string id, IPackageStore store, ConnectorService connector, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, Domain.Entities.TokenScopes.Read, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var (_, list) = await MergedAsync(store, connector, request!.Feed, id, includeDependencies: false, cancellationToken, versionsOnly: true);
        if (list.Count == 0)
        {
            return Results.NotFound();
        }

        // The package base address lists every stored version, unlisted included, as nuget.org does, and on
        // a proxy feed everything the upstreams hold as well.
        var versions = list.Select(e => e.Payload!.NormalizedVersionLower).ToList();
        return Results.Json(new FlatContainerVersions(versions), Json);
    }

    private static async Task<IResult> FlatContainerFileAsync(HttpContext http, string feed, string id, string version, string file, IPackageStore store, IPackageStorage storage, ConnectorService connector, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, Domain.Entities.TokenScopes.Read, cancellationToken);
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

        var fileLower = file.ToLowerInvariant();
        var rawLower = version.ToLowerInvariant();
        var isNupkg = fileLower == $"{idLower}.{versionLower}.nupkg" || fileLower == $"{idLower}.{rawLower}.nupkg";
        var isNuspec = fileLower == $"{idLower}.nuspec";
        if (!isNupkg && !isNuspec)
        {
            return Results.NotFound();
        }

        var row = await store.GetVersionAsync(request!.Feed.Key, idLower, versionLower, cancellationToken);
        if (row is null && request.Feed.Upstreams.Count > 0 && NuGetVersion.TryParse(version, out var wanted))
        {
            // Look-through: an exact version an upstream holds is fetched and cached on first request,
            // because a meta-package pins its dependencies to exact versions.
            row = await connector.EnsureCachedAsync(request.Feed, id, wanted, cancellationToken);
        }

        if (row is null)
        {
            return Results.NotFound();
        }

        var key = new PackageStorageKey(request.Feed.Key, idLower, versionLower);
        var stream = isNupkg
            ? await storage.OpenPackageAsync(key, cancellationToken)
            : await storage.OpenNuspecAsync(key, cancellationToken);
        if (stream is null
            && await connector.RepairMissingFileAsync(request.Feed, id, row, cancellationToken) is { } repaired)
        {
            row = repaired;
            stream = isNupkg
                ? await storage.OpenPackageAsync(key, cancellationToken)
                : await storage.OpenNuspecAsync(key, cancellationToken);
        }

        if (stream is null)
        {
            return Results.NotFound();
        }

        if (isNupkg)
        {
            await store.IncrementDownloadsAsync(row.Key, http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime, cancellationToken);
        }

        return Results.Stream(stream, isNupkg ? "application/octet-stream" : "application/xml", enableRangeProcessing: true);
    }

    private static async Task<IResult> SearchAsync(HttpContext http, string feed, IPackageStore store, ConnectorService connector, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, Domain.Entities.TokenScopes.Read, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var query = http.Request.Query;
        var skip = Math.Max(0, IntParam(query["skip"], 0));
        var take = Math.Clamp(IntParam(query["take"], DefaultTake), 0, MaxTake);
        var prerelease = BoolParam(query["prerelease"]);
        var semVer2 = IsSemVer2Level(query["semVerLevel"]);
        var packageType = query["packageType"].ToString().Trim().ToLowerInvariant();

        var filter = new PackageSearchFilter(SearchQueryParser.Parse(query["q"]), prerelease, semVer2, packageType.Length == 0 ? null : packageType);
        var page = await store.SearchAsync(request!.Feed.Key, filter, skip, take, cancellationToken);
        var packages = (await store.GetPackagesAsync(page.PackageKeys, cancellationToken)).ToDictionary(p => p.Key);
        var feedUrl = PublicUrls.Feed(http, request.Feed.Name);

        // The merged version list on a proxy feed, as the registration serves it: a cached older copy is not the latest
        // while the upstream holds a newer version.
        var upstream = request.Feed.Upstreams.Count > 0
            ? await connector.StoredUpstreamCandidatesAsync(request.Feed, [.. packages.Values], cancellationToken)
            : new Dictionary<string, UpstreamCandidates>();

        var data = new List<SearchResult>(page.PackageKeys.Count);
        foreach (var packageKey in page.PackageKeys)
        {
            if (!packages.TryGetValue(packageKey, out var package))
            {
                continue;
            }

            var list = upstream.TryGetValue(package.IdLower, out var candidates)
                ? VersionListBuilder.Build(package.Versions.Select(VersionListBuilder.ToCandidate).Concat(candidates.Versions), semVer2)
                : VersionListBuilder.BuildLocal(package.Versions, semVer2);
            var latest = list.Latest(prerelease);
            if (latest is null)
            {
                continue;
            }

            var urls = new UrlSet(feedUrl, package.IdLower);
            var metadata = latest.Payload!;
            var visible = list.Where(e => e.Listed && (prerelease || !e.Version.IsPrerelease)).ToList();
            data.Add(new SearchResult
            {
                Id = urls.RegistrationIndex,
                Registration = urls.RegistrationIndex,
                PackageId = package.Id,
                Version = FullVersion(metadata),
                Description = metadata.Description,
                Summary = metadata.Summary,
                Title = metadata.Title,
                IconUrl = metadata.IconUrl,
                LicenseUrl = metadata.LicenseUrl,
                ProjectUrl = metadata.ProjectUrl,
                Tags = SplitTags(metadata.Tags),
                Authors = SplitAuthors(metadata.Authors),
                TotalDownloads = package.Versions.Sum(v => v.Downloads),
                PackageTypes = SplitPackageTypes(metadata.PackageTypes).Select(t => new SearchPackageType(t)).ToList(),
                Versions = visible.Select(e => new SearchVersion(FullVersion(e.Payload!), e.Payload!.Downloads, urls.Leaf(e.Payload!.NormalizedVersionLower))).ToList(),
            });
        }

        // A proxy feed searches its upstreams too, so a package nobody has cached here is still findable.
        var term = query["q"].ToString();
        if (request.Feed.Upstreams.Count > 0 && !string.IsNullOrWhiteSpace(term))
        {
            var known = data.Select(d => d.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (upstreamId, metadata) in await connector.SearchPlaceholdersAsync(request.Feed, term, prerelease, take, cancellationToken))
            {
                if (!known.Add(upstreamId))
                {
                    continue;
                }

                var urls = new UrlSet(feedUrl, upstreamId.ToLowerInvariant());
                data.Add(new SearchResult
                {
                    Id = urls.RegistrationIndex,
                    Registration = urls.RegistrationIndex,
                    PackageId = upstreamId,
                    Version = FullVersion(metadata),
                    Description = metadata.Description,
                    Summary = metadata.Summary,
                    Title = metadata.Title,
                    IconUrl = metadata.IconUrl,
                    LicenseUrl = metadata.LicenseUrl,
                    ProjectUrl = metadata.ProjectUrl,
                    Tags = SplitTags(metadata.Tags),
                    Authors = SplitAuthors(metadata.Authors),
                    TotalDownloads = metadata.Downloads,
                    PackageTypes = SplitPackageTypes(metadata.PackageTypes).Select(t => new SearchPackageType(t)).ToList(),
                    Versions = [new SearchVersion(FullVersion(metadata), metadata.Downloads, urls.Leaf(metadata.NormalizedVersionLower))],
                });
            }
        }

        return Results.Json(
            new SearchResponse(
                new SearchContext("http://schema.nuget.org/schema#", feedUrl + "/v3/registration/"),
                Math.Max(page.TotalHits, data.Count),
                data),
            Json);
    }

    private static async Task<IResult> AutocompleteAsync(HttpContext http, string feed, IPackageStore store, ConnectorService connector, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, Domain.Entities.TokenScopes.Read, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var query = http.Request.Query;
        var prerelease = BoolParam(query["prerelease"]);
        var semVer2 = IsSemVer2Level(query["semVerLevel"]);
        var context = new SearchContext("http://schema.nuget.org/schema#", "");

        var id = query["id"].ToString();
        if (id.Length > 0)
        {
            var idLower = id.ToLowerInvariant();
            var package = await store.GetPackageAsync(request!.Feed.Key, idLower, includeDependencies: false, cancellationToken);

            // Every version a client could install, so on a proxy feed also those the upstream holds, from the stored catalogue.
            var upstream = request.Feed.Upstreams.Count > 0
                ? await connector.StoredUpstreamCandidatesAsync(request.Feed, package, idLower, cancellationToken)
                : new UpstreamCandidates([], "");
            var versions = VersionListBuilder.Build((package?.Versions ?? []).Select(VersionListBuilder.ToCandidate).Concat(upstream.Versions), semVer2)
                    .Where(e => e.Listed && (prerelease || !e.Version.IsPrerelease))
                    .Select(e => FullVersion(e.Payload!))
                    .ToList();
            return Results.Json(new AutocompleteResponse(context, versions.Count, versions), Json);
        }

        var skip = Math.Max(0, IntParam(query["skip"], 0));
        var take = Math.Clamp(IntParam(query["take"], DefaultTake), 0, MaxTake);
        var ids = await store.AutocompleteIdsAsync(request!.Feed.Key, query["q"].ToString(), prerelease, semVer2, skip, take, cancellationToken);
        return Results.Json(new AutocompleteResponse(context, skip + ids.Count, ids), Json);
    }

    private static async Task<IResult> PushAsync(HttpContext http, string feed, PackageIngestionService ingestion, ConnectorService connector, IOptions<UploadOptions> upload, AuditLog audit, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, Domain.Entities.TokenScopes.Push, cancellationToken);
        if (error is not null)
        {
            await PackageUpload.DiscardRefusedBodyAsync(http.Request, error, upload.Value, cancellationToken);
            return error;
        }

        return await UploadAsync(
            http,
            upload.Value,
            async file =>
            {
                var result = await ingestion.PushAsync(request!.Feed, file, cancellationToken);
                if (result.Outcome is PushOutcome.Created or PushOutcome.Replaced)
                {
                    audit.Record(http, "package.push", result.Id ?? "", $"feed={feed} version={result.Version} outcome={result.Outcome}");
                    await PushWarning.AddAsync(http, connector, request.Feed, result.Id, cancellationToken);
                }

                return result;
            },
            cancellationToken);
    }

    private static async Task<IResult> PushSymbolsAsync(HttpContext http, string feed, PackageIngestionService ingestion, IOptions<UploadOptions> upload, AuditLog audit, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, Domain.Entities.TokenScopes.Push, cancellationToken);
        if (error is not null)
        {
            await PackageUpload.DiscardRefusedBodyAsync(http.Request, error, upload.Value, cancellationToken);
            return error;
        }

        return await UploadAsync(
            http,
            upload.Value,
            async file =>
            {
                var result = await ingestion.PushSymbolsAsync(request!.Feed, file, cancellationToken);
                if (result.Outcome is PushOutcome.Created or PushOutcome.Replaced)
                {
                    audit.Record(http, "symbols.push", result.Id ?? "", $"feed={feed} version={result.Version}");
                }

                return result;
            },
            cancellationToken);
    }

    private static async Task<IResult> DeleteAsync(HttpContext http, string feed, string id, string version, PackageIngestionService ingestion, AuditLog audit, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, Domain.Entities.TokenScopes.Delete, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (!await ingestion.DeleteAsync(request!.Feed, id, version, cancellationToken))
        {
            return Results.NotFound();
        }

        // What the feed's deletion behaviour did - unlist or remove - is the feed's setting, and the
        // audit line for that setting is where it was chosen.
        audit.Record(http, "package.delete", id, $"feed={feed} version={version}");
        return Results.NoContent();
    }

    private static async Task<IResult> RelistAsync(HttpContext http, string feed, string id, string version, PackageIngestionService ingestion, AuditLog audit, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, Domain.Entities.TokenScopes.Delete, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (!await ingestion.RelistAsync(request!.Feed, id, version, cancellationToken))
        {
            return Results.NotFound();
        }

        audit.Record(http, "package.relist", id, $"feed={feed} version={version}");
        return Results.Ok();
    }

    private static async Task<IResult> SymbolFileAsync(HttpContext http, string feed, string file, string key, string file2, IPackageStore store, IPackageStorage storage, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, Domain.Entities.TokenScopes.Read, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (!file.Equals(file2, StringComparison.OrdinalIgnoreCase))
        {
            return Results.NotFound();
        }

        var row = await store.FindSymbolFileAsync(request!.Feed.Key, file.ToLowerInvariant(), key.ToLowerInvariant(), cancellationToken);
        if (row is null)
        {
            return Results.NotFound();
        }

        var stream = await storage.OpenSymbolAsync(new SymbolStorageKey(request.Feed.Key, row.FileNameLower, row.SymbolKeyLower), cancellationToken);
        return stream is null ? Results.NotFound() : Results.Stream(stream, "application/octet-stream", enableRangeProcessing: true);
    }

    /// <summary>Buffers the upload, hands it to the ingestion step and maps the outcome to a status code.</summary>
    private static async Task<IResult> UploadAsync(HttpContext http, UploadOptions options, Func<Stream, Task<PushResult>> ingest, CancellationToken cancellationToken)
    {
        FileStream? file;
        try
        {
            file = await PackageUpload.ReadAsync(http.Request, options, cancellationToken);
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
            var result = await ingest(file);
            return result.Outcome switch
            {
                PushOutcome.Created or PushOutcome.Replaced => Status(http, StatusCodes.Status201Created, result.Message),
                PushOutcome.Conflict => Status(http, StatusCodes.Status409Conflict, result.Message),
                PushOutcome.NotFound => Status(http, StatusCodes.Status404NotFound, result.Message),
                _ => Status(http, StatusCodes.Status400BadRequest, result.Message),
            };
        }
    }

    /// <summary>
    /// Sets the status with the message as the HTTP/1.1 reason phrase, which nuget.exe and the dotnet CLI
    /// print, and as a JSON body for everything else.
    /// </summary>
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

    /// <summary>
    /// A package row and its version list. On a proxy feed the upstreams' versions are merged in, so a
    /// package nobody has downloaded yet is listed, with exactly one version flagged latest. An id that
    /// exists only upstream has no local row, so a stand-in carries the id for URL building.
    /// </summary>
    private static async Task<(Package Package, IReadOnlyList<VersionListEntry<PackageVersion>> List)> MergedAsync(
        IPackageStore store,
        ConnectorService connector,
        Domain.Entities.Feed feed,
        string id,
        bool includeDependencies,
        CancellationToken cancellationToken,
        bool versionsOnly = false)
    {
        var idLower = id.ToLowerInvariant();
        if (feed.Upstreams.Count == 0)
        {
            var curated = await store.GetPackageAsync(feed.Key, idLower, includeDependencies, cancellationToken);
            return (
                curated ?? new Package { FeedKey = feed.Key, Id = id, IdLower = idLower },
                curated is null ? [] : VersionListBuilder.BuildLocal(curated.Versions, includeSemVer2: true));
        }

        // Upstreams first: that refresh unlists cached copies the upstream has withdrawn, and the local
        // rows have to be read after it to reflect that in this same response.
        var upstream = await connector.UpstreamCandidatesAsync(feed, idLower, cancellationToken, versionsOnly);
        var package = await store.GetPackageAsync(feed.Key, idLower, includeDependencies, cancellationToken);

        // A registration URL is lower-cased by convention, so `id` here is "powershellget" however the
        // gallery spells it. Once a version is cached the local row carries the real spelling; until then
        // it has to come from the upstream, or the page renames the package for as long as nobody has
        // downloaded it.
        var stand = package ?? new Package { FeedKey = feed.Key, Id = upstream.Spell(id), IdLower = idLower };
        var local = package?.Versions ?? [];
        var merged = VersionListBuilder
            .Build(local.Select(VersionListBuilder.ToCandidate).Concat(upstream.Versions), includeSemVer2: true)
            .Where(e => e.Payload is not null)
            .ToList();

        return (stand, merged);
    }

    private static RegistrationPage BuildPage(UrlSet urls, Package package, IReadOnlyList<VersionListEntry<PackageVersion>> chunk, bool inline)
    {
        var latest = chunk.Max(e => e.Payload!.LastUpdatedUtc);
        var lower = chunk[0].Version.ToNormalizedString();
        var upper = chunk[^1].Version.ToNormalizedString();
        return new RegistrationPage(
            inline ? $"{urls.RegistrationIndex}#page/{lower}/{upper}" : urls.Page(chunk),
            "catalog:CatalogPage",
            CommitId(package.IdLower + lower + upper, latest),
            Timestamp(latest),
            chunk.Count,
            inline ? chunk.Select(e => BuildLeafItem(urls, package, e.Payload!)).ToList() : null,
            urls.RegistrationIndex,
            lower,
            upper);
    }

    private static RegistrationLeafItem BuildLeafItem(UrlSet urls, Package package, PackageVersion v)
    {
        var leaf = urls.Leaf(v.NormalizedVersionLower);
        var catalog = urls.Catalog(v.NormalizedVersionLower);
        var content = urls.Content(v.NormalizedVersion);
        var groups = v.Dependencies
            .GroupBy(d => d.TargetFramework, StringComparer.OrdinalIgnoreCase)
            .Select(g => new DependencyGroup(
                $"{leaf}#dependencygroup/{(g.Key.Length == 0 ? "any" : g.Key)}",
                "PackageDependencyGroup",
                g.Key,
                g.Where(d => d.Id is not null)
                    .Select(d => new Dependency(
                        $"{leaf}#dependencygroup/{(g.Key.Length == 0 ? "any" : g.Key)}/{d.Id!.ToLowerInvariant()}",
                        "PackageDependency",
                        d.Id!,
                        d.VersionRange.Length == 0 ? "(, )" : d.VersionRange,
                        urls.RegistrationIndexOf(d.Id!)))
                    .ToList()))
            .ToList();

        return new RegistrationLeafItem(
            leaf,
            "Package",
            CommitId(package.IdLower + v.NormalizedVersionLower, v.LastUpdatedUtc),
            Timestamp(v.LastUpdatedUtc),
            new CatalogEntry
            {
                Id = catalog,
                Authors = v.Authors,
                DependencyGroups = groups,
                Description = v.Description,
                IconUrl = v.IconUrl,
                PackageId = package.Id,
                Language = v.Language,
                LicenseExpression = v.LicenseExpression,
                LicenseUrl = v.LicenseUrl,
                Listed = v.Listed,
                MinClientVersion = v.MinClientVersion,
                PackageContent = content,
                ProjectUrl = v.ProjectUrl,
                Published = v.Listed ? Timestamp(v.PublishedUtc) : UnlistedPublished,
                RequireLicenseAcceptance = v.RequireLicenseAcceptance,
                Summary = v.Summary,
                Tags = SplitTags(v.Tags),
                Title = v.Title,
                Version = FullVersion(v),
            },
            content,
            urls.RegistrationIndex);
    }

    /// <summary>The normalised version plus build metadata, as nuget.org shows it.</summary>
    private static string FullVersion(PackageVersion v) =>
        NuGetVersion.TryParse(v.OriginalVersion, out var parsed) ? parsed.ToFullString() : v.NormalizedVersion;

    private static List<string> SplitTags(string tags) =>
        tags.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static List<string> SplitAuthors(string authors) =>
        authors.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static List<string> SplitPackageTypes(string packageTypes) =>
        packageTypes.Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static string Timestamp(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);

    /// <summary>A stable pseudo commit id, so identical content produces identical responses.</summary>
    private static string CommitId(string seed, DateTime utc) =>
        new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(seed + "|" + utc.Ticks.ToString(CultureInfo.InvariantCulture)))[..16]).ToString();

    private static int IntParam(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static bool BoolParam(string? value) =>
        bool.TryParse(value, out var parsed) && parsed;

    private static bool IsSemVer2Level(string? value) =>
        NuGetVersion.TryParse(value, out var level) && level.Major >= 2;

    private sealed record UrlSet(string FeedUrl, string IdLower)
    {
        public string RegistrationIndex => RegistrationIndexOf(IdLower);

        public string RegistrationIndexOf(string id) => $"{FeedUrl}/v3/registration/{id.ToLowerInvariant()}/index.json";

        public string Leaf(string versionLower) => $"{FeedUrl}/v3/registration/{IdLower}/{versionLower}.json";

        public string Catalog(string versionLower) => $"{FeedUrl}/v3/catalog/{IdLower}/{versionLower}.json";

        /// <summary>
        /// The download address, with the version's prerelease label in its own case. The flat container answers any case,
        /// but PSResourceGet looks for the version inside this address with a case-sensitive match, so a lower-cased
        /// <c>1.0.1-prev007</c> could not be installed as <c>1.0.1-PREv007</c> (PSResourceGet #1787).
        /// </summary>
        public string Content(string version) => $"{FeedUrl}/v3/flatcontainer/{IdLower}/{version}/{IdLower}.{version}.nupkg";

        public string Page(IReadOnlyList<VersionListEntry<PackageVersion>> chunk) =>
            $"{FeedUrl}/v3/registration/{IdLower}/page/{chunk[0].Version.ToNormalizedString().ToLowerInvariant()}/{chunk[^1].Version.ToNormalizedString().ToLowerInvariant()}.json";
    }
}
