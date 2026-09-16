using System.Net;
using FiGet.Application.Accounts;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

/// <summary>
/// The database page: what it tells an administrator, and that it tells nobody else. Its own fixture, because it asserts
/// on a whole rendered page and a sibling's traffic would not change it but a sibling's account would.
/// </summary>
public sealed class DatabasePageTests(SqliteServerFixture server) : IClassFixture<SqliteServerFixture>
{
    private const string Password = "database-page-password-01";

    [Fact]
    public async Task An_administrator_is_told_what_the_database_is_and_what_prunes_it()
    {
        using var client = Browser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(client));

        var page = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/admin/database"));

        // What it is, where, and which schema: the four questions the page exists for.
        Assert.Contains("SQLite", page, StringComparison.Ordinal);
        Assert.Contains("figet", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("JobLeaseTakenUtc", page, StringComparison.Ordinal);

        // Every job, with what stops growing when it stops - the part a table list would not have said.
        foreach (var job in (string[])["Retention", "Audit prune", "Usage prune", "Upload sweep", "Catalogue sweep", "Change report"])
        {
            Assert.Contains(job, page, StringComparison.Ordinal);
        }

        // Nothing waiting: a started server has applied its migrations.
        Assert.DoesNotContain("have not been applied", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one thing this page must never print. The test fixture's connection string has no password, so the assertion
    /// is on the shape: a connection string on the page would bring its other keys with it.
    /// </summary>
    [Fact]
    public async Task The_page_shows_where_the_database_is_and_not_how_to_connect_to_it()
    {
        using var client = Browser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(client));

        var page = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/admin/database"));

        Assert.DoesNotContain("Password=", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Data Source=", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Integrated Security", page, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Where the database lives, how large it is and what protects it is not for every signed-in account.</summary>
    [Fact]
    public async Task Anyone_below_an_administrator_cannot_read_it()
    {
        using var anonymous = Browser();
        HttpAssert.Status(HttpStatusCode.Redirect, await anonymous.GetAsync("/admin/database"));

        var name = "plain" + Guid.NewGuid().ToString("N")[..8];
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<IUserStore>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            Assert.True(await users.AddAsync(
                new User
                {
                    UserName = name,
                    UserNameLower = name,
                    Role = UserRole.User,
                    PasswordHash = hasher.Hash(Password),
                    SecurityStamp = AccountService.NewStamp(),
                    CreatedUtc = DateTime.UtcNow,
                },
                TestContext.Current.CancellationToken));
        }

        using var plain = Browser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(plain, name, Password));

        // A page, not an endpoint: the cookie scheme sends an account without the role to the sign-in page rather than
        // answering 403, and either way it never sees the database.
        using var refused = await plain.GetAsync("/admin/database");
        HttpAssert.Status(HttpStatusCode.Redirect, refused);
        Assert.Contains("/account/login", refused.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    /// <summary>A client that keeps its cookies and follows nothing: pages authenticate by cookie, not by token.</summary>
    private HttpClient Browser() =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() })
        {
            BaseAddress = server.BaseAddress,
        };
}
