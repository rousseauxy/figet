namespace FiGet.Application.Ports;

/// <summary>
/// One share under the mount: its name, as the pages show it, the full path stored on a directory that serves the whole
/// share, and the names of the first folders inside it, so an administrator can see what is there to point at.
/// </summary>
public sealed record ShareFolder(string Name, string Path, IReadOnlyList<string> Inside);

/// <summary>
/// The folders an administrator may back an asset directory with: a share mounted as a direct sub-folder of one
/// configured root, or a folder inside such a share. A free path in a form would let a page serve the database folder
/// or the system; a share name and a folder walked from it, one real directory at a time, cannot leave the root. Read
/// from disk on every call - a handful of entries - so a mount added to one replica is offered at once and no replica
/// holds a stale list. The resolver is the only way from posted text to a path.
/// </summary>
public interface IShareFolders
{
    /// <summary>The configured root as a full path, or null when the pages offer no folders.</summary>
    string? Root { get; }

    bool IsConfigured { get; }

    /// <summary>The shares offered right now, by name. Empty without a root, or when it cannot be read.</summary>
    IReadOnlyList<ShareFolder> List();

    /// <summary>
    /// The full path of a share on the list right now, or of a folder inside it (<paramref name="inside"/>, segments
    /// separated by <c>/</c>, empty or null for the share itself); null for anything else.
    /// </summary>
    string? Resolve(string? share, string? inside);

    /// <summary>A stored folder path as a share name and the path inside it; null when it is not under the root.</summary>
    (string Share, string Inside)? Describe(string? folderRoot);

    /// <summary>Whether a stored folder path lies under the root - one the pages could have chosen. False without a root.</summary>
    bool IsUnderRoot(string? folderRoot);
}
