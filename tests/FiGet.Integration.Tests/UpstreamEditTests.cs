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

    /// <summary>
    /// Manage on a feed covers an upstream's name, patterns and switch, and adding a known public gallery. Where an upstream
    /// points and with which credential is an admin's: that is a request the server makes from inside its network, with a
    /// key it sends along.
    /// </summary>
    [Fact]
    public async Task A_feed_manager_changes_an_upstreams_settings_but_not_where_it_points()
    {
        var feed = await CreateProxyFeedAsync(("stub", "https://stub.invalid/v3/index.json"));
        var upstream = Assert.Single((await FindAsync(feed))!.Upstreams);
        using var manager = await ManagerAsync(feed);

        // The edit form shows the source without a field for it, and the add form offers the known galleries only.
        var edit = await HttpAssert.SuccessBodyAsync(await manager.GetAsync($"/admin/feeds/{feed}/upstreams?edit={Key(upstream)}"));
        var row = Regex.Match(edit, $"<tr class=\"fg-row-panel\" id=\"upstream-{Key(upstream)}\">.*?</tr>", RegexOptions.Singleline).Value;
        Assert.Contains("upstreams/update", row, StringComparison.Ordinal);
        Assert.DoesNotContain($"id=\"upstream-{Key(upstream)}-url\"", row, StringComparison.Ordinal);
        Assert.DoesNotContain($"id=\"upstream-{Key(upstream)}-credential\"", row, StringComparison.Ordinal);
        Assert.Contains("name=\"known\"", edit, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"upstream-new-url\"", edit, StringComparison.Ordinal);

        using (var renamed = await PostAsync(manager, feed, "update", ("key", Key(upstream)), ("name", "renamed"), ("url", upstream.Url), ("kind", "V3"), ("allow", "^Contoso\\."), ("enabled", "true")))
        {
            Assert.Equal($"/admin/feeds/{feed}/upstreams?upstream=saved", renamed.Headers.Location?.OriginalString);
        }

        foreach (var (field, value) in new[] { ("url", "http://127.0.0.1:1/v3/index.json"), ("credentialRef", FeedUpstream.CredentialPrefix + "GALLERY"), ("kind", "V2") })
        {
            var fields = new Dictionary<string, string> { ["key"] = Key(upstream), ["name"] = "renamed", ["url"] = upstream.Url, ["kind"] = "V3", ["enabled"] = "true" };
            fields[field] = value;
            using var refused = await PostAsync(manager, feed, "update", [.. fields.Select(f => (f.Key, f.Value))]);
            Assert.Equal($"/admin/feeds/{feed}/upstreams?edit={Key(upstream)}&upstream=admin-only#upstream-{Key(upstream)}", refused.Headers.Location?.OriginalString);
        }

        using (var added = await PostAsync(manager, feed, "add", ("name", "own"), ("url", "http://127.0.0.1:1/v3/index.json"), ("kind", "V3")))
        {
            Assert.Equal($"/admin/feeds/{feed}/upstreams?upstream=admin-only&open=add-upstream", added.Headers.Location?.OriginalString);
            var reopened = await HttpAssert.SuccessBodyAsync(await manager.GetAsync(added.Headers.Location));
            Assert.Contains("Only an admin changes where an upstream points", reopened, StringComparison.Ordinal);
        }

        HttpAssert.Status(HttpStatusCode.Redirect, await PostAsync(manager, feed, "add", ("name", "keyed"), ("known", "nuget.org"), ("credentialRef", FeedUpstream.CredentialPrefix + "GALLERY")));
        HttpAssert.Status(HttpStatusCode.Redirect, await PostAsync(manager, feed, "add", ("name", "gallery"), ("known", "PowerShell Gallery"), ("returnUrl", $"/admin/feeds/{feed}/upstreams?upstream=added")));

        var saved = (await FindAsync(feed))!.Upstreams.OrderBy(u => u.Ordinal).ToList();
        Assert.Equal(["renamed", "gallery"], saved.Select(u => u.Name));
        Assert.Equal(("https://stub.invalid/v3/index.json", UpstreamKind.V3, (string?)null, "^Contoso\\."), (saved[0].Url, saved[0].Kind, saved[0].CredentialRef, saved[0].Allow));
        Assert.Equal(("https://www.powershellgallery.com/api/v2", UpstreamKind.V2, (string?)null), (saved[1].Url, saved[1].Kind, saved[1].CredentialRef));
    }

    /// <summary>A credential reference names a variable under the upstream prefix, for an admin too: nothing else of the process's environment.</summary>
    [Fact]
    public async Task A_credential_outside_the_upstream_prefix_is_refused_even_for_an_admin()
    {
        var feed = await CreateProxyFeedAsync(("stub", "https://stub.invalid/v3/index.json"));
        var upstream = Assert.Single((await FindAsync(feed))!.Upstreams);
        using var admin = await AdminAsync();

        foreach (var reference in new[] { "FiGet__Database__ConnectionString", "FIGET_UPSTREAM_", "figet_upstream_lower", "PATH" })
        {
            using var refused = await PostAsync(admin, feed, "update", ("key", Key(upstream)), ("name", "stub"), ("url", upstream.Url), ("kind", "V3"), ("credentialRef", reference), ("enabled", "true"));
            Assert.Equal($"/admin/feeds/{feed}/upstreams?edit={Key(upstream)}&upstream=credential#upstream-{Key(upstream)}", refused.Headers.Location?.OriginalString);
        }

        using (var added = await PostAsync(admin, feed, "add", ("name", "other"), ("url", "https://other.invalid/v3/index.json"), ("credentialRef", "PATH")))
        {
            Assert.Equal($"/admin/feeds/{feed}/upstreams?upstream=credential&open=add-upstream", added.Headers.Location?.OriginalString);
        }

        using (var saved = await PostAsync(admin, feed, "update", ("key", Key(upstream)), ("name", "stub"), ("url", "http://127.0.0.1:1/v3/index.json"), ("kind", "V3"), ("credentialRef", FeedUpstream.CredentialPrefix + "GALLERY_2"), ("enabled", "true")))
        {
            Assert.Equal($"/admin/feeds/{feed}/upstreams?upstream=saved", saved.Headers.Location?.OriginalString);
        }

        Assert.Equal(FeedUpstream.CredentialPrefix + "GALLERY_2", Assert.Single((await FindAsync(feed))!.Upstreams).CredentialRef);
    }

    private const string ManagerPassword = "upstream-manager-pass-01";

    private async Task<HttpClient> ManagerAsync(string feed)
    {
        var name = "m" + Guid.NewGuid().ToString("N")[..10];
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<IUserStore>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            Assert.True(await users.AddAsync(
                new User { UserName = name, UserNameLower = name, PasswordHash = hasher.Hash(ManagerPassword), SecurityStamp = FiGet.Application.Accounts.AccountService.NewStamp(), CreatedUtc = DateTime.UtcNow },
                CancellationToken.None));
            var feedKey = (await scope.ServiceProvider.GetRequiredService<IFeedStore>().FindAsync(feed, CancellationToken.None))!.Key;
            var userKey = (await users.FindByUserNameAsync(name, CancellationToken.None))!.Key;
            await scope.ServiceProvider.GetRequiredService<IFeedPermissionStore>().SetAsync(feedKey, userKey, null, FeedAccessLevel.Manage, CancellationToken.None);
        }

        var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = server.BaseAddress };
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(browser, name, ManagerPassword));
        return browser;
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
    private static Task<HttpResponseMessage> PostAsync(HttpClient browser, string feed, params (string Name, string Value)[] fields) =>
        PostAsync(browser, feed, "update", fields);

    private static async Task<HttpResponseMessage> PostAsync(HttpClient browser, string feed, string action, params (string Name, string Value)[] fields)
    {
        var html = await HttpAssert.SuccessBodyAsync(await browser.GetAsync($"/admin/feeds/{feed}/upstreams"));
        var values = fields.ToDictionary(f => f.Name, f => f.Value);
        values["__RequestVerificationToken"] = WebUtility.HtmlDecode(AntiforgeryPattern().Match(html).Groups["value"].Value);
        using var content = new FormUrlEncodedContent(values);
        return await browser.PostAsync($"/admin/feeds/{feed}/upstreams/{action}", content);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<value>[^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex AntiforgeryPattern();
}
