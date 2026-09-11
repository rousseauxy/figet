using System.Text.RegularExpressions;
using FiGet.Core.Entities;
using FiGet.Core.Packages;
using FiGet.Core.Stores;
using FiGet.Core.Versions;
using Microsoft.Extensions.Logging;
using NuGet.Versioning;

namespace FiGet.Core.Connectors;

/// <summary>
/// Proxy-feed behaviour (build plan section 5). Two jobs: report what the upstreams hold so the merged
/// version list covers them, and fetch an exact version on demand so a pinned dependency resolves.
/// The merge itself stays in <see cref="VersionListBuilder"/>, which is what keeps exactly one version
/// flagged latest no matter how many sources answered.
/// </summary>
public sealed class ConnectorService(
    IUpstreamClient client,
    IUpstreamIndexStore index,
    IPackageStore packages,
    PackageIngestionService ingestion,
    ConnectorSettings settings,
    TimeProvider time,
    ILogger<ConnectorService> logger)
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Every version the feed's upstreams report for the id, as merge candidates with no local payload.
    /// A cached list is used while it is fresh; a failing upstream falls back to its last known list and
    /// is logged, because answering from a slightly old list beats failing a client's install.
    /// </summary>
    public async Task<IReadOnlyList<VersionCandidate<PackageVersion>>> UpstreamCandidatesAsync(Feed feed, string idLower, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        var candidates = new List<VersionCandidate<PackageVersion>>();
        foreach (var upstream in feed.Upstreams.Where(u => u.Enabled).OrderBy(u => u.Ordinal))
        {
            if (!Allows(upstream, idLower))
            {
                continue;
            }

            foreach (var version in await VersionsAsync(upstream, idLower, cancellationToken))
            {
                candidates.Add(new VersionCandidate<PackageVersion>(
                    version.Version,
                    Listed: true,
                    version.IsSemVer2,
                    VersionSource.Upstream,
                    Placeholder(idLower, version)));
            }
        }

        return candidates;
    }

    /// <summary>
    /// Makes sure one exact version exists locally, fetching it from the first upstream that has it and
    /// storing it as a cached package. Returns the local row, or null when no upstream has that version.
    /// </summary>
    public async Task<PackageVersion?> EnsureCachedAsync(Feed feed, string id, NuGetVersion version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(version);

        var idLower = id.ToLowerInvariant();
        var versionLower = version.ToNormalizedString().ToLowerInvariant();
        var existing = await packages.GetVersionAsync(feed.Key, idLower, versionLower, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        foreach (var upstream in feed.Upstreams.Where(u => u.Enabled).OrderBy(u => u.Ordinal))
        {
            if (!Allows(upstream, idLower))
            {
                continue;
            }

            Stream? nupkg;
            try
            {
                nupkg = await client.OpenPackageAsync(upstream, idLower, version, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Upstream {Upstream} could not serve {Id} {Version}.", upstream.Name, id, version.ToNormalizedString());
                continue;
            }

            if (nupkg is null)
            {
                continue;
            }

            await using (nupkg)
            {
                var result = await ingestion.PushAsync(feed, nupkg, PackageOrigin.Cached, cancellationToken);
                if (result.Outcome is PushOutcome.Created or PushOutcome.Replaced)
                {
                    logger.LogInformation("Cached {Id} {Version} from upstream {Upstream}.", id, version.ToNormalizedString(), upstream.Name);
                    return await packages.GetVersionAsync(feed.Key, idLower, versionLower, cancellationToken);
                }

                if (result.Outcome == PushOutcome.Conflict)
                {
                    // Another request cached it while this one was downloading.
                    return await packages.GetVersionAsync(feed.Key, idLower, versionLower, cancellationToken);
                }

                logger.LogWarning("Upstream {Upstream} served an unusable {Id} {Version}: {Message}", upstream.Name, id, version.ToNormalizedString(), result.Message);
            }
        }

        return null;
    }

    /// <summary>
    /// Metadata for a version that exists only upstream. A listing must be able to show it before anything
    /// has been downloaded, so the fields nobody can know yet stay empty and the real nuspec replaces this
    /// row the moment the package is cached. Marked as cached because that is what it will become.
    /// </summary>
    private static PackageVersion Placeholder(string idLower, UpstreamVersion version)
    {
        var normalized = version.Version.ToNormalizedString();
        return new PackageVersion
        {
            OriginalVersion = normalized,
            NormalizedVersion = normalized,
            NormalizedVersionLower = normalized.ToLowerInvariant(),
            IsPrerelease = version.Version.IsPrerelease,
            IsSemVer2 = version.IsSemVer2,
            Listed = true,
            Origin = PackageOrigin.Cached,
            SearchTextLower = idLower,
        };
    }

    /// <summary>Deny wins over allow, and an empty allow list means every id is allowed.</summary>
    public static bool Allows(FeedUpstream upstream, string idLower)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        foreach (var pattern in FeedUpstream.Patterns(upstream.Deny))
        {
            if (Matches(pattern, idLower))
            {
                return false;
            }
        }

        var allow = FeedUpstream.Patterns(upstream.Allow);
        return allow.Length == 0 || allow.Any(pattern => Matches(pattern, idLower));
    }

    private static bool Matches(string pattern, string idLower)
    {
        try
        {
            return Regex.IsMatch(idLower, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout);
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            // A bad pattern must not take the feed down: it simply matches nothing.
            return false;
        }
    }

    private async Task<IReadOnlyList<UpstreamVersion>> VersionsAsync(FeedUpstream upstream, string idLower, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var cached = await index.FindAsync(upstream.Key, idLower, cancellationToken);
        if (cached is not null && now - cached.FetchedUtc < settings.UpstreamIndexTtl)
        {
            return Parse(cached);
        }

        try
        {
            var versions = await client.GetVersionsAsync(upstream, idLower, cancellationToken);
            await index.SaveAsync(upstream.Key, idLower, versions, stale: false, now, cancellationToken);
            return versions;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Upstream {Upstream} did not answer for {Id}; serving the last known list.", upstream.Name, idLower);
            return cached is null ? [] : Parse(cached);
        }
    }

    private static List<UpstreamVersion> Parse(CachedUpstreamIndex cached)
    {
        var semVer2 = cached.SemVer2Versions.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var versions = new List<UpstreamVersion>();
        foreach (var text in cached.Versions.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (NuGetVersion.TryParse(text, out var version))
            {
                versions.Add(new UpstreamVersion(version, semVer2.Contains(text)));
            }
        }

        return versions;
    }
}
