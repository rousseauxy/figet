using System.Net;
using System.Text.RegularExpressions;
using FiGet.Integration.Tests.Infrastructure;

namespace FiGet.Integration.Tests;

public sealed partial class AdminUiTests(SqliteServerFixture server) : IClassFixture<SqliteServerFixture>
{
    /// <summary>
    /// Reading is open, managing is not: without signing in you see the dashboard and the feeds that allow
    /// anonymous reads, and none of the buttons. This is how the server being replaced behaves.
    /// </summary>
    [Fact]
    public async Task Reading_is_open_without_signing_in()
    {
        using var client = CreateBrowser();

        var home = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/"));
        Assert.Contains(">public<", home, StringComparison.Ordinal);
        Assert.Contains("/account/login", home, StringComparison.Ordinal);

        // A feed that needs credentials is not listed, and neither are the management controls.
        Assert.DoesNotContain(">private<", home, StringComparison.Ordinal);
        Assert.DoesNotContain("Create a feed", home, StringComparison.Ordinal);

        // Matched on the link, not the word: a feed may legitimately be named "settings-target".
        Assert.DoesNotContain("href=\"/admin", home, StringComparison.Ordinal);
        Assert.DoesNotContain("/settings\"", home, StringComparison.Ordinal);

        var feed = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/feeds/public"));
        Assert.DoesNotContain("/feeds/public/settings", feed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Managing_still_requires_signing_in()
    {
        using var client = CreateBrowser();

        foreach (var path in new[] { "/admin/tokens", "/admin/feeds", "/admin/feeds/public" })
        {
            var response = await client.GetAsync(path);
            HttpAssert.Status(HttpStatusCode.Redirect, response);
            Assert.Contains("/account/login", response.Headers.Location!.ToString(), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The bare path is what a person types. It answered 404, which reads as "there is no admin area"
    /// rather than "you are one click away from it".
    /// </summary>
    [Fact]
    public async Task The_bare_admin_path_leads_into_the_admin_area()
    {
        using var client = CreateBrowser();

        // A stranger meets the sign-in page. Not a 404, and not a hint about what is behind it either.
        var anonymous = await client.GetAsync("/admin");
        HttpAssert.Status(HttpStatusCode.Redirect, anonymous);
        Assert.Contains("/account/login", anonymous.Headers.Location!.ToString(), StringComparison.Ordinal);

        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(client));

        foreach (var path in new[] { "/admin", "/admin/" })
        {
            var response = await client.GetAsync(path);
            HttpAssert.Status(HttpStatusCode.Redirect, response);
            Assert.Equal("/admin/feeds", response.Headers.Location!.ToString());
        }
    }

    [Fact]
    public async Task A_feed_that_needs_credentials_is_not_browsable_anonymously()
    {
        using var client = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.NotFound, await client.GetAsync("/feeds/private"));

        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(client));
        await HttpAssert.SuccessBodyAsync(await client.GetAsync("/feeds/private"));
    }

    [Fact]
    public async Task A_wrong_password_is_refused_and_the_right_one_signs_in()
    {
        using var client = CreateBrowser();

        var refused = await BrowserSignIn.SignInAsync(client, FiGetServerFixture.AdminUserName, "not-the-password");
        HttpAssert.Status(HttpStatusCode.OK, refused);
        Assert.Contains("The user name or password is not right.", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var accepted = await BrowserSignIn.SignInAsync(client);
        HttpAssert.Status(HttpStatusCode.Redirect, accepted);

        var feeds = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/"));
        Assert.Contains(">public<", feeds, StringComparison.Ordinal);
        Assert.Contains(">private<", feeds, StringComparison.Ordinal);
        Assert.Contains("/nuget/public/v3/index.json", feeds, StringComparison.Ordinal);

        // The public page is read-only now; it offers the way in rather than the controls themselves.
        Assert.Contains("href=\"/admin/feeds\"", feeds, StringComparison.Ordinal);
        Assert.DoesNotContain("Create a feed", feeds, StringComparison.Ordinal);

        var admin = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/admin/feeds"));
        Assert.Contains("Create a feed", admin, StringComparison.Ordinal);

        await HttpAssert.SuccessBodyAsync(await client.GetAsync("/admin/tokens"));
        await HttpAssert.SuccessBodyAsync(await client.GetAsync("/feeds/public"));
    }

    /// <summary>
    /// An API token no longer opens the pages, admin scope or not: people sign in with accounts, and a key is for
    /// clients. Tried as the password of the token's own name and of a real account.
    /// </summary>
    [Fact]
    public async Task An_api_token_does_not_sign_in_to_the_pages()
    {
        using var client = CreateBrowser();
        foreach (var (user, secret) in new[] { ("bootstrap", FiGetServerFixture.AdminToken), (FiGetServerFixture.AdminUserName, FiGetServerFixture.AdminToken) })
        {
            var response = await BrowserSignIn.SignInAsync(client, user, secret);
            HttpAssert.Status(HttpStatusCode.OK, response);
            Assert.Contains("The user name or password is not right.", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_feed_list_copies_a_url_and_the_admin_area_links_to_settings()
    {
        using var client = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(client));

        // Copying a source URL is reading, so it stays on the page anyone can see.
        var feeds = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/"));
        Assert.Contains("data-copy=\"", feeds, StringComparison.Ordinal);
        Assert.Contains("/nuget/public/v3/index.json\"", feeds, StringComparison.Ordinal);

        // Changing a feed is not, so its link lives behind the admin navigation.
        var admin = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/admin/feeds"));
        Assert.Contains("href=\"/admin/feeds/public\"", admin, StringComparison.Ordinal);
    }

    /// <summary>
    /// The feed page keeps its Settings button for an admin, and it has to point where settings actually
    /// live. It did not: the page moved under /admin and this link kept naming the old route, so the one
    /// button an admin would press from a feed answered 404. Nothing failed, because nothing asserted it.
    /// </summary>
    [Fact]
    public async Task A_feed_page_links_its_settings_into_the_admin_area()
    {
        using var client = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(client));

        var page = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/feeds/public"));

        Assert.Contains("href=\"/admin/feeds/public\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("/feeds/public/settings", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Feed_settings_can_be_changed()
    {
        using var client = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(client));
        var feed = await CreateFeedAsync("settings-target", anonymousRead: false);

        var before = await HttpAssert.SuccessBodyAsync(await client.GetAsync($"/admin/feeds/{feed}"));
        var form = FormBlock(before, "feed-settings");
        var fields = HiddenFields(form);
        fields[FieldName(form, "anonymous-read")] = "true";
        fields[FieldName(form, "allow-overwrite")] = "true";
        fields[FieldName(form, "delete-behaviour")] = nameof(FiGet.Domain.Entities.PackageDeletionBehavior.HardDelete);

        using var content = new FormUrlEncodedContent(fields);
        var saved = await HttpAssert.SuccessBodyAsync(await client.PostAsync($"/admin/feeds/{feed}", content));
        Assert.Contains("Settings saved.", saved, StringComparison.Ordinal);

        var stored = await FindFeedAsync(feed);
        Assert.True(stored!.AnonymousRead);
        Assert.True(stored.AllowOverwrite);
        Assert.Equal(FiGet.Domain.Entities.PackageDeletionBehavior.HardDelete, stored.DeletionBehavior);
    }

    /// <summary>
    /// Choosing a theme has to reach the <c>&lt;head&gt;</c> of every page, which is further than it
    /// looks: the choice is stored in the database, read by the root component, and beats the configured
    /// value. Asserted end to end because each half can work while the whole does nothing - a stored row
    /// nobody reads, or a page that keeps answering from configuration.
    /// </summary>
    [Fact]
    public async Task A_chosen_theme_is_stored_and_linked_by_every_page()
    {
        using var client = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(client));

        var page = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/admin/appearance"));
        Assert.Contains("cobalt", page, StringComparison.Ordinal);

        try
        {
            var form = FormBlock(page, "choose-theme");
            var fields = HiddenFields(form);
            fields[FieldName(form, "theme")] = "cobalt";
            using var content = new FormUrlEncodedContent(fields);

            var applied = await client.PostAsync("/admin/appearance", content);
            Assert.True(
                applied.IsSuccessStatusCode || applied.StatusCode == HttpStatusCode.Redirect,
                $"Unexpected status {applied.StatusCode}.");

            var home = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/"));
            Assert.Contains("/themes/cobalt.css", home, StringComparison.Ordinal);
        }
        finally
        {
            // The fixture is shared, and a theme left set would follow every later test into its page.
            var again = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/admin/appearance"));
            var reset = FormBlock(again, "choose-theme");
            var back = HiddenFields(reset);
            back[FieldName(reset, "theme")] = "";
            using var empty = new FormUrlEncodedContent(back);
            await client.PostAsync("/admin/appearance", empty);
        }
    }

    /// <summary>
    /// Asset directories have their own admin tab, and are created there: the feeds tab no longer offers files,
    /// and the directory tab does not ask package questions.
    /// </summary>
    [Fact]
    public async Task An_asset_directory_is_created_from_its_own_tab()
    {
        using var client = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(client));
        var name = "dir-" + Guid.NewGuid().ToString("N")[..8];

        var feeds = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/admin/feeds"));
        Assert.DoesNotContain("asset directory", FormBlock(feeds, "create-feed"), StringComparison.OrdinalIgnoreCase);

        var page = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/admin/assets"));
        var form = FormBlock(page, "create-feed");
        Assert.DoesNotContain("Delete behaviour", form, StringComparison.Ordinal);
        var fields = HiddenFields(form);
        fields[FieldName(form, "feed-name")] = name;

        using var content = new FormUrlEncodedContent(fields);
        var created = await HttpAssert.SuccessBodyAsync(await client.PostAsync("/admin/assets", content));
        Assert.Contains($"Asset directory &#x27;{name}&#x27; created.", created, StringComparison.Ordinal);
        Assert.Contains($"href=\"/admin/assets/{name}\"", created, StringComparison.Ordinal);
        Assert.Equal(FiGet.Domain.Entities.FeedKind.Assets, (await FindFeedAsync(name))!.Kind);
    }

    /// <summary>
    /// A pull posts back to the page it came from with what it did, as counts the page reads back - including when
    /// nothing could be pulled, which is the answer a reader most needs to see.
    /// </summary>
    [Fact]
    public async Task A_pull_returns_to_its_page_with_the_counts()
    {
        using var client = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(client));

        var page = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/admin/feeds"));
        var fields = HiddenFields(FormBlock(page, "create-feed"));
        fields.Remove("_handler");
        fields["id"] = "No.Such.Package";
        fields["version"] = "1.0.0";
        fields["returnUrl"] = "/feeds/public?q=something";

        using var content = new FormUrlEncodedContent(fields);
        var response = await client.PostAsync("/admin/feeds/public/pull", content);

        HttpAssert.Status(HttpStatusCode.Redirect, response);
        Assert.Equal("/feeds/public?q=something&pulled=0&present=0&missing=1", response.Headers.Location!.ToString());

        var shown = await HttpAssert.SuccessBodyAsync(await client.GetAsync(response.Headers.Location));
        Assert.Contains("No upstream of this feed could serve that package.", shown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_feed_is_deleted_only_when_its_name_is_typed_exactly()
    {
        using var client = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(client));
        var feed = await CreateFeedAsync("delete-target", anonymousRead: true);

        var page = await HttpAssert.SuccessBodyAsync(await client.GetAsync($"/admin/feeds/{feed}"));
        var form = FormBlock(page, "delete-feed");
        var confirmField = FieldName(form, "confirm-name");

        var wrong = HiddenFields(form);
        wrong[confirmField] = "not-the-name";
        using var wrongContent = new FormUrlEncodedContent(wrong);
        var refused = await HttpAssert.SuccessBodyAsync(await client.PostAsync($"/admin/feeds/{feed}", wrongContent));
        Assert.Contains("exactly to confirm", refused, StringComparison.Ordinal);
        Assert.NotNull(await FindFeedAsync(feed));

        var right = HiddenFields(form);
        right[confirmField] = feed;
        using var rightContent = new FormUrlEncodedContent(right);
        var response = await client.PostAsync($"/admin/feeds/{feed}", rightContent);
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Redirect, $"Unexpected status {response.StatusCode}.");
        Assert.Null(await FindFeedAsync(feed));
    }

    /// <summary>
    /// The admin buttons are plain form posts to minimal API handlers, and a browser sends the sign-in cookie
    /// with a post from any page - including a page on a sibling subdomain, which SameSite=Lax counts as the
    /// same site. The antiforgery token is what proves the post came from this server's own page.
    ///
    /// These handlers read their forms by hand, and the framework enforces the token only while binding a
    /// form to a parameter, so for as long as the admin area has existed every one of them accepted a post
    /// without it: this one answered 302 and added the upstream.
    /// </summary>
    [Fact]
    public async Task An_admin_button_post_without_its_page_token_changes_nothing()
    {
        using var client = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(client));
        var feed = await CreateFeedAsync("forgery-target", anonymousRead: true);
        var upstream = new Dictionary<string, string> { ["name"] = "forged", ["url"] = "https://example.invalid/v3/index.json", ["kind"] = "V3" };

        using var forged = new FormUrlEncodedContent(upstream);
        HttpAssert.Status(HttpStatusCode.BadRequest, await client.PostAsync($"/admin/feeds/{feed}/upstreams/add", forged));
        Assert.Empty((await FindFeedAsync(feed))!.Upstreams);

        // The same post from the settings page, token and all, still works.
        var page = await HttpAssert.SuccessBodyAsync(await client.GetAsync($"/admin/feeds/{feed}"));
        var form = FormElement().Matches(page).Single(f => f.Value.Contains("upstreams/add", StringComparison.Ordinal)).Value;
        var fields = HiddenFields(form);
        foreach (var (key, value) in upstream)
        {
            fields[key] = value;
        }

        using var genuine = new FormUrlEncodedContent(fields);
        HttpAssert.Status(HttpStatusCode.Redirect, await client.PostAsync($"/admin/feeds/{feed}/upstreams/add", genuine));
        Assert.Single((await FindFeedAsync(feed))!.Upstreams);
    }

    /// <summary>The HTML of the form whose hidden _handler field carries this form name.</summary>
    private static string FormBlock(string html, string formName)
    {
        foreach (Match form in FormElement().Matches(html))
        {
            if (form.Value.Contains($"value=\"{formName}\"", StringComparison.Ordinal))
            {
                return form.Value;
            }
        }

        Assert.Fail($"No form named '{formName}' in the page.");
        return "";
    }

    private static Dictionary<string, string> HiddenFields(string formHtml)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match hidden in HiddenInput().Matches(formHtml))
        {
            fields[WebUtility.HtmlDecode(hidden.Groups["name"].Value)] = WebUtility.HtmlDecode(hidden.Groups["value"].Value);
        }

        return fields;
    }

    /// <summary>The generated name of the input or select with this id. Blazor derives it from the model path.</summary>
    private static string FieldName(string formHtml, string id)
    {
        var match = Regex.Match(
            formHtml,
            "<(?:input|select)[^>]*id=\"" + Regex.Escape(id) + "\"[^>]*name=\"(?<name>[^\"]+)\"|<(?:input|select)[^>]*name=\"(?<name>[^\"]+)\"[^>]*id=\"" + Regex.Escape(id) + "\"",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"No field with id '{id}' in the form.");
        return WebUtility.HtmlDecode(match.Groups["name"].Value);
    }

    private async Task<string> CreateFeedAsync(string name, bool anonymousRead)
    {
        await using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateAsyncScope(server.Services);
        var feeds = (FiGet.Application.Ports.IFeedStore)scope.ServiceProvider.GetService(typeof(FiGet.Application.Ports.IFeedStore))!;
        var created = await feeds.CreateAsync(
            new FiGet.Domain.Entities.Feed { Name = name, NameLower = name, AnonymousRead = anonymousRead, CreatedUtc = DateTime.UtcNow },
            CancellationToken.None);
        Assert.True(created, $"Could not create the feed '{name}'.");
        return name;
    }

    private async Task<FiGet.Domain.Entities.Feed?> FindFeedAsync(string name)
    {
        await using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateAsyncScope(server.Services);
        var feeds = (FiGet.Application.Ports.IFeedStore)scope.ServiceProvider.GetService(typeof(FiGet.Application.Ports.IFeedStore))!;
        return await feeds.FindAsync(name, CancellationToken.None);
    }

    private HttpClient CreateBrowser() =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = server.BaseAddress };

    /// <summary>Fetches the login form (for its antiforgery token and field names) and posts it.</summary>
    private async Task<string> CreatePushTokenAsync()
    {
        await using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateAsyncScope(server.Services);
        var tokens = (FiGet.Application.Tokens.AccessTokenService)scope.ServiceProvider.GetService(typeof(FiGet.Application.Tokens.AccessTokenService))!;
        return (await tokens.CreateAsync("ui-push", FiGet.Domain.Entities.TokenScopes.Push, null, null, CancellationToken.None)).Secret;
    }

    [GeneratedRegex("<input[^>]*type=\"hidden\"[^>]*name=\"(?<name>[^\"]+)\"[^>]*value=\"(?<value>[^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex HiddenInput();

    [GeneratedRegex("<form[^>]*>.*?</form>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex FormElement();

}
