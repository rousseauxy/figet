using FiGet.Domain.Entities;
using NuGet.Versioning;

namespace FiGet.Application.Packages;

/// <summary>A feed's retention and cache pruning settings. Every null means "no rule".</summary>
public sealed record RetentionRules(
    int? KeepStable = null,
    int? KeepPrerelease = null,
    bool PerMajorVersion = false,
    int? KeepIfUsedWithinDays = null,
    int? PruneCachedAfterDays = null)
{
    public bool Any => KeepStable is not null || KeepPrerelease is not null || PruneCachedAfterDays is not null;

    public static RetentionRules Of(Feed feed)
    {
        ArgumentNullException.ThrowIfNull(feed);
        return new RetentionRules(feed.RetainStableVersions, feed.RetainPrereleaseVersions, feed.RetainPerMajorVersion, feed.RetainIfUsedWithinDays, feed.PruneCachedAfterDays);
    }

    /// <summary>What a person may set: counts of at least one stable and zero prerelease, and days of at least one.</summary>
    public string? Problem() =>
        KeepStable is < 1 ? "Keep at least one stable version per package, or leave it empty for no limit."
        : KeepPrerelease is < 0 ? "The number of prerelease versions to keep cannot be negative."
        : KeepIfUsedWithinDays is < 1 || PruneCachedAfterDays is < 1 ? "A number of days is at least 1, or empty."
        : null;
}

/// <summary>One stored version, with what retention decides on.</summary>
public sealed record RetentionCandidate(
    string Id,
    string IdLower,
    string NormalizedVersion,
    bool IsPrerelease,
    bool Listed,
    PackageOrigin Origin,
    DateTime LastUsedUtc,
    long Size);

public enum RetentionReason
{
    /// <summary>Beyond the stable versions kept for its package (or major version).</summary>
    OlderStable,

    /// <summary>Beyond the prerelease versions kept for its package (or major version).</summary>
    OlderPrerelease,

    /// <summary>A cached copy nobody has downloaded within the pruning period.</summary>
    UnusedCache,
}

public sealed record RetentionRemoval(RetentionCandidate Version, RetentionReason Reason);

/// <summary>
/// Which versions a feed's rules remove. A pure function of the stored versions and the rules, so the preview on the feed
/// page and the run itself cannot disagree.
///
/// Pushed versions: per package, newest first, the configured number of stable and of prerelease versions are kept (per
/// major version when asked), and the newest version of the package is always kept, whatever it is. A version used within
/// the configured days is kept too. When the feed unlists rather than deletes, only listed versions count and are
/// candidates: an unlisted one is already out of the way, and counting it would keep one version fewer than asked.
///
/// Cached copies: only the pruning period applies, and nothing protects them - the upstream still has every one.
/// </summary>
public static class RetentionPolicy
{
    public static IReadOnlyList<RetentionRemoval> Plan(IEnumerable<RetentionCandidate> versions, RetentionRules rules, PackageDeletionBehavior deletion, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(rules);
        var removals = new List<RetentionRemoval>();
        var all = versions.ToList();

        if (rules.PruneCachedAfterDays is { } pruneDays)
        {
            var cutoff = utcNow.AddDays(-pruneDays);
            removals.AddRange(all
                .Where(v => v.Origin == PackageOrigin.Cached && v.LastUsedUtc < cutoff)
                .Select(v => new RetentionRemoval(v, RetentionReason.UnusedCache)));
        }

        if (rules.KeepStable is null && rules.KeepPrerelease is null)
        {
            return removals;
        }

        var usedSince = rules.KeepIfUsedWithinDays is { } usedDays ? utcNow.AddDays(-usedDays) : (DateTime?)null;
        var pushed = all.Where(v => v.Origin == PackageOrigin.Pushed && (deletion == PackageDeletionBehavior.HardDelete || v.Listed));
        foreach (var package in pushed.GroupBy(v => v.IdLower, StringComparer.Ordinal))
        {
            var ordered = package
                .Select(v => (Candidate: v, Version: NuGetVersion.Parse(v.NormalizedVersion)))
                .OrderByDescending(v => v.Version)
                .ToList();
            var newest = ordered[0].Candidate;

            foreach (var line in ordered.GroupBy(v => rules.PerMajorVersion ? v.Version.Major : 0))
            {
                Select(line.Where(v => !v.Version.IsPrerelease).Select(v => v.Candidate), rules.KeepStable, RetentionReason.OlderStable);
                Select(line.Where(v => v.Version.IsPrerelease).Select(v => v.Candidate), rules.KeepPrerelease, RetentionReason.OlderPrerelease);
            }

            void Select(IEnumerable<RetentionCandidate> newestFirst, int? keep, RetentionReason reason)
            {
                if (keep is null)
                {
                    return;
                }

                removals.AddRange(newestFirst
                    .Skip(keep.Value)
                    .Where(v => !ReferenceEquals(v, newest) && !(usedSince is not null && v.LastUsedUtc >= usedSince))
                    .Select(v => new RetentionRemoval(v, reason)));
            }
        }

        return removals;
    }
}
