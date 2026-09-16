using FiGet.Application.Connectors;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Domain.Versions;
using NuGet.Versioning;

namespace FiGet.Application.Reports;

/// <summary>
/// Builds <see cref="ChangeReport"/>: what a feed gained, and what its upstreams now offer for the packages it holds.
///
/// Everything here comes from the database. The upstream half reads the stored catalogues rather than asking a
/// gallery, so a page anybody with Read may open cannot turn into traffic somebody else has to carry; keeping those
/// catalogues current is the sweep's job, not this one's.
/// </summary>
public sealed class ChangeReportService(IPackageStore packages, IUpstreamDescriptionStore descriptions, ConnectorService connector)
{
    /// <summary>The window a page shows when nobody asked for one.</summary>
    public const int DefaultDays = 7;

    /// <summary>The longest window anyone may ask for. Beyond this a report stops being news and becomes a history.</summary>
    public const int MaxDays = 90;

    /// <summary>The most rows one report carries per group.</summary>
    public const int MaxRows = 500;

    /// <summary>Clamps rather than refuses: a hand-edited address should at worst show a different window.</summary>
    public static int Days(int? asked) => Math.Clamp(asked ?? DefaultDays, 1, MaxDays);

    public Task<ChangeReport> BuildAsync(Feed feed, int days, DateTime nowUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        return BuildAsync(feed, nowUtc.AddDays(-Days(days)), nowUtc, cancellationToken);
    }

    /// <summary>The window is half-open, from inclusive to exclusive, so two consecutive reports neither skip nor repeat.</summary>
    public async Task<ChangeReport> BuildAsync(Feed feed, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        if (feed.Kind == FeedKind.Assets || toUtc <= fromUtc)
        {
            return ChangeReport.Empty(feed, fromUtc, toUtc);
        }

        var arrived = await packages.ListPublishedBetweenAsync(feed.Key, fromUtc, toUtc, MaxRows, cancellationToken);
        var offered = await UpstreamChangesAsync(feed, fromUtc, toUtc, cancellationToken);

        // Every version this feed holds of the ids that moved, so each row can say what came before it. Those ids
        // only, never the whole feed: a feed of a thousand packages has three that changed this week.
        var ids = arrived.Select(a => a.IdLower).Concat(offered.Select(u => u.IdLower)).Distinct(StringComparer.Ordinal).ToList();
        var held = (await packages.ListHeldVersionsAsync(feed.Key, ids, cancellationToken))
            .Where(v => v.Listed)
            .GroupBy(v => v.IdLower, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => Parsed(g.Select(v => v.NormalizedVersion)), StringComparer.Ordinal);

        var changes = new List<PackageChange>();
        foreach (var version in arrived)
        {
            var parsed = NuGetVersion.Parse(version.NormalizedVersion);
            changes.Add(new PackageChange(
                version.Id,
                version.NormalizedVersion,
                Previous(held, version.IdLower, parsed),
                Breaking(held, version.IdLower, parsed),
                version.PublishedUtc,
                version.ReleaseNotes,
                version.Authors,
                version.Origin == PackageOrigin.Cached ? PackageChangeKind.Cached : PackageChangeKind.Pushed,
                ""));
        }

        foreach (var change in offered)
        {
            changes.Add(new PackageChange(
                change.Id,
                change.Version.ToNormalizedString(),
                Previous(held, change.IdLower, change.Version),
                Breaking(held, change.IdLower, change.Version),
                change.PublishedUtc,
                "",
                change.Authors,
                PackageChangeKind.Upstream,
                change.Upstream));
        }

        return new ChangeReport(
            feed.Name,
            fromUtc,
            toUtc,
            [.. changes.OrderByDescending(c => c.PublishedUtc).ThenBy(c => c.Id, StringComparer.OrdinalIgnoreCase)],
            arrived.Count >= MaxRows || offered.Count >= MaxRows);
    }

    /// <summary>
    /// The versions this feed's upstreams published in the window, for ids the feed holds and nobody has fetched yet.
    ///
    /// One row per id: the highest such version. A gallery that has been ahead of us for a year publishes something
    /// most weeks, and reporting each of those as news would cry wolf daily; what a reader needs to know is that
    /// there is something newer and how far ahead it now is.
    /// </summary>
    private async Task<IReadOnlyList<UpstreamChange>> UpstreamChangesAsync(Feed feed, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
    {
        if (feed.Upstreams.Count == 0)
        {
            return [];
        }

        var published = new Dictionary<string, Dictionary<string, (DateTime PublishedUtc, string Authors)>>(StringComparer.Ordinal);
        foreach (var upstream in feed.Upstreams.Where(u => u.Enabled))
        {
            foreach (var row in await descriptions.PublishedBetweenAsync(upstream.Key, fromUtc, toUtc, MaxRows, cancellationToken))
            {
                if (!published.TryGetValue(row.IdLower, out var versions))
                {
                    versions = new Dictionary<string, (DateTime, string)>(StringComparer.OrdinalIgnoreCase);
                    published[row.IdLower] = versions;
                }

                versions[row.NormalizedVersion] = (row.PublishedUtc, row.Authors);
            }
        }

        if (published.Count == 0)
        {
            return [];
        }

        // Only ids this feed holds. A gallery publishing something nobody here uses is not this feed's news, and this
        // is the line that makes the report answer "ours" rather than "the gallery's".
        var locals = await packages.ListHeldPackagesAsync(feed.Key, [.. published.Keys], cancellationToken);
        if (locals.Count == 0)
        {
            return [];
        }

        // Through the connector, so the answer obeys the rules a listing obeys: which upstream owns an id, the allow
        // and deny patterns, versions a gallery hides, and whether a pushed id is served from here alone.
        var candidates = await connector.StoredUpstreamCandidatesAsync(feed, locals, cancellationToken);
        var changes = new List<UpstreamChange>();
        foreach (var local in locals)
        {
            if (!candidates.TryGetValue(local.IdLower, out var upstream) || !published.TryGetValue(local.IdLower, out var window))
            {
                continue;
            }

            var highestHeld = local.Versions
                .Where(v => v.Listed)
                .Select(v => NuGetVersion.Parse(v.NormalizedVersion))
                .OrderByDescending(v => v, VersionComparer.Default)
                .FirstOrDefault();

            var newest = upstream.Versions
                .Where(c => c.Listed && window.ContainsKey(c.Version.ToNormalizedString()))
                .Where(c => highestHeld is null || VersionComparer.Default.Compare(c.Version, highestHeld) > 0)
                .OrderByDescending(c => c.Version, VersionComparer.Default)
                .FirstOrDefault();
            if (newest is null)
            {
                continue;
            }

            var (publishedUtc, authors) = window[newest.Version.ToNormalizedString()];
            changes.Add(new UpstreamChange(local.Id, local.IdLower, newest.Version, publishedUtc, authors, upstream.Upstream));
        }

        return changes;
    }

    private static string? Previous(IReadOnlyDictionary<string, IReadOnlyList<NuGetVersion>> held, string idLower, NuGetVersion version) =>
        VersionChange.Previous(held.TryGetValue(idLower, out var versions) ? versions : [], version)?.ToNormalizedString();

    private static bool Breaking(IReadOnlyDictionary<string, IReadOnlyList<NuGetVersion>> held, string idLower, NuGetVersion version) =>
        VersionChange.IsBreaking(VersionChange.Previous(held.TryGetValue(idLower, out var versions) ? versions : [], version), version);

    private static IReadOnlyList<NuGetVersion> Parsed(IEnumerable<string> versions) =>
        [.. versions.Where(v => NuGetVersion.TryParse(v, out _)).Select(NuGetVersion.Parse)];

    private sealed record UpstreamChange(string Id, string IdLower, NuGetVersion Version, DateTime PublishedUtc, string Authors, string Upstream);
}
