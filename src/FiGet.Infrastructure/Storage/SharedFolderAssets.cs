using FiGet.Application.Assets;
using FiGet.Application.Ports;
using FiGet.Domain.Assets;
using FiGet.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace FiGet.Infrastructure.Storage;

/// <summary>
/// An asset directory read straight from a folder on the server, a mounted share as a rule. No index and no copy: what
/// the folder holds is what is served, the moment it is there.
///
/// Two things keep the folder the only thing reachable. Every path is parsed by <see cref="AssetPath"/>, so <c>..</c> and
/// a backslash never arrive; and every segment of the resolved path is checked for a link or junction whose target lies
/// outside the root, which is refused as if it did not exist. Names a web server never served are never served here
/// either: <c>web.config</c>, <c>Thumbs.db</c>, <c>desktop.ini</c>, the <c>~$</c> lock files an office suite leaves
/// beside an open document, and anything hidden or marked as a system file.
/// </summary>
public sealed class SharedFolderAssets(ILogger<SharedFolderAssets> logger) : IFolderAssets
{
    private const int BufferSize = 81920;

    private static readonly HashSet<string> NeverServed = new(StringComparer.OrdinalIgnoreCase) { "web.config", "thumbs.db", "desktop.ini" };

    public Task<AssetItem?> FindAsync(Feed feed, AssetPath path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(path);
        if (path.IsRoot || Resolve(feed, path) is not { } full)
        {
            return Task.FromResult<AssetItem?>(null);
        }

        var directory = new DirectoryInfo(full);
        if (directory.Exists)
        {
            return Task.FromResult<AssetItem?>(IsServed(directory) ? Item(feed, path.Value, directory) : null);
        }

        var file = new FileInfo(full);
        return Task.FromResult(file.Exists && IsServed(file) ? Item(feed, path.Value, file) : null);
    }

    public Task<IReadOnlyList<AssetItem>> ListAsync(Feed feed, AssetPath folder, bool recursive, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(folder);
        IReadOnlyList<AssetItem> empty = [];
        if (Resolve(feed, folder) is not { } full || !Directory.Exists(full))
        {
            return Task.FromResult(empty);
        }

        var items = new List<AssetItem>();
        Walk(feed, new DirectoryInfo(full), folder.Value, recursive, items);
        return Task.FromResult<IReadOnlyList<AssetItem>>(
            [.. items.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.PathLower, StringComparer.Ordinal)]);
    }

    public Task<Stream?> OpenAsync(Feed feed, AssetPath path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(path);
        if (path.IsRoot || Resolve(feed, path) is not { } full)
        {
            return Task.FromResult<Stream?>(null);
        }

        var file = new FileInfo(full);
        if (!file.Exists || !IsServed(file))
        {
            return Task.FromResult<Stream?>(null);
        }

        try
        {
            return Task.FromResult<Stream?>(new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, useAsync: true));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            return Task.FromResult<Stream?>(null);
        }
    }

    public async Task<AssetOutcome> WriteAsync(Feed feed, AssetPath path, Stream content, AssetWriteMode mode, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(content);
        if (path.IsRoot || Resolve(feed, path) is not { } full || !IsServedName(path.Name))
        {
            return AssetOutcome.InvalidPath;
        }

        if (Directory.Exists(full))
        {
            return AssetOutcome.WrongType;
        }

        if (BlockedByFile(feed, path))
        {
            return AssetOutcome.ParentIsFile;
        }

        var exists = File.Exists(full);
        if (exists && mode == AssetWriteMode.CreateOnly)
        {
            return AssetOutcome.AlreadyExists;
        }

        if (!exists && mode == AssetWriteMode.ReplaceOnly)
        {
            return AssetOutcome.NotFound;
        }

        // Written beside the target and moved into place, as FiGet's own storage does, so a reader on the share never sees
        // a partial file; and only up to the limit, deleting the temporary file when it is passed.
        var directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, "." + path.Name + "." + Guid.NewGuid().ToString("N") + ".figet-tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                var buffer = new byte[BufferSize];
                long total = 0;
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                    {
                        return AssetOutcome.TooLarge;
                    }

                    await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temp, full, overwrite: true);
            return exists ? AssetOutcome.Replaced : AssetOutcome.Created;
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public Task<AssetOutcome> CreateFolderAsync(Feed feed, AssetPath path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(path);
        if (path.IsRoot)
        {
            return Task.FromResult(AssetOutcome.AlreadyExists);
        }

        if (Resolve(feed, path) is not { } full || !IsServedName(path.Name))
        {
            return Task.FromResult(AssetOutcome.InvalidPath);
        }

        if (Directory.Exists(full))
        {
            return Task.FromResult(AssetOutcome.AlreadyExists);
        }

        if (File.Exists(full))
        {
            return Task.FromResult(AssetOutcome.WrongType);
        }

        if (BlockedByFile(feed, path))
        {
            return Task.FromResult(AssetOutcome.ParentIsFile);
        }

        Directory.CreateDirectory(full);
        return Task.FromResult(AssetOutcome.Created);
    }

    public Task<AssetOutcome> DeleteAsync(Feed feed, AssetPath path, bool recursive, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(path);
        if (path.IsRoot || Resolve(feed, path) is not { } full)
        {
            return Task.FromResult(AssetOutcome.InvalidPath);
        }

        var directory = new DirectoryInfo(full);
        if (directory.Exists)
        {
            if (!IsServed(directory))
            {
                return Task.FromResult(AssetOutcome.NotFound);
            }

            if (!recursive && directory.EnumerateFileSystemInfos().Any())
            {
                return Task.FromResult(AssetOutcome.NotEmpty);
            }

            directory.Delete(recursive);
            return Task.FromResult(AssetOutcome.Deleted);
        }

        var file = new FileInfo(full);
        if (!file.Exists || !IsServed(file))
        {
            return Task.FromResult(AssetOutcome.NotFound);
        }

        file.Delete();
        return Task.FromResult(AssetOutcome.Deleted);
    }

    /// <summary>
    /// The path on disk for an asset path, or null when it is not under the root or reaches it through a link that leaves
    /// it. The root itself is resolved through links, so a mount that is a link still works.
    /// </summary>
    private string? Resolve(Feed feed, AssetPath path)
    {
        var configured = feed.FolderRoot?.Trim();
        if (string.IsNullOrEmpty(configured))
        {
            return null;
        }

        string root;
        try
        {
            root = Path.GetFullPath(configured);
            root = new DirectoryInfo(root).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? root;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogWarning(ex, "The folder of asset directory {Directory} cannot be resolved: {Folder}.", feed.Name, configured);
            return null;
        }

        root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (path.IsRoot)
        {
            return root;
        }

        var full = Path.GetFullPath(Path.Combine([root, .. path.Segments]));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return null;
        }

        // Each segment that exists and is a link must point back under the root, or the path does not exist for us.
        var current = root;
        foreach (var segment in path.Segments)
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists)
            {
                break;
            }

            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                string? target;
                try
                {
                    target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                }
                catch (IOException)
                {
                    target = null;
                }

                if (target is null || !target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    return null;
                }
            }
        }

        return full;
    }

    private bool BlockedByFile(Feed feed, AssetPath path)
    {
        foreach (var ancestor in path.Ancestors())
        {
            if (Resolve(feed, ancestor) is { } full && File.Exists(full))
            {
                return true;
            }
        }

        return false;
    }

    private static void Walk(Feed feed, DirectoryInfo directory, string prefix, bool recursive, List<AssetItem> items)
    {
        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = directory.EnumerateFileSystemInfos();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (!IsServed(entry))
            {
                continue;
            }

            var path = prefix.Length == 0 ? entry.Name : prefix + "/" + entry.Name;
            items.Add(Item(feed, path, entry));
            if (recursive && entry is DirectoryInfo child && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Walk(feed, child, path, recursive, items);
            }
        }
    }

    private static AssetItem Item(Feed feed, string path, FileSystemInfo entry)
    {
        var slash = path.LastIndexOf('/');
        return new AssetItem
        {
            FeedKey = feed.Key,
            Path = path,
            PathLower = path.ToLowerInvariant(),
            ParentLower = slash < 0 ? "" : path[..slash].ToLowerInvariant(),
            Name = entry.Name,
            IsDirectory = entry is DirectoryInfo,
            Size = entry is FileInfo file ? file.Length : 0,
            CreatedUtc = entry.CreationTimeUtc,
            ModifiedUtc = entry.LastWriteTimeUtc,
        };
    }

    private static bool IsServed(FileSystemInfo entry) =>
        IsServedName(entry.Name)
        && !entry.Attributes.HasFlag(FileAttributes.Hidden)
        && !entry.Attributes.HasFlag(FileAttributes.System);

    /// <summary>The names a web server's virtual folder never served, and the lock files an office suite leaves behind.</summary>
    public static bool IsServedName(string name) =>
        !NeverServed.Contains(name)
        && !name.StartsWith("~$", StringComparison.Ordinal)
        && !name.StartsWith('.');
}
