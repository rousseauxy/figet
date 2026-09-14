using System.Globalization;
using FiGet.Application.Assets;
using FiGet.Application.Ports;
using FiGet.Domain.Assets;
using FiGet.Domain.Entities;
using FiGet.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace FiGet.Protocol.Assets;

/// <summary>
/// The ways a file reaches an asset directory other than one request with the file as its body: in parts, in
/// an archive, or from another server. And the one way a folder leaves it whole.
/// </summary>
public static partial class AssetEndpoints
{
    /// <summary>
    /// The header that asks the server to fetch the file itself. Not part of the API being replaced; it is how a
    /// vendor installer is pinned without downloading it to a workstation first.
    /// </summary>
    public const string SourceUrlHeader = "X-Source-Url";

    private const int BufferSize = 81920;

    /// <summary>
    /// <c>?multipart=upload</c> stores one part, <c>?multipart=complete</c> joins them. Parameter names and
    /// meanings are the reference client's; both answer 200, as documented.
    /// </summary>
    private static async Task<IResult> MultipartAsync(HttpContext http, Feed feed, AssetPath path, string step, AssetService assets, UploadOptions upload, AuditLog audit, CancellationToken cancellationToken)
    {
        var query = http.Request.Query;
        var id = query["id"].ToString();

        if (step.Equals("complete", StringComparison.OrdinalIgnoreCase))
        {
            var outcome = await assets.CompleteUploadAsync(feed, path, id, AssetContentTypes.Resolve(http.Request.ContentType, path.Name), upload.MaxAssetSizeBytes, cancellationToken);
            if (outcome is AssetOutcome.Created or AssetOutcome.Replaced)
            {
                audit.Record(http, "asset.upload", path.Value, $"directory={feed.Name} outcome={outcome} multipart=True");
                return Results.Ok();
            }

            return outcome == AssetOutcome.InvalidUpload
                ? Results.Text("The upload is incomplete: every part from 0 to totalParts-1 must be stored, end to end, adding up to totalSize.", "text/plain", statusCode: StatusCodes.Status400BadRequest)
                : ToResult(outcome);
        }

        if (!step.Equals("upload", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Text("multipart must be 'upload' or 'complete'.", "text/plain", statusCode: StatusCodes.Status400BadRequest);
        }

        if (!int.TryParse(query["index"], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            || !long.TryParse(query["offset"], NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
            || !long.TryParse(query["totalSize"], NumberStyles.None, CultureInfo.InvariantCulture, out var totalSize)
            || !long.TryParse(query["partSize"], NumberStyles.None, CultureInfo.InvariantCulture, out var partSize)
            || !int.TryParse(query["totalParts"], NumberStyles.None, CultureInfo.InvariantCulture, out var totalParts))
        {
            return Results.Text("A part needs id, index, offset, totalSize, partSize and totalParts.", "text/plain", statusCode: StatusCodes.Status400BadRequest);
        }

        RaiseBodyLimit(http, upload.MaxAssetSizeBytes);
        try
        {
            var outcome = await assets.UploadPartAsync(feed, id, index, offset, totalSize, partSize, totalParts, http.Request.Body, upload.MaxAssetSizeBytes, cancellationToken);
            return outcome switch
            {
                AssetOutcome.Updated => Results.Ok(),
                AssetOutcome.InvalidUpload => Results.Text("The part's numbers do not add up, or its body is not partSize bytes.", "text/plain", statusCode: StatusCodes.Status400BadRequest),
                _ => ToResult(outcome),
            };
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return ToResult(AssetOutcome.TooLarge);
        }
    }

    /// <summary>
    /// Fetches <paramref name="source"/> and stores it at <paramref name="path"/>. Shared by the API header and
    /// the page's form. The error, when there is one, is written for the person who asked.
    /// </summary>
    public static async Task<(AssetOutcome Outcome, string? Error)> FetchAsync(
        Feed feed,
        AssetPath path,
        string source,
        AssetWriteMode mode,
        AssetService assets,
        IRemoteFileSource remote,
        UploadOptions upload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(upload);
        if (!Uri.TryCreate(source, UriKind.Absolute, out var url))
        {
            return (AssetOutcome.InvalidPath, "That is not an absolute URL.");
        }

        try
        {
            await using var file = await remote.OpenAsync(url, cancellationToken);
            var outcome = await assets.WriteAsync(
                feed,
                path,
                file.Content,
                AssetContentTypes.Resolve(file.ContentType, path.Name),
                mode,
                upload.MaxAssetSizeBytes,
                cancellationToken);
            return (outcome, null);
        }
        catch (RemoteFetchException ex)
        {
            return (ex.Refused ? AssetOutcome.InvalidPath : AssetOutcome.NotFound, ex.Message);
        }
        catch (IOException ex)
        {
            return (AssetOutcome.NotFound, "The download broke off: " + ex.Message);
        }
    }

    private static async Task<IResult> FetchFromHeaderAsync(HttpContext http, Feed feed, AssetPath path, string source, AssetWriteMode mode, AssetService assets, UploadOptions upload, AuditLog audit, CancellationToken cancellationToken)
    {
        var remote = http.RequestServices.GetRequiredService<IRemoteFileSource>();
        var (outcome, error) = await FetchAsync(feed, path, source, mode, assets, remote, upload, cancellationToken);
        if (error is not null)
        {
            // Refused is the caller's request being declined; anything else is the remote server failing.
            return Results.Text(error, "text/plain", statusCode: outcome == AssetOutcome.InvalidPath ? StatusCodes.Status400BadRequest : StatusCodes.Status502BadGateway);
        }

        if (outcome is AssetOutcome.Created or AssetOutcome.Replaced)
        {
            audit.Record(http, "asset.fetch", path.Value, $"directory={feed.Name} url={source} outcome={outcome}");
        }

        return ToResult(outcome);
    }

    private static async Task<IResult> ImportEndpointAsync(
        HttpContext http,
        string directory,
        string? path,
        string? format,
        string? overwrite,
        AssetArchiveService archives,
        IOptions<UploadOptions> upload,
        AuditLog audit,
        CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAssetsAsync(http, directory, TokenScopes.Push, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (!AssetPath.TryParse(path, out var folder))
        {
            return Results.Text("The path is not a valid folder path.", "text/plain", statusCode: StatusCodes.Status400BadRequest);
        }

        if (ParseFormat(format) is not { } parsedFormat)
        {
            return Results.Text("format must be zip or tgz.", "text/plain", statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await ImportAsync(http, request!.Feed, folder, parsedFormat, Flag(overwrite), archives, upload.Value, cancellationToken);
        audit.Record(http, "asset.import", folder.IsRoot ? "/" : folder.Value, $"directory={request.Feed.Name} imported={result.Imported} skipped={result.Skipped} failed={result.Failed.Count}");
        return ImportResult(result);
    }

    /// <summary>
    /// Imports the request body. A zip needs to seek - its directory is at the end - so it is spooled to a
    /// temporary file first, under the import limit; a tgz is read straight off the request.
    /// </summary>
    public static async Task<AssetImportResult> ImportAsync(
        HttpContext http,
        Feed feed,
        AssetPath folder,
        AssetArchiveFormat format,
        bool overwrite,
        AssetArchiveService archives,
        UploadOptions upload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(archives);
        ArgumentNullException.ThrowIfNull(upload);
        RaiseBodyLimit(http, upload.MaxImportSizeBytes);

        try
        {
            if (format == AssetArchiveFormat.TarGzip)
            {
                return await archives.ImportAsync(feed, folder, http.Request.Body, format, overwrite, upload.MaxAssetSizeBytes, upload.MaxImportSizeBytes, name => AssetContentTypes.Resolve(null, name), cancellationToken);
            }

            await using var spooled = TempFile(upload);
            var buffer = new byte[BufferSize];
            int read;
            while ((read = await http.Request.Body.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (spooled.Length + read > upload.MaxImportSizeBytes)
                {
                    return new AssetImportResult(0, 0, [], TooLarge: true);
                }

                await spooled.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            spooled.Position = 0;
            return await archives.ImportAsync(feed, folder, spooled, format, overwrite, upload.MaxAssetSizeBytes, upload.MaxImportSizeBytes, name => AssetContentTypes.Resolve(null, name), cancellationToken);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return new AssetImportResult(0, 0, [], TooLarge: true);
        }
    }

    /// <summary>200 with what happened; 413 when the archive went past its limit; 400 when nothing could be read.</summary>
    public static IResult ImportResult(AssetImportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var body = new { imported = result.Imported, skipped = result.Skipped, failed = result.Failed };
        if (result.TooLarge)
        {
            return Results.Json(body, Json, statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        return result.Imported == 0 && result.Skipped == 0 && result.Failed.Count > 0
            ? Results.Json(body, Json, statusCode: StatusCodes.Status400BadRequest)
            : Results.Json(body, Json);
    }

    private static async Task<IResult> ExportEndpointAsync(
        HttpContext http,
        string directory,
        string? path,
        string? format,
        string? recursive,
        AssetArchiveService archives,
        IOptions<UploadOptions> upload,
        CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAssetsAsync(http, directory, TokenScopes.Read, cancellationToken, listing: true);
        if (error is not null)
        {
            return error;
        }

        return await ExportAsync(request!.Feed, path, format, Flag(recursive), archives, upload.Value, cancellationToken);
    }

    /// <summary>
    /// The archive is built in a temporary file and then sent, rather than written straight to the response:
    /// the archive writers are synchronous in places and the server refuses synchronous writes to a response,
    /// and a finished file can be sent with its length and resumed with Range.
    /// </summary>
    public static async Task<IResult> ExportAsync(Feed feed, string? path, string? format, bool recursive, AssetArchiveService archives, UploadOptions upload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(archives);
        ArgumentNullException.ThrowIfNull(upload);
        if (!AssetPath.TryParse(path, out var folder))
        {
            return Results.Text("The path is not a valid folder path.", "text/plain", statusCode: StatusCodes.Status400BadRequest);
        }

        if (ParseFormat(format) is not { } parsedFormat)
        {
            return Results.Text("format must be zip or tgz.", "text/plain", statusCode: StatusCodes.Status400BadRequest);
        }

        var archive = TempFile(upload);
        try
        {
            if (!await archives.ExportAsync(feed, folder, recursive, parsedFormat, archive, cancellationToken))
            {
                await archive.DisposeAsync();
                return Results.Text(FileNotFound, "text/plain", statusCode: StatusCodes.Status404NotFound);
            }

            archive.Position = 0;
            var name = (folder.IsRoot ? feed.Name : folder.Name) + (parsedFormat == AssetArchiveFormat.Zip ? ".zip" : ".tar.gz");
            return Results.File(archive, parsedFormat == AssetArchiveFormat.Zip ? "application/zip" : "application/gzip", name, enableRangeProcessing: true);
        }
        catch
        {
            await archive.DisposeAsync();
            throw;
        }
    }

    public static AssetArchiveFormat? ParseFormat(string? format) => format?.Trim().ToLowerInvariant() switch
    {
        "zip" => AssetArchiveFormat.Zip,
        "tgz" or "tar.gz" => AssetArchiveFormat.TarGzip,
        _ => null,
    };

    private static void RaiseBodyLimit(HttpContext http, long limit)
    {
        // Kestrel refuses anything over 30 MB before a handler sees it unless the limit is raised here.
        var sizeFeature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = limit;
        }
    }

    private static FileStream TempFile(UploadOptions upload)
    {
        var directory = string.IsNullOrWhiteSpace(upload.TempPath) ? Path.GetTempPath() : upload.TempPath;
        Directory.CreateDirectory(directory);
        return new FileStream(
            Path.Combine(directory, "figet-archive-" + Guid.NewGuid().ToString("N") + ".tmp"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
    }
}
