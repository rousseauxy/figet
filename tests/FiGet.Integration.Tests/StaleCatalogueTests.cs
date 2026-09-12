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
    /// </summary>
    [Fact]
    public async Task Readers_of_the_same_stale_catalogue_cause_one_refresh()
    {
        var id = FiGetServerFixture.UniqueId("Stale.Once");
        AddUpstream(id, "1.0.0");

        await FindAsync(id);
        var afterFirst = server.Upstream.CatalogCalls;

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => FindAsync(id)));

        // The refresh is out of band, so it is waited for rather than assumed - bounded, because a test
        // that hangs on a broken queue tells nobody anything.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (server.Upstream.CatalogCalls == afterFirst && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        var refreshes = server.Upstream.CatalogCalls - afterFirst;
        Assert.True(refreshes >= 1, "the stale catalogue was never refreshed behind the request");
        Assert.True(refreshes <= 2, $"eight readers caused {refreshes} refreshes; duplicates are not being collapsed");
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
