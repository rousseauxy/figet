using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace FiGet.Http;

public sealed class UploadOptions
{
    public long MaxPackageSizeBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>
    /// Asset directories hold installers, which are far larger than packages: a surveyed production
    /// directory had a 118 MB file in it. Hence its own limit rather than the package one.
    /// </summary>
    public long MaxAssetSizeBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>The most one archive import may be, and may unpack to. What stops a zip bomb filling the disk.</summary>
    public long MaxImportSizeBytes { get; set; } = 4096L * 1024 * 1024;

    /// <summary>Where uploads are buffered while they are validated. Defaults to the system temp directory.</summary>
    public string? TempPath { get; set; }
}

/// <summary>The upload exceeded the configured size limit.</summary>
public sealed class UploadTooLargeException(long limit) : Exception($"The upload exceeds the limit of {limit} bytes.")
{
    public long Limit { get; } = limit;
}

/// <summary>
/// Buffers a pushed package to a temporary file. NuGet clients send multipart/form-data with one file part;
/// a raw body is accepted too. The returned stream is seekable and deletes its file when disposed.
/// </summary>
public static class PackageUpload
{
    private const int BufferSize = 81920;

    /// <summary>
    /// Reads and discards the body of a push that is being refused, up to the package limit, before the refusal is sent. A
    /// NuGet client with a stored user name and password sends the push without credentials first and expects 401; left
    /// unread, the body is drained by the server with a 30 MB cap and a 5-second timeout, and a larger or slower upload has
    /// its connection reset - the client never sees the 401 and never retries with credentials. The anonymous rate limit
    /// already bounds how often a stranger can make the server read.
    /// </summary>
    public static Task DiscardRefusedBodyAsync(HttpRequest request, IResult refusal, UploadOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        return DiscardRefusedBodyAsync(request, refusal, options.MaxPackageSizeBytes, cancellationToken);
    }

    /// <summary>
    /// The same for any upload with its own limit: an asset directory's file or archive import, which curl, Invoke-WebRequest
    /// and HttpClient with stored credentials answer by challenge just as NuGet clients do.
    /// </summary>
    public static async Task DiscardRefusedBodyAsync(HttpRequest request, IResult refusal, long limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (refusal is not IStatusCodeHttpResult { StatusCode: StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden }
            || (request.ContentLength is null or 0 && !request.Headers.TransferEncoding.Any(v => v?.Contains("chunked", StringComparison.OrdinalIgnoreCase) == true)))
        {
            return;
        }

        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
        {
            sizeFeature.MaxRequestBodySize = limit + (1024 * 1024);
        }

        try
        {
            var buffer = new byte[BufferSize];
            long total = 0;
            int read;
            while ((read = await request.Body.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > limit + (1024 * 1024))
                {
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or BadHttpRequestException or OperationCanceledException)
        {
            // The client went away or sent more than a package may be: the refusal is sent, or not, either way.
        }
    }

    public static async Task<FileStream?> ReadAsync(HttpRequest request, UploadOptions options, CancellationToken cancellationToken)
    {
        var sizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            // Multipart framing adds a little; the exact package limit is enforced while copying.
            sizeFeature.MaxRequestBodySize = options.MaxPackageSizeBytes + (1024 * 1024);
        }

        var directory = string.IsNullOrWhiteSpace(options.TempPath) ? Path.GetTempPath() : options.TempPath;
        Directory.CreateDirectory(directory);
        var file = new FileStream(
            Path.Combine(directory, "figet-upload-" + Guid.NewGuid().ToString("N") + ".tmp"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);

        try
        {
            var copied = false;
            if (MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)
                && contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            {
                var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
                if (string.IsNullOrEmpty(boundary))
                {
                    await file.DisposeAsync();
                    return null;
                }

                var reader = new MultipartReader(boundary, request.Body) { BodyLengthLimit = options.MaxPackageSizeBytes };
                MultipartSection? section;
                while ((section = await reader.ReadNextSectionAsync(cancellationToken)) is not null)
                {
                    if (ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                        && disposition.IsFileDisposition())
                    {
                        await CopyLimitedAsync(section.Body, file, options.MaxPackageSizeBytes, cancellationToken);
                        copied = true;
                        break;
                    }
                }
            }
            else
            {
                await CopyLimitedAsync(request.Body, file, options.MaxPackageSizeBytes, cancellationToken);
                copied = file.Length > 0;
            }

            if (!copied || file.Length == 0)
            {
                await file.DisposeAsync();
                return null;
            }

            file.Position = 0;
            return file;
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            await file.DisposeAsync();
            throw new UploadTooLargeException(options.MaxPackageSizeBytes);
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
    }

    private static async Task CopyLimitedAsync(Stream source, Stream destination, long limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > limit)
            {
                throw new UploadTooLargeException(limit);
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
}
