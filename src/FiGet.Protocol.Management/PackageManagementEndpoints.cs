using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using FiGet.Application.Connectors;
using FiGet.Application.Packages;
using FiGet.Application.Ports;
using FiGet.Application.Reports;
using FiGet.Domain.Entities;
using FiGet.Domain.Versions;
using FiGet.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
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
        group.MapGet("/changes", ChangesAsync);
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

    /// <summary>
    /// What changed among the packages this feed holds: pushed here, cached from an upstream, or offered by an
    /// upstream and not fetched yet. FiGet's own route, not one the reference server has, so no client expects a
    /// different shape.
    ///
    /// Unlike <c>/versions</c> it reports versions this feed does not hold, because "the gallery has something newer
    /// than what you are running" is the question it exists to answer. It never reaches an upstream to answer it: the
    /// stored catalogues are what it reads, and the sweep is what keeps those current.
    /// </summary>
    private static async Task<IResult> ChangesAsync(
        HttpContext http,
        string feed,
        string? days,
        ChangeReportService reports,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, TokenScopes.Read, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        // Read as text and clamped, never refused: bound as a number, the framework answers 400 before this handler
        // runs, and a hand-edited address should at worst show a different window.
        var asked = int.TryParse(days, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : (int?)null;
        var report = await reports.BuildAsync(request!.Feed, ChangeReportService.Days(asked), time.GetUtcNow().UtcDateTime, cancellationToken);
        return Results.Json(ChangesJson.Of(report), Json);
    }

    private static async Task<IResult> DownloadAsync(HttpContext http, string feed, string? name, string? version, IPackageStore store, IPackageStorage storage, ConnectorService connector, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAsync(http, feed, TokenScopes.Read, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (await FindForDownloadAsync(request!.Feed, name, version, store, cancellationToken) is not { } row)
        {
            return NotFound(name, version);
        }

        var key = new PackageStorageKey(request.Feed.Key, row.Package!.IdLower, row.NormalizedVersionLower);
        var stream = await storage.OpenPackageAsync(key, cancellationToken);
        if (stream is null && await connector.RepairMissingFileAsync(request.Feed, row.Package.Id, row, cancellationToken) is not null)
        {
            stream = await storage.OpenPackageAsync(key, cancellationToken);
        }

        if (stream is null)
        {
            return NotFound(name, version);
        }

        // Counted like a v2 or v3 download: without it a copy a nightly script fetches here every day looked unused, and
        // retention's KeepIfUsedWithinDays could prune it.
        await store.IncrementDownloadsAsync(row.Key, http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime, cancellationToken);

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

        if (await FindForDownloadAsync(request!.Feed, name, version, store, cancellationToken) is not { } row)
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

    /// <summary>
    /// The version a download names, where the reference API also accepts <c>latest</c> (the version <c>/latest</c> reports
    /// with stableOnly) and <c>latest-unstable</c> (the highest listed, prerelease included). Download only: deleting or
    /// relisting "the latest" by a word would act on whichever version that happens to be at the time.
    /// </summary>
    private static async Task<PackageVersion?> FindForDownloadAsync(Feed feed, string? name, string? version, IPackageStore store, CancellationToken cancellationToken)
    {
        var word = version?.Trim();
        var stable = string.Equals(word, "latest", StringComparison.OrdinalIgnoreCase);
        if (!stable && !string.Equals(word, "latest-unstable", StringComparison.OrdinalIgnoreCase))
        {
            return await FindAsync(feed, name, version, store, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(name) || await store.GetPackageAsync(feed.Key, name.Trim().ToLowerInvariant(), includeDependencies: false, cancellationToken) is not { } package)
        {
            return null;
        }

        var latest = VersionListBuilder.BuildLocal(package.Versions, includeSemVer2: true)
            .FirstOrDefault(e => stable ? e.IsLatestVersion : e.IsAbsoluteLatestVersion)?.Payload;
        if (latest is not null)
        {
            latest.Package ??= package;
        }

        return latest;
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

    /// <summary>A change report, as the changes route answers it.</summary>
    private sealed record ChangesJson(string Feed, DateTime From, DateTime To, int Days, bool Truncated, IReadOnlyList<ChangeJson> Changes)
    {
        public static ChangesJson Of(ChangeReport report) => new(
            report.Feed,
            report.FromUtc,
            report.ToUtc,
            (int)Math.Round((report.ToUtc - report.FromUtc).TotalDays),
            report.StoppedAtLimit,
            [.. report.Changes.Select(ChangeJson.Of)]);
    }

    /// <summary>
    /// One row of a change report. <c>kind</c> is written by hand rather than from the enum's name: the wire contract
    /// must not move the day somebody renames a member.
    /// </summary>
    private sealed record ChangeJson(
        string Name,
        string Version,
        string? PreviousVersion,
        bool Breaking,
        DateTime Published,
        string Kind,
        string? Upstream,
        string? Authors,
        string? ReleaseNotes)
    {
        public static ChangeJson Of(PackageChange change) => new(
            change.Id,
            change.Version,
            change.PreviousVersion,
            change.Breaking,
            change.PublishedUtc,
            change.Kind switch
            {
                PackageChangeKind.Pushed => "pushed",
                PackageChangeKind.Cached => "cached",
                _ => "upstream",
            },
            change.Upstream.Length == 0 ? null : change.Upstream,
            change.Authors.Length == 0 ? null : change.Authors,
            change.ReleaseNotes.Length == 0 ? null : change.ReleaseNotes);
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
