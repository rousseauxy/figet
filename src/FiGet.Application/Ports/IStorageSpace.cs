namespace FiGet.Application.Ports;

/// <param name="Volume">The mount the storage root is on, as the system names it.</param>
/// <param name="TotalBytes">The volume's size. Zero when the system would not say.</param>
/// <param name="FreeBytes">What is left on it, for everything on that volume and not for FiGet alone.</param>
public sealed record VolumeSpace(string Volume, long TotalBytes, long FreeBytes)
{
    /// <summary>Whether the system answered at all: a volume of no size is one nobody could measure.</summary>
    public bool Known => TotalBytes > 0;

    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
}

/// <summary>
/// How much room is left where packages are written. The single operational risk a package server runs into first: a
/// full volume fails a push, and nothing else on this server can warn about it.
///
/// Deliberately the volume and not FiGet's own footprint. What FiGet uses would mean walking every stored file, which
/// is what the storage check does on request; how full the disk is has to be free enough to show on a page.
/// </summary>
public interface IStorageSpace
{
    VolumeSpace Read();
}
