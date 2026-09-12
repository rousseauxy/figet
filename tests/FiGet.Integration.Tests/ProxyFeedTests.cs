using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Xml.Linq;
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
    /// An uncached package keeps the upstream's spelling. A v3 registration URL is lower-cased by
    /// convention, and echoing that back renamed the package until somebody downloaded it: "powershellget"
    /// before the install, "PowerShellGet" after.
    /// </summary>
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
