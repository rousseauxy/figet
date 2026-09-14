using System.Net;
using System.Text.RegularExpressions;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

/// <summary>
/// A feed or asset directory limited to listed networks (its settings page, "Allowed networks"). Behind the
/// forwarded-headers fixture, so a request can say where it comes from; without the header it comes from loopback.
/// </summary>
public sealed partial class FeedNetworkTests(ForwardedHeadersServerFixture server) : IClassFixture<ForwardedHeadersServerFixture>
{
    private const string Elsewhere = "10.0.0.0/8";

    /// <summary>The loopback both ways, since the test client may connect over either.</summary>
    private const string Here = "127.0.0.0/8\n::1";

    [Fact]
    public async Task A_feed_limited_to_other_networks_answers_403_and_is_off_the_pages_whatever_the_credentials()
    {
        var feed = await CreateFeedAsync(FeedKind.Curated);
        var directory = await CreateFeedAsync(FeedKind.Assets);
        await SetNetworksAsync(feed, Elsewhere);
        await SetNetworksAsync(directory, Elsewhere);

        using var anonymous = server.CreateClient();
        var refused = await anonymous.GetAsync($"nuget/{feed}/v3/index.json");
        HttpAssert.Status(HttpStatusCode.Forbidden, refused);
        Assert.Contains("not reachable from your network address", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        HttpAssert.Status(HttpStatusCode.Forbidden, await anonymous.GetAsync($"endpoints/{directory}/content/readme.txt"));
        HttpAssert.Status(HttpStatusCode.Forbidden, await anonymous.GetAsync($"endpoints/{directory}/dir/"));

        // A key does not move the limit.
        using var withKey = server.CreateClient(FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.Forbidden, await withKey.GetAsync($"nuget/{feed}/v3/index.json"));
        HttpAssert.Status(HttpStatusCode.Forbidden, await withKey.GetAsync($"nuget/{feed}/api/v2/Packages()"));

        // The pages: not listed, and the feed's own page does not exist from here.
        Assert.DoesNotContain($">{feed}<", await HttpAssert.SuccessBodyAsync(await anonymous.GetAsync("/")), StringComparison.Ordinal);
        Assert.DoesNotContain($">{directory}<", await HttpAssert.SuccessBodyAsync(await anonymous.GetAsync("/assets")), StringComparison.Ordinal);
        HttpAssert.Status(HttpStatusCode.NotFound, await anonymous.GetAsync($"/feeds/{feed}"));
        HttpAssert.Status(HttpStatusCode.NotFound, await anonymous.GetAsync($"/assets/{directory}"));

        // From the listed network, everything is as it was.
        using var fromInside = server.CreateClient();
        fromInside.DefaultRequestHeaders.Add("X-Forwarded-For", "10.20.30.40");
        HttpAssert.Status(HttpStatusCode.OK, await fromInside.GetAsync($"nuget/{feed}/v3/index.json"));
        HttpAssert.Status(HttpStatusCode.NotFound, await fromInside.GetAsync($"endpoints/{directory}/content/readme.txt"));
        HttpAssert.Status(HttpStatusCode.OK, await fromInside.GetAsync($"/feeds/{feed}"));
        HttpAssert.Status(HttpStatusCode.OK, await fromInside.GetAsync($"/assets/{directory}"));
        Assert.Contains($">{feed}<", await HttpAssert.SuccessBodyAsync(await fromInside.GetAsync("/")), StringComparison.Ordinal);

        // The settings pages are not limited: an administrator elsewhere can still undo the setting.
        using var admin = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(admin));
        HttpAssert.Status(HttpStatusCode.OK, await admin.GetAsync($"/admin/feeds/{feed}"));
        HttpAssert.Status(HttpStatusCode.NotFound, await admin.GetAsync($"/feeds/{feed}"));

        // Recorded, once per feed and address a window; the newest is whichever of the two was refused last.
        var entry = await AuditWait.ForAsync(server, "feed.network.refused");
        Assert.Contains(entry.Subject, new[] { feed, directory });
    }

    [Fact]
    public async Task The_settings_page_saves_the_list_and_refuses_text_that_is_not_an_address()
    {
        var feed = await CreateFeedAsync(FeedKind.Curated);
        using var admin = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(admin));

        var page = await HttpAssert.SuccessBodyAsync(await admin.GetAsync($"/admin/feeds/{feed}"));
        Assert.Contains("Allowed networks: any", page, StringComparison.Ordinal);
        Assert.Contains("<details class=\"fg-accordion\" id=\"networks\">", page, StringComparison.Ordinal);

        var refused = await PostNetworksAsync(admin, feed, page, "10.0.0.1/8");
        Assert.Contains("&#x27;10.0.0.1/8&#x27; is not an address or a range", refused, StringComparison.Ordinal);
        Assert.Contains("<details class=\"fg-accordion\" id=\"networks\" open>", refused, StringComparison.Ordinal);

        var saved = await PostNetworksAsync(admin, feed, page, $"{Here}\n{Elsewhere}");
        Assert.Contains("Allowed networks saved.", saved, StringComparison.Ordinal);
        Assert.Contains("Allowed networks: 3 listed", saved, StringComparison.Ordinal);
        using var anonymous = server.CreateClient();
        HttpAssert.Status(HttpStatusCode.OK, await anonymous.GetAsync($"nuget/{feed}/v3/index.json"));
        Assert.Contains("listed networks", await HttpAssert.SuccessBodyAsync(await admin.GetAsync("/admin/feeds")), StringComparison.Ordinal);

        var entry = await AuditWait.ForAsync(server, "feed.networks");
        Assert.Equal(feed, entry.Subject);

        var cleared = await PostNetworksAsync(admin, feed, page, " ");
        Assert.Contains("Allowed networks: any", cleared, StringComparison.Ordinal);
    }

    private static async Task<string> PostNetworksAsync(HttpClient browser, string feed, string page, string text)
    {
        var form = FormElement().Matches(page).Select(m => m.Value).Single(f => f.Contains("value=\"feed-networks\"", StringComparison.Ordinal));
        var fields = HiddenInput().Matches(form).ToDictionary(m => WebUtility.HtmlDecode(m.Groups["name"].Value), m => WebUtility.HtmlDecode(m.Groups["value"].Value));
        fields[BrowserSignIn.InputName(form, "allowed-networks")] = text;
        using var content = new FormUrlEncodedContent(fields);
        return await HttpAssert.SuccessBodyAsync(await browser.PostAsync($"/admin/feeds/{feed}", content));
    }

    private async Task<string> CreateFeedAsync(FeedKind kind)
    {
        var name = (kind == FeedKind.Assets ? "netdir" : "netfeed") + Guid.NewGuid().ToString("N")[..8];
        await using var scope = server.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IFeedStore>().CreateAsync(
            new Feed { Name = name, NameLower = name, Kind = kind, AnonymousRead = true, AnonymousList = kind == FeedKind.Assets, CreatedUtc = DateTime.UtcNow },
            CancellationToken.None));
        return name;
    }

    private async Task SetNetworksAsync(string feed, string networks)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var feeds = scope.ServiceProvider.GetRequiredService<IFeedStore>();
        var target = (await feeds.FindAsync(feed, CancellationToken.None))!;
        Assert.True(await feeds.UpdateAllowedNetworksAsync(target.Key, networks, CancellationToken.None));
    }

    private HttpClient CreateBrowser() =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = server.BaseAddress };

    [GeneratedRegex("<input[^>]*type=\"hidden\"[^>]*name=\"(?<name>[^\"]+)\"[^>]*value=\"(?<value>[^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex HiddenInput();

    [GeneratedRegex("<form[^>]*>.*?</form>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex FormElement();
}
