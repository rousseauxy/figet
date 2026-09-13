using FiGet.Application.Ports;

namespace FiGet.Infrastructure.Storage;

/// <summary>
/// Stores asset files as <c>assets/{directory}/{first two characters}/{blob id}</c> under a root. The path a
/// user chose never reaches the file system - it lives in the database - so no name can escape the root,
/// collide on a case-insensitive disk, or be a name Windows refuses. The two-character fan-out keeps any one
/// folder small. Writes go to a temporary file beside the target and are moved into place.
/// </summary>
public sealed class FileSystemAssetStorage : IAssetStorage
{
    private const int BufferSize = 81920;
    private readonly string root;

    public FileSystemAssetStorage(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        root = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(root);
    }

    public async Task SaveAsync(AssetBlobKey key, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var path = BlobPath(key);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, "." + key.BlobId + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                await content.CopyToAsync(stream, BufferSize, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public Task<Stream?> OpenAsync(AssetBlobKey key, CancellationToken cancellationToken)
    {
        try
        {
            return Task.FromResult<Stream?>(new FileStream(BlobPath(key), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, BufferSize, useAsync: true));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Task.FromResult<Stream?>(null);
        }
    }

    public Task DeleteAsync(AssetBlobKey key, CancellationToken cancellationToken)
    {
        var path = BlobPath(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public Task DeleteFeedAsync(string feedLower, CancellationToken cancellationToken)
    {
        var directory = SafePath(feedLower);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private string BlobPath(AssetBlobKey key)
    {
        // Blob ids are generated here as 32 lower-case hex characters; anything else did not come from us.
        if (key.BlobId.Length != 32 || !key.BlobId.All(char.IsAsciiHexDigitLower))
        {
            throw new ArgumentException($"Invalid asset blob id '{key.BlobId}'.", nameof(key));
        }

        return SafePath(key.Feed, key.BlobId[..2], key.BlobId);
    }

    /// <summary>Joins segments under <c>assets</c> and refuses anything that could escape the root.</summary>
    private string SafePath(params string[] segments)
    {
        foreach (var segment in segments)
        {
            if (string.IsNullOrEmpty(segment)
                || segment is "." or ".."
                || segment.IndexOfAny(['/', '\\', ':']) >= 0
                || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || segment.Any(char.IsUpper))
            {
                throw new ArgumentException($"Invalid storage path segment '{segment}'.", nameof(segments));
            }
        }

        var path = Path.GetFullPath(Path.Combine([root, "assets", .. segments]));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("The storage path escapes the storage root.", nameof(segments));
        }

        return path;
    }
}
