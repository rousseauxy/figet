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
    /// Found by the 2026-09-14 review: every id a client asked a proxy feed about became a stored catalogue row, whether or
    /// not any upstream held it, so made-up ids grew the table without bound. An id nobody holds leaves nothing stored, and
    /// asking again within the refresh window does not ask the upstream again either.
    /// </summary>
    [Fact]
    public async Task An_id_no_upstream_holds_is_remembered_in_memory_and_leaves_no_row()
    {
        using var client = server.CreateClient();
        var ids = Enumerable.Range(0, 3).Select(_ => FiGetServerFixture.UniqueId("Proxy.Absent").ToLowerInvariant()).ToList();
        foreach (var id in ids)
        {
            HttpAssert.Status(HttpStatusCode.NotFound, await client.GetAsync($"nuget/proxy/v3/flatcontainer/{id}/index.json"));
        }

        var asked = server.Upstream.CatalogCalls;
        foreach (var id in ids)
        {
            HttpAssert.Status(HttpStatusCode.NotFound, await client.GetAsync($"nuget/proxy/v3/flatcontainer/{id}/index.json"));
        }

        Assert.Equal(asked, server.Upstream.CatalogCalls);
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FiGet.Infrastructure.Persistence.FiGetDbContext>();
        Assert.Equal(0, db.CachedUpstreamIndexes.Count(c => ids.Contains(c.IdLower)));
    }

    /// <summary>
    /// The failure that drove this project: a version held locally and the same package upstream must
    /// not produce two entries, and the newer upstream version must win the latest flag. Pushed rather than
    /// cached here, so it runs on the feed that opts into merging pushed ids with its upstream.
    /// </summary>
    [Fact]
    public async Task A_local_version_and_a_newer_upstream_version_make_one_list()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.Merge");

        // The upstream holds the package before the push, as it would: a push asks the upstreams about the id
        // for its warning, and what they answer is remembered like any other listing.
        AddUpstream(id, "1.0.0");
        AddUpstream(id, "1.1.0");
        using (var local = TestPackages.Create(id, "1.0.0"))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushAsync("merging", local));
        }

        var entries = await FindAsync("merging", id);

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
    /// A cached copy reads as published when its author published it, not when somebody first installed it.
    /// The usual order - list, then download - stores the upstream's date straight away.
    /// </summary>
    [Fact]
    public async Task A_cached_copy_keeps_the_upstream_publish_date()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.PublishedDate");
        AddUpstream(id, "1.0.0");
        var idLower = id.ToLowerInvariant();

        using var client = server.CreateClient();
        await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/proxy/v3/registration/{idLower}/index.json"));
        await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/proxy/v3/flatcontainer/{idLower}/1.0.0/{idLower}.1.0.0.nupkg"));

        Assert.Equal(StubUpstreamPublished, await StoredPublishedAsync(idLower, "1.0.0"));
    }

    /// <summary>
    /// Every copy cached before the upstream's date was carried holds its fetch date, and takes the upstream's
    /// date the next time the upstream describes the package. Such a copy is made here by writing a wrong date
    /// over a fresh one: a download now asks which upstream owns the id first, which describes it on the way.
    /// </summary>
    [Fact]
    public async Task A_copy_cached_with_its_fetch_date_is_corrected_by_the_next_listing()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.PublishedLater");
        AddUpstream(id, "1.0.0");
        var idLower = id.ToLowerInvariant();

        using var client = server.CreateClient();
        await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/proxy/v3/flatcontainer/{idLower}/1.0.0/{idLower}.1.0.0.nupkg"));
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FiGetDbContext>();
            await db.PackageVersions
                .Where(v => v.Package!.IdLower == idLower)
                .ExecuteUpdateAsync(u => u.SetProperty(v => v.PublishedUtc, new DateTime(2026, 9, 13, 10, 50, 0, DateTimeKind.Utc)), TestContext.Current.CancellationToken);
        }

        Assert.NotEqual(StubUpstreamPublished, await StoredPublishedAsync(idLower, "1.0.0"));

        await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/proxy/v3/registration/{idLower}/index.json"));
        Assert.Equal(StubUpstreamPublished, await StoredPublishedAsync(idLower, "1.0.0"));
    }

    /// <summary>
    /// The clash a tester asked about: a module published here whose name also exists on the gallery. The feed
    /// serves the pushed id only from what was pushed, so the gallery's higher version of an unrelated package
    /// never becomes its latest, and none of the gallery's versions can be installed through it.
    /// </summary>
    [Fact]
    public async Task A_pushed_id_is_served_only_from_this_feed()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.Clash");
        AddUpstream(id, "5.0.0");

        HttpResponseMessage pushed;
        using (var mine = TestPackages.Create(id, "1.0.0"))
        {
            pushed = await PushAsync("proxy", mine);
        }

        HttpAssert.Status(HttpStatusCode.Created, pushed);
        var warning = Assert.Single(pushed.Headers.GetValues("X-NuGet-Warning"));
        Assert.Contains("also exists on upstream 'stub'", warning, StringComparison.Ordinal);
        Assert.Contains("only from what is pushed", warning, StringComparison.Ordinal);

        var entries = await FindAsync("proxy", id);
        Assert.Equal(["1.0.0"], entries.Select(e => Property(e, "Version")).ToArray());
        Assert.Equal("true", Property(entries.Single(), "IsLatestVersion"));

        using var client = server.CreateClient();
        var idLower = id.ToLowerInvariant();
        HttpAssert.Status(HttpStatusCode.NotFound, await client.GetAsync($"nuget/proxy/package/{id}/5.0.0"));
        HttpAssert.Status(HttpStatusCode.NotFound, await client.GetAsync($"nuget/proxy/v3/flatcontainer/{idLower}/5.0.0/{idLower}.5.0.0.nupkg"));

        var versions = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/proxy/v3/flatcontainer/{idLower}/index.json")))!["versions"]!.AsArray();
        Assert.Equal(["1.0.0"], versions.Select(v => (string?)v));
    }

    /// <summary>
    /// A copy of the gallery's package cached before anything was pushed under its name is the other package.
    /// Once the id is pushed it stops being offered - unlisted rather than deleted, so a deployment pinned to it
    /// can still fetch it by exact version.
    /// </summary>
    [Fact]
    public async Task A_copy_cached_before_the_id_was_pushed_is_unlisted()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.ClashCached");
        AddUpstream(id, "5.0.0");
        using var client = server.CreateClient();
        await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/proxy/package/{id}/5.0.0"));

        using (var mine = TestPackages.Create(id, "1.0.0"))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushAsync("proxy", mine));
        }

        var entries = await FindAsync("proxy", id);
        var latest = Assert.Single(entries, e => Property(e, "IsLatestVersion") == "true");
        Assert.Equal("1.0.0", Property(latest, "Version"));

        var search = await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/proxy/Search()?searchTerm='{id}'&includePrerelease=true"));
        Assert.DoesNotContain(">5.0.0<", search, StringComparison.Ordinal);

        // Still there for whoever pinned it.
        await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/proxy/package/{id}/5.0.0"));
    }

    [Fact]
    public async Task A_feed_that_opts_in_merges_a_pushed_id_and_warns_about_it()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.OptIn");
        AddUpstream(id, "5.0.0");

        HttpResponseMessage pushed;
        using (var mine = TestPackages.Create(id, "1.0.0"))
        {
            pushed = await PushAsync("merging", mine);
        }

        Assert.Contains("merges both", Assert.Single(pushed.Headers.GetValues("X-NuGet-Warning")), StringComparison.Ordinal);
        var entries = await FindAsync("merging", id);
        Assert.Equal(["1.0.0", "5.0.0"], entries.Select(e => Property(e, "Version")).ToArray());
    }

    [Fact]
    public async Task A_push_of_an_id_no_upstream_holds_carries_no_warning()
    {
        using var mine = TestPackages.Create(FiGetServerFixture.UniqueId("Proxy.NoClash"), "1.0.0");
        var pushed = await PushAsync("proxy", mine);

        HttpAssert.Status(HttpStatusCode.Created, pushed);
        Assert.False(pushed.Headers.Contains("X-NuGet-Warning"));
    }

    /// <summary>
    /// Two upstreams holding different packages under one id: the first in priority order owns the id, and the
    /// second is neither listed nor fetched from for it - not even a version only the second one has.
    /// </summary>
    [Fact]
    public async Task The_first_upstream_that_holds_an_id_owns_it()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.Owner");
        AddUpstream(id, "1.0.0");
        AddSecondUpstream(id, "9.0.0");

        var entries = await FindAsync("layered", id);
        Assert.Equal(["1.0.0"], entries.Select(e => Property(e, "Version")).ToArray());

        using var client = server.CreateClient();
        HttpAssert.Status(HttpStatusCode.NotFound, await client.GetAsync($"nuget/layered/package/{id}/9.0.0"));
        await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/layered/package/{id}/1.0.0"));
    }

    /// <summary>
    /// An upstream holding only unlisted versions of an id does not own it: a gallery keeps withdrawn packages under their
    /// name, and that must not hide a lower upstream's real package (DscTestModule, 2026-09-13). Listing and download both
    /// follow the upstream that offers it.
    /// </summary>
    [Fact]
    public async Task An_upstream_holding_only_unlisted_versions_does_not_own_the_id()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.Hidden");
        AddUpstreamUnlisted(id, "2.5.0");
        AddUpstreamUnlisted(id, "2.6.0");
        AddSecondUpstream(id, "1.0.0");
        AddSecondUpstream(id, "2.5.0");

        var entries = await FindAsync("layered", id);
        Assert.Equal(["1.0.0", "2.5.0"], entries.Select(e => Property(e, "Version")).ToArray());

        var before = server.SecondUpstream.DownloadCalls;
        using var client = server.CreateClient();
        await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/layered/package/{id}/2.5.0"));
        Assert.True(server.SecondUpstream.DownloadCalls > before, "2.5.0 was not fetched from the upstream that offers the id.");
    }

    /// <summary>A package hidden on every upstream stays with the first that holds it, as before.</summary>
    [Fact]
    public async Task An_id_hidden_everywhere_stays_with_the_first_upstream_holding_it()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.HiddenEverywhere");
        AddUpstreamUnlisted(id, "2.5.0");

        var entries = await FindAsync("layered", id);
        Assert.Equal(["2.5.0"], entries.Select(e => Property(e, "Version")).ToArray());
    }

    [Fact]
    public async Task An_id_only_a_lower_upstream_holds_is_served_from_it()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.Lower");
        AddSecondUpstream(id, "3.0.0");

        var entries = await FindAsync("layered", id);
        Assert.Equal(["3.0.0"], entries.Select(e => Property(e, "Version")).ToArray());

        using var client = server.CreateClient();
        await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/layered/package/{id}/3.0.0"));
    }

    /// <summary>
    /// While the upstream ahead cannot be asked and nothing about the id is remembered, whether it owns the id is
    /// unknown, so a lower upstream's package of that name is not served in the meantime - once cached here it
    /// would stay, whichever package the id really belongs to.
    /// </summary>
    [Fact]
    public async Task A_lower_upstream_is_not_used_while_a_higher_one_cannot_be_asked()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.Undecided");
        AddSecondUpstream(id, "3.0.0");

        server.Upstream.Fails = true;
        try
        {
            Assert.Empty(await FindAsync("layered", id));
            using var client = server.CreateClient();
            HttpAssert.Status(HttpStatusCode.NotFound, await client.GetAsync($"nuget/layered/package/{id}/3.0.0"));
        }
        finally
        {
            server.Upstream.Fails = false;
        }

        Assert.Equal(["3.0.0"], (await FindAsync("layered", id)).Select(e => Property(e, "Version")).ToArray());
    }

    /// <summary>Moving an upstream up the priority order hands it the ids both hold.</summary>
    [Fact]
    public async Task Changing_the_priority_order_changes_the_owner()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.Reorder");
        AddUpstream(id, "1.0.0");
        AddSecondUpstream(id, "9.0.0");
        Assert.Equal(["1.0.0"], (await FindAsync("layered", id)).Select(e => Property(e, "Version")).ToArray());

        await using var scope = server.Services.CreateAsyncScope();
        var feeds = scope.ServiceProvider.GetRequiredService<IFeedStore>();
        var feed = (await feeds.FindAsync("layered", TestContext.Current.CancellationToken))!;
        var secondary = feed.Upstreams.Single(u => u.Name == "secondary");
        var primary = feed.Upstreams.Single(u => u.Name == "primary");

        Assert.False(await feeds.MoveUpstreamAsync(feed.Key, primary.Key, up: true, TestContext.Current.CancellationToken));
        Assert.True(await feeds.MoveUpstreamAsync(feed.Key, secondary.Key, up: true, TestContext.Current.CancellationToken));
        try
        {
            Assert.Equal(["9.0.0"], (await FindAsync("layered", id)).Select(e => Property(e, "Version")).ToArray());
        }
        finally
        {
            // The fixture is shared by this class's tests, which expect primary first.
            Assert.True(await feeds.MoveUpstreamAsync(feed.Key, secondary.Key, up: false, TestContext.Current.CancellationToken));
        }
    }

    /// <summary>
    /// A pull caches what the package depends on, so the machine it is for can install it. Resolved the way a client
    /// resolves: the lowest version a range allows, stable unless the range starts at a prerelease, and a dependency of a
    /// dependency too.
    /// </summary>
    [Fact]
    public async Task A_pull_caches_the_dependency_closure_at_the_versions_a_client_would_pick()
    {
        var meta = FiGetServerFixture.UniqueId("Pull.Meta");
        var sub = FiGetServerFixture.UniqueId("Pull.Sub");
        var ranged = FiGetServerFixture.UniqueId("Pull.Ranged");
        var leaf = FiGetServerFixture.UniqueId("Pull.Leaf");

        AddUpstreamPackage(meta, "1.0.0", b => { b.AddDependency("any", sub, "[1.0.0]"); b.AddDependency("any", ranged, "2.0.0"); });
        AddUpstreamPackage(sub, "1.0.0", b => b.AddDependency("any", leaf, "1.0.0"));
        AddUpstreamPackage(sub, "1.1.0");
        AddUpstreamPackage(ranged, "2.0.0");
        AddUpstreamPackage(ranged, "2.1.0");
        AddUpstreamPackage(ranged, "3.0.0-beta1");
        AddUpstreamPackage(leaf, "1.0.0");
        AddUpstreamPackage(leaf, "1.5.0");

        var report = await PullAsync(meta, "1.0.0");

        Assert.Equal(4, report.Fetched.Count);
        Assert.Empty(report.Unavailable);
        Assert.False(report.StoppedAtLimit);
        Assert.Equal(1, await CountAsync(meta, PackageOrigin.Cached));
        Assert.Equal(["1.0.0"], await CachedVersionsAsync(sub));
        Assert.Equal(["2.0.0"], await CachedVersionsAsync(ranged));
        Assert.Equal(["1.0.0"], await CachedVersionsAsync(leaf));
    }

    /// <summary>A package cached on its own earlier is exactly what this repairs: pulling it again fetches what it needs.</summary>
    [Fact]
    public async Task Pulling_a_package_already_here_still_fetches_its_dependencies()
    {
        var meta = FiGetServerFixture.UniqueId("Pull.Again");
        var sub = FiGetServerFixture.UniqueId("Pull.AgainSub");
        AddUpstreamPackage(meta, "1.0.0", b => b.AddDependency("any", sub, "[1.0.0]"));
        AddUpstreamPackage(sub, "1.0.0");

        using var client = server.CreateClient();
        await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/proxy/package/{meta}/1.0.0"));
        Assert.Empty(await CachedVersionsAsync(sub));

        var report = await PullAsync(meta, "1.0.0");

        Assert.Equal([$"{meta} 1.0.0"], report.AlreadyHere);
        Assert.Equal([$"{sub} 1.0.0"], report.Fetched);
    }

    [Fact]
    public async Task A_dependency_no_upstream_has_is_reported_and_the_rest_is_still_pulled()
    {
        var meta = FiGetServerFixture.UniqueId("Pull.Gap");
        var present = FiGetServerFixture.UniqueId("Pull.GapHere");
        var absent = FiGetServerFixture.UniqueId("Pull.GapGone");
        AddUpstreamPackage(meta, "1.0.0", b => { b.AddDependency("any", present, "1.0.0"); b.AddDependency("any", absent, "[4.0.0]"); });
        AddUpstreamPackage(present, "1.0.0");

        var report = await PullAsync(meta, "1.0.0");

        Assert.Equal(2, report.Fetched.Count);
        Assert.Single(report.Unavailable, u => u.StartsWith(absent, StringComparison.Ordinal));
        Assert.False(report.Failed);
    }

    /// <summary>The page reports a pull from counts in its address, and nothing else from there.</summary>
    [Fact]
    public async Task The_feed_page_reports_a_pull_from_counts_only()
    {
        using var client = server.CreateClient();
        var page = await HttpAssert.SuccessBodyAsync(await client.GetAsync("feeds/proxy?pulled=38&present=1&missing=2&capped=1"));
        Assert.Contains("38 fetched, 1 already here, 2 unavailable", page, StringComparison.Ordinal);
        Assert.Contains("Stopped at the limit", page, StringComparison.Ordinal);

        // A link edited by hand is no pull at all - not text on the page, and not a failed page either.
        var crafted = await HttpAssert.SuccessBodyAsync(await client.GetAsync("feeds/proxy?pulled=%3Cb%3Ehello%3C%2Fb%3E&present=x"));
        Assert.DoesNotContain("<b>hello", crafted, StringComparison.Ordinal);
        Assert.DoesNotContain("fetched,", crafted, StringComparison.Ordinal);
        Assert.DoesNotContain("already here", crafted, StringComparison.Ordinal);
    }

    private async Task<PullReport> PullAsync(string id, string version)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var feed = await scope.ServiceProvider.GetRequiredService<IFeedStore>().FindAsync("proxy", TestContext.Current.CancellationToken);
        var puller = scope.ServiceProvider.GetRequiredService<DependencyPuller>();
        return await puller.PullAsync(feed!, id, NuGet.Versioning.NuGetVersion.Parse(version), TestContext.Current.CancellationToken);
    }

    private async Task<string[]> CachedVersionsAsync(string id)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FiGetDbContext>();
        var idLower = id.ToLowerInvariant();
        return await db.PackageVersions
            .Where(v => v.Package!.IdLower == idLower && v.Package.Feed!.NameLower == "proxy")
            .Select(v => v.NormalizedVersion)
            .OrderBy(v => v)
            .ToArrayAsync(TestContext.Current.CancellationToken);
    }

    private void AddUpstreamPackage(string id, string version, Action<NuGet.Packaging.PackageBuilder>? configure = null)
    {
        using var package = TestPackages.Create(id, version, configure);
        server.Upstream.Add(id, version, package.ToArray());
    }

    /// <summary>
    /// After a restart a proxied package reads as it did before, not as a bare version list: the descriptions are
    /// stored, and read back when memory has none - with the upstream unreachable, so nothing is fetched to get them.
    /// </summary>
    [Fact]
    public async Task Descriptions_survive_a_restart()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.Restart");
        AddUpstream(id, "1.0.0");
        AddUpstream(id, "1.1.0");
        Assert.Equal("Described by the stub upstream.", Property((await FindAsync("proxy", id))[0], "Description"));

        await ForgetDescriptionsAsync(id);
        server.Upstream.Fails = true;
        try
        {
            var entries = await FindAsync("proxy", id);
            Assert.Equal(2, entries.Count);
            Assert.All(entries, e => Assert.Equal("Described by the stub upstream.", Property(e, "Description")));
            Assert.All(entries, e => Assert.Contains("PSEdition_Desktop", Property(e, "Tags"), StringComparison.Ordinal));
        }
        finally
        {
            server.Upstream.Fails = false;
        }
    }

    /// <summary>
    /// A PowerShell module repeats its tag list on every version, and that repetition is what made one package a
    /// hundred megabytes. Each distinct list is stored once; a version the upstream stops describing is dropped with
    /// its list when nothing else uses it.
    /// </summary>
    [Fact]
    public async Task Tag_lists_are_stored_once_and_forgotten_with_the_last_version_using_them()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.TagSets");
        server.Upstream.AddVersions(id, Enumerable.Range(0, 30).Select(n => $"1.0.{n}"));
        await FindAsync("proxy", id);

        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FiGetDbContext>();
        var idLower = id.ToLowerInvariant();
        var rows = await db.CachedUpstreamDescriptions.Where(d => d.IdLower == idLower).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(30, rows.Count);
        var hash = Assert.Single(rows.Select(r => r.TagSetHash).Distinct());

        var store = scope.ServiceProvider.GetRequiredService<IUpstreamDescriptionStore>();
        var upstreamKey = rows[0].FeedUpstreamKey;
        var remaining = new UpstreamMetadata(NuGet.Versioning.NuGetVersion.Parse("1.0.0"), "d", "s", "t", "a", "different tags", "", "", "", null, 0);
        await store.SaveAsync(upstreamKey, idLower, [remaining], TestContext.Current.CancellationToken);

        Assert.Equal(["1.0.0"], await db.CachedUpstreamDescriptions.Where(d => d.IdLower == idLower).Select(d => d.NormalizedVersion).ToListAsync(TestContext.Current.CancellationToken));
        Assert.False(await db.CachedUpstreamTagSets.AnyAsync(t => t.Hash == hash && !db.CachedUpstreamDescriptions.Any(d => d.TagSetHash == t.Hash), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The v3 flat container needs version numbers and nothing else, so against an upstream that can list versions on
    /// their own it does not wait for the catalogue: with every catalogue call held, the list still comes back. The
    /// full description is queued behind it and arrives once released.
    /// </summary>
    [Fact]
    public async Task The_flat_container_asks_a_v3_upstream_for_versions_alone()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.VersionsOnly");
        var idLower = id.ToLowerInvariant();
        AddUpstream(id, "1.0.0");
        AddUpstream(id, "2.0.0");

        server.Upstream.AnswersVersionsOnly = true;
        server.Upstream.HoldCatalogues();
        try
        {
            using var client = server.CreateClient();
            var before = server.Upstream.VersionsOnlyCalls;
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var body = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(
                await client.GetAsync($"nuget/proxy/v3/flatcontainer/{idLower}/index.json", cancel.Token)))!;

            Assert.Equal(["1.0.0", "2.0.0"], body["versions"]!.AsArray().Select(v => (string?)v));
            Assert.Equal(before + 1, server.Upstream.VersionsOnlyCalls);
        }
        finally
        {
            server.Upstream.ReleaseCatalogues();
            server.Upstream.AnswersVersionsOnly = false;
        }

        // The queued refresh describes it, so a listing that needs descriptions has them.
        for (var attempt = 0; attempt < 50 && Property((await FindAsync("proxy", id))[0], "Description").Length == 0; attempt++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Equal("Described by the stub upstream.", Property((await FindAsync("proxy", id))[0], "Description"));
    }

    /// <summary>A registration needs the facts, so it never takes the versions-only path, even where it is available.</summary>
    [Fact]
    public async Task A_registration_still_reads_the_full_catalogue()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.NotVersionsOnly");
        AddUpstream(id, "1.0.0");
        server.Upstream.AnswersVersionsOnly = true;
        try
        {
            var before = server.Upstream.VersionsOnlyCalls;
            using var client = server.CreateClient();
            await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/proxy/v3/registration/{id.ToLowerInvariant()}/index.json"));
            Assert.Equal(before, server.Upstream.VersionsOnlyCalls);
        }
        finally
        {
            server.Upstream.AnswersVersionsOnly = false;
        }
    }

    private void AddSecondUpstream(string id, string version)
    {
        using var package = TestPackages.Create(id, version);
        server.SecondUpstream.Add(id, version, package.ToArray());
    }

    /// <summary>What <see cref="StubUpstreamClient"/> reports as every version's publish date.</summary>
    private static readonly DateTime StubUpstreamPublished = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private async Task<DateTime> StoredPublishedAsync(string idLower, string version)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FiGetDbContext>();
        var published = await db.PackageVersions
            .Where(v => v.Package!.IdLower == idLower && v.NormalizedVersionLower == version)
            .Select(v => v.PublishedUtc)
            .SingleAsync();
        return DateTime.SpecifyKind(published, DateTimeKind.Utc);
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
            HttpAssert.Status(HttpStatusCode.Created, await PushAsync("merging", mine));
        }

        // Downloading from a proxy feed is what caches a version, so this is the state being undone. On the feed
        // that merges pushed ids with its upstream: elsewhere a pushed id is not fetched from an upstream at all.
        await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/merging/package/{id}/1.0.0"));
        await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/merging/package/{id}/1.1.0"));
        Assert.Equal(2, await CountAsync(id, PackageOrigin.Cached));

        var removed = await UncacheAsync(id, "merging");
        Assert.Equal(2, removed);

        // The held copies are gone; what was pushed here is untouched.
        Assert.Equal(0, await CountAsync(id, PackageOrigin.Cached));
        Assert.Equal(1, await CountAsync(id, PackageOrigin.Pushed));

        // And the package still resolves - from the upstream now - which is the whole point of the action.
        var entries = await FindAsync("merging", id);
        Assert.Contains(entries, e => Property(e, "Version") == "1.1.0");
        await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/merging/package/{id}/1.0.0"));
    }

    /// <summary>
    /// The management API lists what a proxy feed stores - pushed and cached - and not what only its upstream
    /// has, as the server being replaced does. A clean-up script there tells cached from pushed by the
    /// publisher, which is what the cached rows carry here.
    /// </summary>
    [Fact]
    public async Task The_management_api_lists_stored_versions_and_marks_cached_ones()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.Management");
        AddUpstream(id, "1.0.0");
        AddUpstream(id, "2.0.0");
        using (var mine = TestPackages.Create(id, "0.9.0"))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushAsync("merging", mine));
        }

        using var client = server.CreateClient();
        await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/merging/package/{id}/1.0.0"));

        var versions = System.Text.Json.Nodes.JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync($"api/packages/merging/versions?name={id}")))!.AsArray();
        Assert.Equal(["1.0.0", "0.9.0"], versions.Select(v => (string?)v!["version"]));
        Assert.Equal("SYSTEM", (string?)versions[0]!["publishedBy"]);
        Assert.Null(versions[1]!["publishedBy"]);
    }

    private async Task<int> UncacheAsync(string id, string feedName = "proxy")
    {
        await using var scope = server.Services.CreateAsyncScope();
        var feeds = scope.ServiceProvider.GetRequiredService<IFeedStore>();
        var ingestion = scope.ServiceProvider.GetRequiredService<FiGet.Application.Packages.PackageIngestionService>();
        var feed = await feeds.FindAsync(feedName, TestContext.Current.CancellationToken);
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

    /// <summary>
    /// A row written before the facts columns existed must not re-list a version the upstream hides.
    ///
    /// Found in production within minutes of deploying the persistence. The migration defaults both
    /// columns to empty, so every row already in the database looked like an upstream that hides nothing -
    /// and three seconds after a restart a hidden nightly was listed again, eligible to be "latest", until
    /// its first refresh forty seconds later put it back. That is the defect reconciliation exists to
    /// prevent, arriving through the back door of a default value.
    ///
    /// Empty now means "we were not told", not "we were told nothing is hidden".
    /// </summary>
    [Fact]
    public async Task A_row_written_before_the_facts_existed_does_not_relist_a_hidden_version()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.OldRow");
        AddUpstream(id, "1.0.0");
        AddUpstream(id, "1.1.0");

        using var client = server.CreateClient();
        await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/proxy/package/{id}/1.1.0"));

        AddUpstreamUnlisted(id, "1.1.0");
        await ForgetUpstreamListingsAsync();
        var settled = await FindAsync("proxy", id);
        Assert.Equal("false", Property(settled.Single(e => Property(e, "Version") == "1.1.0"), "Listed"));

        // Exactly what the migration leaves behind: the version list, and both facts blank.
        await BlankStoredFactsAsync(id);
        await ForgetDescriptionsAsync(id);

        server.Upstream.Describes = false;
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
    /// Blanks the stored facts, leaving a row shaped like one the migration has just added them to. That migration
    /// predates stored descriptions, so a row it left behind has none: they are removed too.
    /// </summary>
    private async Task BlankStoredFactsAsync(string id)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FiGetDbContext>();
        var idLower = id.ToLowerInvariant();
        await db.CachedUpstreamDescriptions.Where(d => d.IdLower == idLower).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await db.CachedUpstreamIndexes
            .Where(c => c.IdLower == idLower)
            .ExecuteUpdateAsync(
                u => u.SetProperty(c => c.UnlistedVersions, "").SetProperty(c => c.Dependencies, ""),
                TestContext.Current.CancellationToken);
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

    /// <summary>
    /// v3 must agree with v2 about which version is latest when this feed holds a hidden copy above the
    /// one the gallery advertises.
    ///
    /// Reported from testing: `Install-PSResource` over v3 fetched PowerShellGet 2.2.5.1 - a version the
    /// gallery unlists, cached here by an earlier install - while reporting that it had installed 2.2.5,
    /// and the installed manifest said 2.2.5.1. The v2 client got it right against the same data. The v2
    /// side of this is already covered; the registration this asserts is what the v3 client actually reads,
    /// and nothing covered it for a proxy feed.
    /// </summary>
    [Fact]
    public async Task The_v3_registration_never_offers_a_hidden_cached_copy_as_latest()
    {
        var id = FiGetServerFixture.UniqueId("Proxy.V3Hidden");
        AddUpstream(id, "1.0.0");
        AddUpstream(id, "1.1.0");

        using var client = server.CreateClient();
        var idLower = id.ToLowerInvariant();

        // Cache the higher one, the way a look-through install does, then have the gallery hide it.
        await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/proxy/package/{id}/1.1.0"));
        AddUpstreamUnlisted(id, "1.1.0");
        await ForgetUpstreamListingsAsync();

        var index = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(
            await client.GetAsync($"nuget/proxy/v3/registration/{idLower}/index.json")))!;

        var leaves = index["items"]!.AsArray()
            .SelectMany(page => page!["items"]!.AsArray())
            .Select(leaf => (
                Version: (string)leaf!["catalogEntry"]!["version"]!,
                Listed: (bool)leaf!["catalogEntry"]!["listed"]!,
                Content: (string)leaf!["packageContent"]!))
            .ToList();

        var hidden = leaves.Single(l => l.Version == "1.1.0");
        var offered = leaves.Single(l => l.Version == "1.0.0");

        Assert.False(hidden.Listed);
        Assert.True(offered.Listed);

        // Each leaf must carry its own bytes. Handing 1.0.0's leaf the hidden version's content is how a
        // client installs one version while believing it installed another.
        Assert.Contains("/1.0.0/", offered.Content, StringComparison.Ordinal);
        Assert.Contains("/1.1.0/", hidden.Content, StringComparison.Ordinal);
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
