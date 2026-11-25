using System.Net;
using FiGet.Application.Accounts;
using FiGet.Application.Ports;
using FiGet.Application.Tokens;
using FiGet.Domain.Entities;
using FiGet.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

/// <summary>
/// The bell in the top bar for tokens that end soon (asked for after QA, 2026-11-25: the warning was only in the audit
/// log, the server log and the tokens page, where nobody looks until a pipeline fails). Each account sees its own keys;
/// an administrator also sees the service tokens.
/// </summary>
public sealed class TokenExpiryBellTests(SqliteServerFixture server) : IClassFixture<SqliteServerFixture>
{
    private const string Password = "bell-test-password-01";

    [Fact]
    public async Task An_administrator_is_told_about_a_service_token_that_ends_soon()
    {
        var name = "bell-service-" + Guid.NewGuid().ToString("N")[..6];
        await CreateServiceTokenAsync(name, DateTime.UtcNow.AddDays(2).AddHours(1));

        using var admin = Browser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(admin));
        var page = await HttpAssert.SuccessBodyAsync(await admin.GetAsync("/assets"));

        Assert.Contains("class=\"fg-bell\" href=\"/admin/tokens\"", page, StringComparison.Ordinal);
        Assert.Contains(name + " (service)", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_account_is_told_about_its_own_key_and_not_about_service_tokens()
    {
        var service = "bell-hidden-" + Guid.NewGuid().ToString("N")[..6];
        await CreateServiceTokenAsync(service, DateTime.UtcNow.AddDays(2));
        var (user, actor) = await CreateUserAsync();

        using var browser = Browser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(browser, user, Password));
        var quiet = await HttpAssert.SuccessBodyAsync(await browser.GetAsync("/assets"));
        Assert.DoesNotContain("fg-bell", quiet, StringComparison.Ordinal);

        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AccessTokenService>()
                .CreatePersonalKeyAsync(actor, "laptop", TokenScopes.Read, null, DateTime.UtcNow.AddDays(5).AddHours(-1), CancellationToken.None);
        }

        var page = await HttpAssert.SuccessBodyAsync(await browser.GetAsync("/assets"));
        Assert.Contains("class=\"fg-bell\" href=\"/account/profile#keys\"", page, StringComparison.Ordinal);
        Assert.Contains("A token ends in 5 days: laptop.", page, StringComparison.Ordinal);
        Assert.DoesNotContain(service, page, StringComparison.Ordinal);

        // And the profile page, where the bell leads, marks the key the way the tokens page marks a service token.
        Assert.Contains("ends in 5 days", await HttpAssert.SuccessBodyAsync(await browser.GetAsync("/account/profile")), StringComparison.Ordinal);
    }

    private async Task CreateServiceTokenAsync(string name, DateTime expiresUtc)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<AccessTokenService>()
            .CreateServiceTokenAsync(FiGetServerFixture.SuperAdminActor, name, TokenScopes.Read, null, expiresUtc, CancellationToken.None);
        Assert.NotNull(created.Created);
    }

    private async Task<(string Name, AccountActor Actor)> CreateUserAsync()
    {
        var name = "bell" + Guid.NewGuid().ToString("N")[..8];
        await using var scope = server.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserStore>();
        var user = new User
        {
            UserName = name,
            UserNameLower = name,
            Role = UserRole.User,
            PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Password),
            SecurityStamp = AccountService.NewStamp(),
            CreatedUtc = DateTime.UtcNow,
        };
        Assert.True(await users.AddAsync(user, CancellationToken.None));
        return (name, new AccountActor(user.Key, UserRole.User));
    }

    private HttpClient Browser() =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() })
        {
            BaseAddress = server.BaseAddress,
        };
}
