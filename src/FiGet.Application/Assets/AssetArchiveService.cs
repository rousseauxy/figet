using System.Formats.Tar;
using System.IO.Compression;
using FiGet.Domain.Assets;
using FiGet.Domain.Entities;

namespace FiGet.Application.Assets;

public enum AssetArchiveFormat
{
    Zip,

    /// <summary>A gzipped tar, which the API calls <c>tgz</c>.</summary>
    TarGzip,
}

/// <summary>What an import did, entry by entry where it matters.</summary>
public sealed record AssetImportResult(int Imported, int Skipped, IReadOnlyList<string> Failed, bool TooLarge);

/// <summary>
/// A folder in and out of an archive. Import goes through <see cref="AssetService.WriteAsync"/> one entry at a
/// time, so an archive can put nothing in a directory that an upload could not: every entry name is parsed as
/// an asset path (a <c>..</c> entry is refused, not resolved), every file is hashed and size-limited, and the
/// folders above it are created the usual way.
/// </summary>
public sealed class AssetArchiveService(AssetService assets)
{
    /// <summary>
    /// Imports an archive into <paramref name="folder"/>. Without <paramref name="overwrite"/> an entry whose
    /// path already holds a file is skipped, as documented for the API; with it the file is replaced.
    ///
    /// Not atomic: entries imported before a failure stay. What went and what did not is in the result.
    /// </summary>
    /// <param name="archive">A zip must be seekable; a tgz is read front to back and need not be.</param>
    /// <param name="maxEntryBytes">The asset size limit, applied to each file.</param>
    /// <param name="maxTotalBytes">The most the whole archive may unpack to, which is what stops a zip bomb.</param>
    /// <param name="contentTypeFor">The content type for a file name; an archive carries none of its own.</param>
    public async Task<AssetImportResult> ImportAsync(
        Feed feed,
        AssetPath folder,
        Stream archive,
        AssetArchiveFormat format,
        bool overwrite,
        long maxEntryBytes,
        long maxTotalBytes,
        Func<string, string> contentTypeFor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(contentTypeFor);

        var import = new Import(assets, feed, folder, overwrite, maxEntryBytes, maxTotalBytes, contentTypeFor);
        try
        {
            if (format == AssetArchiveFormat.Zip)
            {
                using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
                foreach (var entry in zip.Entries)
                {
                    if (IsFolderName(entry.FullName))
                    {
                        await import.FolderAsync(entry.FullName, cancellationToken);
                        continue;
                    }

                    await using var content = entry.Open();
                    await import.FileAsync(entry.FullName, content, cancellationToken);
                }
            }
            else
            {
                await using var gzip = new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true);
                await using var tar = new TarReader(gzip, leaveOpen: true);
                while (await tar.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
                {
                    switch (entry.EntryType)
                    {
                        case TarEntryType.Directory:
                            await import.FolderAsync(entry.Name, cancellationToken);
                            break;
                        case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                            await import.FileAsync(entry.Name, entry.DataStream ?? Stream.Null, cancellationToken);
                            break;
                        default:
                            // Links, devices and the tar format's own bookkeeping entries have no meaning here,
                            // and following a link is exactly what an archive must not be able to make us do.
                            if (entry.EntryType is not (TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes))
                            {
                                import.Fail(entry.Name, "not a file or folder");
                            }

                            break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or FormatException)
        {
            import.Fail("(archive)", "not a readable " + (format == AssetArchiveFormat.Zip ? "zip" : "tar.gz") + " file");
        }
        catch (AssetTooLargeException)
        {
            import.TooLarge = true;
        }

        return import.Result();
    }

    /// <summary>
    /// Writes the files in <paramref name="folder"/> - and, with <paramref name="recursive"/>, everything
    /// below it - into an archive, named relative to that folder. Returns false when the folder does not exist.
    /// </summary>
    public async Task<bool> ExportAsync(
        Feed feed,
        AssetPath folder,
        bool recursive,
        AssetArchiveFormat format,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(destination);
        if (!folder.IsRoot && await assets.FindAsync(feed, folder, cancellationToken) is not { IsDirectory: true })
        {
            return false;
        }

        var items = (await assets.ListAsync(feed, folder, recursive, cancellationToken))
            .Where(item => recursive || !item.IsDirectory)
            .OrderBy(item => item.PathLower, StringComparer.Ordinal)
            .ToList();

        if (format == AssetArchiveFormat.Zip)
        {
            using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var item in items)
            {
                var name = Relative(folder, item);
                if (item.IsDirectory)
                {
                    zip.CreateEntry(name + "/").LastWriteTime = item.ModifiedUtc;
                    continue;
                }

                await using var source = await OpenAsync(feed, item, cancellationToken);
                var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
                entry.LastWriteTime = item.ModifiedUtc;
                await using var target = entry.Open();
                await source.CopyToAsync(target, cancellationToken);
            }
        }
        else
        {
            await using var gzip = new GZipStream(destination, CompressionLevel.Fastest, leaveOpen: true);
            await using var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true);
            foreach (var item in items)
            {
                var name = Relative(folder, item);
                if (item.IsDirectory)
                {
                    await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, name + "/") { ModificationTime = item.ModifiedUtc }, cancellationToken);
                    continue;
                }

                await using var source = await OpenAsync(feed, item, cancellationToken);
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { ModificationTime = item.ModifiedUtc, DataStream = source };
                await tar.WriteEntryAsync(entry, cancellationToken);
            }
        }

        return true;
    }

    private async Task<Stream> OpenAsync(Feed feed, AssetItem item, CancellationToken cancellationToken) =>
        await assets.OpenAsync(feed, item, cancellationToken)
            ?? throw new IOException($"The stored file of '{item.Path}' is missing.");

    private static string Relative(AssetPath folder, AssetItem item) =>
        folder.IsRoot ? item.Path : item.Path[(folder.Value.Length + 1)..];

    private static bool IsFolderName(string name) => name.EndsWith('/') || name.EndsWith('\\');

    /// <summary>The running state of one import.</summary>
    private sealed class Import(AssetService assets, Feed feed, AssetPath folder, bool overwrite, long maxEntryBytes, long maxTotalBytes, Func<string, string> contentTypeFor)
    {
        private readonly List<string> failed = [];
        private long total;
        private int imported;
        private int skipped;

        public bool TooLarge { get; set; }

        public void Fail(string name, string reason) => failed.Add($"{name}: {reason}");

        public AssetImportResult Result() => new(imported, skipped, failed, TooLarge);

        public async Task FolderAsync(string name, CancellationToken cancellationToken)
        {
            if (Resolve(name) is not { } path)
            {
                return;
            }

            if (!path.IsRoot && await assets.CreateFolderAsync(feed, path, cancellationToken) is not (AssetOutcome.Created or AssetOutcome.AlreadyExists) and var outcome)
            {
                Fail(name, Describe(outcome));
            }
        }

        public async Task FileAsync(string name, Stream content, CancellationToken cancellationToken)
        {
            if (Resolve(name) is not { } path || path.IsRoot)
            {
                return;
            }

            // The total is enforced on bytes actually read, not on sizes an archive declares about itself.
            var counted = new CountingStream(content, maxTotalBytes - total);
            var outcome = await assets.WriteAsync(
                feed,
                path,
                counted,
                contentTypeFor(path.Name),
                overwrite ? AssetWriteMode.CreateOrReplace : AssetWriteMode.CreateOnly,
                maxEntryBytes,
                cancellationToken);
            total += counted.Count;
            if (total > maxTotalBytes)
            {
                // The archive as a whole unpacked past its limit. Stop here rather than refuse entry by entry:
                // what remains is more of the same, and it is the disk that is at stake.
                throw new AssetTooLargeException(maxTotalBytes);
            }

            switch (outcome)
            {
                case AssetOutcome.Created or AssetOutcome.Replaced:
                    imported++;
                    break;
                case AssetOutcome.AlreadyExists:
                    skipped++;
                    break;
                case AssetOutcome.TooLarge:
                    Fail(name, "larger than this server accepts");
                    break;
                default:
                    Fail(name, Describe(outcome));
                    break;
            }
        }

        /// <summary>
        /// An entry name under the target folder. Backslashes are separators here - archives made on Windows
        /// write them - and a name that does not parse, <c>..</c> included, is recorded as failed.
        /// </summary>
        private AssetPath? Resolve(string name)
        {
            var normalised = name.Replace('\\', '/');
            if (normalised.StartsWith("./", StringComparison.Ordinal))
            {
                normalised = normalised[2..];
            }

            if (AssetPath.TryParse(folder.IsRoot ? normalised : folder.Value + "/" + normalised, out var path))
            {
                return path;
            }

            Fail(name, "not a valid path");
            return null;
        }

        private static string Describe(AssetOutcome outcome) => outcome switch
        {
            AssetOutcome.WrongType => "a folder and a file with the same name",
            AssetOutcome.ParentIsFile => "a file is in the way",
            _ => "not stored (" + outcome + ")",
        };
    }
}
