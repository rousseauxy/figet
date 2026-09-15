namespace FiGet.Application.Ports;

/// <summary>A stored file no database row names, relative to the storage root with forward slashes.</summary>
public sealed record StrayFile(string Path, long Size, DateTime ModifiedUtc);

/// <param name="Stray">The stray files found, at most <see cref="IStorageAudit.MaxListed"/>.</param>
/// <param name="StrayCount">How many were found in all.</param>
/// <param name="StrayBytes">Their size in all.</param>
/// <param name="Scanned">How many files were looked at.</param>
public sealed record StorageAuditReport(IReadOnlyList<StrayFile> Stray, int StrayCount, long StrayBytes, int Scanned);

/// <summary>
/// Finds, and on request removes, stored package, symbol and asset files no database row names: left by a failed delete,
/// by two replaces of one asset at the same moment, or by a process stopped between a file and its row. Files newer than
/// <see cref="MinimumAge"/> are never counted, so an upload that has written its file and not yet its row is left alone.
/// </summary>
public interface IStorageAudit
{
    public const int MaxListed = 500;

    public static readonly TimeSpan MinimumAge = TimeSpan.FromHours(1);

    Task<StorageAuditReport> FindStrayFilesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Removes the stray files among <paramref name="paths"/>, each checked again first: a path a row names by now, one that
    /// became too new, or one outside the storage folders is left alone. Returns the files removed.
    /// </summary>
    Task<IReadOnlyList<StrayFile>> RemoveStrayFilesAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken);
}
