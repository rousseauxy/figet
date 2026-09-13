using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using Microsoft.Extensions.Logging;
using NuGet.Versioning;

namespace FiGet.Application.Connectors;

/// <summary>
/// Pulls a package and everything it depends on, so what the pull was for - a machine with no internet - can
/// install it. Fetching one package was not enough: <c>Microsoft.Graph</c> pins 38 sub-modules, and a machine given
/// only the meta-module failed on its first install. A client install already caches the whole closure, because the
/// client resolves the graph and asks for each package; this does the same walk for a pull from the UI.
///
/// The walk follows the rules a NuGet client does, because caching something no client would ask for is waste:
/// the lowest version that satisfies a range, stable unless the range itself starts at a prerelease. Dependencies
/// are read from the stored package, so each level is known only once the level above is here.
/// </summary>
public sealed class DependencyPuller(ConnectorService connector, IPackageStore packages, ILogger<DependencyPuller> logger)
{
    /// <summary>Packages one pull may touch. Microsoft.Graph is 39; a loose range elsewhere could be far more.</summary>
    public const int MaxPackages = 100;

    /// <summary>How deep the walk goes below the package that was pulled.</summary>
    public const int MaxDepth = 10;

    public async Task<PullReport> PullAsync(Feed feed, string id, NuGetVersion version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(version);

        var fetched = new List<string>();
        var present = new List<string>();
        var missing = new List<string>();
        var capped = false;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Id, NuGetVersion Version, int Depth)>();
        queue.Enqueue((id, version, 0));
        seen.Add(Key(id, version));

        while (queue.Count > 0)
        {
            var (currentId, currentVersion, depth) = queue.Dequeue();
            if (fetched.Count + present.Count >= MaxPackages)
            {
                capped = true;
                break;
            }

            var idLower = currentId.ToLowerInvariant();
            var label = $"{currentId} {currentVersion.ToNormalizedString()}";
            var held = await packages.GetVersionAsync(feed.Key, idLower, currentVersion.ToNormalizedString().ToLowerInvariant(), cancellationToken);
            if (held is null)
            {
                if (await connector.EnsureCachedAsync(feed, currentId, currentVersion, cancellationToken) is null)
                {
                    missing.Add(label);
                    continue;
                }

                fetched.Add(label);
            }
            else
            {
                // Already here, but its dependencies may not be: a package cached by an earlier single pull is the
                // exact case this exists for.
                present.Add(label);
            }

            if (depth >= MaxDepth)
            {
                capped = true;
                continue;
            }

            foreach (var dependency in await DependenciesAsync(feed, idLower, currentVersion, cancellationToken))
            {
                var resolved = await ResolveAsync(feed, dependency.Id, dependency.Range, cancellationToken);
                if (resolved is null)
                {
                    missing.Add($"{dependency.Id} {dependency.Range.PrettyPrint()}");
                    continue;
                }

                if (seen.Add(Key(dependency.Id, resolved)))
                {
                    queue.Enqueue((dependency.Id, resolved, depth + 1));
                }
            }
        }

        if (queue.Count > 0)
        {
            capped = true;
        }

        logger.LogInformation(
            "Pulled {Id} {Version} into {Feed} with its dependencies: {Fetched} fetched, {Present} already here, {Missing} unavailable{Capped}.",
            id,
            version.ToNormalizedString(),
            feed.Name,
            fetched.Count,
            present.Count,
            missing.Count,
            capped ? ", stopped at the limit" : "");

        return new PullReport(fetched, present, missing, capped);
    }

    /// <summary>
    /// What one stored version depends on. The group without a framework when there is one - every PowerShell module
    /// has only that - and otherwise every framework's group together: a pull does not know which framework the
    /// machine it is for will run, and the caps bound what that costs.
    /// </summary>
    private async Task<IReadOnlyList<(string Id, VersionRange Range)>> DependenciesAsync(Feed feed, string idLower, NuGetVersion version, CancellationToken cancellationToken)
    {
        var package = await packages.GetPackageAsync(feed.Key, idLower, includeDependencies: true, cancellationToken);
        var row = package?.Versions.FirstOrDefault(v => string.Equals(v.NormalizedVersion, version.ToNormalizedString(), StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            return [];
        }

        var declared = row.Dependencies.Where(d => !string.IsNullOrEmpty(d.Id)).ToList();
        var neutral = declared.Where(d => string.IsNullOrEmpty(d.TargetFramework)).ToList();
        var chosen = neutral.Count > 0 ? neutral : declared;

        return chosen
            .GroupBy(d => d.Id!, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.First().Id!, ParseRange(g.First().VersionRange)))
            .ToList();
    }

    /// <summary>
    /// The version a client would pick for a range: the lowest satisfying listed version, stable unless the range's
    /// lower bound is a prerelease. An unlisted version only when nothing listed fits - a pinned dependency may point
    /// at one the gallery has since hidden, and the client would still fetch it by exact version.
    /// </summary>
    private async Task<NuGetVersion?> ResolveAsync(Feed feed, string id, VersionRange range, CancellationToken cancellationToken)
    {
        var idLower = id.ToLowerInvariant();
        var candidates = new List<(NuGetVersion Version, bool Listed)>();

        var local = await packages.GetPackageAsync(feed.Key, idLower, includeDependencies: false, cancellationToken);
        if (local is not null)
        {
            candidates.AddRange(local.Versions.Select(v => (NuGetVersion.Parse(v.NormalizedVersion), v.Listed)));
        }

        var upstream = await connector.UpstreamCandidatesAsync(feed, idLower, cancellationToken);
        candidates.AddRange(upstream.Versions.Select(c => (c.Version, c.Listed)));

        var allowPrerelease = range.MinVersion?.IsPrerelease == true;
        var fitting = candidates
            .Where(c => range.Satisfies(c.Version) && (allowPrerelease || !c.Version.IsPrerelease))
            .ToList();

        return Lowest(fitting.Where(c => c.Listed).Select(c => c.Version)) ?? Lowest(fitting.Select(c => c.Version));
    }

    private static NuGetVersion? Lowest(IEnumerable<NuGetVersion> versions) =>
        versions.OrderBy(v => v, VersionComparer.Default).FirstOrDefault();

    private static VersionRange ParseRange(string text) =>
        string.IsNullOrWhiteSpace(text) || !VersionRange.TryParse(text, out var range) ? VersionRange.All : range;

    private static string Key(string id, NuGetVersion version) => id.ToLowerInvariant() + "|" + version.ToNormalizedString().ToLowerInvariant();
}

/// <summary>What a pull with dependencies did, as package labels ("Id Version"), and whether it stopped at a limit.</summary>
public sealed record PullReport(
    IReadOnlyList<string> Fetched,
    IReadOnlyList<string> AlreadyHere,
    IReadOnlyList<string> Unavailable,
    bool StoppedAtLimit)
{
    /// <summary>The package the pull was for could not be fetched at all.</summary>
    public bool Failed => Fetched.Count == 0 && AlreadyHere.Count == 0;
}
