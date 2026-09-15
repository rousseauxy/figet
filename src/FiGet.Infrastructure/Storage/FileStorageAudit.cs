using FiGet.Application.Ports;
using FiGet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FiGet.Infrastructure.Storage;

/// <summary>
/// Compares the file storage with the database: <c>feeds/{key}/packages/{id}/{version}/{id}.{version}.nupkg|nuspec</c>
/// against package versions, <c>feeds/{key}/symbols/{file}/{key}/{file}</c> against symbol files, and
/// <c>feeds/{key}/assets/{xx}/{blob}</c> against asset items. Unfinished multipart uploads have their own sweep and are
/// not looked at, nor are the dot-files an atomic write leaves while it runs.
/// </summary>
public sealed class FileStorageAudit(FiGetDbContext db, string filesRoot, TimeProvider time) : IStorageAudit
{
    private string FeedsRoot => Path.GetFullPath(Path.Combine(filesRoot, StorageLayout.Feeds));

    public async Task<StorageAuditReport> FindStrayFilesAsync(CancellationToken cancellationToken)
    {
        var known = await KnownAsync(cancellationToken);
        var stray = new List<StrayFile>();
        var count = 0;
        long bytes = 0;
        var scanned = 0;
        foreach (var file in Files())
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanned++;
            if (StrayFor(file, known) is { } found)
            {
                count++;
                bytes += found.Size;
                if (stray.Count < IStorageAudit.MaxListed)
                {
                    stray.Add(found);
                }
            }
        }

        return new StorageAuditReport(stray, count, bytes, scanned);
    }

    public async Task<IReadOnlyList<StrayFile>> RemoveStrayFilesAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var known = await KnownAsync(cancellationToken);
        var removed = new List<StrayFile>();
        var feeds = FeedsRoot + Path.DirectorySeparatorChar;
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            var full = Path.GetFullPath(Path.Combine(filesRoot, path.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(feeds, StringComparison.Ordinal) || !File.Exists(full)
                || StrayFor(new FileInfo(full), known) is not { } stray)
            {
                continue;
            }

            File.Delete(full);
            removed.Add(stray);
            PruneEmptyFolders(Path.GetDirectoryName(full)!);
        }

        return removed;
    }

    private IEnumerable<FileInfo> Files()
    {
        var root = new DirectoryInfo(FeedsRoot);
        if (!root.Exists)
        {
            return [];
        }

        return root.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(f => !f.Name.StartsWith('.'));
    }

    /// <summary>The file as a stray, or null when a row names it, it is too new, or it is an upload in progress.</summary>
    private StrayFile? StrayFor(FileInfo file, Known known)
    {
        var relative = Path.GetRelativePath(FeedsRoot, file.FullName).Split(Path.DirectorySeparatorChar);
        if (relative.Length < 2 || relative[1] == "asset-uploads" || file.Name.StartsWith('.')
            || time.GetUtcNow().UtcDateTime - file.LastWriteTimeUtc < IStorageAudit.MinimumAge)
        {
            return null;
        }

        var named = int.TryParse(relative[0], out var feed) && known.Feeds.Contains(feed) && (relative[1], relative.Length) switch
        {
            ("packages", 5) => known.Packages.Contains((feed, relative[2], relative[3]))
                && (relative[4] == $"{relative[2]}.{relative[3]}.nupkg" || relative[4] == $"{relative[2]}.{relative[3]}.nuspec"),
            ("symbols", 5) => relative[2] == relative[4] && known.Symbols.Contains((feed, relative[2], relative[3])),
            ("assets", 4) => known.Blobs.Contains((feed, relative[3])),
            _ => false,
        };

        return named ? null : new StrayFile(StorageLayout.Feeds + "/" + string.Join('/', relative), file.Length, file.LastWriteTimeUtc);
    }

    /// <summary>Removes the folders a removal left empty, up to the area folder of the feed.</summary>
    private void PruneEmptyFolders(string directory)
    {
        var stop = FeedsRoot.Split(Path.DirectorySeparatorChar).Length + 2;
        var current = new DirectoryInfo(directory);
        while (current is not null && current.FullName.Split(Path.DirectorySeparatorChar).Length > stop
            && current.Exists && !current.EnumerateFileSystemInfos().Any())
        {
            current.Delete();
            current = current.Parent;
        }
    }

    private async Task<Known> KnownAsync(CancellationToken cancellationToken)
    {
        var feeds = (await db.Feeds.AsNoTracking().Select(f => f.Key).ToListAsync(cancellationToken)).ToHashSet();
        var packages = (await db.PackageVersions.AsNoTracking()
            .Select(v => new { v.Package!.FeedKey, v.Package.IdLower, v.NormalizedVersionLower })
            .ToListAsync(cancellationToken))
            .Select(v => (v.FeedKey, v.IdLower, v.NormalizedVersionLower))
            .ToHashSet();
        var symbols = (await db.SymbolFiles.AsNoTracking()
            .Select(s => new { s.FeedKey, s.FileNameLower, s.SymbolKeyLower })
            .ToListAsync(cancellationToken))
            .Select(s => (s.FeedKey, s.FileNameLower, s.SymbolKeyLower))
            .ToHashSet();
        var blobs = (await db.AssetItems.AsNoTracking()
            .Where(a => a.BlobId != null)
            .Select(a => new { a.FeedKey, a.BlobId })
            .ToListAsync(cancellationToken))
            .Select(a => (a.FeedKey, a.BlobId!))
            .ToHashSet();
        return new Known(feeds, packages, symbols, blobs);
    }

    private sealed record Known(
        HashSet<int> Feeds,
        HashSet<(int, string, string)> Packages,
        HashSet<(int, string, string)> Symbols,
        HashSet<(int, string)> Blobs);
}
