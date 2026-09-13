using FiGet.Application.Ports;
using FiGet.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FiGet.Integration.Tests;

/// <summary>
/// A feed whose catalogues are stale the moment they are written, so the refresh-behind-the-request path
/// is the one every request takes.
///
/// Its own fixture rather than a setting on the shared one: with a zero window every request queues a
/// refresh, and <c>The_upstream_listing_is_cached_for_the_configured_time</c> counts fetches on the shared
/// stub. A background refresh landing a moment after that assertion would make it fail sometimes and pass
/// usually, which is worse than either.
/// </summary>
public sealed class StaleCatalogueFixture() : FiGetServerFixture(TestDatabase.Sqlite)
{
    public StubUpstreamClient Upstream { get; } = new();

    protected override void Configure(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseSetting("FiGet:Feeds:5:Name", "stale");
        builder.UseSetting("FiGet:Feeds:5:AnonymousRead", "true");
        builder.UseSetting("FiGet:Feeds:5:Upstreams:0:Name", "stub");
        builder.UseSetting("FiGet:Feeds:5:Upstreams:0:Url", "https://stub.invalid/v3/index.json");

        // Nothing is ever fresh here, which is the point.
        builder.UseSetting("FiGet:Connector:UpstreamIndexTtl", "00:00:00");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IUpstreamClient>();
            services.AddSingleton<IUpstreamClient>(Upstream);
        });
    }
}

public sealed class StaleCatalogueTests(StaleCatalogueFixture server) : IClassFixture<StaleCatalogueFixture>
{
    /// <summary>
    /// The second request does not wait for the upstream. Before this, a catalogue older than the window
    /// was refetched in front of the reader: for a package with two thousand versions that is a walk of
    /// several megabytes, 13 to 15 seconds, paid again by whoever arrived after the window passed.
    ///
    /// Proven by making the upstream unreachable rather than by counting calls. A request that still
    /// answers with the package cannot have been waiting on an upstream that is refusing - and unlike a
    /// counter, that cannot be confused by the refresh landing behind the answer, which is precisely what
    /// this feature causes and what made the counting version of this test race itself.
    /// </summary>
    [Fact]
    public async Task A_stale_catalogue_is_served_without_waiting_for_the_upstream()
    {
        var id = FiGetServerFixture.UniqueId("Stale.Served");
        AddUpstream(id, "1.0.0");

        // First view of a package: nothing is cached, so this one does wait.
        Assert.Single(await FindAsync(id));

        server.Upstream.Fails = true;
        try
        {
            // Served from the catalogue written a moment ago, which is already stale here. With nothing
            // cached this same call answers with no entries at all, so one entry is the discriminator.
            Assert.Single(await FindAsync(id));
        }
        finally
        {
            server.Upstream.Fails = false;
        }
    }

    /// <summary>
    /// Readers arriving together cause one walk, not one each. Without collapsing duplicates, a package
    /// that goes stale while twenty people are looking at it would queue twenty identical fetches of the
    /// same several megabytes.
    ///
    /// The first version of this test raced and sometimes failed, because it measured the wrong thing. With
    /// a zero window every read is stale, and the queue promises one refresh *in flight at a time* - not one
    /// per burst. So if the refresh finished while readers were still arriving, a later one rightly started
    /// another, and under a loaded machine a third. It allowed two.
    ///
    /// Holding the upstream keeps the refresh from finishing mid-burst, so every duplicate lands while one is
    /// in flight. Counting while held would prove nothing, though: the refresh loop takes one item at a time,
    /// so even with collapsing broken the other seven would wait in the queue and never reach the upstream.
    /// The difference shows only once released - one queued refresh runs once, eight run eight times - so
    /// that is where this asserts, after letting the queue drain.
    /// </summary>
    [Fact]
    public async Task Readers_of_the_same_stale_catalogue_cause_one_refresh()
    {
        var id = FiGetServerFixture.UniqueId("Stale.Once");
        AddUpstream(id, "1.0.0");

        // First view: nothing cached, so this one fetches and waits.
        await FindAsync(id);
        var afterFirst = server.Upstream.CatalogCalls;

        server.Upstream.HoldCatalogues();
        try
        {
            // Every reader is answered from the stale catalogue at once, and each asks for a refresh.
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => FindAsync(id)));

            // Wait for the refresh to reach the upstream, bounded so a broken queue fails rather than hangs.
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (server.Upstream.CatalogCalls == afterFirst && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            Assert.Equal(afterFirst + 1, server.Upstream.CatalogCalls);
        }
        finally
        {
            server.Upstream.ReleaseCatalogues();
        }

        // Let the queue drain. Waiting longer can only expose duplicates, never invent them: nothing else is
        // queued when collapsing works, so the count cannot rise.
        await Task.Delay(1000);

        Assert.Equal(afterFirst + 1, server.Upstream.CatalogCalls);
    }

    /// <summary>
    /// A catalogue at the weight a real one has. Every other test here uses one to three versions, and
    /// that is how a defect this size reached production with a green suite: the descriptions were briefly
    /// persisted as JSON, which for a 2098-version package is 101 MB against 31 KB of version strings, and
    /// deserialising it threw OutOfMemoryException inside a one-gigabyte container.
    ///
    /// Four hundred versions carrying twenty kilobytes of tags each is about eight megabytes - far short
    /// of a gigabyte, but enough that anything round-tripping the whole description set per request shows
    /// up as a failure rather than as a page nobody loads until it is live.
    /// </summary>
    [Fact]
    public async Task A_heavily_described_catalogue_is_served()
    {
        var id = FiGetServerFixture.UniqueId("Stale.Heavy");
        server.Upstream.AddVersions(id, Enumerable.Range(1, 400).Select(n => $"1.0.{n}"));
        server.Upstream.TagPadding = " " + string.Join(' ', Enumerable.Range(0, 900).Select(n => $"PSCommand_Verb-Noun{n}"));

        try
        {
            // Twice: the first fetches and describes, the second takes the cached path that the OutOfMemory
            // came from - it was reading back what had just been written that failed, not writing it.
            Assert.NotEmpty(await FindAsync(id));
            Assert.NotEmpty(await FindAsync(id));
        }
        finally
        {
            server.Upstream.TagPadding = "";
        }
    }

    private void AddUpstream(string id, string version)
    {
        using var package = FiGet.Testing.TestPackages.Create(id, version);
        server.Upstream.Add(id, version, package.ToArray());
    }

    private async Task<IReadOnlyList<System.Xml.Linq.XElement>> FindAsync(string id)
    {
        using var client = server.CreateClient();
        var body = await HttpAssert.SuccessBodyAsync(
            await client.GetAsync($"nuget/stale/FindPackagesById()?id='{id}'"));

        System.Xml.Linq.XNamespace atom = "http://www.w3.org/2005/Atom";
        return System.Xml.Linq.XDocument.Parse(body).Root!.Elements(atom + "entry").ToList();
    }
}
