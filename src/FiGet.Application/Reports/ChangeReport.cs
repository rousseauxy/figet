using FiGet.Domain.Entities;

namespace FiGet.Application.Reports;

/// <summary>Where a change came from, which is the distinction the whole report exists to make.</summary>
public enum PackageChangeKind
{
    /// <summary>Pushed to this feed.</summary>
    Pushed,

    /// <summary>Fetched from an upstream and kept here.</summary>
    Cached,

    /// <summary>An upstream offers it and nobody here has fetched it. The early warning, and the reason for the report.</summary>
    Upstream,
}

/// <summary>
/// One version that arrived, or became available, inside a window.
/// </summary>
/// <param name="PreviousVersion">What a client would have installed before it, or null when nothing came before.</param>
/// <param name="Breaking">Whether the major version grew; see <see cref="Domain.Versions.VersionChange"/>.</param>
/// <param name="ReleaseNotes">
/// From the package's own nuspec, so it is empty for a version nobody has fetched: the galleries do not report release
/// notes in the answers a version list is built from.
/// </param>
/// <param name="Upstream">The upstream that offers it, for <see cref="PackageChangeKind.Upstream"/>; else empty.</param>
public sealed record PackageChange(
    string Id,
    string Version,
    string? PreviousVersion,
    bool Breaking,
    DateTime PublishedUtc,
    string ReleaseNotes,
    string Authors,
    PackageChangeKind Kind,
    string Upstream);

/// <summary>
/// What changed among the packages one feed actually holds, over a window. Deliberately not "what changed upstream":
/// a proxy feed can reach a gallery of half a million packages, and a report of all of them answers nobody's question.
/// </summary>
/// <param name="StoppedAtLimit">
/// True when there were more changes than the report may carry. The reader is told rather than quietly shown a
/// shorter list: a truncated report of a busy week is exactly when somebody needs to know they are not seeing it all.
/// </param>
public sealed record ChangeReport(
    string Feed,
    DateTime FromUtc,
    DateTime ToUtc,
    IReadOnlyList<PackageChange> Changes,
    bool StoppedAtLimit)
{
    public IReadOnlyList<PackageChange> Of(PackageChangeKind kind) => [.. Changes.Where(c => c.Kind == kind)];

    public bool Any => Changes.Count > 0;

    /// <summary>An empty report for a feed nothing can be said about - an asset directory, or one with nothing in it.</summary>
    public static ChangeReport Empty(Feed feed, DateTime fromUtc, DateTime toUtc) =>
        new(feed?.Name ?? "", fromUtc, toUtc, [], false);
}
