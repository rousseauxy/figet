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
