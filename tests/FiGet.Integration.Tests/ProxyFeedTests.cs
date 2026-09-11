using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Testing;

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

    private void AddUpstream(string id, string version)
    {
        using var package = TestPackages.Create(id, version);
        server.Upstream.Add(id, version, package.ToArray());
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
