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

    /// <summary>
    /// A name no account has answers exactly as a real one does, attempt by attempt, lockout included, so six tries do not
    /// tell an outsider which names exist. Found by the 2026-09-14 review (S1.3): the lockout message came only for real names.
    /// </summary>
    [Fact]
    public async Task A_name_nobody_has_locks_out_after_the_same_attempts_as_a_real_one()
    {
        var real = await CreateUserAsync(UserRole.User);
        var nobody = "n" + Guid.NewGuid().ToString("N")[..10];

        async Task<string[]> AnswersAsync(string name)
        {
            using var client = CreateBrowser();
            var answers = new List<string>();
            for (var attempt = 0; attempt <= AccountService.MaxFailedSignIns; attempt++)
            {
                var response = await BrowserSignIn.SignInAsync(client, name, "wrong-password");
                HttpAssert.Status(HttpStatusCode.OK, response);
                var body = await response.Content.ReadAsStringAsync();
                answers.Add(body.Contains("Too many failed attempts", StringComparison.Ordinal) ? "locked"
                    : body.Contains("The user name or password is not right.", StringComparison.Ordinal) ? "wrong"
                    : "other");
            }

            return [.. answers];
        }

        var forReal = await AnswersAsync(real);
        Assert.Contains("locked", forReal);
        Assert.Equal(forReal, await AnswersAsync(nobody));
    }

    /// <summary>
    /// Found by the 2026-09-14 review: the count was read, raised in memory and written back, so attempts that overlapped all
    /// wrote the same "one more". Thirty at once left it at one, and the account open.
    /// </summary>
    [Fact]
    public async Task Overlapping_wrong_passwords_still_lock_the_account()
    {
        var name = await CreateUserAsync(UserRole.User);
        const int attempts = 30;
        var clients = Enumerable.Range(0, attempts).Select(_ => CreateBrowser()).ToList();
        try
        {
            await Task.WhenAll(clients.Select(c => BrowserSignIn.SignInAsync(c, name, "wrong-password-xx")));
        }
        finally
        {
            clients.ForEach(c => c.Dispose());
        }

        var user = await FindAsync(name);
        Assert.True(user!.LockedUntilUtc > DateTime.UtcNow, $"Not locked after {attempts} overlapping attempts: FailedSignIns={user.FailedSignIns}.");
    }

    /// <summary>
    /// The password changes on the profile page itself, in a dialog opened beside the profile form: a refusal comes back with
    /// the dialog marked to open and the reason inside; a change signs the account in again and leaves by redirect. The key
    /// accordion below stays closed until asked, also with no key yet.
    /// </summary>
    [Fact]
    public async Task The_password_changes_on_the_profile_page()
    {
        var name = await CreateUserAsync(UserRole.User);
        using var client = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(client, name, Password));

        var profile = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/account/profile"));
        Assert.Contains("<a class=\"fg-btn\" href=\"/account/password\" data-dialog-open=\"password\">Change password</a>", profile, StringComparison.Ordinal);
        Assert.Contains("<dialog class=\"fg-modal\" id=\"password\" aria-labelledby=\"password-title\">", profile, StringComparison.Ordinal);
        Assert.Contains("form=\"profile-form\"", profile, StringComparison.Ordinal);
        Assert.Contains("<details class=\"fg-accordion\" id=\"create-key\">", profile, StringComparison.Ordinal);

        var refused = await ChangePasswordAsync(client, "not-the-password", "a-proper-new-password", "/account/profile");
        Assert.Contains("The current password is not right.", refused, StringComparison.Ordinal);
        Assert.Contains("<dialog class=\"fg-modal\" id=\"password\" aria-labelledby=\"password-title\" data-open-on-load=\"true\">", refused, StringComparison.Ordinal);

        var changed = await ChangePasswordAsync(client, Password, "a-proper-new-password", "/account/profile");
        Assert.Contains("Password changed.", changed, StringComparison.Ordinal);
        Assert.DoesNotContain("fg-error", changed, StringComparison.Ordinal);

        using var other = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.OK, await BrowserSignIn.SignInAsync(other, name, Password));
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(other, name, "a-proper-new-password"));
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
        Assert.Equal($"/admin/users/{key}?done=disable", disabled.Headers.Location!.ToString());

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

        var promoteKey = (await FindAsync(plain))!.Key;
        var promote = await PostUserActionAsync(client, promoteKey, "role", ("role", nameof(UserRole.Admin)));
        Assert.Equal($"/admin/users/{promoteKey}?done=forbidden", promote.Headers.Location!.ToString());

        var disableAdminKey = (await FindAsync(otherAdmin))!.Key;
        var disableAdmin = await PostUserActionAsync(client, disableAdminKey, "disable");
        Assert.Equal($"/admin/users/{disableAdminKey}?done=forbidden", disableAdmin.Headers.Location!.ToString());

        var selfKey = (await FindAsync(adminName))!.Key;
        var self = await PostUserActionAsync(client, selfKey, "role", ("role", nameof(UserRole.SuperAdmin)));
        Assert.Equal($"/admin/users/{selfKey}?done=forbidden", self.Headers.Location!.ToString());

        var resetKey = (await FindAsync(plain))!.Key;
        var reset = await PostUserActionAsync(client, resetKey, "password", ("password", "reset-by-an-admin-01"));
        Assert.Equal($"/admin/users/{resetKey}?done=password", reset.Headers.Location!.ToString());
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

    /// <summary>
    /// The list links to each account and carries no controls of its own; the changes are on the account's page, which
    /// offers them only when they are the viewer's to make.
    /// </summary>
    [Fact]
    public async Task Each_account_is_changed_on_its_own_page()
    {
        var plain = await CreateUserAsync(UserRole.User);
        var superAdmin = await CreateUserAsync(UserRole.SuperAdmin);
        var adminName = await CreateUserAsync(UserRole.Admin);
        using var client = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(client, adminName, Password));

        var list = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/admin/users"));
        var plainKey = (await FindAsync(plain))!.Key;
        var superKey = (await FindAsync(superAdmin))!.Key;
        Assert.Contains($"href=\"/admin/users/{plainKey}\"", list, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"password\"", list, StringComparison.Ordinal);
        Assert.DoesNotContain("action=\"/admin/users/", list, StringComparison.Ordinal);

        var editable = await HttpAssert.SuccessBodyAsync(await client.GetAsync($"/admin/users/{plainKey}"));
        Assert.Contains($"action=\"/admin/users/{plainKey}/password\"", editable, StringComparison.Ordinal);
        Assert.Contains($"action=\"/admin/users/{plainKey}/delete\"", editable, StringComparison.Ordinal);

        var readOnly = await HttpAssert.SuccessBodyAsync(await client.GetAsync($"/admin/users/{superKey}"));
        Assert.DoesNotContain("action=\"/admin/users/", readOnly, StringComparison.Ordinal);
        Assert.Contains("managed by a super admin", readOnly, StringComparison.Ordinal);

        HttpAssert.Status(HttpStatusCode.NotFound, await client.GetAsync("/admin/users/999999"));
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

    /// <summary>Posts the change-password form of a page: the full page for a forced change, or the profile's dialog.</summary>
    private static async Task<string> ChangePasswordAsync(HttpClient client, string current, string next, string path = "/account/password")
    {
        var page = FormElement().Matches(await HttpAssert.SuccessBodyAsync(await client.GetAsync(path)))
            .Select(m => m.Value)
            .Single(f => f.Contains("value=\"change-password\"", StringComparison.Ordinal));
        var fields = HiddenInput().Matches(page).ToDictionary(m => WebUtility.HtmlDecode(m.Groups["name"].Value), m => WebUtility.HtmlDecode(m.Groups["value"].Value));
        fields[BrowserSignIn.InputName(page, "current-password")] = current;
        fields[BrowserSignIn.InputName(page, "new-password")] = next;
        fields[BrowserSignIn.InputName(page, "confirm-password")] = next;
        using var content = new FormUrlEncodedContent(fields);
        var response = await client.PostAsync(path, content);
        return response.StatusCode == HttpStatusCode.Redirect
            ? await HttpAssert.SuccessBodyAsync(await client.GetAsync(response.Headers.Location))
            : await response.Content.ReadAsStringAsync();
    }

    /// <summary>A row button on the users page, posted with the antiforgery token that page carries.</summary>
    private static async Task<HttpResponseMessage> PostUserActionAsync(HttpClient client, int key, string action, params (string Name, string Value)[] extra)
    {
        var page = await HttpAssert.SuccessBodyAsync(await client.GetAsync($"/admin/users/{key}"));
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
