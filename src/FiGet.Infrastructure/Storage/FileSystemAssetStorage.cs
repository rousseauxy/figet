using System.Globalization;
using FiGet.Application.Ports;

namespace FiGet.Infrastructure.Storage;

/// <summary>
/// Stores asset files as <c>feeds/{directory key}/assets/{first two characters}/{blob id}</c> under a root. The path a
/// user chose never reaches the file system - it lives in the database - so no name can escape the root,
/// collide on a case-insensitive disk, or be a name Windows refuses. The two-character fan-out keeps any one
/// folder small. Writes go to a temporary file beside the target and are moved into place.
///
/// Unfinished multipart uploads live beside them, as <c>feeds/{directory key}/asset-uploads/{upload id}/{index}.{offset}</c>
/// plus a manifest, on the same shared volume: the parts of one upload may reach different replicas.
/// </summary>
public sealed class FileSystemAssetStorage : IAssetStorage
{
    private const int BufferSize = 81920;
    private const string ManifestName = "manifest";
    private readonly string root;

    public FileSystemAssetStorage(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        root = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(root);
    }

    public Task SaveAsync(AssetBlobKey key, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        return WriteAtomicallyAsync(BlobPath(key), content, cancellationToken);
    }

    public Task<Stream?> OpenAsync(AssetBlobKey key, CancellationToken cancellationToken) =>
        Task.FromResult(OpenRead(BlobPath(key)));

    public Task DeleteAsync(AssetBlobKey key, CancellationToken cancellationToken)
    {
        var path = BlobPath(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public Task DeleteFeedAsync(int feedKey, CancellationToken cancellationToken)
    {
        foreach (var area in (string[])["assets", "asset-uploads"])
        {
            var directory = SafePath(StorageLayout.Feeds, StorageLayout.FeedFolder(feedKey), area);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        return Task.CompletedTask;
    }

    public Task SaveUploadPartAsync(AssetUploadKey key, int index, long offset, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        // A part sent again - a client retrying after a timeout - replaces the earlier copy, whatever offset it
        // claimed then.
        var directory = UploadPath(key);
        if (Directory.Exists(directory))
        {
            foreach (var stale in Directory.EnumerateFiles(directory, PartPrefix(index) + ".*"))
            {
                File.Delete(stale);
            }
        }

        return WriteAtomicallyAsync(Path.Combine(directory, $"{PartPrefix(index)}.{offset.ToString(CultureInfo.InvariantCulture)}"), content, cancellationToken);
    }

    public async Task SaveUploadManifestAsync(AssetUploadKey key, long totalSize, int totalParts, CancellationToken cancellationToken)
    {
        using var text = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(
            totalSize.ToString(CultureInfo.InvariantCulture) + " " + totalParts.ToString(CultureInfo.InvariantCulture)));
        await WriteAtomicallyAsync(Path.Combine(UploadPath(key), ManifestName), text, cancellationToken);
    }

    public async Task<(long TotalSize, int TotalParts)?> ReadUploadManifestAsync(AssetUploadKey key, CancellationToken cancellationToken)
    {
        var path = Path.Combine(UploadPath(key), ManifestName);
        if (!File.Exists(path))
        {
            return null;
        }

        var parts = (await File.ReadAllTextAsync(path, cancellationToken)).Split(' ');
        return parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var size)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                ? (size, count)
                : null;
    }

    public Task<IReadOnlyList<AssetUploadPart>> ListUploadPartsAsync(AssetUploadKey key, CancellationToken cancellationToken)
    {
        var directory = UploadPath(key);
        var parts = new List<AssetUploadPart>();
        if (Directory.Exists(directory))
        {
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("part-*"))
            {
                var pieces = file.Name["part-".Length..].Split('.');
                if (pieces.Length == 2
                    && int.TryParse(pieces[0], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                    && long.TryParse(pieces[1], NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
                {
                    parts.Add(new AssetUploadPart(index, offset, file.Length));
                }
            }
        }

        return Task.FromResult<IReadOnlyList<AssetUploadPart>>([.. parts.OrderBy(p => p.Index)]);
    }

    public Task<Stream?> OpenUploadPartAsync(AssetUploadKey key, AssetUploadPart part, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(part);
        return Task.FromResult(OpenRead(Path.Combine(UploadPath(key), $"{PartPrefix(part.Index)}.{part.Offset.ToString(CultureInfo.InvariantCulture)}")));
    }

    public Task DeleteUploadAsync(AssetUploadKey key, CancellationToken cancellationToken)
    {
        var directory = UploadPath(key);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    public Task<int> PruneUploadsAsync(DateTime olderThanUtc, CancellationToken cancellationToken)
    {
        var feeds = Path.Combine(root, StorageLayout.Feeds);
        var removed = 0;
        if (!Directory.Exists(feeds))
        {
            return Task.FromResult(0);
        }

        foreach (var upload in new DirectoryInfo(feeds).EnumerateDirectories()
            .Select(feed => new DirectoryInfo(Path.Combine(feed.FullName, "asset-uploads")))
            .Where(uploads => uploads.Exists)
            .SelectMany(uploads => uploads.EnumerateDirectories()))
        {
            // The newest file decides, not the folder's own time: adding a part does not touch the folder on
            // every file system, and an upload still receiving parts must never be swept away.
            var files = upload.EnumerateFiles().ToList();
            var lastActivity = files.Count == 0 ? upload.LastWriteTimeUtc : files.Max(f => f.LastWriteTimeUtc);
            if (lastActivity >= olderThanUtc)
            {
                continue;
            }

            try
            {
                upload.Delete(recursive: true);
                removed++;
            }
            catch (IOException)
            {
                // A part is being written right now after all; the next sweep decides again.
            }
        }

        return Task.FromResult(removed);
    }

    private static string PartPrefix(int index) => "part-" + index.ToString("D6", CultureInfo.InvariantCulture);

    private string BlobPath(AssetBlobKey key)
    {
        RequireHexId(key.BlobId);
        return SafePath(StorageLayout.Feeds, StorageLayout.FeedFolder(key.Feed), "assets", key.BlobId[..2], key.BlobId);
    }

    private string UploadPath(AssetUploadKey key)
    {
        RequireHexId(key.UploadId);
        return SafePath(StorageLayout.Feeds, StorageLayout.FeedFolder(key.Feed), "asset-uploads", key.UploadId);
    }

    /// <summary>Ids are generated or derived here as 32 lower-case hex characters; anything else did not come from us.</summary>
    private static void RequireHexId(string id)
    {
        if (id.Length != 32 || !id.All(char.IsAsciiHexDigitLower))
        {
            throw new ArgumentException($"Invalid storage id '{id}'.", nameof(id));
        }
    }

    /// <summary>Joins segments under the root and refuses anything that could escape it.</summary>
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

        var path = Path.GetFullPath(Path.Combine([root, .. segments]));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("The storage path escapes the storage root.", nameof(segments));
        }

        return path;
    }

    private static Stream? OpenRead(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, BufferSize, useAsync: true);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static async Task WriteAtomicallyAsync(string path, Stream content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
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
}
