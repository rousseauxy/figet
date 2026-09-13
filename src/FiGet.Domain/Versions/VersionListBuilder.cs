using FiGet.Domain.Entities;
using NuGet.Versioning;

namespace FiGet.Domain.Versions;

/// <summary>Where a version in a merged version list comes from.</summary>
public enum VersionSource
{
    Local,
    Upstream,
}

/// <summary>A version reported by one source, before merging.</summary>
/// <typeparam name="T">The payload carried along, typically the local metadata row.</typeparam>
public sealed record VersionCandidate<T>(NuGetVersion Version, bool Listed, bool IsSemVer2, VersionSource Source, T? Payload);

/// <summary>A version after merging, with the latest flags computed over the whole merged list.</summary>
public sealed record VersionListEntry<T>(
    NuGetVersion Version,
    bool Listed,
    bool IsSemVer2,
    bool IsLatestVersion,
    bool IsAbsoluteLatestVersion,
    VersionSource Source,
    T? Payload);

/// <summary>
/// The single implementation of the merged version list rule (build plan §5). Every endpoint that lists
/// versions goes through this, so no two entries for one id can both claim to be latest.
/// </summary>
public static class VersionListBuilder
{
    /// <summary>
    /// Merges candidates from all sources, de-duplicated by normalised version (local wins over upstream),
    /// sorted ascending. <c>IsLatestVersion</c> marks the highest listed stable version and
    /// <c>IsAbsoluteLatestVersion</c> the highest listed version; each is true on at most one entry.
    /// </summary>
    /// <param name="candidates">Versions from every source.</param>
    /// <param name="includeSemVer2">
    /// When false, SemVer 2.0.0 versions are removed before the flags are computed: a client that cannot
    /// see them must still be told which visible version is latest.
    /// </param>
    public static IReadOnlyList<VersionListEntry<T>> Build<T>(IEnumerable<VersionCandidate<T>> candidates, bool includeSemVer2)
    {
        var merged = new Dictionary<NuGetVersion, VersionCandidate<T>>(VersionComparer.Default);
        foreach (var candidate in candidates)
        {
            if (!includeSemVer2 && candidate.IsSemVer2)
            {
                continue;
            }

            if (!merged.TryGetValue(candidate.Version, out var existing)
                || (existing.Source != VersionSource.Local && candidate.Source == VersionSource.Local))
            {
                merged[candidate.Version] = candidate;
            }
        }

        var ordered = merged.Values.OrderBy(c => c.Version, VersionComparer.Default).ToList();

        VersionCandidate<T>? latestStable = null;
        VersionCandidate<T>? absoluteLatest = null;
        foreach (var candidate in ordered)
        {
            if (!candidate.Listed)
            {
                continue;
            }

            absoluteLatest = candidate;
            if (!candidate.Version.IsPrerelease)
            {
                latestStable = candidate;
            }
        }

        return ordered
            .Select(c => new VersionListEntry<T>(
                c.Version,
                c.Listed,
                c.IsSemVer2,
                IsLatestVersion: ReferenceEquals(c, latestStable),
                IsAbsoluteLatestVersion: ReferenceEquals(c, absoluteLatest),
                c.Source,
                c.Payload))
            .ToList();
    }

    /// <summary>Merges the local rows of one package (phase 1 has no upstream sources yet).</summary>
    public static IReadOnlyList<VersionListEntry<PackageVersion>> BuildLocal(IEnumerable<PackageVersion> versions, bool includeSemVer2) =>
        Build(versions.Select(ToCandidate), includeSemVer2);

    /// <summary>The entry a listing shows as "the" version: absolute latest with prerelease, latest stable without.</summary>
    public static VersionListEntry<T>? Latest<T>(this IReadOnlyList<VersionListEntry<T>> list, bool includePrerelease) =>
        list.LastOrDefault(e => includePrerelease ? e.IsAbsoluteLatestVersion : e.IsLatestVersion);

    /// <summary>
    /// The version a page shows for a package: the latest stable, or with prerelease the absolute latest. A package with
    /// only prereleases shows its newest prerelease either way, rather than nothing.
    /// </summary>
    public static VersionListEntry<T>? Shown<T>(this IReadOnlyList<VersionListEntry<T>> list, bool includePrerelease) =>
        list.Latest(includePrerelease) ?? list.Latest(includePrerelease: true) ?? (list.Count > 0 ? list[^1] : null);

    /// <summary>One local row as a merge candidate, so callers can merge it with upstream candidates.</summary>
    public static VersionCandidate<PackageVersion> ToCandidate(PackageVersion v)
    {
        ArgumentNullException.ThrowIfNull(v);
        return
            new(NuGetVersion.Parse(v.NormalizedVersion), v.Listed, v.IsSemVer2, VersionSource.Local, v);
    }
}
