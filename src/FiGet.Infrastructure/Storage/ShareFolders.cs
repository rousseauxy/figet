using FiGet.Application.Assets;
using FiGet.Application.Ports;
using Microsoft.Extensions.Logging;

namespace FiGet.Infrastructure.Storage;

/// <summary>
/// The shares under the mount and the folders inside them, read from disk each time they are asked for. Only real
/// directories count: a link or junction is one command away from pointing anywhere, and a hidden or system folder, or
/// a name a web server never served, is not something a page should offer. A path is built by walking from the root
/// one listed entry at a time, never from the text that was posted.
/// </summary>
public sealed class ShareFolders(ShareFolderSettings settings, ILogger<ShareFolders> logger) : IShareFolders
{
    public const int MaxNameLength = 128;

    /// <summary>How many folders inside a share the pages show as a hint; a share holds what it holds.</summary>
    public const int InsideShown = 24;

    /// <summary>How deep a folder inside a share may be chosen: enough for a project's sub-folder, not a path to type at length.</summary>
    public const int MaxDepth = 8;

    public string? Root => string.IsNullOrWhiteSpace(settings.Root) ? null : Path.GetFullPath(settings.Root.Trim());

    public bool IsConfigured => Root is not null;

    public IReadOnlyList<ShareFolder> List()
    {
        if (Root is not { } root)
        {
            return [];
        }

        return
        [
            .. Children(root)
                .Select(entry => new ShareFolder(
                    entry.Name,
                    Path.Combine(root, entry.Name),
                    [.. Children(entry.FullName).Take(InsideShown).Select(inner => inner.Name)]))
                .OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    public string? Resolve(string? share, string? inside)
    {
        if (Root is not { } root || !IsPlainName(share) || !SharedFolderAssets.IsServedName(share))
        {
            return null;
        }

        var current = Children(root).FirstOrDefault(entry => string.Equals(entry.Name, share, StringComparison.Ordinal));
        if (current is null)
        {
            return null;
        }

        var path = Path.Combine(root, current.Name);
        var segments = (inside ?? "").Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length > MaxDepth)
        {
            return null;
        }

        // One real directory at a time from the share, matched by its own name: "..", a link, a file, a name that is not
        // there, or one a web server never served ends the walk with nothing.
        foreach (var segment in segments)
        {
            if (!IsPlainName(segment) || !SharedFolderAssets.IsServedName(segment))
            {
                return null;
            }

            var next = Children(path).FirstOrDefault(entry => string.Equals(entry.Name, segment, StringComparison.Ordinal));
            if (next is null)
            {
                return null;
            }

            path = Path.Combine(path, next.Name);
        }

        return path;
    }

    public (string Share, string Inside)? Describe(string? folderRoot)
    {
        if (Root is not { } root || !IsUnderRoot(folderRoot))
        {
            return null;
        }

        var relative = Path.GetRelativePath(root, Path.GetFullPath(folderRoot!.Trim()));
        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? null : (segments[0], string.Join('/', segments.Skip(1)));
    }

    public bool IsUnderRoot(string? folderRoot)
    {
        if (Root is not { } root || string.IsNullOrWhiteSpace(folderRoot))
        {
            return false;
        }

        try
        {
            return Path.GetFullPath(folderRoot.Trim()).StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// A name in a folder itself: no separators, no parent references, no character a file name cannot hold, nothing a
    /// path could escape through, and no space at either end that a file system would drop.
    /// </summary>
    public static bool IsPlainName([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= MaxNameLength
        && value.IndexOfAny(['/', '\\', ':']) < 0
        && value != "." && value != ".."
        && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !char.IsWhiteSpace(value[0]) && !char.IsWhiteSpace(value[^1]);

    /// <summary>The real, offered sub-folders of a folder, by name; empty when it is missing or cannot be read.</summary>
    private IEnumerable<DirectoryInfo> Children(string folder)
    {
        DirectoryInfo[] entries;
        try
        {
            var directory = new DirectoryInfo(folder);
            entries = directory.Exists ? directory.GetDirectories() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            logger.LogWarning(ex, "The folder {Folder} under the shares root cannot be listed.", folder);
            return [];
        }

        return entries.Where(IsOffered).OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsOffered(DirectoryInfo entry) =>
        !entry.Attributes.HasFlag(FileAttributes.ReparsePoint)
        && !entry.Attributes.HasFlag(FileAttributes.Hidden)
        && !entry.Attributes.HasFlag(FileAttributes.System)
        && IsPlainName(entry.Name)
        && SharedFolderAssets.IsServedName(entry.Name);
}
