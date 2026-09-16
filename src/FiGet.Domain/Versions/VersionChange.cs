using NuGet.Versioning;

namespace FiGet.Domain.Versions;

/// <summary>
/// What changed between two versions of one package, as a report of recent activity reads it. Here rather than in the
/// page or the job that shows it, for the reason decision 0001 lets this project's domain reference NuGet.Versioning:
/// which version is newer is the rule this server exists to get right, and two copies of it would drift.
/// </summary>
public static class VersionChange
{
    /// <summary>
    /// Whether moving from <paramref name="previous"/> to <paramref name="next"/> crosses a major version - the one
    /// thing a person reading a list of updates wants marked, because that is where a package is allowed to remove
    /// what somebody's script calls.
    ///
    /// A first version is not a breaking change: there was nothing to break. A prerelease of the next major is one,
    /// because that is exactly where the removals land and where somebody testing it needs to look.
    ///
    /// Says nothing about 0.x, where the ecosystem lets a minor version break just as thoroughly. Reporting every 0.x
    /// minor as breaking would mark most of them and mean nothing; the version number is in the row for the reader.
    /// </summary>
    public static bool IsBreaking(NuGetVersion? previous, NuGetVersion next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return previous is not null && next.Major > previous.Major;
    }

    /// <summary>
    /// The version a client would have been installing before <paramref name="version"/> appeared: the highest one
    /// below it, or null when it is the first. Compared as NuGet compares versions, so 1.10.0 is above 1.9.0 and
    /// 1.0 and 1.0.0.0 are the same version rather than two.
    /// </summary>
    public static NuGetVersion? Previous(IEnumerable<NuGetVersion> held, NuGetVersion version)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(version);
        return held
            .Where(v => VersionComparer.Default.Compare(v, version) < 0)
            .OrderByDescending(v => v, VersionComparer.Default)
            .FirstOrDefault();
    }
}
