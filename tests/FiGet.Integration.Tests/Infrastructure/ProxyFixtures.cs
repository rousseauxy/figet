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

    private readonly ConcurrentDictionary<string, bool> unlisted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When this upstream says it published a version, keyed "id|version"; the default below when absent.</summary>
    private readonly ConcurrentDictionary<string, DateTime> published = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What a version was published on when a test does not care. Fixed, so an assertion can name it.</summary>
    public static readonly DateTime DefaultPublished = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private int versionCalls;
    private int downloadCalls;
    private int searchCalls;

    /// <summary>How often a catalogue was actually fetched, as opposed to answered from the cache.</summary>
    public int VersionCalls => Volatile.Read(ref versionCalls);

    /// <summary>The same count under the name the one-walk rule is about.</summary>
    public int CatalogCalls => Volatile.Read(ref versionCalls);

    private readonly ConcurrentDictionary<string, int> catalogCallsById = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How often this upstream was asked for one id's catalogue.</summary>
    public int CatalogCallsFor(string id) => catalogCallsById.TryGetValue(id.ToLowerInvariant(), out var count) ? count : 0;

    public int DownloadCalls => Volatile.Read(ref downloadCalls);

    public int SearchCalls => Volatile.Read(ref searchCalls);

    private int versionsOnlyCalls;

    /// <summary>How often the versions-only call was answered.</summary>
    public int VersionsOnlyCalls => Volatile.Read(ref versionsOnlyCalls);

    /// <summary>
    /// Whether this upstream answers the versions-only call, the way a v3 source does. Off by default, which is how a
    /// v2 gallery behaves: every listing then comes from the full catalogue.
    /// </summary>
    public bool AnswersVersionsOnly { get; set; }

    public Task<IReadOnlyList<UpstreamVersion>?> GetVersionsAsync(FeedUpstream upstream, string idLower, CancellationToken cancellationToken)
    {
        FailIfAsked();
        if (!AnswersVersionsOnly)
        {
            return Task.FromResult<IReadOnlyList<UpstreamVersion>?>(null);
        }

        Interlocked.Increment(ref versionsOnlyCalls);
        IReadOnlyList<UpstreamVersion> versions = packages.TryGetValue(idLower, out var found)
            ? found.Keys.Select(v => new UpstreamVersion(NuGetVersion.Parse(v), IsSemVer2: false)).ToList()
            : [];
        return Task.FromResult<IReadOnlyList<UpstreamVersion>?>(versions);
    }

    /// <summary>Set when a test wants the upstream to behave as unreachable.</summary>
    public bool Fails { get; set; }

    /// <summary>
    /// Padding added to every version's tags, so a test can stand a package up at the weight a real one
    /// has. A PowerShell gallery writes one tag per exported command per version: PnP.PowerShell's 2098
    /// versions are 31 KB of version strings and 101 MB of description, which is the difference between
    /// what may be stored and what may only be held.
    /// </summary>
    public string TagPadding { get; set; } = "";

    /// <summary>
    /// Set when a test wants the upstream to be slower than the connector's timeout. That surfaces as a
    /// cancelled task, which must be handled as "this upstream did not answer" and never reach the client.
    /// </summary>
    public bool TimesOut { get; set; }

    /// <summary>
    /// Whether this upstream says anything *about* the versions it lists. False models the state every
    /// restart begins in: the version list is read back from the database, while what the gallery says
    /// about those versions lives in memory and is gone.
    /// </summary>
    public bool Describes { get; set; } = true;

    /// <summary>
    /// While set, catalogue calls wait here until released. Lets a test keep a refresh in flight for as long
    /// as it needs, so what it asserts about duplicate refreshes does not depend on how quickly one finishes.
    /// </summary>
    private TaskCompletionSource? catalogGate;

    /// <summary>Makes catalogue calls wait until <see cref="ReleaseCatalogues"/> is called.</summary>
    public void HoldCatalogues() =>
        Volatile.Write(ref catalogGate, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    /// <summary>Lets any waiting catalogue calls finish, and stops holding new ones.</summary>
    public void ReleaseCatalogues() => Interlocked.Exchange(ref catalogGate, null)?.TrySetResult();

    /// <summary>Dependencies this upstream declares, keyed by "id|version".</summary>
    private readonly ConcurrentDictionary<string, List<UpstreamDependency>> dependencies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Declares a dependency for one version, the way a gallery does for a module.</summary>
    public void AddDependency(string id, string version, string dependencyId, string range) =>
        dependencies.GetOrAdd(id + "|" + NuGetVersion.Parse(version).ToNormalizedString(), _ => [])
            .Add(new UpstreamDependency("", dependencyId, range));

    private void FailIfAsked()
    {
        if (TimesOut)
        {
            throw new TaskCanceledException("The stub upstream took longer than the connector allows.");
        }

        if (Fails)
        {
            throw new HttpRequestException("The stub upstream is unreachable.");
        }
    }

    /// <summary>
    /// Registers many versions at once without building a package for each. The catalogue path never
    /// reads the bytes - only a download does - and standing up hundreds of real nupkgs would make a
    /// scale test too slow to keep.
    /// </summary>
    public void AddVersions(string id, IEnumerable<string> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        var known = packages.GetOrAdd(id, _ => new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase));
        foreach (var version in versions)
        {
            known[NuGetVersion.Parse(version).ToNormalizedString()] = [];
        }
    }

    public void Add(string id, string version, byte[] nupkg)
    {
        var versions = packages.GetOrAdd(id, _ => new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase));
        versions[NuGetVersion.Parse(version).ToNormalizedString()] = nupkg;
    }

    /// <summary>
    /// The same, for a version this upstream says it published on a particular day. A gallery's date is the one thing
    /// a listing cannot invent for a version nobody has cached, so a test about dates has to be able to set it.
    /// </summary>
    public void Add(string id, string version, byte[] nupkg, DateTime publishedUtc)
    {
        Add(id, version, nupkg);
        published[id + "|" + NuGetVersion.Parse(version).ToNormalizedString()] = publishedUtc;
    }

    /// <summary>
    /// Adds a version the upstream holds but no longer advertises, the way a gallery hides an old nightly.
    /// It must still be downloadable by exact version: a pinned dependency asks for one.
    /// </summary>
    public void AddUnlisted(string id, string version, byte[] nupkg)
    {
        Add(id, version, nupkg);
        unlisted[id + "|" + NuGetVersion.Parse(version).ToNormalizedString()] = true;
    }

    /// <summary>Withdraws a version, the way a gallery pulls a module that should no longer be used.</summary>
    public void Remove(string id, string version)
    {
        if (packages.TryGetValue(id, out var versions))
        {
            versions.TryRemove(NuGetVersion.Parse(version).ToNormalizedString(), out _);
        }
    }

    /// <summary>
    /// The versions and their descriptions in one answer, the way a real upstream gives them: a v2 gallery
    /// walks one paged endpoint for both. <see cref="CatalogCalls"/> counts the walks, so a test can prove
    /// a listing costs one and not two.
    /// </summary>
    public async Task<UpstreamCatalog> GetCatalogAsync(FeedUpstream upstream, string idLower, CancellationToken cancellationToken)
    {
        // Counted on the way in, before the gate: a test holding the upstream needs to see that a caller
        // arrived, not only that one finished. Per id as well as in total, because "this package was never
        // asked about" is a different claim from "fewer calls happened".
        Interlocked.Increment(ref versionCalls);
        catalogCallsById.AddOrUpdate(idLower, 1, (_, count) => count + 1);
        if (Volatile.Read(ref catalogGate) is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        FailIfAsked();

        if (!packages.TryGetValue(idLower, out var found))
        {
            return new UpstreamCatalog([], []);
        }

        var versions = found.Keys
            .Select(v => new UpstreamVersion(NuGetVersion.Parse(v), IsSemVer2: false))
            .ToList();

        // Tagged the way a PowerShell gallery tags a module, because those tags are what a client reads
        // to decide a version can run at all.
        var described = found.Keys
            .Select(v => new UpstreamMetadata(
                NuGetVersion.Parse(v),
                "Described by the stub upstream.",
                "Stub summary.",
                idLower,
                "stub-author",
                "PSModule PSEdition_Desktop" + TagPadding,
                "",
                "",
                "",
                published.TryGetValue(idLower + "|" + v, out var publishedUtc) ? publishedUtc : DefaultPublished,
                7,
                !unlisted.ContainsKey(idLower + "|" + v),
                dependencies.TryGetValue(idLower + "|" + v, out var declared) ? declared : []))
            .ToList();

        // The spelling this upstream knows the package by, recovered from the key it was added under:
        // a real gallery answers a lower-cased request with its own casing, and so must this.
        var casedId = packages.Keys.FirstOrDefault(k => k.Equals(idLower, StringComparison.OrdinalIgnoreCase)) ?? idLower;
        return new UpstreamCatalog(versions, Describes ? described : [], casedId);
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
            // In a stable order, as a gallery's ranking is, so paging through it means something.
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
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

    /// <summary>Release notes this upstream reports for one version, keyed "id|version".</summary>
    private readonly ConcurrentDictionary<string, string> releaseNotes = new(StringComparer.OrdinalIgnoreCase);

    private int noteCalls;

    /// <summary>How often this upstream was asked for release notes: the fetching is meant to be rare and capped.</summary>
    public int NoteCalls => Volatile.Read(ref noteCalls);

    /// <summary>Gives a version release notes, the way a v2 gallery reports them on its single-entry route.</summary>
    public void SetReleaseNotes(string id, string version, string notes) =>
        releaseNotes[id + "|" + NuGetVersion.Parse(version).ToNormalizedString()] = notes;

    public Task<string?> GetReleaseNotesAsync(FeedUpstream upstream, string idLower, NuGetVersion version, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref noteCalls);
        FailIfAsked();
        return Task.FromResult(releaseNotes.TryGetValue(idLower + "|" + version.ToNormalizedString(), out var notes) ? notes : null);
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
/// Sends each upstream's calls to its own stub by upstream name, so a feed with two upstreams can hold two
/// different packages under one id. Any name without a stub of its own goes to the default stub.
/// </summary>
public sealed class RoutingUpstreamClient(StubUpstreamClient fallback, IReadOnlyDictionary<string, StubUpstreamClient> byName) : IUpstreamClient
{
    private StubUpstreamClient For(FeedUpstream upstream) =>
        byName.TryGetValue(upstream.Name, out var stub) ? stub : fallback;

    public Task<UpstreamCatalog> GetCatalogAsync(FeedUpstream upstream, string idLower, CancellationToken cancellationToken) =>
        For(upstream).GetCatalogAsync(upstream, idLower, cancellationToken);

    public Task<IReadOnlyList<UpstreamSearchHit>> SearchAsync(FeedUpstream upstream, string query, bool includePrerelease, int skip, int take, CancellationToken cancellationToken) =>
        For(upstream).SearchAsync(upstream, query, includePrerelease, skip, take, cancellationToken);

    public Task<Stream?> OpenPackageAsync(FeedUpstream upstream, string idLower, NuGetVersion version, CancellationToken cancellationToken) =>
        For(upstream).OpenPackageAsync(upstream, idLower, version, cancellationToken);

    public Task<IReadOnlyList<UpstreamVersion>?> GetVersionsAsync(FeedUpstream upstream, string idLower, CancellationToken cancellationToken) =>
        For(upstream).GetVersionsAsync(upstream, idLower, cancellationToken);

    public Task<string?> GetReleaseNotesAsync(FeedUpstream upstream, string idLower, NuGetVersion version, CancellationToken cancellationToken) =>
        For(upstream).GetReleaseNotesAsync(upstream, idLower, version, cancellationToken);
}

/// <summary>
/// A server with proxy feeds over stub upstreams: <c>proxy</c> accepts every id, <c>guarded</c> denies
/// ids ending in <c>.secret</c> and allows only ids starting with <c>allowed</c>, <c>merging</c> opts into
/// merging pushed ids with its upstream, and <c>layered</c> has two upstreams - <c>primary</c> first, answered by
/// <see cref="Upstream"/>, and <c>secondary</c>, answered by <see cref="SecondUpstream"/>.
/// </summary>
public sealed class ProxyServerFixture() : FiGetServerFixture(TestDatabase.Sqlite)
{
    public StubUpstreamClient Upstream { get; } = new();

    /// <summary>The <c>secondary</c> upstream of the <c>layered</c> feed.</summary>
    public StubUpstreamClient SecondUpstream { get; } = new();

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

        builder.UseSetting("FiGet:Feeds:5:Name", "merging");
        builder.UseSetting("FiGet:Feeds:5:AnonymousRead", "true");
        builder.UseSetting("FiGet:Feeds:5:MergePushedIdsWithUpstreams", "true");
        builder.UseSetting("FiGet:Feeds:5:Upstreams:0:Name", "stub");
        builder.UseSetting("FiGet:Feeds:5:Upstreams:0:Url", "https://stub.invalid/v3/index.json");

        builder.UseSetting("FiGet:Feeds:6:Name", "layered");
        builder.UseSetting("FiGet:Feeds:6:AnonymousRead", "true");
        builder.UseSetting("FiGet:Feeds:6:Upstreams:0:Name", "primary");
        builder.UseSetting("FiGet:Feeds:6:Upstreams:0:Url", "https://primary.invalid/v3/index.json");
        builder.UseSetting("FiGet:Feeds:6:Upstreams:1:Name", "secondary");
        builder.UseSetting("FiGet:Feeds:6:Upstreams:1:Url", "https://secondary.invalid/v3/index.json");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IUpstreamClient>();
            services.AddSingleton<IUpstreamClient>(new RoutingUpstreamClient(
                Upstream,
                new Dictionary<string, StubUpstreamClient>(StringComparer.OrdinalIgnoreCase) { ["secondary"] = SecondUpstream }));

            // No pause after a failing upstream: these tests switch the stub's failures on and off in quick succession, and a
            // pause left by one would answer the next test's first question. The test of the pause turns it on for itself.
            var settings = services.Last(d => d.ServiceType == typeof(ConnectorSettings));
            services.Remove(settings);
            services.AddSingleton(provider =>
            {
                var built = (ConnectorSettings)settings.ImplementationFactory!.Invoke(provider);
                built.UnreachableBackoff = TimeSpan.Zero;
                return built;
            });

            // The real file storage, with writes failing for the ids a test names, as a full disk or a lost share does.
            var registered = services.Last(d => d.ServiceType == typeof(IPackageStorage));
            services.Remove(registered);
            services.AddSingleton<IPackageStorage>(provider => new FailingPackageStorage(
                (IPackageStorage)(registered.ImplementationFactory?.Invoke(provider) ?? registered.ImplementationInstance!),
                FailingWrites));
        });
    }

    /// <summary>Lower-cased ids whose package writes throw an <see cref="IOException"/>.</summary>
    public ConcurrentDictionary<string, bool> FailingWrites { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A package storage whose writes fail for chosen ids; everything else goes to the real storage.</summary>
public sealed class FailingPackageStorage(IPackageStorage inner, ConcurrentDictionary<string, bool> failing) : IPackageStorage
{
    public Task SavePackageAsync(PackageStorageKey key, Stream nupkg, ReadOnlyMemory<byte> nuspec, bool overwrite, CancellationToken cancellationToken) =>
        failing.ContainsKey(key.Id)
            ? throw new IOException("There is not enough space on the disk.")
            : inner.SavePackageAsync(key, nupkg, nuspec, overwrite, cancellationToken);

    public Task<Stream?> OpenPackageAsync(PackageStorageKey key, CancellationToken cancellationToken) => inner.OpenPackageAsync(key, cancellationToken);

    public Task<Stream?> OpenNuspecAsync(PackageStorageKey key, CancellationToken cancellationToken) => inner.OpenNuspecAsync(key, cancellationToken);

    public Task DeletePackageAsync(PackageStorageKey key, CancellationToken cancellationToken) => inner.DeletePackageAsync(key, cancellationToken);

    public Task SaveSymbolAsync(SymbolStorageKey key, Stream pdb, CancellationToken cancellationToken) => inner.SaveSymbolAsync(key, pdb, cancellationToken);

    public Task<Stream?> OpenSymbolAsync(SymbolStorageKey key, CancellationToken cancellationToken) => inner.OpenSymbolAsync(key, cancellationToken);

    public Task DeleteSymbolAsync(SymbolStorageKey key, CancellationToken cancellationToken) => inner.DeleteSymbolAsync(key, cancellationToken);

    public Task DeleteFeedAsync(int feedKey, CancellationToken cancellationToken) => inner.DeleteFeedAsync(feedKey, cancellationToken);
}
