using System.Runtime.InteropServices;

namespace FiGet.Web.Security;

/// <summary>
/// Makes every file and directory this process creates writable by its group.
///
/// Two clusters run FiGet against one shared volume, and OpenShift gives each cluster its own UID range: the same
/// workload is a different user on each one. What they share is the group - every pod runs with GID 0 and new files
/// carry it. Under the default mask of 022 a file is created <c>rw-r--r--</c>, so the cluster that did not write it can
/// read the file and never replace or remove it. Deleting a version, pruning a cache, overwriting an asset: each works
/// or fails depending on which cluster served the request and which one wrote the file, while the database row says the
/// work was done.
///
/// The usual fix wraps the entry point in a shell - <c>umask 0002 &amp;&amp; exec …</c> - which this image cannot do:
/// it is chiseled and carries no shell. So the process sets its own mask, before anything has had a chance to create a
/// file.
///
/// Not configurable on purpose. A mask that denies the group is wrong on a shared volume and harmless on a private one,
/// so there is no deployment that wants the choice, and a setting here is one somebody eventually sets wrongly.
/// </summary>
public static class ProcessUmask
{
    /// <summary>Clear no group bits, and keep others out: files become 0664 and directories 0775.</summary>
    private const uint GroupWritable = 0b000_000_010;

    /// <summary>What the mask was before this ran, and what it is now, for the one line the start-up logs.</summary>
    public static (uint Previous, uint Applied)? Changed { get; private set; }

    /// <summary>
    /// Called as the first statement of the program. On anything but Linux it does nothing: Windows has no umask, and a
    /// developer's machine is not the deployment this exists for.
    /// </summary>
    public static void AllowTheGroup()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // umask returns the mask it replaced and cannot fail; there is no errno to read.
        var previous = Umask(GroupWritable);
        Changed = (previous, GroupWritable);
    }

    // DllImport rather than the source-generated LibraryImport, which would require unsafe code to be switched on for
    // the whole project. One call, two blittable integers, nothing to marshal.
#pragma warning disable SYSLIB1054
    [DllImport("libc", EntryPoint = "umask")]
    private static extern uint Umask(uint mask);
#pragma warning restore SYSLIB1054
}
