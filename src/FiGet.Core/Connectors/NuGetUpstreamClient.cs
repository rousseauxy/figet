using System.Collections.Concurrent;
using FiGet.Core.Entities;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace FiGet.Core.Connectors;

/// <summary>How the connector talks to upstreams. Bound from configuration by the host.</summary>
public sealed class ConnectorSettings
{
    /// <summary>How long a cached upstream version list stays usable before it is fetched again.</summary>
    public TimeSpan UpstreamIndexTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long one upstream call may take before that upstream counts as unavailable. Listing a package
    /// with hundreds of versions on a v2 gallery is a paged walk of several megabytes, so this is not the
    /// latency of a single request.
    /// </summary>
    public TimeSpan UpstreamTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Talks to upstream feeds with NuGet's own client library, which speaks both v2 and v3, so FiGet never
/// implements either protocol as a client. Repositories are kept per upstream because NuGet caches
/// resources and connections inside them.
/// </summary>
public sealed class NuGetUpstreamClient(ConnectorSettings settings) : IUpstreamClient, IDisposable
{
    private readonly ConcurrentDictionary<string, SourceRepository> repositories = new(StringComparer.Ordinal);
    private readonly SourceCacheContext cache = new() { NoCache = true, DirectDownload = true };

    public async Task<IReadOnlyList<UpstreamVersion>> GetVersionsAsync(FeedUpstream upstream, string idLower, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        using var timeout = Timeout(cancellationToken);
        // NuGet returns null when the source does not expose the resource, for example a URL that is not
        // a feed at all. The connector catches this per upstream and falls back to the cached list.
        var resource = await Repository(upstream).GetResourceAsync<FindPackageByIdResource>(timeout.Token)
            ?? throw new InvalidOperationException($"Upstream '{upstream.Name}' does not expose a package resource.");
        var versions = await resource.GetAllVersionsAsync(idLower, cache, NullLogger.Instance, timeout.Token);
        return versions.Select(v => new UpstreamVersion(v, IsSemVer2(v))).ToList();
    }

    public async Task<Stream?> OpenPackageAsync(FeedUpstream upstream, string idLower, NuGetVersion version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        ArgumentNullException.ThrowIfNull(version);
        using var timeout = Timeout(cancellationToken);
        // NuGet returns null when the source does not expose the resource, for example a URL that is not
        // a feed at all. The connector catches this per upstream and falls back to the cached list.
        var resource = await Repository(upstream).GetResourceAsync<FindPackageByIdResource>(timeout.Token)
            ?? throw new InvalidOperationException($"Upstream '{upstream.Name}' does not expose a package resource.");

        // Buffered to a temporary file that deletes itself: the caller needs a seekable stream to index the
        // package, and a cached package can be far larger than is comfortable in memory.
        var file = new FileStream(
            Path.Combine(Path.GetTempPath(), "figet-upstream-" + Guid.NewGuid().ToString("N") + ".tmp"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);

        try
        {
            if (!await resource.CopyNupkgToStreamAsync(idLower, version, file, cache, NullLogger.Instance, timeout.Token))
            {
                await file.DisposeAsync();
                return null;
            }

            file.Position = 0;
            return file;
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
    }

    public async Task<IReadOnlyList<UpstreamSearchHit>> SearchAsync(
        FeedUpstream upstream,
        string query,
        bool includePrerelease,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        using var timeout = Timeout(cancellationToken);
        var resource = await Repository(upstream).GetResourceAsync<PackageSearchResource>(timeout.Token)
            ?? throw new InvalidOperationException($"Upstream '{upstream.Name}' does not expose a search resource.");

        var results = await resource.SearchAsync(
            query,
            new SearchFilter(includePrerelease),
            skip,
            take,
            NullLogger.Instance,
            timeout.Token);

        return results
            .Select(r => new UpstreamSearchHit(
                r.Identity.Id,
                r.Identity.Version,
                r.Description ?? "",
                r.Authors ?? "",
                r.Tags ?? "",
                r.DownloadCount ?? 0))
            .ToList();
    }

    public void Dispose()
    {
        cache.Dispose();
        repositories.Clear();
    }

    /// <summary>
    /// Whether a version needs SemVer 2.0.0 to be understood, judged from the version alone: build
    /// metadata or dotted prerelease labels. A cached package is re-judged from its nuspec when it is
    /// indexed, which also sees dependency ranges.
    /// </summary>
    private static bool IsSemVer2(NuGetVersion version) =>
        version.HasMetadata || (version.IsPrerelease && version.ReleaseLabels.Count() > 1);

    private CancellationTokenSource Timeout(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(settings.UpstreamTimeout);
        return source;
    }

    private SourceRepository Repository(FeedUpstream upstream) =>
        repositories.GetOrAdd(upstream.Key.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + upstream.Url, _ =>
        {
            var source = new PackageSource(upstream.Url, "figet-upstream-" + upstream.Key.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                // Upstreams inside a network are routinely plain HTTP; the operator configured this URL.
                AllowInsecureConnections = true,
            };

            var secret = ReadSecret(upstream.CredentialRef);
            if (secret is not null)
            {
                source.Credentials = new PackageSourceCredential(source.Name, "figet", secret, isPasswordClearText: true, validAuthenticationTypesText: null);
            }

            return NuGet.Protocol.Core.Types.Repository.Factory.GetCoreV3(source);
        });

    /// <summary>Secrets are referenced by name and read from the environment, never stored in the database.</summary>
    private static string? ReadSecret(string? credentialRef)
    {
        if (string.IsNullOrWhiteSpace(credentialRef))
        {
            return null;
        }

        var value = Environment.GetEnvironmentVariable(credentialRef);
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
