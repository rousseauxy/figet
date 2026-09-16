using FiGet.Application.Ports;

namespace FiGet.Infrastructure.Storage;

/// <summary>
/// The volume the storage root sits on, found by the longest mount point that contains it - not by the path's root,
/// which on Linux is "/" for every path and would report the container's own filesystem rather than the mounted volume
/// packages are actually written to.
/// </summary>
public sealed class FileSystemStorageSpace(string root) : IStorageSpace
{
    public VolumeSpace Read()
    {
        try
        {
            var full = Path.GetFullPath(root);
            DriveInfo? best = null;
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!Contains(drive, full))
                {
                    continue;
                }

                if (best is null || drive.RootDirectory.FullName.Length > best.RootDirectory.FullName.Length)
                {
                    best = drive;
                }
            }

            return best is null
                ? new VolumeSpace(full, 0, 0)
                : new VolumeSpace(best.RootDirectory.FullName, best.TotalSize, best.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A page reporting nothing beats a page that will not load: an unreadable mount is shown as unknown.
            return new VolumeSpace(root, 0, 0);
        }
    }

    private static bool Contains(DriveInfo drive, string path)
    {
        try
        {
            if (!drive.IsReady)
            {
                return false;
            }

            var mount = drive.RootDirectory.FullName;
            return path.StartsWith(mount, StringComparison.OrdinalIgnoreCase)
                && (mount.EndsWith(Path.DirectorySeparatorChar) || path.Length == mount.Length || path[mount.Length] == Path.DirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A drive that will not answer is not the one we are on, as far as this page is concerned.
            return false;
        }
    }
}
