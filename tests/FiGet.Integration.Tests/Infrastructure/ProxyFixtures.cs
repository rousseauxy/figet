using System.Collections.Concurrent;
using FiGet.Application.Connectors;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NuGet.Versioning;

namespace FiGet.Integration.Tests.Infrastructure;

/// <summary>
/// An upstream that serves what a test puts in it, and counts what was asked of it. Stubbing here rather
/// than running a second server keeps the proxy tests about FiGet's own merge and cache rules, and lets a
/// test prove that a cached answer was reused instead of fetched again.
/// </summary>
public sealed class StubUpstreamClient : IUpstreamClient
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte[]>> packages =
        new(StringComparer.OrdinalIgnoreCase);

    private int versionCalls;
    private int downloadCalls;
    private int searchCalls;

    /// <summary>How often a version list was actually fetched, as opposed to answered from the cache.</summary>
    public int VersionCalls => Volatile.Read(ref versionCalls);

    public int DownloadCalls => Volatile.Read(ref downloadCalls);

    public int SearchCalls => Volatile.Read(ref searchCalls);

    /// <summary>Set when a test wants the upstream to behave as unreachable.</summary>
    public bool Fails { get; set; }

    /// <summary>
    /// Set when a test wants the upstream to be slower than the connector's timeout. That surfaces as a
    /// cancelled task, which must be handled as "this upstream did not answer" and never reach the client.
    /// </summary>
    public bool TimesOut { get; set; }

    private void FailIfAsked()
    {
        if (TimesOut)
        {
            throw new TaskCanceledException("The stub upstream took longer than the connector allows.");
        }

        if (Fails)
        {
            throw new InvalidOperationException("The stub upstream is unreachable.");
        }
    }

    public void Add(string id, string version, byte[] nupkg)
    {
        var versions = packages.GetOrAdd(id, _ => new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase));
        versions[NuGetVersion.Parse(version).ToNormalizedString()] = nupkg;
    }

    /// <summary>Withdraws a version, the way a gallery pulls a module that should no longer be used.</summary>
    public void Remove(string id, string version)
    {
        if (packages.TryGetValue(id, out var versions))
        {
            versions.TryRemove(NuGetVersion.Parse(version).ToNormalizedString(), out _);
        }
    }

    public Task<IReadOnlyList<UpstreamVersion>> GetVersionsAsync(FeedUpstream upstream, string idLower, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref versionCalls);
        FailIfAsked();

        IReadOnlyList<UpstreamVersion> versions = packages.TryGetValue(idLower, out var found)
            ? found.Keys.Select(v => new UpstreamVersion(NuGetVersion.Parse(v), IsSemVer2: false)).ToList()
            : [];

        return Task.FromResult(versions);
    }

    /// <summary>Matches on the id, which is all the real galleries are asked for in these tests.</summary>
    public Task<IReadOnlyList<UpstreamSearchHit>> SearchAsync(
        FeedUpstream upstream,
        string query,
        bool includePrerelease,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref searchCalls);
        FailIfAsked();

        IReadOnlyList<UpstreamSearchHit> hits = packages
            .Where(p => string.IsNullOrWhiteSpace(query) || p.Key.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(p => new UpstreamSearchHit(
                p.Key,
                p.Value.Keys.Select(NuGetVersion.Parse).OrderBy(v => v).Last(),
                "Stub upstream package.",
                "stub",
                "",
                0))
            .Skip(skip)
            .Take(take)
            .ToList();

        return Task.FromResult(hits);
    }

    /// <summary>
    /// What this upstream "publishes" about each version. Tagged the way a PowerShell gallery tags a
    /// module, because those tags are what a client reads to decide a version can run at all.
    /// </summary>
    public Task<IReadOnlyList<UpstreamMetadata>> GetMetadataAsync(FeedUpstream upstream, string idLower, CancellationToken cancellationToken)
    {
        FailIfAsked();

        IReadOnlyList<UpstreamMetadata> described = packages.TryGetValue(idLower, out var versions)
            ? versions.Keys.Select(v => new UpstreamMetadata(
                NuGetVersion.Parse(v),
                "Described by the stub upstream.",
                "Stub summary.",
                idLower,
                "stub-author",
                "PSModule PSEdition_Desktop",
                "",
                "",
                "",
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                7)).ToList()
            : [];

        return Task.FromResult(described);
    }

    public Task<Stream?> OpenPackageAsync(FeedUpstream upstream, string idLower, NuGetVersion version, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref downloadCalls);
        FailIfAsked();

        if (packages.TryGetValue(idLower, out var versions)
            && versions.TryGetValue(version.ToNormalizedString(), out var bytes))
        {
            return Task.FromResult<Stream?>(new MemoryStream(bytes, writable: false));
        }

        return Task.FromResult<Stream?>(null);
    }
}

/// <summary>
/// A server with two proxy feeds and a stub upstream: <c>proxy</c> accepts every id, <c>guarded</c> denies
/// ids ending in <c>.secret</c> and allows only ids starting with <c>allowed</c>.
/// </summary>
public sealed class ProxyServerFixture() : FiGetServerFixture(TestDatabase.Sqlite)
{
    public StubUpstreamClient Upstream { get; } = new();

    protected override void Configure(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseSetting("FiGet:Feeds:3:Name", "proxy");
        builder.UseSetting("FiGet:Feeds:3:AnonymousRead", "true");
        builder.UseSetting("FiGet:Feeds:3:Upstreams:0:Name", "stub");
        builder.UseSetting("FiGet:Feeds:3:Upstreams:0:Url", "https://stub.invalid/v3/index.json");

        builder.UseSetting("FiGet:Feeds:4:Name", "guarded");
        builder.UseSetting("FiGet:Feeds:4:AnonymousRead", "true");
        builder.UseSetting("FiGet:Feeds:4:Upstreams:0:Name", "stub");
        builder.UseSetting("FiGet:Feeds:4:Upstreams:0:Url", "https://stub.invalid/v3/index.json");
        builder.UseSetting("FiGet:Feeds:4:Upstreams:0:Allow:0", "^allowed");
        builder.UseSetting("FiGet:Feeds:4:Upstreams:0:Deny:0", "secret$");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IUpstreamClient>();
            services.AddSingleton<IUpstreamClient>(Upstream);
        });
    }
}
