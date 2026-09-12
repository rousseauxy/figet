using System.Collections.Concurrent;
using FiGet.Application.Connectors;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace FiGet.Infrastructure.Upstream;


/// <summary>
/// Talks to upstream feeds with NuGet's own client library, which speaks both v2 and v3, so FiGet never
/// implements either protocol as a client. Repositories are kept per upstream because NuGet caches
/// resources and connections inside them.
/// </summary>
public sealed class NuGetUpstreamClient(ConnectorSettings settings) : IUpstreamClient, IDisposable
{
    private readonly ConcurrentDictionary<string, SourceRepository> repositories = new(StringComparer.Ordinal);
    private readonly SourceCacheContext cache = new() { NoCache = true, DirectDownload = true };

    /// <summary>
    /// One upstream answer carrying both the version list and the descriptions.
    ///
    /// The split is the whole point. On a v2 gallery the version list and the metadata are the same paged
    /// walk of <c>FindPackagesById()</c> behind two resources, so asking both cost that walk twice: for a
    /// package with 2098 versions, 13.4s for the versions and another 8.5s for the descriptions, measured
    /// 2026-09-12 against the real gallery. Taking both from the metadata walk costs 8.4s and misses no
    /// version. On a v3 source they are genuinely different endpoints, and the cheap one is also the
    /// authoritative one: the flat container answered in 0.17s where the registration walk took 3.2s, so
    /// there the version list is still read from the resource that decides what can be downloaded.
    /// </summary>
    public async Task<UpstreamCatalog> GetCatalogAsync(FeedUpstream upstream, string idLower, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        using var timeout = Timeout(cancellationToken);
        var repository = Repository(upstream);

        // NuGet returns null when the source does not expose the resource, for example a URL that is not
        // a feed at all. The connector catches this per upstream and falls back to the cached catalogue.
        var metadata = await repository.GetResourceAsync<PackageMetadataResource>(timeout.Token)
            ?? throw new InvalidOperationException($"Upstream '{upstream.Name}' does not expose a metadata resource.");

        // includeUnlisted: true, because this has to answer for every version the upstream holds, not only
        // the ones it advertises: a pinned dependency asks for an exact version and does not care whether
        // the gallery still lists it. What FiGet lists is decided after the merge, not here.
        var items = await metadata.GetMetadataAsync(idLower, includePrerelease: true, includeUnlisted: true, cache, NullLogger.Instance, timeout.Token);
        var described = items.Select(ToMetadata).ToList();

        // The gallery's own spelling, which only the metadata carries: the version resources answer for an
        // id they were given and hand back nothing about how it is written.
        var casedId = items.Select(m => m.Identity.Id).FirstOrDefault(i => !string.IsNullOrEmpty(i)) ?? "";

        // A source with no v3 service index is a v2 gallery, where the walk above already listed every
        // version and a second resource would only repeat it.
        var serviceIndex = await repository.GetResourceAsync<ServiceIndexResourceV3>(timeout.Token);
        if (serviceIndex is null)
        {
            var walked = described
                .Select(m => m.Version)
                .Distinct()
                .OrderBy(v => v, VersionComparer.Default)
                .Select(v => new UpstreamVersion(v, IsSemVer2(v)))
                .ToList();

            return new UpstreamCatalog(walked, described, casedId);
        }

        var byId = await repository.GetResourceAsync<FindPackageByIdResource>(timeout.Token)
            ?? throw new InvalidOperationException($"Upstream '{upstream.Name}' does not expose a package resource.");
        var versions = await byId.GetAllVersionsAsync(idLower, cache, NullLogger.Instance, timeout.Token);
        return new UpstreamCatalog(versions.Select(v => new UpstreamVersion(v, IsSemVer2(v))).ToList(), described, casedId);
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

    /// <summary>What one upstream entry says about itself, with the gaps turned into empty strings.</summary>
    private static UpstreamMetadata ToMetadata(IPackageSearchMetadata m) => new(
        m.Identity.Version,
        m.Description ?? "",
        m.Summary ?? "",
        m.Title ?? "",
        m.Authors ?? "",
        m.Tags ?? "",
        m.ProjectUrl?.ToString() ?? "",
        m.IconUrl?.ToString() ?? "",
        m.LicenseUrl?.ToString() ?? "",
        m.Published?.UtcDateTime,
        m.DownloadCount ?? 0,
        m.IsListed,
        ToDependencies(m));

    /// <summary>
    /// What the upstream declares this version depends on, in exactly the shape <c>PackageIndexer</c>
    /// produces for a pushed package - same framework spelling, same range normalisation, same
    /// one-row-for-an-empty-group rule - so a package reports the same dependencies before and after
    /// somebody caches it. A difference there would be a nastier defect than the one this fixes.
    ///
    /// Free of extra network traffic: the metadata resource already returns these in the call being made
    /// for the description.
    /// </summary>
    private static IReadOnlyList<UpstreamDependency> ToDependencies(IPackageSearchMetadata m)
    {
        var groups = m.DependencySets?.ToList();
        if (groups is null || groups.Count == 0)
        {
            return [];
        }

        var dependencies = new List<UpstreamDependency>();
        foreach (var group in groups)
        {
            var framework = group.TargetFramework is null || group.TargetFramework.IsAny || group.TargetFramework.IsUnsupported
                ? ""
                : group.TargetFramework.GetShortFolderName();

            var packages = group.Packages?.ToList() ?? [];
            if (packages.Count == 0)
            {
                dependencies.Add(new UpstreamDependency(framework, null, ""));
                continue;
            }

            dependencies.AddRange(packages.Select(d => new UpstreamDependency(
                framework,
                d.Id,
                d.VersionRange is null || d.VersionRange.Equals(VersionRange.All) ? "" : d.VersionRange.ToNormalizedString())));
        }

        return dependencies;
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
