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
        Assert.DoesNotContain("href=\"/tokens\"", home, StringComparison.Ordinal);
        Assert.DoesNotContain("/settings\"", home, StringComparison.Ordinal);

        var feed = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/feeds/public"));
        Assert.DoesNotContain("/feeds/public/settings", feed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Managing_still_requires_signing_in()
    {
        using var client = CreateBrowser();

        foreach (var path in new[] { "/tokens", "/feeds/public/settings" })
        {
            var response = await client.GetAsync(path);
            HttpAssert.Status(HttpStatusCode.Redirect, response);
            Assert.Contains("/account/login", response.Headers.Location!.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_feed_that_needs_credentials_is_not_browsable_anonymously()
    {
        using var client = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.NotFound, await client.GetAsync("/feeds/private"));

        HttpAssert.Status(HttpStatusCode.Redirect, await SignInAsync(client, FiGetServerFixture.AdminToken));
        await HttpAssert.SuccessBodyAsync(await client.GetAsync("/feeds/private"));
    }

    [Fact]
    public async Task A_wrong_token_is_refused_and_an_admin_token_signs_in()
    {
        using var client = CreateBrowser();

        var refused = await SignInAsync(client, "figet_wrong");
        HttpAssert.Status(HttpStatusCode.OK, refused);
        Assert.Contains("not a valid admin token", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var accepted = await SignInAsync(client, FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.Redirect, accepted);

        var feeds = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/"));
        Assert.Contains(">public<", feeds, StringComparison.Ordinal);
        Assert.Contains(">private<", feeds, StringComparison.Ordinal);
        Assert.Contains("Create a feed", feeds, StringComparison.Ordinal);
        Assert.Contains("/nuget/public/v3/index.json", feeds, StringComparison.Ordinal);

        await HttpAssert.SuccessBodyAsync(await client.GetAsync("/tokens"));
        await HttpAssert.SuccessBodyAsync(await client.GetAsync("/feeds/public"));
    }

    [Fact]
    public async Task A_non_admin_token_cannot_sign_in()
    {
        using var client = CreateBrowser();
        var response = await SignInAsync(client, await CreatePushTokenAsync());

        HttpAssert.Status(HttpStatusCode.OK, response);
        Assert.Contains("not a valid admin token", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_feed_list_offers_a_copy_button_and_a_settings_link()
    {
        using var client = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await SignInAsync(client, FiGetServerFixture.AdminToken));

        var feeds = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/"));

        Assert.Contains("data-copy=\"", feeds, StringComparison.Ordinal);
        Assert.Contains("/nuget/public/v3/index.json\"", feeds, StringComparison.Ordinal);
        Assert.Contains("href=\"/feeds/public/settings\"", feeds, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Feed_settings_can_be_changed()
    {
        using var client = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await SignInAsync(client, FiGetServerFixture.AdminToken));
        var feed = await CreateFeedAsync("settings-target", anonymousRead: false);

        var before = await HttpAssert.SuccessBodyAsync(await client.GetAsync($"/feeds/{feed}/settings"));
        var form = FormBlock(before, "feed-settings");
        var fields = HiddenFields(form);
        fields[FieldName(form, "anonymous-read")] = "true";
        fields[FieldName(form, "allow-overwrite")] = "true";
        fields[FieldName(form, "delete-behaviour")] = nameof(FiGet.Core.Entities.PackageDeletionBehavior.HardDelete);

        using var content = new FormUrlEncodedContent(fields);
        var saved = await HttpAssert.SuccessBodyAsync(await client.PostAsync($"/feeds/{feed}/settings", content));
        Assert.Contains("Settings saved.", saved, StringComparison.Ordinal);

        var stored = await FindFeedAsync(feed);
        Assert.True(stored!.AnonymousRead);
        Assert.True(stored.AllowOverwrite);
        Assert.Equal(FiGet.Core.Entities.PackageDeletionBehavior.HardDelete, stored.DeletionBehavior);
    }

    [Fact]
    public async Task A_feed_is_deleted_only_when_its_name_is_typed_exactly()
    {
        using var client = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await SignInAsync(client, FiGetServerFixture.AdminToken));
        var feed = await CreateFeedAsync("delete-target", anonymousRead: true);

        var page = await HttpAssert.SuccessBodyAsync(await client.GetAsync($"/feeds/{feed}/settings"));
        var form = FormBlock(page, "delete-feed");
        var confirmField = FieldName(form, "confirm-name");

        var wrong = HiddenFields(form);
        wrong[confirmField] = "not-the-name";
        using var wrongContent = new FormUrlEncodedContent(wrong);
        var refused = await HttpAssert.SuccessBodyAsync(await client.PostAsync($"/feeds/{feed}/settings", wrongContent));
        Assert.Contains("exactly to confirm", refused, StringComparison.Ordinal);
        Assert.NotNull(await FindFeedAsync(feed));

        var right = HiddenFields(form);
        right[confirmField] = feed;
        using var rightContent = new FormUrlEncodedContent(right);
        var response = await client.PostAsync($"/feeds/{feed}/settings", rightContent);
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Redirect, $"Unexpected status {response.StatusCode}.");
        Assert.Null(await FindFeedAsync(feed));
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
        var feeds = (FiGet.Core.Stores.IFeedStore)scope.ServiceProvider.GetService(typeof(FiGet.Core.Stores.IFeedStore))!;
        var created = await feeds.CreateAsync(
            new FiGet.Core.Entities.Feed { Name = name, NameLower = name, AnonymousRead = anonymousRead, CreatedUtc = DateTime.UtcNow },
            CancellationToken.None);
        Assert.True(created, $"Could not create the feed '{name}'.");
        return name;
    }

    private async Task<FiGet.Core.Entities.Feed?> FindFeedAsync(string name)
    {
        await using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateAsyncScope(server.Services);
        var feeds = (FiGet.Core.Stores.IFeedStore)scope.ServiceProvider.GetService(typeof(FiGet.Core.Stores.IFeedStore))!;
        return await feeds.FindAsync(name, CancellationToken.None);
    }

    private HttpClient CreateBrowser() =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = server.BaseAddress };

    /// <summary>Fetches the login form (for its antiforgery token and field names) and posts it.</summary>
    private static async Task<HttpResponseMessage> SignInAsync(HttpClient client, string token)
    {
        var form = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/account/login"));
        var fields = new Dictionary<string, string>();
        foreach (Match hidden in HiddenInput().Matches(form))
        {
            fields[WebUtility.HtmlDecode(hidden.Groups["name"].Value)] = WebUtility.HtmlDecode(hidden.Groups["value"].Value);
        }

        var tokenField = TokenInputName().Match(form);
        Assert.True(tokenField.Success, "The login form has no token input.");
        fields[WebUtility.HtmlDecode(tokenField.Groups["name"].Value)] = token;

        using var content = new FormUrlEncodedContent(fields);
        return await client.PostAsync("/account/login", content);
    }

    private async Task<string> CreatePushTokenAsync()
    {
        await using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateAsyncScope(server.Services);
        var tokens = (FiGet.Core.Tokens.AccessTokenService)scope.ServiceProvider.GetService(typeof(FiGet.Core.Tokens.AccessTokenService))!;
        return (await tokens.CreateAsync("ui-push", FiGet.Core.Entities.TokenScopes.Push, null, null, CancellationToken.None)).Secret;
    }

    [GeneratedRegex("<input[^>]*type=\"hidden\"[^>]*name=\"(?<name>[^\"]+)\"[^>]*value=\"(?<value>[^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex HiddenInput();

    [GeneratedRegex("<form[^>]*>.*?</form>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex FormElement();

    [GeneratedRegex("<input[^>]*id=\"token\"[^>]*name=\"(?<name>[^\"]+)\"|<input[^>]*name=\"(?<name>[^\"]+)\"[^>]*id=\"token\"", RegexOptions.CultureInvariant)]
    private static partial Regex TokenInputName();
}
