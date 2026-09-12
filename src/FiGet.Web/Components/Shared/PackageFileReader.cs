using System.IO.Compression;
using FiGet.Application.Ports;

namespace FiGet.Web.Components.Shared;

/// <summary>One entry inside a stored package.</summary>
public sealed record PackageFileEntry(string Path, long Length);

public static class PackageFileReader
{
    /// <summary>
    /// The entries of a stored package, read on demand. Null when the file is missing or is not a
    /// readable zip: that is a storage problem to be shown as such, not an error page. Only ever called
    /// for a version held here — there is nothing to open for one that still lives upstream.
    /// </summary>
    public static async Task<IReadOnlyList<PackageFileEntry>?> ReadAsync(
        IPackageStorage storage,
        PackageStorageKey key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storage);

        var stream = await storage.OpenPackageAsync(key, cancellationToken);
        if (stream is null)
        {
            return null;
        }

        await using (stream)
        {
            try
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                return
                [
                    .. archive.Entries
                        .Where(entry => entry.Length > 0 || !entry.FullName.EndsWith('/'))
                        .Select(entry => new PackageFileEntry(entry.FullName, entry.Length))
                        .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                ];
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }
    }
}
