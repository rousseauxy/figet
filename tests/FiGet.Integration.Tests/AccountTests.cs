using System.Net;
using System.Text.RegularExpressions;
using FiGet.Application.Accounts;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

/// <summary>A server started with recovery settings, as an operator would after everyone got locked out.</summary>
public sealed class RecoveryServerFixture() : FiGetServerFixture(TestDatabase.Sqlite)
{
    public const string RecoveredUser = "rescue";
    public const string RecoveredPassword = "rescue-password-0123";

    protected override void Configure(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSetting("FiGet:Auth:Recovery:UserName", RecoveredUser);
        builder.UseSetting("FiGet:Auth:Recovery:Password", RecoveredPassword);
    }
}

/// <summary>Local accounts: the first administrator, passwords, lockout, sessions that end when access changes, and roles.</summary>
public sealed partial class AccountTests(SqliteServerFixture server) : IClassFixture<SqliteServerFixture>
{
    /// <summary>
    /// A new instance has <c>admin</c> / <c>admin</c>, and that password opens nothing but the page to replace it.
    /// </summary>
    [Fact]
    public async Task The_first_administrator_must_choose_a_password_before_anything_else()
    {
        using var client = CreateBrowser();
        var signedIn = await BrowserSignIn.SignInAsync(client, AccountService.FirstAdminUserName, AccountService.FirstAdminUserName);
        HttpAssert.Status(HttpStatusCode.Redirect, signedIn);
        Assert.EndsWith("/account/password", signedIn.Headers.Location!.ToString());

        foreach (var path in new[] { "/", "/admin/feeds", "/admin/users" })
        {
            var blocked = await client.GetAsync(path);
            HttpAssert.Status(HttpStatusCode.Redirect, blocked);
            Assert.EndsWith("/account/password", blocked.Headers.Location!.ToString());
        }

        // Protocols are not pages: a package client is never sent to a password form.
        HttpAssert.Status(HttpStatusCode.OK, await client.GetAsync("nuget/public/v3/index.json"));

        var tooShort = await ChangePasswordAsync(client, AccountService.FirstAdminUserName, "short");
        Assert.Contains("at least 12 characters", tooShort, StringComparison.Ordinal);

        var page = await ChangePasswordAsync(client, AccountService.FirstAdminUserName, "a-proper-new-password");
        Assert.DoesNotContain("fg-error", page, StringComparison.Ordinal);
        await HttpAssert.SuccessBodyAsync(await client.GetAsync("/admin/users"));

        // And the old password is gone.
        using var other = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.OK, await BrowserSignIn.SignInAsync(other, AccountService.FirstAdminUserName, AccountService.FirstAdminUserName));
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_even_for_the_right_one()
    {
        var name = await CreateUserAsync(UserRole.User);
        using var client = CreateBrowser();
        for (var attempt = 0; attempt < AccountService.MaxFailedSignIns; attempt++)
        {
            HttpAssert.Status(HttpStatusCode.OK, await BrowserSignIn.SignInAsync(client, name, "wrong-password"));
        }

        var locked = await BrowserSignIn.SignInAsync(client, name, Password);
        HttpAssert.Status(HttpStatusCode.OK, locked);
        Assert.Contains("Too many failed attempts", await locked.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>A disabled account is signed out on its next request, not when its cookie would have expired.</summary>
    [Fact]
    public async Task Disabling_an_account_ends_its_session()
    {
        var name = await CreateUserAsync(UserRole.User);
        using var user = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(user, name, Password));
        await HttpAssert.SuccessBodyAsync(await user.GetAsync("/account/profile"));

        using var admin = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(admin));
        var key = (await FindAsync(name))!.Key;
        var disabled = await PostUserActionAsync(admin, key, "disable");
        Assert.Equal("/admin/users?done=disable", disabled.Headers.Location!.ToString());

        var after = await user.GetAsync("/account/profile");
        HttpAssert.Status(HttpStatusCode.Redirect, after);
        Assert.Contains("/account/login", after.Headers.Location!.ToString(), StringComparison.Ordinal);

        var again = await BrowserSignIn.SignInAsync(user, name, Password);
        Assert.Contains("This account is disabled.", await again.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_user_does_not_reach_the_admin_area()
    {
        var name = await CreateUserAsync(UserRole.User);
        using var client = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(client, name, Password));

        var page = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/"));
        Assert.DoesNotContain("href=\"/admin/feeds\"", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/account/profile\"", page, StringComparison.Ordinal);

        foreach (var path in new[] { "/admin/feeds", "/admin/users", "/admin/tokens" })
        {
            var refused = await client.GetAsync(path);
            HttpAssert.Status(HttpStatusCode.Redirect, refused);
            Assert.Contains("/account/login", refused.Headers.Location!.ToString(), StringComparison.Ordinal);
        }
    }

    /// <summary>An admin manages accounts with the user role, and cannot make anyone an admin - including themselves.</summary>
    [Fact]
    public async Task An_admin_manages_users_but_not_admins()
    {
        var adminName = await CreateUserAsync(UserRole.Admin);
        var otherAdmin = await CreateUserAsync(UserRole.Admin);
        var plain = await CreateUserAsync(UserRole.User);

        using var client = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(client, adminName, Password));

        var promote = await PostUserActionAsync(client, (await FindAsync(plain))!.Key, "role", ("role", nameof(UserRole.Admin)));
        Assert.Equal("/admin/users?done=forbidden", promote.Headers.Location!.ToString());

        var disableAdmin = await PostUserActionAsync(client, (await FindAsync(otherAdmin))!.Key, "disable");
        Assert.Equal("/admin/users?done=forbidden", disableAdmin.Headers.Location!.ToString());

        var self = await PostUserActionAsync(client, (await FindAsync(adminName))!.Key, "role", ("role", nameof(UserRole.SuperAdmin)));
        Assert.Equal("/admin/users?done=forbidden", self.Headers.Location!.ToString());

        var reset = await PostUserActionAsync(client, (await FindAsync(plain))!.Key, "password", ("password", "reset-by-an-admin-01"));
        Assert.Equal("/admin/users?done=password", reset.Headers.Location!.ToString());
        Assert.True((await FindAsync(plain))!.MustChangePassword);
    }

    [Fact]
    public async Task The_last_enabled_super_admin_cannot_be_removed()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
        var users = scope.ServiceProvider.GetRequiredService<IUserStore>();

        // Every super admin but one disabled through the store, so the rule is tested on its own and not on the order
        // other tests happen to run in.
        var remaining = await CreateUserAsync(UserRole.SuperAdmin);
        var acting = await CreateUserAsync(UserRole.SuperAdmin);
        var restore = new List<User>();
        foreach (var other in (await users.ListAsync(CancellationToken.None)).Where(u => u.Role == UserRole.SuperAdmin && !u.IsDisabled && u.UserName != remaining && u.UserName != acting))
        {
            other.IsDisabled = true;
            await users.UpdateAsync(other, CancellationToken.None);
            restore.Add(other);
        }

        try
        {
            var actor = new AccountActor((await FindAsync(acting))!.Key, UserRole.SuperAdmin);
            var target = (await FindAsync(remaining))!.Key;

            // Two enabled: one may go.
            Assert.Equal(AccountOutcome.Done, await accounts.SetDisabledAsync(actor, target, disabled: true, CancellationToken.None));
            Assert.Equal(AccountOutcome.Done, await accounts.SetDisabledAsync(actor, target, disabled: false, CancellationToken.None));

            // The acting one stands aside; the remaining one is now the last.
            var acted = (await FindAsync(acting))!;
            acted.IsDisabled = true;
            await users.UpdateAsync(acted, CancellationToken.None);
            var otherActor = new AccountActor((await FindAsync(FiGetServerFixture.AdminUserName))!.Key, UserRole.SuperAdmin);
            var tester = (await FindAsync(FiGetServerFixture.AdminUserName))!;
            tester.IsDisabled = true;
            await users.UpdateAsync(tester, CancellationToken.None);
            try
            {
                Assert.Equal(AccountOutcome.LastSuperAdmin, await accounts.SetDisabledAsync(otherActor, target, disabled: true, CancellationToken.None));
                Assert.Equal(AccountOutcome.LastSuperAdmin, await accounts.SetRoleAsync(otherActor, target, UserRole.Admin, CancellationToken.None));
                Assert.Equal(AccountOutcome.LastSuperAdmin, await accounts.DeleteAsync(otherActor, target, CancellationToken.None));
            }
            finally
            {
                tester.IsDisabled = false;
                await users.UpdateAsync(tester, CancellationToken.None);
            }
        }
        finally
        {
            foreach (var other in restore)
            {
                other.IsDisabled = false;
                await users.UpdateAsync(other, CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task The_users_page_creates_an_account_that_must_choose_its_own_password()
    {
        using var admin = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(admin));
        var name = "new" + Guid.NewGuid().ToString("N")[..8];

        var page = await HttpAssert.SuccessBodyAsync(await admin.GetAsync("/admin/users"));
        var form = FormElement().Matches(page).Select(m => m.Value).Single(f => f.Contains("value=\"create-user\"", StringComparison.Ordinal));
        var fields = HiddenInput().Matches(form).ToDictionary(m => WebUtility.HtmlDecode(m.Groups["name"].Value), m => WebUtility.HtmlDecode(m.Groups["value"].Value));
        fields[BrowserSignIn.InputName(form, "user-name")] = name;
        fields[BrowserSignIn.InputName(form, "user-password")] = "first-password-0123";
        using (var content = new FormUrlEncodedContent(fields))
        {
            var created = await HttpAssert.SuccessBodyAsync(await admin.PostAsync("/admin/users", content));
            Assert.Contains($"<strong>{name}</strong>", created, StringComparison.Ordinal);
        }

        using var person = CreateBrowser();
        var signedIn = await BrowserSignIn.SignInAsync(person, name, "first-password-0123");
        Assert.EndsWith("/account/password", signedIn.Headers.Location!.ToString());
    }

    private const string Password = "account-test-password-01";

    private async Task<string> CreateUserAsync(UserRole role)
    {
        var name = role.ToString().ToLowerInvariant() + Guid.NewGuid().ToString("N")[..8];
        await using var scope = server.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserStore>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        Assert.True(await users.AddAsync(
            new User
            {
                UserName = name,
                UserNameLower = name,
                Role = role,
                PasswordHash = hasher.Hash(Password),
                SecurityStamp = AccountService.NewStamp(),
                CreatedUtc = DateTime.UtcNow,
            },
            CancellationToken.None));
        return name;
    }

    private async Task<User?> FindAsync(string name)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IUserStore>().FindByUserNameAsync(name, CancellationToken.None);
    }

    private static async Task<string> ChangePasswordAsync(HttpClient client, string current, string next)
    {
        var page = FormElement().Matches(await HttpAssert.SuccessBodyAsync(await client.GetAsync("/account/password")))
            .Select(m => m.Value)
            .Single(f => f.Contains("value=\"change-password\"", StringComparison.Ordinal));
        var fields = HiddenInput().Matches(page).ToDictionary(m => WebUtility.HtmlDecode(m.Groups["name"].Value), m => WebUtility.HtmlDecode(m.Groups["value"].Value));
        fields[BrowserSignIn.InputName(page, "current-password")] = current;
        fields[BrowserSignIn.InputName(page, "new-password")] = next;
        fields[BrowserSignIn.InputName(page, "confirm-password")] = next;
        using var content = new FormUrlEncodedContent(fields);
        var response = await client.PostAsync("/account/password", content);
        return response.StatusCode == HttpStatusCode.Redirect
            ? await HttpAssert.SuccessBodyAsync(await client.GetAsync(response.Headers.Location))
            : await response.Content.ReadAsStringAsync();
    }

    /// <summary>A row button on the users page, posted with the antiforgery token that page carries.</summary>
    private static async Task<HttpResponseMessage> PostUserActionAsync(HttpClient client, int key, string action, params (string Name, string Value)[] extra)
    {
        var page = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/admin/users"));
        var token = AntiforgeryToken().Match(page);
        Assert.True(token.Success, "The users page carries no antiforgery token.");
        var fields = new Dictionary<string, string> { ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups["value"].Value) };
        foreach (var (name, value) in extra)
        {
            fields[name] = value;
        }

        using var content = new FormUrlEncodedContent(fields);
        return await client.PostAsync($"/admin/users/{key}/{action}", content);
    }

    private HttpClient CreateBrowser() =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = server.BaseAddress };

    [GeneratedRegex("<input[^>]*type=\"hidden\"[^>]*name=\"(?<name>[^\"]+)\"[^>]*value=\"(?<value>[^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex HiddenInput();

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<value>[^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex AntiforgeryToken();

    [GeneratedRegex("<form[^>]*>.*?</form>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex FormElement();
}

/// <summary>Recovery from configuration: the account exists, is a super admin, and must choose a password.</summary>
public sealed class RecoveryTests(RecoveryServerFixture server) : IClassFixture<RecoveryServerFixture>
{
    [Fact]
    public async Task A_recovered_account_signs_in_and_must_choose_a_password()
    {
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var user = await scope.ServiceProvider.GetRequiredService<IUserStore>().FindByUserNameAsync(RecoveryServerFixture.RecoveredUser, CancellationToken.None);
            Assert.NotNull(user);
            Assert.Equal(UserRole.SuperAdmin, user.Role);
            Assert.True(user.MustChangePassword);
        }

        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = server.BaseAddress };
        var signedIn = await BrowserSignIn.SignInAsync(client, RecoveryServerFixture.RecoveredUser, RecoveryServerFixture.RecoveredPassword);
        Assert.EndsWith("/account/password", signedIn.Headers.Location!.ToString());
    }
}
