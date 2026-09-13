using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using FiGet.Application.Assets;
using FiGet.Domain.Assets;
using FiGet.Domain.Entities;
using FiGet.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace FiGet.Protocol.Assets;

/// <summary>
/// Asset directories under <c>/endpoints/{directory}</c>, shaped like the server being replaced so that
/// scripts and <c>win_get_url</c> tasks keep working unchanged. The contract is written down in
/// docs/protocol-assets.md, with where each rule came from.
/// </summary>
public static partial class AssetEndpoints
{
    /// <summary>The body the server being replaced answers a missing file with, recorded from it.</summary>
    public const string FileNotFound = "The specified asset was not found.";

    /// <summary>The body the server being replaced answers missing metadata with, recorded from it.</summary>
    public const string MetadataNotFound = "Asset not found.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static IEndpointRouteBuilder MapAssetEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/endpoints/{directory}");
        group.MapMethods("/content/{**path}", [HttpMethods.Get, HttpMethods.Head], DownloadAsync);
        group.MapMethods("/content/{**path}", [HttpMethods.Put, HttpMethods.Post, HttpMethods.Patch], UploadAsync).DisableAntiforgery();
        group.MapDelete("/content/{**path}", DeleteFileAsync);
        group.MapGet("/dir/{**path}", ListAsync);
        group.MapPost("/dir/{**path}", CreateFolderAsync).DisableAntiforgery();
        group.MapPost("/delete/{**path}", DeleteAsync).DisableAntiforgery();
        group.MapGet("/metadata/{**path}", GetMetadataAsync);
        group.MapPost("/metadata/{**path}", SetMetadataAsync).DisableAntiforgery();
        group.MapPost("/import/{**path}", ImportEndpointAsync).DisableAntiforgery();
        group.MapGet("/export/{**path}", ExportEndpointAsync);
        return app;
    }

    /// <summary>The download URL of a file, each segment escaped.</summary>
    public static string ContentUrl(HttpContext http, string directory, string path) =>
        $"{PublicUrls.Base(http)}/endpoints/{Uri.EscapeDataString(directory)}/content/{EscapePath(path)}";

    public static string EscapePath(string path) =>
        string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    private static async Task<IResult> DownloadAsync(HttpContext http, string directory, string? path, AssetService assets, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAssetsAsync(http, directory, TokenScopes.Read, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (!AssetPath.TryParse(path, out var parsed)
            || await assets.FindAsync(request!.Feed, parsed, cancellationToken) is not { IsDirectory: false } file
            || await assets.OpenAsync(request.Feed, file, cancellationToken) is not { } stream)
        {
            return Results.Text(FileNotFound, "text/plain", statusCode: StatusCodes.Status404NotFound);
        }

        ApplyCacheHeader(http, file);

        // Results.File does the parts a download client relies on: Range for a resumed transfer,
        // If-None-Match and If-Modified-Since for a cache that already has it, and no body on HEAD.
        return Results.File(
            stream,
            file.ContentType ?? AssetContentTypes.Fallback,
            lastModified: new DateTimeOffset(file.ModifiedUtc),
            entityTag: file.Sha256 is null ? null : new EntityTagHeaderValue($"\"{file.Sha256}\""),
            enableRangeProcessing: true);
    }

    private static async Task<IResult> UploadAsync(
        HttpContext http,
        string directory,
        string? path,
        AssetService assets,
        IOptions<UploadOptions> upload,
        AuditLog audit,
        CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAssetsAsync(http, directory, TokenScopes.Push, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (!AssetPath.TryParse(path, out var parsed) || parsed.IsRoot)
        {
            return Results.Text("The path is not a valid file path.", "text/plain", statusCode: StatusCodes.Status400BadRequest);
        }

        var mode = http.Request.Method switch
        {
            "PUT" => AssetWriteMode.CreateOnly,
            "PATCH" => AssetWriteMode.ReplaceOnly,
            _ => AssetWriteMode.CreateOrReplace,
        };

        if (HttpMethods.IsPost(http.Request.Method) && http.Request.Query["multipart"].ToString() is { Length: > 0 } step)
        {
            return await MultipartAsync(http, request!.Feed, parsed, step, assets, upload.Value, audit, cancellationToken);
        }

        if (http.Request.Headers[SourceUrlHeader].ToString() is { Length: > 0 } source)
        {
            return await FetchFromHeaderAsync(http, request!.Feed, parsed, source.Trim(), mode, assets, upload.Value, audit, cancellationToken);
        }

        var outcome = await WriteAsync(http, request!.Feed, parsed, http.Request.Body, http.Request.ContentType, mode, assets, upload.Value, cancellationToken);
        if (outcome is AssetOutcome.Created or AssetOutcome.Replaced)
        {
            audit.Record(http, "asset.upload", parsed.Value, $"directory={request.Feed.Name} outcome={outcome}");
        }

        return ToResult(outcome);
    }

    /// <summary>
    /// Stores a request body at a path under the asset size limit. Shared with the upload page, so a file
    /// dropped in a browser is stored by exactly the code a script's upload is.
    /// </summary>
    public static async Task<AssetOutcome> WriteAsync(
        HttpContext http,
        Feed feed,
        AssetPath path,
        Stream body,
        string? contentType,
        AssetWriteMode mode,
        AssetService assets,
        UploadOptions upload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentNullException.ThrowIfNull(assets);

        RaiseBodyLimit(http, upload.MaxAssetSizeBytes);

        try
        {
            return await assets.WriteAsync(
                feed,
                path,
                body,
                AssetContentTypes.Resolve(contentType, path.Name),
                mode,
                upload.MaxAssetSizeBytes,
                cancellationToken);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return AssetOutcome.TooLarge;
        }
    }

    private static async Task<IResult> DeleteFileAsync(HttpContext http, string directory, string? path, AssetService assets, AuditLog audit, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAssetsAsync(http, directory, TokenScopes.Delete, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (!AssetPath.TryParse(path, out var parsed) || parsed.IsRoot)
        {
            return Results.Text("The path is not a valid file path.", "text/plain", statusCode: StatusCodes.Status400BadRequest);
        }

        var existing = await assets.FindAsync(request!.Feed, parsed, cancellationToken);
        if (existing is null)
        {
            // Documented as not an error: the file the caller wanted gone is gone.
            return Results.Ok();
        }

        if (existing.IsDirectory)
        {
            return Results.Text("The path is a folder; delete folders through /delete.", "text/plain", statusCode: StatusCodes.Status400BadRequest);
        }

        var outcome = await assets.DeleteAsync(request.Feed, parsed, recursive: false, cancellationToken);
        if (outcome == AssetOutcome.Deleted)
        {
            audit.Record(http, "asset.delete", parsed.Value, $"directory={request.Feed.Name}");
        }

        return outcome is AssetOutcome.Deleted or AssetOutcome.NotFound ? Results.Ok() : ToResult(outcome);
    }

    private static async Task<IResult> ListAsync(HttpContext http, string directory, string? path, string? recursive, AssetService assets, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAssetsAsync(http, directory, TokenScopes.Read, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (!AssetPath.TryParse(path, out var parsed))
        {
            return Results.Text("The path is not a valid folder path.", "text/plain", statusCode: StatusCodes.Status400BadRequest);
        }

        // A folder that does not exist lists as empty rather than 404, as documented and as recorded.
        var items = await assets.ListAsync(request!.Feed, parsed, Flag(recursive), cancellationToken);
        return Results.Json(items.Select(item => Describe(http, request.Feed, item)).ToList(), Json);
    }

    private static async Task<IResult> CreateFolderAsync(HttpContext http, string directory, string? path, AssetService assets, AuditLog audit, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAssetsAsync(http, directory, TokenScopes.Push, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (!AssetPath.TryParse(path, out var parsed) || parsed.IsRoot)
        {
            return Results.Text("The path is not a valid folder path.", "text/plain", statusCode: StatusCodes.Status400BadRequest);
        }

        var outcome = await assets.CreateFolderAsync(request!.Feed, parsed, cancellationToken);
        if (outcome == AssetOutcome.Created)
        {
            audit.Record(http, "asset.folder.create", parsed.Value, $"directory={request.Feed.Name}");
        }

        // Creating a folder that exists is documented as not an error.
        return outcome is AssetOutcome.Created or AssetOutcome.AlreadyExists ? Results.StatusCode(StatusCodes.Status201Created) : ToResult(outcome);
    }

    private static async Task<IResult> DeleteAsync(HttpContext http, string directory, string? path, string? recursive, AssetService assets, AuditLog audit, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAssetsAsync(http, directory, TokenScopes.Delete, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (!AssetPath.TryParse(path, out var parsed) || parsed.IsRoot)
        {
            return Results.Text("The path is not a valid path.", "text/plain", statusCode: StatusCodes.Status400BadRequest);
        }

        var outcome = await assets.DeleteAsync(request!.Feed, parsed, Flag(recursive), cancellationToken);
        if (outcome == AssetOutcome.Deleted)
        {
            audit.Record(http, "asset.delete", parsed.Value, $"directory={request.Feed.Name} recursive={Flag(recursive)}");
        }

        // Deleting what does not exist is documented as not an error.
        return outcome is AssetOutcome.Deleted or AssetOutcome.NotFound ? Results.Ok() : ToResult(outcome);
    }

    private static async Task<IResult> GetMetadataAsync(HttpContext http, string directory, string? path, AssetService assets, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAssetsAsync(http, directory, TokenScopes.Read, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (!AssetPath.TryParse(path, out var parsed) || await assets.FindAsync(request!.Feed, parsed, cancellationToken) is not { } item)
        {
            return Results.Text(MetadataNotFound, "text/plain", statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(Describe(http, request.Feed, item), Json);
    }

    private static async Task<IResult> SetMetadataAsync(HttpContext http, string directory, string? path, AssetService assets, AuditLog audit, CancellationToken cancellationToken)
    {
        var (request, error) = await FeedAccess.ResolveAssetsAsync(http, directory, TokenScopes.Push, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (!AssetPath.TryParse(path, out var parsed) || parsed.IsRoot)
        {
            return Results.Text(MetadataNotFound, "text/plain", statusCode: StatusCodes.Status404NotFound);
        }

        MetadataUpdateJson? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<MetadataUpdateJson>(http.Request.Body, Json, cancellationToken);
            if (body?.UserMetadata is not null)
            {
                _ = ReadUserMetadata(body.UserMetadata);
            }
        }
        catch (JsonException)
        {
            body = null;
        }

        if (body is null)
        {
            return Results.Text("The body must be a JSON metadata update.", "text/plain", statusCode: StatusCodes.Status400BadRequest);
        }

        var change = new AssetMetadataChange(
            body.Type,
            body.UserMetadata is null ? null : ReadUserMetadata(body.UserMetadata),
            string.Equals(body.UserMetadataUpdateMode, "replace", StringComparison.OrdinalIgnoreCase),
            body.CacheHeader?.Type,
            body.CacheHeader?.Value is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } value
                ? (value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText())
                : null);

        var outcome = await assets.UpdateMetadataAsync(request!.Feed, parsed, change, cancellationToken);
        if (outcome == AssetOutcome.NotFound)
        {
            return Results.Text(MetadataNotFound, "text/plain", statusCode: StatusCodes.Status404NotFound);
        }

        audit.Record(http, "asset.metadata", parsed.Value, $"directory={request.Feed.Name}");
        return Results.Ok();
    }

    /// <summary>
    /// A time-to-live is the one cache setting with an obvious meaning, so it is the one applied. Other types
    /// are kept and reported back, but not turned into headers: guessing what they should send would be a
    /// behaviour nobody asked for.
    /// </summary>
    private static void ApplyCacheHeader(HttpContext http, AssetItem file)
    {
        if (string.Equals(file.CacheHeaderType, "ttl", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(file.CacheHeaderValue, out var seconds)
            && seconds >= 0)
        {
            http.Response.Headers.CacheControl = $"public, max-age={seconds}";
        }
    }

    private static IResult ToResult(AssetOutcome outcome) => outcome switch
    {
        AssetOutcome.Created or AssetOutcome.Replaced => Results.StatusCode(StatusCodes.Status201Created),
        AssetOutcome.Deleted or AssetOutcome.Updated => Results.Ok(),
        AssetOutcome.AlreadyExists => Results.Text("A file already exists at this path. PUT never replaces one; use POST.", "text/plain", statusCode: StatusCodes.Status409Conflict),
        AssetOutcome.NotFound => Results.Text(FileNotFound, "text/plain", statusCode: StatusCodes.Status404NotFound),
        AssetOutcome.WrongType => Results.Text("The path is a folder where a file was expected, or a file where a folder was.", "text/plain", statusCode: StatusCodes.Status400BadRequest),
        AssetOutcome.ParentIsFile => Results.Text("A file is in the way: one of the folders on this path is a file.", "text/plain", statusCode: StatusCodes.Status400BadRequest),
        AssetOutcome.NotEmpty => Results.Text("The folder is not empty. Pass recursive=true to delete it with its contents.", "text/plain", statusCode: StatusCodes.Status400BadRequest),
        AssetOutcome.TooLarge => Results.Text("The file is larger than this server accepts.", "text/plain", statusCode: StatusCodes.Status413PayloadTooLarge),
        _ => Results.Text("The path is not valid.", "text/plain", statusCode: StatusCodes.Status400BadRequest),
    };

    private static ItemJson Describe(HttpContext http, Feed feed, AssetItem item)
    {
        var parent = item.Path.LastIndexOf('/') is var slash and > 0 ? item.Path[..slash] : null;
        var metadata = AssetService.ReadUserMetadata(item);
        return new ItemJson(
            item.Name,
            parent,
            item.IsDirectory ? null : item.Size,
            item.IsDirectory ? "dir" : item.ContentType ?? AssetContentTypes.Fallback,
            item.IsDirectory ? null : ContentUrl(http, feed.Name, item.Path),
            item.CreatedUtc,
            item.ModifiedUtc,
            item.Md5,
            item.Sha1,
            item.Sha256,
            item.Sha512,
            metadata.Count == 0 ? null : metadata.ToDictionary(p => p.Key, p => p.Value.IncludeInResponseHeader ? (object)new UserMetadataJson(p.Value.Value, true) : p.Value.Value),
            item.CacheHeaderType is null ? null : new CacheHeaderJson(item.CacheHeaderType, item.CacheHeaderValue));
    }

    /// <summary>One listing entry, field for field what the reference client library reads.</summary>
    private sealed record ItemJson(
        string Name,
        string? Parent,
        long? Size,
        string Type,
        string? Content,
        DateTime Created,
        DateTime Modified,
        string? Md5,
        string? Sha1,
        string? Sha256,
        string? Sha512,
        Dictionary<string, object>? UserMetadata,
        CacheHeaderJson? CacheHeader);

    private sealed record UserMetadataJson(string Value, bool IncludeInResponseHeader);

    private sealed record CacheHeaderJson(string Type, string? Value);

    private sealed class MetadataUpdateJson
    {
        public string? Type { get; set; }

        public string? UserMetadataUpdateMode { get; set; }

        public Dictionary<string, JsonElement>? UserMetadata { get; set; }

        public CacheHeaderUpdateJson? CacheHeader { get; set; }
    }

    /// <summary>
    /// Reads user metadata the way the reference client writes it: a plain string for an ordinary value, and
    /// an object with <c>value</c> and <c>includeInResponseHeader</c> only when that flag is set. Anything else
    /// is a malformed update.
    /// </summary>
    private static Dictionary<string, AssetUserMetadataValue> ReadUserMetadata(Dictionary<string, JsonElement> values)
    {
        var read = new Dictionary<string, AssetUserMetadataValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, element) in values)
        {
            read[key] = element.ValueKind switch
            {
                JsonValueKind.String => new AssetUserMetadataValue(element.GetString()!, false),
                JsonValueKind.Object when element.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String =>
                    new AssetUserMetadataValue(
                        value.GetString()!,
                        element.TryGetProperty("includeInResponseHeader", out var header) && header.ValueKind == JsonValueKind.True),
                _ => throw new JsonException($"User metadata '{key}' must be a string or an object with a string value."),
            };
        }

        return read;
    }

    /// <summary>
    /// A true/false query value, read leniently. The reference client builds its listing URL as
    /// <c>?recursive=false)</c>, stray parenthesis included, and the server it was written against accepts
    /// that; a strict boolean binding answered its every listing with 400.
    /// </summary>
    public static bool Flag(string? value) =>
        value is not null && value.Trim().TrimEnd(')').Trim() is var trimmed && (trimmed.Equals("true", StringComparison.OrdinalIgnoreCase) || trimmed == "1");

    private sealed class CacheHeaderUpdateJson
    {
        public string? Type { get; set; }

        public JsonElement? Value { get; set; }
    }
}
