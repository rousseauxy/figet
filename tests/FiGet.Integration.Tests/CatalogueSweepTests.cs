using System.Net;
using FiGet.Application.Ports;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Testing;
using FiGet.Web.Connectors;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

/// <summary>
/// The sweep that keeps stored catalogues current for the packages a feed holds. What it must not do matters as much
/// as what it does: it is the one job here that spends somebody else's bandwidth.
/// </summary>
public sealed class CatalogueSweepTests(ProxyServerFixture server) : IClassFixture<ProxyServerFixture>
{
    [Fact]
    public async Task The_sweep_refreshes_the_catalogue_of_an_id_the_feed_holds()
    {
        var id = FiGetServerFixture.UniqueId("Sweep.Held");
        using (var package = TestPackages.Create(id, "1.0.0"))
        {
            server.Upstream.Add(id, "1.0.0", package.ToArray());
        }

        // Held: fetched once, so the feed has a row and the upstream has a catalogue.
        using (var client = server.CreateClient())
        {
            HttpAssert.Status(HttpStatusCode.OK, await client.GetAsync($"nuget/proxy/package/{id}/1.0.0"));
        }

        await AgeCatalogueAsync(id);
        var before = server.Upstream.CatalogCallsFor(id);

        await RunAsync();
        await DrainAsync();

        Assert.True(server.Upstream.CatalogCallsFor(id) > before, "The sweep did not refresh the catalogue of an id this feed holds.");
    }

    /// <summary>
    /// The bound that matters most: the sweep asks about what this feed holds, never about the rest of a gallery.
    /// An id with no local row has no business costing a gallery a paged walk every night.
    /// </summary>
    [Fact]
    public async Task The_sweep_never_asks_about_an_id_the_feed_does_not_hold()
    {
        var id = FiGetServerFixture.UniqueId("Sweep.Stranger");
        using (var package = TestPackages.Create(id, "1.0.0"))
        {
            server.Upstream.Add(id, "1.0.0", package.ToArray());
        }

        // Listed but never fetched: a catalogue row exists, no package row does.
        using (var client = server.CreateClient())
        {
            HttpAssert.Status(HttpStatusCode.OK, await client.GetAsync($"nuget/proxy/FindPackagesById()?id='{id}'"));
        }

        await AgeCatalogueAsync(id);
        var before = server.Upstream.CatalogCallsFor(id);

        await RunAsync();
        await DrainAsync();

        // A negative assertion is only worth what its counter is worth, and the test above proves this counter moves
        // for an id the feed does hold, by the same mechanism.
        // Other ids in the fixture may well be refreshed; this one must not be asked about again.
        Assert.Equal(before, server.Upstream.CatalogCallsFor(id));
    }

    /// <summary>A catalogue somebody refreshed a minute ago is left alone, so a busy feed queues almost nothing.</summary>
    [Fact]
    public async Task A_catalogue_refreshed_within_the_interval_is_skipped()
    {
        var id = FiGetServerFixture.UniqueId("Sweep.Fresh");
        using (var package = TestPackages.Create(id, "1.0.0"))
        {
            server.Upstream.Add(id, "1.0.0", package.ToArray());
        }

        using (var client = server.CreateClient())
        {
            HttpAssert.Status(HttpStatusCode.OK, await client.GetAsync($"nuget/proxy/package/{id}/1.0.0"));
        }

        var before = server.Upstream.CatalogCallsFor(id);

        await RunAsync();
        await DrainAsync();

        Assert.Equal(before, server.Upstream.CatalogCallsFor(id));
    }

    private async Task RunAsync()
    {
        var sweep = server.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<CatalogueSweepService>().Single();
        await sweep.RunOnceAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The sweep only queues; the worker fetches. Waits for it to drain, so the assertion is about a fetch.</summary>
    private static async Task DrainAsync() => await Task.Delay(TimeSpan.FromSeconds(2));

    /// <summary>Makes the stored catalogue old enough for the sweep to consider it, without waiting a day.</summary>
    private async Task AgeCatalogueAsync(string id)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var feeds = scope.ServiceProvider.GetRequiredService<IFeedStore>();
        var index = scope.ServiceProvider.GetRequiredService<IUpstreamIndexStore>();
        var feed = await feeds.FindAsync("proxy", TestContext.Current.CancellationToken);
        foreach (var upstream in feed!.Upstreams)
        {
            var cached = await index.FindAsync(upstream.Key, id.ToLowerInvariant(), TestContext.Current.CancellationToken);
            if (cached is not null)
            {
                await index.SaveAsync(
                    upstream.Key,
                    id.ToLowerInvariant(),
                    cached.Id,
                    cached.Versions,
                    [],
                    stale: false,
                    DateTime.UtcNow.AddDays(-7),
                    TestContext.Current.CancellationToken);
            }
        }
    }
}
