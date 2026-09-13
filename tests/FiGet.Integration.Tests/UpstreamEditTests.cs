using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

/// <summary>
/// Editing an upstream in place (asked by the tester: a mistyped name or a gallery's new address meant removing it and
/// adding it again). The stubs answer by upstream name, so pointing an upstream at "the other stub" is a change of name
/// and URL together.
/// </summary>
public sealed partial class UpstreamEditTests(ProxyServerFixture server) : IClassFixture<ProxyServerFixture>
{
    /// <summary>
    /// Pointed somewhere else, the upstream answers from there at once: what was stored about the old source is dropped,
    /// not served until it ages out. Its place in the order is kept.
    /// </summary>
    [Fact]
    public async Task An_upstream_pointed_elsewhere_answers_from_there_at_once_and_keeps_its_place()
    {
        var feed = await CreateProxyFeedAsync(("secondary", "https://secondary.invalid/v3/index.json"), ("third", "https://third.invalid/v3/index.json"));
        var id = FiGetServerFixture.UniqueId("Upstream.Moved");
        server.SecondUpstream.AddVersions(id, ["1.0.0"]);
        server.Upstream.AddVersions(id, ["2.0.0"]);

        using var client = server.CreateClient();
        Assert.Equal(["1.0.0"], await VersionsAsync(client, feed, id));

        var upstream = (await FindAsync(feed))!.Upstreams.Single(u => u.Name == "secondary");
        using var admin = await AdminAsync();
        using (var response = await PostAsync(admin, feed, ("key", Key(upstream)), ("name", "stub"), ("url", "https://stub.invalid/v3/index.json"), ("kind", "V3"), ("enabled", "true")))
        {
            Assert.Equal($"/admin/feeds/{feed}/upstreams?upstream=saved", response.Headers.Location?.OriginalString);
        }

        Assert.Equal(["2.0.0"], await VersionsAsync(client, feed, id));
        var edited = (await FindAsync(feed))!.Upstreams.OrderBy(u => u.Ordinal).ToList();
        Assert.Equal(["stub", "third"], edited.Select(u => u.Name));
        Assert.Equal("https://stub.invalid/v3/index.json", edited[0].Url);
    }

    [Fact]
    public async Task A_name_another_upstream_of_the_feed_has_is_refused_and_nothing_changes()
    {
        var feed = await CreateProxyFeedAsync(("first", "https://first.invalid/v3/index.json"), ("second", "https://second.invalid/v3/index.json"));
        var second = (await FindAsync(feed))!.Upstreams.Single(u => u.Name == "second");

        using var admin = await AdminAsync();
        using (var response = await PostAsync(admin, feed, ("key", Key(second)), ("name", "FIRST"), ("url", "https://moved.invalid/v3/index.json"), ("enabled", "true")))
        {
            Assert.Equal($"/admin/feeds/{feed}/upstreams?edit={Key(second)}&upstream=taken#upstream-{Key(second)}", response.Headers.Location?.OriginalString);
            var reopened = await HttpAssert.SuccessBodyAsync(await admin.GetAsync(response.Headers.Location));
            Assert.Contains("role=\"alert\">Another upstream of this feed already has that name.</div>", reopened, StringComparison.Ordinal);
        }

        using (var response = await PostAsync(admin, feed, ("key", Key(second)), ("name", "second"), ("url", "")))
        {
            Assert.Equal($"/admin/feeds/{feed}/upstreams?edit={Key(second)}&upstream=invalid#upstream-{Key(second)}", response.Headers.Location?.OriginalString);
        }

        var unchanged = (await FindAsync(feed))!.Upstreams.Single(u => u.Key == second.Key);
        Assert.Equal("second", unchanged.Name);
        Assert.Equal("https://second.invalid/v3/index.json", unchanged.Url);
    }

    /// <summary>Unticked, an upstream is kept but not asked; the page says so.</summary>
    [Fact]
    public async Task A_disabled_upstream_is_not_asked()
    {
        var feed = await CreateProxyFeedAsync(("stub", "https://stub.invalid/v3/index.json"));
        var id = FiGetServerFixture.UniqueId("Upstream.Paused");
        server.Upstream.AddVersions(id, ["1.0.0"]);
        var upstream = Assert.Single((await FindAsync(feed))!.Upstreams);

        using var admin = await AdminAsync();
        HttpAssert.Status(HttpStatusCode.Redirect, await PostAsync(admin, feed, ("key", Key(upstream)), ("name", "stub"), ("url", upstream.Url), ("kind", "V3")));

        using var client = server.CreateClient();
        HttpAssert.Status(HttpStatusCode.NotFound, await client.GetAsync($"nuget/{feed}/v3/flatcontainer/{id.ToLowerInvariant()}/index.json"));
        var page = await HttpAssert.SuccessBodyAsync(await admin.GetAsync($"/admin/feeds/{feed}/upstreams"));
        Assert.Contains(">disabled</span>", page, StringComparison.Ordinal);
        Assert.Contains($"href=\"/admin/feeds/{feed}/upstreams?edit={Key(upstream)}#upstream-{Key(upstream)}\"", page, StringComparison.Ordinal);
        Assert.Contains("<details class=\"fg-accordion\" id=\"add-upstream\">", page, StringComparison.Ordinal);
        Assert.DoesNotContain("upstreams/update", page, StringComparison.Ordinal);

        // Opened, the form sits in a row under its upstream, filled in with what is stored - the checkbox included.
        var edit = await HttpAssert.SuccessBodyAsync(await admin.GetAsync($"/admin/feeds/{feed}/upstreams?edit={Key(upstream)}"));
        var row = Regex.Match(edit, $"<tr class=\"fg-row-panel\" id=\"upstream-{Key(upstream)}\">.*?</tr>", RegexOptions.Singleline).Value;
        Assert.Contains("upstreams/update", row, StringComparison.Ordinal);
        Assert.Contains("value=\"https://stub.invalid/v3/index.json\"", row, StringComparison.Ordinal);
        Assert.DoesNotContain("checked", row, StringComparison.Ordinal);
    }

    private static string Key(FeedUpstream upstream) => upstream.Key.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<string[]> VersionsAsync(HttpClient client, string feed, string id)
    {
        var body = await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/{feed}/v3/flatcontainer/{id.ToLowerInvariant()}/index.json"));
        return [.. JsonNode.Parse(body)!["versions"]!.AsArray().Select(v => v!.GetValue<string>())];
    }

    private async Task<string> CreateProxyFeedAsync(params (string Name, string Url)[] upstreams)
    {
        var name = "edit" + Guid.NewGuid().ToString("N")[..8];
        await using var scope = server.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IFeedStore>().CreateAsync(
            new Feed
            {
                Name = name,
                NameLower = name,
                Kind = FeedKind.Proxy,
                AnonymousRead = true,
                CreatedUtc = DateTime.UtcNow,
                Upstreams = [.. upstreams.Select((u, i) => new FeedUpstream { Name = u.Name, Url = u.Url, Ordinal = i })],
            },
            CancellationToken.None));
        return name;
    }

    private async Task<Feed?> FindAsync(string feed)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IFeedStore>().FindAsync(feed, CancellationToken.None);
    }

    private async Task<HttpClient> AdminAsync()
    {
        var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = server.BaseAddress };
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(browser));
        return browser;
    }

    /// <summary>Posts the edit form with the antiforgery token of the feed's settings page.</summary>
    private static async Task<HttpResponseMessage> PostAsync(HttpClient browser, string feed, params (string Name, string Value)[] fields)
    {
        var html = await HttpAssert.SuccessBodyAsync(await browser.GetAsync($"/admin/feeds/{feed}/upstreams"));
        var values = fields.ToDictionary(f => f.Name, f => f.Value);
        values["__RequestVerificationToken"] = WebUtility.HtmlDecode(AntiforgeryPattern().Match(html).Groups["value"].Value);
        using var content = new FormUrlEncodedContent(values);
        return await browser.PostAsync($"/admin/feeds/{feed}/upstreams/update", content);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<value>[^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex AntiforgeryPattern();
}
