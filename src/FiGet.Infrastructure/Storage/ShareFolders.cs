using FiGet.Application.Assets;
using FiGet.Application.Ports;
using Microsoft.Extensions.Logging;

namespace FiGet.Infrastructure.Storage;

/// <summary>
/// The direct sub-folders of the shares mount, read from disk each time they are asked for. Only real directories count:
/// a link or junction under the root is one command away from pointing anywhere, and a hidden or system folder, or a
/// name a web server never served, is not something a page should offer. The path returned for a name is built from
/// the listed entry's own name, never from the text that was posted.
/// </summary>
public sealed class ShareFolders(ShareFolderSettings settings, ILogger<ShareFolders> logger) : IShareFolders
{
    public const int MaxNameLength = 128;

    public string? Root => string.IsNullOrWhiteSpace(settings.Root) ? null : Path.GetFullPath(settings.Root.Trim());

    public bool IsConfigured => Root is not null;

    public IReadOnlyList<ShareFolder> List()
    {
        if (Root is not { } root)
        {
            return [];
        }

        DirectoryInfo[] entries;
        try
        {
            var directory = new DirectoryInfo(root);
            entries = directory.Exists ? directory.GetDirectories() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            logger.LogWarning(ex, "The shares root {Root} cannot be listed.", root);
            return [];
        }

        return
        [
            .. entries
                .Where(IsOffered)
                .Select(entry => new ShareFolder(entry.Name, Path.Combine(root, entry.Name)))
                .OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    public string? Resolve(string? name) =>
        IsPlainName(name) && SharedFolderAssets.IsServedName(name)
            ? List().FirstOrDefault(folder => string.Equals(folder.Name, name, StringComparison.Ordinal))?.Path
            : null;

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
    /// A name in the root itself: no separators, no parent references, no character a file name cannot hold, nothing a
    /// path could escape through, and no space at either end that a file system would drop.
    /// </summary>
    public static bool IsPlainName([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= MaxNameLength
        && value.IndexOfAny(['/', '\\', ':']) < 0
        && value != "." && value != ".."
        && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !char.IsWhiteSpace(value[0]) && !char.IsWhiteSpace(value[^1]);

    private static bool IsOffered(DirectoryInfo entry) =>
        !entry.Attributes.HasFlag(FileAttributes.ReparsePoint)
        && !entry.Attributes.HasFlag(FileAttributes.Hidden)
        && !entry.Attributes.HasFlag(FileAttributes.System)
        && IsPlainName(entry.Name)
        && SharedFolderAssets.IsServedName(entry.Name);
}
