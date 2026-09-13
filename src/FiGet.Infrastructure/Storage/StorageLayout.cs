using System.Globalization;
using Microsoft.Extensions.Logging;

namespace FiGet.Infrastructure.Storage;

/// <summary>
/// Where a feed's files live, and the one-time move from the layout before it.
///
/// Until 2026-09-13 every area had a folder per feed *name* - <c>packages/{name}</c>, <c>symbols/{name}</c>,
/// <c>assets/{name}</c>, <c>asset-uploads/{name}</c> - which is what made renaming a feed a file move. Now each feed has
/// one folder named by its key, with the areas inside: <c>feeds/{key}/packages</c> and so on.
/// </summary>
public static partial class StorageLayout
{
    /// <summary>The folder under the storage root that holds one folder per feed.</summary>
    public const string Feeds = "feeds";

    private static readonly string[] Areas = ["packages", "symbols", "assets", "asset-uploads"];

    public static string FeedFolder(int feedKey)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(feedKey);
        return feedKey.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Moves the folders of the name-based layout to the key-based one, for the feeds given. Safe to run on every start:
    /// what has moved is gone from the old place, so a second run finds nothing. Each move is a rename on the same volume,
    /// so it is quick whatever a folder holds, and a start interrupted half way is finished by the next one.
    ///
    /// A folder of the old layout that no feed is called any more is left where it is and named in the log: it holds
    /// files no row points at, and deleting what nobody can account for is not a startup's decision.
    /// </summary>
    /// <returns>How many folders were moved.</returns>
    public static int MoveNameFoldersToKeyFolders(string root, IEnumerable<(int Key, string NameLower)> feeds, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(feeds);
        ArgumentNullException.ThrowIfNull(logger);
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var byName = feeds.ToDictionary(f => f.NameLower, f => f.Key, StringComparer.Ordinal);
        var moved = 0;
        foreach (var area in Areas)
        {
            var old = Path.Combine(root, area);
            if (!Directory.Exists(old))
            {
                continue;
            }

            foreach (var folder in Directory.EnumerateDirectories(old))
            {
                var name = Path.GetFileName(folder);
                if (!byName.TryGetValue(name, out var key))
                {
                    LogUnclaimed(logger, Path.Combine(area, name));
                    continue;
                }

                var target = Path.Combine(root, Feeds, FeedFolder(key), area);
                try
                {
                    MergeInto(folder, target);
                    moved++;
                }
                catch (DirectoryNotFoundException)
                {
                    // Another replica starting at the same moment moved it first.
                }
            }

            TryRemoveIfEmpty(old);
        }

        if (moved > 0)
        {
            LogMoved(logger, moved);
        }

        return moved;
    }

    /// <summary>
    /// Moves a folder to where it belongs. One rename when the target does not exist yet, which is the usual case; entry by
    /// entry when it does, so files written to the new place already are kept, and a file present in both is left in the
    /// old place for a person to compare rather than overwritten.
    /// </summary>
    private static void MergeInto(string source, string target)
    {
        if (!Directory.Exists(target))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(source, target);
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            MergeInto(directory, Path.Combine(target, Path.GetFileName(directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(file));
            if (!File.Exists(destination))
            {
                File.Move(file, destination);
            }
        }

        TryRemoveIfEmpty(source);
    }

    private static void TryRemoveIfEmpty(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (IOException)
        {
            // Something was added in the meantime; an empty folder left behind is harmless.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Moved {Count} storage folder(s) from folders named by feed name to folders named by feed key.")]
    private static partial void LogMoved(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Storage folder {Folder} belongs to no feed of that name and was not moved. Its files are not served; remove it once nobody needs them.")]
    private static partial void LogUnclaimed(ILogger logger, string folder);
}
