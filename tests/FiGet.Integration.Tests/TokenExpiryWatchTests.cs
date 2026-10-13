using System.Net;
using System.Text.Json.Nodes;
using FiGet.Application.Ports;
using FiGet.Application.Tokens;
using FiGet.Domain.Entities;
using FiGet.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

public sealed class SqliteTokenExpiryWatchTests(SqliteServerFixture fixture) : TokenExpiryWatchTests(fixture), IClassFixture<SqliteServerFixture>;

public sealed class SqlServerTokenExpiryWatchTests(SqlServerServerFixture fixture) : TokenExpiryWatchTests(fixture), IClassFixture<SqlServerServerFixture>;

/// <summary>
/// Tokens that end soon: found once per threshold by the watch, marked on the tokens page, and counted down in the API's
/// list. The rule itself (which day gets a warning) is in the unit tests; here it is what the database remembers.
/// </summary>
public abstract class TokenExpiryWatchTests
{
    private readonly FiGetServerFixture server;

    protected TokenExpiryWatchTests(FiGetServerFixture server)
    {
        this.server = server;
        server.SkipIfUnavailable();
    }

    [Fact]
    public async Task A_token_that_ends_soon_is_found_once_and_not_again()
    {
        var soon = await CreateAsync(TimeSpan.FromDays(10));
        var sooner = await CreateAsync(TimeSpan.FromHours(30));
        var later = await CreateAsync(TimeSpan.FromDays(60));
        var never = await CreateAsync(lifetime: null);

        var first = await TakeDueAsync();
        Assert.Equal((10, 14), Found(first, soon));
        Assert.Equal((2, 3), Found(first, sooner));
        Assert.DoesNotContain(first, w => w.Token.Key == later.Key || w.Token.Key == never.Key);

        // Looking again - another day, another replica, after a restart - says nothing twice: it is in the database.
        var second = await TakeDueAsync();
        Assert.DoesNotContain(second, w => w.Token.Key == soon.Key || w.Token.Key == sooner.Key);
    }

    [Fact]
    public async Task A_revoked_token_is_not_warned_about()
    {
        var token = await CreateAsync(TimeSpan.FromDays(2));
        await using (var scope = server.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<AccessTokenService>().RevokeAsync(token.Key, CancellationToken.None));
        }

        Assert.DoesNotContain(await TakeDueAsync(), w => w.Token.Key == token.Key);
    }

    [Fact]
    public async Task The_tokens_page_marks_a_token_that_ends_soon()
    {
        var soon = await CreateAsync(TimeSpan.FromDays(5));
        var later = await CreateAsync(TimeSpan.FromDays(60));

        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = server.BaseAddress };
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(browser));
        var page = await HttpAssert.SuccessBodyAsync(await browser.GetAsync("/admin/tokens"));

        Assert.Contains("ends in 5 days", Row(page, soon.Name), StringComparison.Ordinal);
        Assert.DoesNotContain("ends in", Row(page, later.Name), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_api_list_counts_the_days_a_token_has_left()
    {
        var soon = await CreateAsync(TimeSpan.FromDays(5));
        var never = await CreateAsync(lifetime: null);

        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        var list = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await admin.GetAsync("api/management/tokens")))!.AsArray();

        Assert.Equal(5, list.Single(t => t!["id"]!.GetValue<int>() == soon.Key)!["daysLeft"]!.GetValue<int>());
        Assert.Null(list.Single(t => t!["id"]!.GetValue<int>() == never.Key)!["daysLeft"]);
    }

    private async Task<AccessToken> CreateAsync(TimeSpan? lifetime)
    {
        await using var scope = server.Services.CreateAsyncScope();

        // A minute short of what was asked: the page and the list are read a moment later, and "5 days" must still be 5 then.
        DateTime? expires = lifetime is { } span ? DateTime.UtcNow.Add(span).AddMinutes(-1) : null;
        var created = await scope.ServiceProvider.GetRequiredService<AccessTokenService>()
            .CreateServiceTokenAsync(FiGetServerFixture.SuperAdminActor, "expiry-" + Guid.NewGuid().ToString("N")[..10], TokenScopes.Read, feedKey: null, expires, CancellationToken.None);
        return created.Created!.Token;
    }

    private async Task<IReadOnlyList<TokenExpiryWarning>> TakeDueAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TokenExpiryWatch>().TakeDueAsync(CancellationToken.None);
    }

    private static (int DaysLeft, int Threshold) Found(IReadOnlyList<TokenExpiryWarning> warnings, AccessToken token)
    {
        var warning = Assert.Single(warnings, w => w.Token.Key == token.Key);
        return (warning.DaysLeft, warning.Threshold);
    }

    /// <summary>The table row that names the token, so what is asserted is said about that token and no other.</summary>
    private static string Row(string page, string name)
    {
        var at = page.IndexOf($"<td>{name}</td>", StringComparison.Ordinal);
        Assert.True(at >= 0, $"The tokens page does not list {name}.");
        var end = page.IndexOf("</tr>", at, StringComparison.Ordinal);
        return page[at..end];
    }
}
