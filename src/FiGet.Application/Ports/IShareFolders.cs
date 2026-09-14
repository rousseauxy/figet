namespace FiGet.Application.Ports;

/// <summary>One sub-folder of the shares mount: its name, as the pages show it, and the full path stored on the directory.</summary>
public sealed record ShareFolder(string Name, string Path);

/// <summary>
/// The folders an administrator may back an asset directory with: the direct sub-folders of one mount root the operator
/// configured. A free path in a form would let a page serve the database folder or the system; a name chosen from this
/// list cannot leave the root. Read from disk on every call - a handful of entries - so a mount added to one replica is
/// offered at once and no replica holds a stale list. The resolver is the only way from a posted name to a path.
/// </summary>
public interface IShareFolders
{
    /// <summary>The configured root as a full path, or null when the pages offer no folders.</summary>
    string? Root { get; }

    bool IsConfigured { get; }

    /// <summary>The folders offered right now, by name. Empty without a root, or when it cannot be read.</summary>
    IReadOnlyList<ShareFolder> List();

    /// <summary>The full path of a folder whose name is on the list right now; null for anything else.</summary>
    string? Resolve(string? name);

    /// <summary>Whether a stored folder path lies under the root - one the pages could have chosen. False without a root.</summary>
    bool IsUnderRoot(string? folderRoot);
}
