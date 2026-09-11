using System.Net;
using System.Text.RegularExpressions;
using FiGet.Integration.Tests.Infrastructure;

namespace FiGet.Integration.Tests;

public sealed partial class AdminUiTests(SqliteServerFixture server) : IClassFixture<SqliteServerFixture>
{
    [Fact]
    public async Task Pages_require_sign_in()
    {
        using var client = CreateBrowser();

        foreach (var path in new[] { "/", "/tokens", "/feeds/public" })
        {
            var response = await client.GetAsync(path);
            HttpAssert.Status(HttpStatusCode.Redirect, response);
            Assert.Contains("/account/login", response.Headers.Location!.ToString(), StringComparison.Ordinal);
        }
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

    [GeneratedRegex("<input[^>]*id=\"token\"[^>]*name=\"(?<name>[^\"]+)\"|<input[^>]*name=\"(?<name>[^\"]+)\"[^>]*id=\"token\"", RegexOptions.CultureInvariant)]
    private static partial Regex TokenInputName();
}
