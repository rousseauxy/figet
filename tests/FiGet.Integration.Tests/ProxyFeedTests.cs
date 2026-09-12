using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FiGet.Application.Connectors;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Infrastructure.Persistence;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

/// <summary>
/// Proxy feeds: the merged version list across local and upstream versions, look-through downloads that
/// cache, the cached upstream listing, allow and deny lists, and what happens when an upstream is down.
/// These are the behaviours the server being replaced gets wrong (docs/protocol-v2.md).
/// </summary>
public sealed class ProxyFeedTests(ProxyServerFixture server) : IClassFixture<ProxyServerFixture>
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Data = "http://schemas.microsoft.com/ado/2007/08/dataservices";
    private static readonly XNamespace Meta = "http://schemas.microsoft.com/ado/2007/08/dataservices/metadata";

    [Fact]
    public async Task Upstream_versions_are_listed_with_exactly_one_latest()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.Upstream");
        AddUpstream(id, "1.0.0");
        AddUpstream(id, "1.1.0");
        AddUpstream(id, "2.0.0-beta1");

        var entries = await FindAsync("proxy", id);

        Assert.Equal(["1.0.0", "1.1.0", "2.0.0-beta1"], entries.Select(e => Property(e, "Version")).ToArray());
        Assert.Single(entries, e => Property(e, "IsLatestVersion") == "true");
        Assert.Single(entries, e => Property(e, "IsAbsoluteLatestVersion") == "true");
        Assert.Equal("1.1.0", Property(entries.Single(e => Property(e, "IsLatestVersion") == "true"), "Version"));
    }

    /// <summary>
    /// The failure that drove this project: a version cached locally and the same package upstream must
    /// not produce two entries, and the newer upstream version must win the latest flag.
    /// </summary>
    [Fact]
    public async Task A_local_version_and_a_newer_upstream_version_make_one_list()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.Merge");
        using (var local = TestPackages.Create(id, "1.0.0"))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushAsync("proxy", local));
        }

        AddUpstream(id, "1.0.0");
        AddUpstream(id, "1.1.0");

        var entries = await FindAsync("proxy", id);

        Assert.Equal(["1.0.0", "1.1.0"], entries.Select(e => Property(e, "Version")).ToArray());
        Assert.Single(entries, e => Property(e, "IsLatestVersion") == "true");
        Assert.Equal("1.1.0", Property(entries.Single(e => Property(e, "IsLatestVersion") == "true"), "Version"));
    }

    [Fact]
    public async Task Downloading_an_upstream_version_caches_it_and_serves_it_locally_afterwards()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.Download");
        AddUpstream(id, "1.2.3");

        using var client = server.CreateClient();
        var before = server.Upstream.DownloadCalls;

        var first = await client.GetAsync($"nuget/proxy/package/{id}/1.2.3");
        Assert.True(first.IsSuccessStatusCode, $"{(int)first.StatusCode} {first.ReasonPhrase}");
        var bytes = await first.Content.ReadAsByteArrayAsync();
        Assert.Equal(0x50, bytes[0]);
        Assert.Equal(0x4b, bytes[1]);
        Assert.Equal(before + 1, server.Upstream.DownloadCalls);

        // Cached now: the second download must not reach the upstream again.
        await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/proxy/package/{id}/1.2.3"));
        Assert.Equal(before + 1, server.Upstream.DownloadCalls);

        // And the cached copy carries the real metadata from the nuspec, not the listing placeholder.
        var entry = (await FindAsync("proxy", id)).Single();
        Assert.Equal("SHA512", Property(entry, "PackageHashAlgorithm"));
        Assert.NotEmpty(Property(entry, "PackageHash"));
    }

    [Fact]
    public async Task An_unknown_upstream_version_is_still_a_404()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.Missing");
        AddUpstream(id, "1.0.0");

        using var client = server.CreateClient();
        HttpAssert.Status(HttpStatusCode.NotFound, await client.GetAsync($"nuget/proxy/package/{id}/9.9.9"));
    }

    [Fact]
    public async Task The_upstream_listing_is_cached_for_the_configured_time()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.Cache");
        AddUpstream(id, "1.0.0");

        var before = server.Upstream.VersionCalls;
        await FindAsync("proxy", id);
        var afterFirst = server.Upstream.VersionCalls;
        await FindAsync("proxy", id);

        Assert.Equal(before + 1, afterFirst);
        Assert.Equal(afterFirst, server.Upstream.VersionCalls);
    }

    /// <summary>
    /// A version the upstream no longer advertises is listed as unlisted, and is never the latest. It is
    /// not dropped: unlisted means undiscoverable, not gone, and an exact version must still resolve
    /// because a pinned dependency asks for one. Both halves are asserted here, because honouring the flag
    /// by filtering the version out would pass the first half and break every pinned install.
    /// </summary>
    [Fact]
    public async Task An_unlisted_upstream_version_is_not_latest_and_still_downloads()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.Unlisted");
        AddUpstream(id, "1.0.0");
        AddUpstreamUnlisted(id, "2.0.0");

        var entries = await FindAsync("proxy", id);
        var byVersion = entries.ToDictionary(e => Property(e, "Version"), StringComparer.Ordinal);

        Assert.Equal(["1.0.0", "2.0.0"], byVersion.Keys.OrderBy(v => v, StringComparer.Ordinal).ToArray());
        Assert.Equal("false", Property(byVersion["2.0.0"], "Listed"));
        Assert.Equal("true", Property(byVersion["1.0.0"], "Listed"));

        // The one the gallery still advertises is the latest, not the higher one it hides.
        Assert.Equal("1.0.0", Property(entries.Single(e => Property(e, "IsAbsoluteLatestVersion") == "true"), "Version"));

        using var client = server.CreateClient();
        var download = await client.GetAsync($"nuget/proxy/package/{id}/2.0.0");
        Assert.True(download.IsSuccessStatusCode, $"{(int)download.StatusCode} {download.ReasonPhrase}");
    }

    /// <summary>
    /// The descriptions have to arrive in the same call as the versions. They used to be two calls, and on
    /// a v2 gallery both are the same paged walk of <c>FindPackagesById()</c> behind two different NuGet
    /// resources, so every listing paid for that walk twice. Measured against the real gallery on
    /// 2026-09-12: for a package with 2098 versions the pair cost 21.9s, one walk costs 8.4s and misses no
    /// version. Counting the calls is the only way to see the difference from inside a test.
    /// </summary>
    [Fact]
    public async Task An_upstream_listing_is_described_without_a_second_call()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.OneWalk");
        AddUpstream(id, "1.0.0");

        var before = server.Upstream.CatalogCalls;
        var entries = await FindAsync("proxy", id);

        Assert.Equal(before + 1, server.Upstream.CatalogCalls);
        Assert.Contains("Described by the stub upstream.", entries.Single().ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Allow_and_deny_patterns_decide_which_ids_reach_the_upstream()
    {
        var allowed = "allowed." + Guid.NewGuid().ToString("N")[..8];
        var elsewhere = "other." + Guid.NewGuid().ToString("N")[..8];
        var secret = "allowed." + Guid.NewGuid().ToString("N")[..8] + ".secret";
        AddUpstream(allowed, "1.0.0");
        AddUpstream(elsewhere, "1.0.0");
        AddUpstream(secret, "1.0.0");

        Assert.Single(await FindAsync("guarded", allowed));
        Assert.Empty(await FindAsync("guarded", elsewhere));
        Assert.Empty(await FindAsync("guarded", secret));

        // The same ids are fine on the feed without patterns.
        Assert.Single(await FindAsync("proxy", elsewhere));
    }

    /// <summary>An upstream that cannot be reached must not fail the client while a list is still known.</summary>
    [Fact]
    public async Task A_failing_upstream_falls_back_to_the_last_known_list()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.Outage");
        AddUpstream(id, "1.0.0");
        Assert.Single(await FindAsync("proxy", id));

        server.Upstream.Fails = true;
        try
        {
            var entries = await FindAsync("proxy", id);
            Assert.Single(entries);
            Assert.Equal("1.0.0", Property(entries[0], "Version"));
        }
        finally
        {
            server.Upstream.Fails = false;
        }
    }

    [Fact]
    public async Task Upstream_versions_appear_in_the_v3_registration_and_flat_container()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.V3");
        AddUpstream(id, "1.0.0");
        AddUpstream(id, "1.1.0");

        using var client = server.CreateClient();
        var idLower = id.ToLowerInvariant();

        var registration = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(
            await client.GetAsync($"nuget/proxy/v3/registration/{idLower}/index.json")))!;
        var leaves = registration["items"]!.AsArray()
            .SelectMany(page => page!["items"]!.AsArray())
            .Select(leaf => (string?)leaf!["catalogEntry"]!["version"])
            .ToList();
        Assert.Equal(["1.0.0", "1.1.0"], leaves);

        var flat = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(
            await client.GetAsync($"nuget/proxy/v3/flatcontainer/{idLower}/index.json")))!;
        Assert.Equal(["1.0.0", "1.1.0"], flat["versions"]!.AsArray().Select(v => (string?)v).ToArray());
    }

    /// <summary>
    /// A proxied package with more versions than the index inlines. Above 128 the index stops embedding
    /// leaves and starts advertising page URLs, and those pages were built from what this feed holds
    /// rather than from the merged list the index had just paged - so every link 404'd and a client
    /// asking for the package by name was told it does not exist.
    ///
    /// The existing paging test could not catch it: it pushes to a curated feed, where local versions are
    /// the whole truth and the two lists cannot disagree. Found on the live instance instead, where
    /// dbatools advertised sixteen pages and served none of them.
    /// </summary>
    [Fact]
    public async Task A_paged_registration_on_a_proxy_feed_serves_every_page_it_advertises()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.Paged");
        server.Upstream.AddVersions(id, Enumerable.Range(1, 130).Select(n => $"1.0.{n}"));

        using var client = server.CreateClient();
        var idLower = id.ToLowerInvariant();

        var index = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(
            await client.GetAsync($"nuget/proxy/v3/registration/{idLower}/index.json")))!;

        var pages = index["items"]!.AsArray();
        Assert.True(pages.Count > 1, $"130 versions should page, got {pages.Count}");
        Assert.All(pages, page => Assert.Null(page!["items"]));

        // Every range the index advertises has to be fetchable, or the index is lying about itself.
        foreach (var page in pages)
        {
            var document = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(
                await client.GetAsync(new Uri((string)page!["@id"]!))))!;
            Assert.NotEmpty(document["items"]!.AsArray());
        }

        // And the per-version documents, for a version nobody has cached: the leaf a page points at, and
        // the catalog entry the NuGet provider follows to resolve a version.
        await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/proxy/v3/registration/{idLower}/1.0.130.json"));
        var entry = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(
            await client.GetAsync($"nuget/proxy/v3/catalog/{idLower}/1.0.130.json")))!;
        Assert.Equal("1.0.130", (string?)entry["version"]);
    }

    [Fact]
    public async Task Downloading_through_the_v3_flat_container_caches_the_package()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.V3Download");
        AddUpstream(id, "2.0.0");

        using var client = server.CreateClient();
        var idLower = id.ToLowerInvariant();
        var before = server.Upstream.DownloadCalls;

        var response = await client.GetAsync($"nuget/proxy/v3/flatcontainer/{idLower}/2.0.0/{idLower}.2.0.0.nupkg");
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {response.ReasonPhrase}");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(0x50, bytes[0]);
        Assert.Equal(before + 1, server.Upstream.DownloadCalls);

        await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/proxy/v3/flatcontainer/{idLower}/2.0.0/{idLower}.2.0.0.nupkg"));
        Assert.Equal(before + 1, server.Upstream.DownloadCalls);
    }

    /// <summary>
    /// Find-Module with a name goes through Search(), so a proxy feed has to reach its upstreams there as
    /// well. Searching only what is cached is what made the feed look empty until someone downloaded.
    /// </summary>
    [Fact]
    public async Task Search_finds_a_package_that_nobody_has_cached_yet()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.Search");
        AddUpstream(id, "1.0.0");

        using var client = server.CreateClient();
        var body = await HttpAssert.SuccessBodyAsync(await client.GetAsync(
            $"nuget/proxy/Search()?$filter=IsLatestVersion&searchTerm='{id}'&targetFramework=''&includePrerelease=false&$skip=0&$top=40"));

        var entries = XDocument.Parse(body).Root!.Elements(Atom + "entry").ToList();
        Assert.Contains(entries, e => Property(e, "Id") == id);

        var v3 = await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/proxy/v3/query?q={id}"));
        Assert.Contains(id, v3, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_denied_id_is_not_returned_by_search_either()
    {
        var secret = "allowed." + Guid.NewGuid().ToString("N")[..8] + ".secret";
        AddUpstream(secret, "1.0.0");

        using var client = server.CreateClient();
        var body = await HttpAssert.SuccessBodyAsync(await client.GetAsync(
            $"nuget/guarded/Search()?$filter=IsLatestVersion&searchTerm='{secret}'&$top=40"));

        Assert.Empty(XDocument.Parse(body).Root!.Elements(Atom + "entry"));
    }

    /// <summary>
    /// What happens when a gallery pulls a module version that this feed already cached. It must stop
    /// being offered: not found, not latest, not what an install of "the newest" picks up. It stays
    /// downloadable by exact version, so a deployment already pinned to it is not broken mid-flight.
    /// </summary>
    [Fact]
    public async Task A_version_withdrawn_upstream_stops_being_offered_even_when_cached()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.Withdrawn");
        AddUpstream(id, "1.0.0");
        AddUpstream(id, "1.1.0");

        using var client = server.CreateClient();
        await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/proxy/package/{id}/1.1.0"));

        server.Upstream.Remove(id, "1.1.0");
        await ForgetUpstreamListingsAsync();

        var entries = await FindAsync("proxy", id);
        Assert.Equal("false", Property(entries.Single(e => Property(e, "Version") == "1.1.0"), "Listed"));
        Assert.Equal("1.0.0", Property(entries.Single(e => Property(e, "IsLatestVersion") == "true"), "Version"));

        // Pinned installs still work while the version is being moved off.
        await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/proxy/package/{id}/1.1.0"));

        // And if the gallery puts it back, it is offered again.
        AddUpstream(id, "1.1.0");
        await ForgetUpstreamListingsAsync();
        var restored = await FindAsync("proxy", id);
        Assert.Equal("true", Property(restored.Single(e => Property(e, "Version") == "1.1.0"), "Listed"));
        Assert.Equal("1.1.0", Property(restored.Single(e => Property(e, "IsLatestVersion") == "true"), "Version"));
    }

    /// <summary>
    /// A cached copy of a version the upstream has *hidden* stops being offered here too. Withdrawal was
    /// already handled; being unlisted upstream was not, and the two are the same intent.
    ///
    /// Found in production: PowerShellGet 2.2.5.1 is unlisted on the gallery, a look-through install
    /// cached it, and from then on it won "latest" over the 2.2.5 the gallery advertises - so asking this
    /// server for the newest PowerShellGet installed something the gallery deliberately hides.
    /// </summary>
    [Fact]
    public async Task A_cached_copy_of_a_version_the_upstream_unlists_stops_being_latest()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.Hidden");
        AddUpstream(id, "1.0.0");
        AddUpstream(id, "1.1.0");

        using var client = server.CreateClient();

        // Cache the higher one, the way a look-through install does.
        await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/proxy/package/{id}/1.1.0"));

        // The gallery keeps serving it but stops advertising it.
        AddUpstreamUnlisted(id, "1.1.0");
        await ForgetUpstreamListingsAsync();

        var entries = await FindAsync("proxy", id);
        Assert.Equal("false", Property(entries.Single(e => Property(e, "Version") == "1.1.0"), "Listed"));
        Assert.Equal("1.0.0", Property(entries.Single(e => Property(e, "IsLatestVersion") == "true"), "Version"));

        // Still fetchable by exact version: hidden is not gone, and something may be pinned to it.
        await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/proxy/package/{id}/1.1.0"));
    }

    /// <summary>
    /// A cached catalogue with no stored spelling still serves, falling back to the id that was asked
    /// for. That is the state real rows reach: the column was added nullable, 42 of 55 live rows read
    /// back null, one `.Length` on it threw, and every registration index for a cached package answered
    /// 500 - ten out of ten sampled.
    ///
    /// Deliberately not "set the column to null": the schema now forbids that, so such a test proves only
    /// that SQLite enforces NOT NULL. The migration fills existing nulls with an empty string, so empty
    /// is what those rows become, and this pins that it is harmless.
    /// </summary>
    [Fact]
    public async Task A_cached_catalogue_with_no_stored_spelling_still_serves()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.BlankSpelling");
        AddUpstream(id, "1.0.0");

        await FindAsync("proxy", id);

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FiGetDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                "update CachedUpstreamIndexes set Id = '' where IdLower = {0}", id.ToLowerInvariant());
        }

        // Must answer rather than throw, and use the requested spelling since nothing better is stored.
        var entries = await FindAsync("proxy", id);
        Assert.Single(entries);
        Assert.Equal(id, Property(entries[0], "Id"));

        using var client = server.CreateClient();
        await HttpAssert.SuccessBodyAsync(
            await client.GetAsync($"nuget/proxy/v3/registration/{id.ToLowerInvariant()}/index.json"));
    }


    [Fact]
    public async Task An_uncached_package_keeps_the_spelling_the_upstream_uses()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.Casing");
        AddUpstream(id, "1.0.0");

        using var client = server.CreateClient();

        // Twice, and the second one matters most. The first fetches from the upstream, which is the only
        // path that ever carried the spelling; every request after it answers from the cached catalogue,
        // which is what nearly all real traffic hits - and what shipped renaming the package.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var before = server.Upstream.CatalogCalls;
            var registration = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(
                await client.GetAsync($"nuget/proxy/v3/registration/{id.ToLowerInvariant()}/index.json")))!;
            var entry = registration["items"]!.AsArray()[0]!["items"]!.AsArray()[0]!["catalogEntry"]!;
            Assert.Equal(id, (string?)entry["id"]);

            if (attempt == 1)
            {
                Assert.Equal(before, server.Upstream.CatalogCalls);
            }
        }

        // And the same over v2, which asks with its own casing and must not be contradicted.
        var entries = await FindAsync("proxy", id);
        Assert.Equal(id, Property(entries.Single(), "Id"));
    }

    /// <summary>
    /// Un-caching forgets what this feed holds of a package so it follows the gallery again, and leaves
    /// anything pushed here alone.
    ///
    /// Asked for while testing: a cached copy wins the merge, so one held version keeps being answered -
    /// and keeps being latest - however the upstream moves on, with no way to undo it short of deleting
    /// versions one at a time. PowerShellGet 2.2.5.1 is how that looks in practice.
    ///
    /// Driven through the service rather than the admin button: the button is a form post behind
    /// authentication and an antiforgery token, and the fixture that has an upstream to un-cache from is
    /// not the one with the browser harness. What is worth pinning is which rows and files go.
    /// </summary>
    [Fact]
    public async Task Un_caching_a_package_leaves_pushed_versions_and_follows_the_upstream_again()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.Uncache");
        AddUpstream(id, "1.0.0");
        AddUpstream(id, "1.1.0");

        using var client = server.CreateClient();
        using (var mine = TestPackages.Create(id, "9.0.0"))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushAsync("proxy", mine));
        }

        // Downloading from a proxy feed is what caches a version, so this is the state being undone.
        await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/proxy/package/{id}/1.0.0"));
        await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/proxy/package/{id}/1.1.0"));
        Assert.Equal(2, await CountAsync(id, PackageOrigin.Cached));

        var removed = await UncacheAsync(id);
        Assert.Equal(2, removed);

        // The held copies are gone; what was pushed here is untouched.
        Assert.Equal(0, await CountAsync(id, PackageOrigin.Cached));
        Assert.Equal(1, await CountAsync(id, PackageOrigin.Pushed));

        // And the package still resolves - from the upstream now - which is the whole point of the action.
        var entries = await FindAsync("proxy", id);
        Assert.Contains(entries, e => Property(e, "Version") == "1.1.0");
        await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/proxy/package/{id}/1.0.0"));
    }

    private async Task<int> UncacheAsync(string id)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var feeds = scope.ServiceProvider.GetRequiredService<IFeedStore>();
        var ingestion = scope.ServiceProvider.GetRequiredService<FiGet.Application.Packages.PackageIngestionService>();
        var feed = await feeds.FindAsync("proxy", TestContext.Current.CancellationToken);
        return await ingestion.UncacheAsync(feed!, id, TestContext.Current.CancellationToken);
    }

    private async Task<int> CountAsync(string id, PackageOrigin origin)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FiGetDbContext>();
        var idLower = id.ToLowerInvariant();
        return await db.PackageVersions
            .CountAsync(v => v.Package!.IdLower == idLower && v.Origin == origin, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_version_pushed_here_is_never_withdrawn_by_an_upstream()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.Mine");
        using (var local = TestPackages.Create(id, "3.0.0"))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushAsync("proxy", local));
        }

        // The upstream knows this id but not that version, which says nothing about what was pushed here.
        AddUpstream(id, "1.0.0");
        await ForgetUpstreamListingsAsync();

        var entries = await FindAsync("proxy", id);
        Assert.Equal("true", Property(entries.Single(e => Property(e, "Version") == "3.0.0"), "Listed"));
    }

    /// <summary>
    /// A restart must not put a hidden version back in the running for "latest".
    ///
    /// The version list survives a restart in the database; what the gallery says about those versions
    /// does not, because it is held in memory on purpose. Reconciliation used to read "nothing described"
    /// as "still offered", so every container start re-listed a cached copy the gallery hides - observed
    /// on the live instance, one re-list and one correction per start. Between the two, the hidden version
    /// was eligible to win "latest" again, which is the single thing that reconciliation exists to stop.
    ///
    /// Absence still withdraws. Only re-listing needs the upstream to actually say so.
    /// </summary>
    [Fact]
    public async Task A_restart_does_not_relist_a_version_the_upstream_hides()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.ColdRelist");
        AddUpstream(id, "1.0.0");
        AddUpstream(id, "1.1.0");

        using var client = server.CreateClient();
        await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/proxy/package/{id}/1.1.0"));

        // The gallery hides it, and this feed follows: the cached copy stops being latest.
        AddUpstreamUnlisted(id, "1.1.0");
        await ForgetUpstreamListingsAsync();
        var settled = await FindAsync("proxy", id);
        Assert.Equal("false", Property(settled.Single(e => Property(e, "Version") == "1.1.0"), "Listed"));

        // Now a restart: the versions are still known, nothing is described any more.
        server.Upstream.Describes = false;
        await ForgetUpstreamListingsAsync();
        try
        {
            var entries = await FindAsync("proxy", id);
            Assert.Equal("false", Property(entries.Single(e => Property(e, "Version") == "1.1.0"), "Listed"));
            Assert.Equal("1.0.0", Property(entries.Single(e => Property(e, "IsLatestVersion") == "true"), "Version"));
        }
        finally
        {
            server.Upstream.Describes = true;
        }
    }

    /// <summary>
    /// A version nobody has cached is listed with the dependencies its upstream declares.
    ///
    /// Reported from testing: `Install-Module Microsoft.Entra` brought none of its nine sub-modules the
    /// first time and all of them the second. The request log showed why - on the first attempt the client
    /// never asked about a single dependency, because the entry it read declared none. An uncached version
    /// was described with everything except its dependencies, so a client concluded there were none; by
    /// the second attempt the first had cached the package, and then they came from the nuspec.
    ///
    /// For a proxy feed in front of a gallery, that is the first install of anything - which makes it the
    /// normal case, not an edge one.
    /// </summary>
    [Fact]
    public async Task An_uncached_version_is_listed_with_the_dependencies_its_upstream_declares()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.Dependencies");
        AddUpstream(id, "1.0.0");
        server.Upstream.AddDependency(id, "1.0.0", "Some.Dependency", "[1.2.0, )");
        server.Upstream.AddDependency(id, "1.0.0", "Another.Dependency", "[2.0.0, 2.0.0]");

        // Nothing is cached: this is the look-through listing, exactly what a client reads before deciding
        // what else it has to fetch.
        var entries = await FindAsync("proxy", id);
        var declared = Property(entries.Single(e => Property(e, "Version") == "1.0.0"), "Dependencies");

        Assert.Contains("Some.Dependency:[1.2.0, ):", declared, StringComparison.Ordinal);
        Assert.Contains("Another.Dependency:[2.0.0, 2.0.0]:", declared, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same over v3, because the same client fails the same way there: `Install-PSResource` reads
    /// `dependencyGroups` out of the registration, and an uncached version declared none of them.
    ///
    /// Both protocols read the one collection on the row, so one fix serves both - but it is asserted
    /// separately because "it must be fine, it is the same field" is how a protocol regression hides.
    /// </summary>
    [Fact]
    public async Task An_uncached_version_declares_its_dependencies_over_v3_too()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.DependenciesV3");
        AddUpstream(id, "1.0.0");
        server.Upstream.AddDependency(id, "1.0.0", "Some.Dependency", "[1.2.0, )");

        using var client = server.CreateClient();
        var idLower = id.ToLowerInvariant();

        // The catalog entry is what the NuGet provider follows to resolve what else it must fetch.
        var entry = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(
            await client.GetAsync($"nuget/proxy/v3/catalog/{idLower}/1.0.0.json")))!;

        var groups = entry["dependencyGroups"]!.AsArray();
        Assert.NotEmpty(groups);

        var dependency = groups[0]!["dependencies"]!.AsArray().Single();
        Assert.Equal("Some.Dependency", (string?)dependency!["id"]);
        Assert.Equal("[1.2.0, )", (string?)dependency!["range"]);
    }

    /// <summary>
    /// A restart keeps the two facts that change an answer: what a version depends on, and whether the
    /// upstream still advertises it.
    ///
    /// The version list has always survived a restart - it is in the database - but what the gallery said
    /// *about* those versions lived only in memory. So after every restart a hidden version looked listed
    /// until the first refresh landed, and an uncached version declared no dependencies, which is why a
    /// first install brought nothing with it. Both are now written beside the version list.
    ///
    /// Not the descriptions. Those stay in memory on purpose, and this test pins that too: the rows come
    /// back listed and with their dependencies while the upstream is saying nothing at all.
    /// </summary>
    [Fact]
    public async Task A_restart_keeps_the_dependencies_and_the_hidden_versions()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.Remembered");
        AddUpstream(id, "1.0.0");
        AddUpstreamUnlisted(id, "1.1.0");
        server.Upstream.AddDependency(id, "1.0.0", "Some.Dependency", "[1.2.0, )");

        // Warm: descriptions in memory, versions and facts written to the database.
        var warm = await FindAsync("proxy", id);
        Assert.Contains(
            "Some.Dependency",
            Property(warm.Single(e => Property(e, "Version") == "1.0.0"), "Dependencies"),
            StringComparison.Ordinal);

        // A restart: memory forgets, the database does not, and the upstream describes nothing any more.
        await ForgetDescriptionsAsync(id);
        server.Upstream.Describes = false;
        try
        {
            var entries = await FindAsync("proxy", id);

            Assert.Contains(
                "Some.Dependency",
                Property(entries.Single(e => Property(e, "Version") == "1.0.0"), "Dependencies"),
                StringComparison.Ordinal);
            Assert.Equal("false", Property(entries.Single(e => Property(e, "Version") == "1.1.0"), "Listed"));
        }
        finally
        {
            server.Upstream.Describes = true;
        }
    }

    /// <summary>Drops the in-memory descriptions for an id, which is what a restart does to them.</summary>
    private async Task ForgetDescriptionsAsync(string id)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var feeds = scope.ServiceProvider.GetRequiredService<IFeedStore>();
        var cache = scope.ServiceProvider.GetRequiredService<UpstreamMetadataCache>();
        var feed = await feeds.FindAsync("proxy", TestContext.Current.CancellationToken);
        foreach (var upstream in feed!.Upstreams)
        {
            cache.Forget(upstream.Key, id.ToLowerInvariant());
        }
    }

    [Fact]
    public async Task An_unreachable_upstream_withdraws_nothing()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.NoOutageWithdrawal");
        AddUpstream(id, "1.0.0");

        using var client = server.CreateClient();
        await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/proxy/package/{id}/1.0.0"));

        server.Upstream.Fails = true;
        await ForgetUpstreamListingsAsync();
        try
        {
            var entries = await FindAsync("proxy", id);
            Assert.Equal("true", Property(entries.Single(e => Property(e, "Version") == "1.0.0"), "Listed"));
        }
        finally
        {
            server.Upstream.Fails = false;
        }
    }

    /// <summary>
    /// A slow upstream is this server's problem, not the client's. Listing a package with hundreds of
    /// versions on a v2 gallery can outrun the connector's timeout, and that must degrade to "the upstream
    /// did not answer" rather than becoming a 500 in the middle of someone's install.
    /// </summary>
    [Fact]
    public async Task An_upstream_that_times_out_never_reaches_the_client()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.Timeout");
        AddUpstream(id, "1.0.0");

        using var client = server.CreateClient();
        await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/proxy/package/{id}/1.0.0"));

        server.Upstream.TimesOut = true;
        await ForgetUpstreamListingsAsync();
        try
        {
            var entries = await FindAsync("proxy", id);
            Assert.Equal("true", Property(entries.Single(e => Property(e, "Version") == "1.0.0"), "Listed"));

            await HttpAssert.SuccessBodyAsync(await client.GetAsync(
                $"nuget/proxy/Search()?$filter=IsLatestVersion&searchTerm='{id}'&$top=40"));

            await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/proxy/v3/query?q={id}"));
        }
        finally
        {
            server.Upstream.TimesOut = false;
        }
    }

    /// <summary>
    /// A version nobody has downloaded yet must still be described. The tags are the point: a PowerShell
    /// client reads PSEdition_Desktop and PSEdition_Core to decide whether a version can run at all, so a
    /// feed that lists uncached versions with no tags takes that choice away from it.
    /// </summary>
    [Fact]
    public async Task A_version_nobody_has_cached_is_still_described()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.Described");
        AddUpstream(id, "1.0.0");

        using var client = server.CreateClient();
        var body = await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/proxy/Packages(Id='{id}',Version='1.0.0')"));
        var entry = XDocument.Parse(body).Root!;

        Assert.Contains("PSEdition_Desktop", Property(entry, "Tags"), StringComparison.Ordinal);
        Assert.NotEmpty(Property(entry, "Description"));
        Assert.NotEmpty(Property(entry, "Authors"));

        // Still a placeholder in the sense that matters: nothing was downloaded to produce it.
        Assert.Equal("0", Property(entry, "PackageSize"));
    }

    /// <summary>Expires the cached upstream listings, instead of waiting out the time-to-live.</summary>
    private async Task ForgetUpstreamListingsAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FiGetDbContext>();
        await db.CachedUpstreamIndexes.ExecuteDeleteAsync();
    }

    private void AddUpstream(string id, string version)
    {
        using var package = TestPackages.Create(id, version);
        server.Upstream.Add(id, version, package.ToArray());
    }

    /// <summary>A version the upstream holds but no longer advertises.</summary>
    private void AddUpstreamUnlisted(string id, string version)
    {
        using var package = TestPackages.Create(id, version);
        server.Upstream.AddUnlisted(id, version, package.ToArray());
    }

    private async Task<IReadOnlyList<XElement>> FindAsync(string feed, string id)
    {
        using var client = server.CreateClient();
        var body = await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/{feed}/FindPackagesById()?id='{id}'"));
        var root = XDocument.Parse(body).Root!;
        return root.Elements(Atom + "entry").ToList();
    }

    private async Task<HttpResponseMessage> PushAsync(string feed, Stream package)
    {
        using var client = server.CreateClient();
        using var content = new MultipartFormDataContent();
        using var file = new StreamContent(package);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "package", "package.nupkg");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"nuget/{feed}/") { Content = content };
        request.Headers.Add("X-NuGet-ApiKey", FiGetServerFixture.AdminToken);
        var response = await client.SendAsync(request);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    private static string Property(XElement entry, string name) =>
        entry.Element(Meta + "properties")?.Element(Data + name)?.Value ?? "";
}
