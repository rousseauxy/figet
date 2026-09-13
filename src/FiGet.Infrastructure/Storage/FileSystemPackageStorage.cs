using FiGet.Application.Ports;

namespace FiGet.Infrastructure.Storage;

/// <summary>
/// Stores files under a root directory, one folder per feed named by its key:
/// <c>feeds/{key}/packages/{id}/{version}/{id}.{version}.nupkg</c> (+ <c>.nuspec</c>) and
/// <c>feeds/{key}/symbols/{file}/{key}/{file}</c>. By key rather than name, so renaming a feed moves nothing, and
/// everything one feed holds is one folder to measure, back up or remove. Writes go to a temporary file in the target
/// directory and are moved into place, so a reader never sees a partial file, including on a shared volume.
/// </summary>
public sealed class FileSystemPackageStorage : IPackageStorage
{
    private const int BufferSize = 81920;
    private readonly string root;

    public FileSystemPackageStorage(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        root = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(root);
    }

    public async Task SavePackageAsync(PackageStorageKey key, Stream nupkg, ReadOnlyMemory<byte> nuspec, bool overwrite, CancellationToken cancellationToken)
    {
        var nupkgPath = PackagePath(key, "nupkg");
        if (!overwrite && File.Exists(nupkgPath))
        {
            throw new IOException($"The package file for {key.Id} {key.Version} already exists.");
        }

        await WriteAtomicallyAsync(PackagePath(key, "nuspec"), stream => stream.WriteAsync(nuspec, cancellationToken).AsTask(), cancellationToken);
        await WriteAtomicallyAsync(nupkgPath, stream => nupkg.CopyToAsync(stream, BufferSize, cancellationToken), cancellationToken);
    }

    public Task<Stream?> OpenPackageAsync(PackageStorageKey key, CancellationToken cancellationToken) =>
        Task.FromResult<Stream?>(OpenRead(PackagePath(key, "nupkg")));

    public Task<Stream?> OpenNuspecAsync(PackageStorageKey key, CancellationToken cancellationToken) =>
        Task.FromResult<Stream?>(OpenRead(PackagePath(key, "nuspec")));

    public Task DeletePackageAsync(PackageStorageKey key, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(PackagePath(key, "nupkg"))!;
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        TryRemoveEmptyParent(Path.GetDirectoryName(directory)!);
        return Task.CompletedTask;
    }

    public Task SaveSymbolAsync(SymbolStorageKey key, Stream pdb, CancellationToken cancellationToken) =>
        WriteAtomicallyAsync(SymbolPath(key), stream => pdb.CopyToAsync(stream, BufferSize, cancellationToken), cancellationToken);

    public Task<Stream?> OpenSymbolAsync(SymbolStorageKey key, CancellationToken cancellationToken) =>
        Task.FromResult<Stream?>(OpenRead(SymbolPath(key)));

    public Task DeleteSymbolAsync(SymbolStorageKey key, CancellationToken cancellationToken)
    {
        var path = SymbolPath(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        TryRemoveEmptyParent(Path.GetDirectoryName(path)!);
        return Task.CompletedTask;
    }

    public Task DeleteFeedAsync(int feedKey, CancellationToken cancellationToken)
    {
        // The areas only: an asset directory's files share the feed's folder, and are the asset storage's to remove.
        foreach (var area in (string[])["packages", "symbols"])
        {
            var directory = SafePath(StorageLayout.Feeds, Folder(feedKey), area);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        return Task.CompletedTask;
    }

    private string PackagePath(PackageStorageKey key, string extension) =>
        SafePath(StorageLayout.Feeds, Folder(key.Feed), "packages", key.Id, key.Version, $"{key.Id}.{key.Version}.{extension}");

    private string SymbolPath(SymbolStorageKey key) =>
        SafePath(StorageLayout.Feeds, Folder(key.Feed), "symbols", key.FileName, key.SymbolKey, key.FileName);

    private static string Folder(int feedKey) => StorageLayout.FeedFolder(feedKey);

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

    private static FileStream? OpenRead(string path)
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

    private static async Task WriteAtomicallyAsync(string path, Func<Stream, Task> write, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                await write(stream);
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

    private void TryRemoveEmptyParent(string directory)
    {
        try
        {
            if (directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && Directory.Exists(directory)
                && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (IOException)
        {
            // Another writer created something in the meantime; leaving an empty directory is harmless.
        }
    }
}
