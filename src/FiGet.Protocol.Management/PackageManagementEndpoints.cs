using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using FiGet.Application.Connectors;
using FiGet.Application.Packages;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Domain.Versions;
using FiGet.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace FiGet.Protocol.Management;

/// <summary>
/// The packages part of the management API of the server being replaced, under <c>/api/packages/{feed}</c>:
/// what scripts call to find the versions a feed holds, find the latest, and clean up. Shapes are the
/// reference client's; docs/protocol-management.md records them and what FiGet does differently.
/// </summary>
public static class PackageManagementEndpoints
{
    /// <summary>
    /// What the reference server calls the publisher of a version it cached from a connector. FiGet does not
    /// record who pushed a version, so a pushed one has no publisher at all.
    /// </summary>
    public const string CachedPublisher = "SYSTEM";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static IEndpointRouteBuilder MapPackageManagement(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/packages/{feed}");

        // One template only: "" and "/" both normalise to the group prefix, and mapping both is an ambiguous match.
        group.MapGet("", FeedInfoAsync);
        group.MapGet("/versions", VersionsAsync);
        group.MapGet("/latest", LatestAsync);
        group.MapGet("/download", DownloadAsync);
        group.MapPost("/delete", DeleteAsync).DisableAntiforgery();
        group.MapPost("/status", StatusAsync).DisableAntiforgery();
        group.MapPut("/upload", UploadAsync).DisableAntiforgery();
        group.MapPut("/upload/{fileName}", UploadAsync).DisableAntiforgery();
        group.MapPost("/upload", UploadAsync).DisableAntiforgery();
        return app;
    }

    /// <summary>
    /// What kind of feed this is. The reference client asks before it downloads or deletes, to learn whether
    /// packages of this type take qualifiers; without an answer it gives up with a 404 before it tries.
    /// </summary>
    private static async Task<IResult> FeedInfoAsync(HttpContext http, string feed, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, TokenScopes.Read, cancellationToken);
        return error ?? Results.Json(new { id = request!.Feed.Key, name = request.Feed.Name, feedType = "nuget", packageType = "nuget" }, Json);
    }

    /// <summary>
    /// Every version the feed stores, optionally of one package and one version. Unlisted versions are
    /// included, marked <c>listed: false</c>; a clean-up script needs to see those most of all.
    /// </summary>
    private static async Task<IResult> VersionsAsync(HttpContext http, string feed, string? name, string? version, IPackageStore store, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, TokenScopes.Read, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var packages = await PackagesAsync(request!.Feed, name, store, cancellationToken);
        var versionLower = string.IsNullOrWhiteSpace(version) ? null : PackageIngestionService.NormalizeLower(version);
        if (!string.IsNullOrWhiteSpace(version) && versionLower is null)
        {
            return Results.Json(Array.Empty<VersionJson>(), Json);
        }

        var rows = packages
            .SelectMany(package => package.Versions
                .Where(v => versionLower is null || v.NormalizedVersionLower == versionLower)
                .OrderByDescending(v => NuGet.Versioning.NuGetVersion.Parse(v.NormalizedVersion))
                .Select(v => Describe(package, v)))
            .ToList();
        return Results.Json(rows, Json);
    }

    /// <summary>
    /// The latest version of each package, or of the one named: the highest listed version, or the highest
    /// listed stable one with <c>stableOnly=true</c>. A list either way, as the reference client reads it.
    /// Decided by the same rule every NuGet listing uses, so this cannot disagree with what a client installs.
    /// </summary>
    private static async Task<IResult> LatestAsync(HttpContext http, string feed, string? name, string? stableOnly, IPackageStore store, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, TokenScopes.Read, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var stable = string.Equals(stableOnly?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        var rows = new List<VersionJson>();
        foreach (var package in await PackagesAsync(request!.Feed, name, store, cancellationToken))
        {
            var latest = VersionListBuilder.BuildLocal(package.Versions, includeSemVer2: true)
                .FirstOrDefault(e => stable ? e.IsLatestVersion : e.IsAbsoluteLatestVersion);
            if (latest?.Payload is { } row)
            {
                rows.Add(Describe(package, row));
            }
        }

        return Results.Json(rows, Json);
    }

    private static async Task<IResult> DownloadAsync(HttpContext http, string feed, string? name, string? version, IPackageStore store, IPackageStorage storage, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, TokenScopes.Read, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (await FindAsync(request!.Feed, name, version, store, cancellationToken) is not { } row
            || await storage.OpenPackageAsync(new PackageStorageKey(request.Feed.Key, row.Package!.IdLower, row.NormalizedVersionLower), cancellationToken) is not { } stream)
        {
            return NotFound(name, version);
        }

        return Results.File(stream, "application/zip", $"{row.Package.Id}.{row.NormalizedVersion}.nupkg", enableRangeProcessing: true);
    }

    /// <summary>
    /// Removes the version outright, files included. Deliberately not the feed's delete behaviour, which may
    /// only unlist: this API has a separate call for listing, so "delete" here means delete.
    /// </summary>
    private static async Task<IResult> DeleteAsync(HttpContext http, string feed, string? name, string? version, IPackageStore store, PackageIngestionService ingestion, AuditLog audit, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, TokenScopes.Delete, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (await FindAsync(request!.Feed, name, version, store, cancellationToken) is not { } row
            || !await ingestion.PurgeAsync(request.Feed, row.Package!.Id, row.NormalizedVersion, cancellationToken))
        {
            return NotFound(name, version);
        }

        audit.Record(http, "package.delete", row.Package.Id, $"feed={request.Feed.Name} version={row.NormalizedVersion} api=management");
        return Results.Ok();
    }

    /// <summary>
    /// Lists or unlists a version. The reference API's other two switches - a download override and
    /// deprecation - have nothing in FiGet to act on, and are refused rather than silently ignored.
    /// </summary>
    private static async Task<IResult> StatusAsync(HttpContext http, string feed, string? name, string? version, IPackageStore store, PackageIngestionService ingestion, AuditLog audit, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, TokenScopes.Delete, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        StatusJson? status;
        try
        {
            status = await JsonSerializer.DeserializeAsync<StatusJson>(http.Request.Body, Json, cancellationToken);
        }
        catch (JsonException)
        {
            status = null;
        }

        if (status is null)
        {
            return Results.Text("The body must be a JSON package status.", "text/plain", statusCode: StatusCodes.Status400BadRequest);
        }

        if (status.Allow is not null || status.Deprecated == true)
        {
            return Results.Text("Download overrides and deprecation are not supported; only listed can be set.", "text/plain", statusCode: StatusCodes.Status400BadRequest);
        }

        if (await FindAsync(request!.Feed, name, version, store, cancellationToken) is not { } row)
        {
            return NotFound(name, version);
        }

        if (status.Listed is { } listed && listed != row.Listed)
        {
            var changed = listed
                ? await ingestion.RelistAsync(request.Feed, row.Package!.Id, row.NormalizedVersion, cancellationToken)
                : await ingestion.UnlistAsync(request.Feed, row.Package!.Id, row.NormalizedVersion, cancellationToken);
            if (changed)
            {
                audit.Record(http, listed ? "package.relist" : "package.unlist", row.Package.Id, $"feed={request.Feed.Name} version={row.NormalizedVersion} api=management");
            }
        }

        return Results.Ok();
    }

    /// <summary>Pushes a package: the body is the nupkg, as the reference client sends it. The file name in the URL is not needed.</summary>
    private static async Task<IResult> UploadAsync(HttpContext http, string feed, PackageIngestionService ingestion, ConnectorService connector, IOptions<UploadOptions> upload, AuditLog audit, CancellationToken cancellationToken)
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
            return Results.Text(ex.Message, "text/plain", statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        if (file is null)
        {
            return Results.Text("The request does not contain a package.", "text/plain", statusCode: StatusCodes.Status400BadRequest);
        }

        await using (file)
        {
            var result = await ingestion.PushAsync(request!.Feed, file, cancellationToken);
            if (result.Outcome is PushOutcome.Created or PushOutcome.Replaced)
            {
                audit.Record(http, "package.push", result.Id ?? "", $"feed={request.Feed.Name} version={result.Version} outcome={result.Outcome} api=management");
                await PushWarning.AddAsync(http, connector, request.Feed, result.Id, cancellationToken);
            }

            return result.Outcome switch
            {
                PushOutcome.Created or PushOutcome.Replaced => Results.StatusCode(StatusCodes.Status201Created),
                PushOutcome.Conflict => Results.Text(result.Message, "text/plain", statusCode: StatusCodes.Status409Conflict),
                _ => Results.Text(result.Message, "text/plain", statusCode: StatusCodes.Status400BadRequest),
            };
        }
    }

    private static async Task<IReadOnlyList<Package>> PackagesAsync(Feed feed, string? name, IPackageStore store, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return await store.ListPackagesAsync(feed.Key, cancellationToken);
        }

        var package = await store.GetPackageAsync(feed.Key, name.Trim().ToLowerInvariant(), includeDependencies: false, cancellationToken);
        return package is null ? [] : [package];
    }

    private static async Task<PackageVersion?> FindAsync(Feed feed, string? name, string? version, IPackageStore store, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version) || PackageIngestionService.NormalizeLower(version) is not { } versionLower)
        {
            return null;
        }

        var package = await store.GetPackageAsync(feed.Key, name.Trim().ToLowerInvariant(), includeDependencies: false, cancellationToken);
        return package?.Versions.FirstOrDefault(v => v.NormalizedVersionLower == versionLower);
    }

    private static IResult NotFound(string? name, string? version) =>
        Results.Text($"Package {name} {version} was not found in this feed.", "text/plain", statusCode: StatusCodes.Status404NotFound);

    private static VersionJson Describe(Package package, PackageVersion version) => new(
        $"pkg:nuget/{package.Id}@{version.NormalizedVersion}",
        package.Id,
        version.NormalizedVersion,
        package.Versions.Sum(v => v.Downloads),
        version.Downloads,
        version.PublishedUtc,
        version.Origin == PackageOrigin.Cached ? CachedPublisher : null,
        version.Size,
        version.Listed,
        Sha512Hex(version),
        Deprecated: false);

    /// <summary>The package hash as the API reports hashes, in hexadecimal; NuGet stores it in base64.</summary>
    private static string? Sha512Hex(PackageVersion version)
    {
        if (!string.Equals(version.HashAlgorithm, "SHA512", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(version.Hash))
        {
            return null;
        }

        try
        {
            return Convert.ToHexStringLower(Convert.FromBase64String(version.Hash));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>One version, named as the reference client's model names it.</summary>
    private sealed record VersionJson(
        [property: JsonPropertyName("purl")] string Purl,
        string Name,
        string Version,
        long TotalDownloads,
        long Downloads,
        DateTime Published,
        string? PublishedBy,
        long Size,
        bool Listed,
        [property: JsonPropertyName("sha512")] string? Sha512,
        bool Deprecated);

    private sealed class StatusJson
    {
        public bool? Listed { get; set; }

        public bool? Allow { get; set; }

        public bool? Deprecated { get; set; }

        public string? DeprecationReason { get; set; }
    }
}
