using System.Net;
using System.Text.RegularExpressions;
using FiGet.Application.Accounts;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Infrastructure.Persistence;
using FiGet.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

public sealed class SqliteExternalSignInTests(SqliteServerFixture fixture) : ExternalSignInTests(fixture), IClassFixture<SqliteServerFixture>;

public sealed class SqlServerExternalSignInTests(SqlServerServerFixture fixture) : ExternalSignInTests(fixture), IClassFixture<SqlServerServerFixture>;

/// <summary>
/// Signing in through OpenID Connect providers (docs/auth-plan.md, phase 4), end to end through a real code flow against
/// <see cref="FakeOidcProvider"/>: the sign-in page, FiGet's handler, the provider's token and user info answers, and the
/// account decisions after them. Every test adds its own provider under a fresh slug.
/// </summary>
public abstract partial class ExternalSignInTests : IAsyncLifetime
{
    private const string Password = "external-test-pass-01";
    private readonly FiGetServerFixture server;
    private FakeOidcProvider idp = null!;

    protected ExternalSignInTests(FiGetServerFixture server)
    {
        this.server = server;
        server.SkipIfUnavailable();
    }

    public async ValueTask InitializeAsync() => idp = await FakeOidcProvider.StartAsync();

    public async ValueTask DisposeAsync()
    {
        await idp.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>A first sign-in makes an account with the user role; the next one with the same identity reaches it again.</summary>
    [Fact]
    public async Task A_first_sign_in_creates_an_account_and_the_next_one_returns_to_it()
    {
        var slug = await AddProviderAsync();
        var sub = Guid.NewGuid().ToString("N");
        var name = "ext" + sub[..8];
        idp.Next = new FakeIdentity(sub, name, $"{name}@example.org", "External Person");

        using (var browser = CreateBrowser())
        {
            var done = await ProviderSignInAsync(browser, slug);
            Assert.Equal("/", done.Headers.Location!.OriginalString);
            Assert.Contains($"<h1>{name}</h1>", await HttpAssert.SuccessBodyAsync(await browser.GetAsync("/account/profile")), StringComparison.Ordinal);
        }

        var user = await FindUserAsync(name);
        Assert.NotNull(user);
        Assert.Equal(UserRole.User, user.Role);
        Assert.Null(user.PasswordHash);
        Assert.Equal("External Person", user.DisplayName);

        using (var again = CreateBrowser())
        {
            await ProviderSignInAsync(again, slug);
            Assert.Contains($"<h1>{name}</h1>", await HttpAssert.SuccessBodyAsync(await again.GetAsync("/account/profile")), StringComparison.Ordinal);
        }

        Assert.Equal(1, await CountUsersAsync(name));
    }

    /// <summary>
    /// An unknown identity bringing an existing account's user name, or its email in any case, is refused: no second account
    /// for the same person, and no automatic joining to the existing one either.
    /// </summary>
    [Fact]
    public async Task An_identity_matching_an_existing_account_is_refused_not_joined_or_duplicated()
    {
        var slug = await AddProviderAsync();
        var local = await CreateLocalUserAsync(email: "Same.Person@Example.org");

        foreach (var identity in new[]
        {
            new FakeIdentity(Guid.NewGuid().ToString("N"), local.ToUpperInvariant()),
            new FakeIdentity(Guid.NewGuid().ToString("N"), "other" + local, "same.person@example.org"),
        })
        {
            idp.Next = identity;
            using var browser = CreateBrowser();
            var refused = await ProviderSignInAsync(browser, slug);
            Assert.Equal("/account/login?external=exists", refused.Headers.Location!.OriginalString);
            HttpAssert.Status(HttpStatusCode.Redirect, await browser.GetAsync("/account/profile"));
        }

        Assert.Equal(1, await CountUsersAsync(local));
        Assert.Null(await FindUserAsync("other" + local));
        Assert.Empty(await LoginsOfAsync(local));
    }

    /// <summary>
    /// Found by the 2026-09-14 review: the build plan asked for an allow list, and without one anyone with an account at a
    /// provider people can register at got an account here. A new identity needs a verified address in an allowed domain,
    /// and a provider that does not make accounts refuses every new identity - while one already connected to an account
    /// still signs in.
    /// </summary>
    [Fact]
    public async Task A_provider_makes_accounts_only_for_allowed_domains_and_only_when_it_may()
    {
        var slug = await AddProviderAsync();
        await ChangeProviderAsync(slug, p => p.AllowedEmailDomains = "example.org");

        foreach (var (email, verified) in new (string?, bool?)[] { ("x@other.test", true), ("x@sub.example.org", true), ("x@example.org", false), (null, null) })
        {
            var name = "dom" + Guid.NewGuid().ToString("N")[..8];
            idp.Next = new FakeIdentity(Guid.NewGuid().ToString("N"), name, email?.Replace("x@", name + "@", StringComparison.Ordinal), EmailVerified: verified);
            using var browser = CreateBrowser();
            var refused = await ProviderSignInAsync(browser, slug);
            Assert.Equal("/account/login?external=email-not-allowed", refused.Headers.Location!.OriginalString);
            Assert.Null(await FindUserAsync(name));
        }

        var allowed = "dom" + Guid.NewGuid().ToString("N")[..8];
        idp.Next = new FakeIdentity(Guid.NewGuid().ToString("N"), allowed, $"{allowed}@EXAMPLE.org", EmailVerified: true);
        using (var browser = CreateBrowser())
        {
            Assert.Equal("/", (await ProviderSignInAsync(browser, slug)).Headers.Location!.OriginalString);
        }

        Assert.NotNull(await FindUserAsync(allowed));

        // Accounts off: a connected identity still signs in, a new one is sent to an administrator.
        var local = await CreateLocalUserAsync();
        var connected = Guid.NewGuid().ToString("N");
        idp.Next = new FakeIdentity(connected, "someone", "someone@example.org", EmailVerified: true);
        using (var browser = await SignedInLocallyAsync(local))
        {
            Assert.EndsWith("/account/profile?linked=ok", (await ProviderLinkAsync(browser, slug)).Headers.Location!.OriginalString, StringComparison.Ordinal);
        }

        await ChangeProviderAsync(slug, p => p.CreateAccounts = false);
        var stranger = "dom" + Guid.NewGuid().ToString("N")[..8];
        idp.Next = new FakeIdentity(Guid.NewGuid().ToString("N"), stranger, $"{stranger}@example.org", EmailVerified: true);
        using (var browser = CreateBrowser())
        {
            Assert.Equal("/account/login?external=no-account", (await ProviderSignInAsync(browser, slug)).Headers.Location!.OriginalString);
            Assert.Contains("There is no account for you yet.", await HttpAssert.SuccessBodyAsync(await browser.GetAsync("/account/login?external=no-account")), StringComparison.Ordinal);
        }

        Assert.Null(await FindUserAsync(stranger));
        idp.Next = new FakeIdentity(connected, "someone", "someone@example.org", EmailVerified: true);
        using (var browser = CreateBrowser())
        {
            await ProviderSignInAsync(browser, slug);
            Assert.Contains($"<h1>{local}</h1>", await HttpAssert.SuccessBodyAsync(await browser.GetAsync("/account/profile")), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Connecting from the profile page joins the identity to the signed-in account, and signing in with it afterwards
    /// reaches that account. An identity that already belongs to someone else is refused.
    /// </summary>
    [Fact]
    public async Task Connecting_from_the_profile_joins_the_identity_to_that_account()
    {
        var slug = await AddProviderAsync();
        var local = await CreateLocalUserAsync();
        var sub = Guid.NewGuid().ToString("N");
        idp.Next = new FakeIdentity(sub, "someone-else", "person@example.org");

        using (var browser = await SignedInLocallyAsync(local))
        {
            var done = await ProviderLinkAsync(browser, slug);
            Assert.EndsWith("/account/profile?linked=ok", done.Headers.Location!.OriginalString, StringComparison.Ordinal);
            Assert.Contains("Disconnect", await HttpAssert.SuccessBodyAsync(await browser.GetAsync("/account/profile")), StringComparison.Ordinal);
        }

        using (var fresh = CreateBrowser())
        {
            await ProviderSignInAsync(fresh, slug);
            Assert.Contains($"<h1>{local}</h1>", await HttpAssert.SuccessBodyAsync(await fresh.GetAsync("/account/profile")), StringComparison.Ordinal);
        }

        var other = await CreateLocalUserAsync();
        using (var browser = await SignedInLocallyAsync(other))
        {
            var refused = await ProviderLinkAsync(browser, slug);
            Assert.EndsWith("/account/profile?linked=taken", refused.Headers.Location!.OriginalString, StringComparison.Ordinal);
        }

        Assert.Single(await LoginsOfAsync(local));
        Assert.Empty(await LoginsOfAsync(other));
    }

    /// <summary>An account made by a provider cannot disconnect it: that would leave no way to sign in. One with a password can.</summary>
    [Fact]
    public async Task Disconnecting_the_last_way_to_sign_in_is_refused()
    {
        var slug = await AddProviderAsync();
        var sub = Guid.NewGuid().ToString("N");
        var name = "only" + sub[..8];
        idp.Next = new FakeIdentity(sub, name);
        using (var browser = CreateBrowser())
        {
            await ProviderSignInAsync(browser, slug);
            var refused = await PostProfileFormAsync(browser, "/account/external/logins/");
            Assert.EndsWith("/account/profile?unlinked=last", refused.Headers.Location!.OriginalString, StringComparison.Ordinal);
            Assert.Single(await LoginsOfAsync(name));
        }

        var local = await CreateLocalUserAsync();
        idp.Next = new FakeIdentity(Guid.NewGuid().ToString("N"), "whoever");
        using (var browser = await SignedInLocallyAsync(local))
        {
            await ProviderLinkAsync(browser, slug);
            var removed = await PostProfileFormAsync(browser, "/account/external/logins/");
            Assert.EndsWith("/account/profile?unlinked=ok", removed.Headers.Location!.OriginalString, StringComparison.Ordinal);
            Assert.Empty(await LoginsOfAsync(local));
        }
    }

    /// <summary>A disabled account does not sign in through a provider; a disabled provider has no button and starts nothing.</summary>
    [Fact]
    public async Task Disabled_accounts_and_providers_do_not_sign_in()
    {
        var slug = await AddProviderAsync();
        var sub = Guid.NewGuid().ToString("N");
        var name = "dis" + sub[..8];
        idp.Next = new FakeIdentity(sub, name);
        using (var browser = CreateBrowser())
        {
            await ProviderSignInAsync(browser, slug);
        }

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<IUserStore>();
            var user = (await users.FindByUserNameAsync(name, CancellationToken.None))!;
            user.IsDisabled = true;
            await users.UpdateAsync(user, CancellationToken.None);
        }

        using (var browser = CreateBrowser())
        {
            var refused = await ProviderSignInAsync(browser, slug);
            Assert.Equal("/account/login?external=disabled", refused.Headers.Location!.OriginalString);
            HttpAssert.Status(HttpStatusCode.Redirect, await browser.GetAsync("/account/profile"));
        }

        await SetEnabledAsync(slug, false);
        using (var browser = CreateBrowser())
        {
            var page = await HttpAssert.SuccessBodyAsync(await browser.GetAsync("/account/login"));
            Assert.DoesNotContain($"action=\"/account/external/{slug}\"", page, StringComparison.Ordinal);
            var fields = Hidden(page);
            using var content = new FormUrlEncodedContent(fields);
            var response = await browser.PostAsync($"/account/external/{slug}", content);
            Assert.Equal("/account/login?external=unknown", response.Headers.Location!.OriginalString);
        }
    }

    /// <summary>
    /// A linked provider group puts the account in the FiGet group at sign-in and takes it out when the provider stops
    /// listing it. A membership an admin added is never touched.
    /// </summary>
    [Fact]
    public async Task Provider_groups_follow_the_groups_claim_and_leave_manual_members_alone()
    {
        var slug = await AddProviderAsync(groupsClaim: "groups");
        var mapped = await CreateGroupAsync();
        var manual = await CreateGroupAsync();
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var provider = (await scope.ServiceProvider.GetRequiredService<IOidcProviderStore>().FindBySlugAsync(slug, CancellationToken.None))!;
            var groups = scope.ServiceProvider.GetRequiredService<IGroupStore>();
            Assert.True(await groups.AddProviderLinkAsync(new GroupProviderLink { GroupKey = mapped, ProviderKey = provider.Key, ProviderGroup = "Developers" }, CancellationToken.None));
            Assert.True(await groups.AddProviderLinkAsync(new GroupProviderLink { GroupKey = manual, ProviderKey = provider.Key, ProviderGroup = "Operators" }, CancellationToken.None));
        }

        var sub = Guid.NewGuid().ToString("N");
        var name = "grp" + sub[..8];
        idp.Next = new FakeIdentity(sub, name, Groups: ["developers", "unrelated"]);
        using (var browser = CreateBrowser())
        {
            await ProviderSignInAsync(browser, slug);
        }

        var userKey = (await FindUserAsync(name))!.Key;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var groups = scope.ServiceProvider.GetRequiredService<IGroupStore>();
            Assert.Equal([mapped], (await groups.GroupsOfAsync(userKey, CancellationToken.None)).Select(g => g.Key));
            Assert.True(await groups.AddMemberAsync(manual, userKey, CancellationToken.None));
        }

        idp.Next = new FakeIdentity(sub, name, Groups: []);
        using (var browser = CreateBrowser())
        {
            await ProviderSignInAsync(browser, slug);
        }

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var groups = scope.ServiceProvider.GetRequiredService<IGroupStore>();
            Assert.Equal([manual], (await groups.GroupsOfAsync(userKey, CancellationToken.None)).Select(g => g.Key));
        }
    }

    /// <summary>A saved provider applies at the next sign-in, with no restart: a wrong secret fails, putting it right works.</summary>
    [Fact]
    public async Task A_changed_provider_applies_at_the_next_sign_in()
    {
        var slug = await AddProviderAsync();
        var sub = Guid.NewGuid().ToString("N");
        idp.Next = new FakeIdentity(sub, "chg" + sub[..8]);
        using (var browser = CreateBrowser())
        {
            Assert.Equal("/", (await ProviderSignInAsync(browser, slug)).Headers.Location!.OriginalString);
        }

        await SetSecretAsync(slug, "not-the-secret");
        using (var browser = CreateBrowser())
        {
            Assert.Equal("/account/login?external=failed", (await ProviderSignInAsync(browser, slug)).Headers.Location!.OriginalString);
        }

        await SetSecretAsync(slug, FakeOidcProvider.ClientSecret);
        using (var browser = CreateBrowser())
        {
            Assert.Equal("/", (await ProviderSignInAsync(browser, slug)).Headers.Location!.OriginalString);
        }
    }

    /// <summary>
    /// The sign-in page: buttons and a local link, or buttons only when a super admin says so; /account/login/local keeps the
    /// form either way. The providers page is for super admins, and stores the secret encrypted.
    /// </summary>
    [Fact]
    public async Task The_sign_in_page_follows_the_mode_and_the_providers_page_is_for_super_admins()
    {
        using var admin = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(admin));
        var slug = "p" + Guid.NewGuid().ToString("N")[..10];
        var page = await HttpAssert.SuccessBodyAsync(await admin.GetAsync("/admin/providers"));
        var form = FormWith(page, "create-provider");
        var fields = Hidden(form);
        fields[BrowserSignIn.InputName(form, "provider-display")] = "Test IdP";
        fields[BrowserSignIn.InputName(form, "provider-slug")] = slug;
        fields[BrowserSignIn.InputName(form, "provider-authority")] = idp.Authority;
        fields[BrowserSignIn.InputName(form, "provider-client")] = FakeOidcProvider.ClientId;
        fields[BrowserSignIn.InputName(form, "provider-secret")] = FakeOidcProvider.ClientSecret;
        fields[BrowserSignIn.InputName(form, "provider-username-claim")] = "preferred_username";
        fields[InputNameOfCheckbox(form, "Enabled")] = "true";

        // A plain-http issuer off this machine is refused when saved, as the sign-in handler would refuse its metadata later.
        var authorityField = BrowserSignIn.InputName(form, "provider-authority");
        fields[authorityField] = "http://auth.example.test/application/o/figet/";
        using (var content = new FormUrlEncodedContent(fields))
        {
            var refused = await admin.PostAsync("/admin/providers", content);
            HttpAssert.Status(HttpStatusCode.OK, refused);
            Assert.Contains("The issuer must be an absolute https:// URL.", await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        fields[authorityField] = idp.Authority;
        fields[WebUtility.HtmlDecode(Regex.Match(form, "<textarea[^>]*id=\"provider-domains\"[^>]*name=\"(?<n>[^\"]+)\"").Groups["n"].Value)] = "Example.org\n@example.net";
        using (var content = new FormUrlEncodedContent(fields))
        {
            var created = await admin.PostAsync("/admin/providers", content);
            var body = created.StatusCode == HttpStatusCode.Redirect ? "" : await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(created.StatusCode == HttpStatusCode.Redirect, $"{(int)created.StatusCode} {Regex.Match(body, "(fg-error|validation-message|<h1)[^<]*").Value}");
        }

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<FiGetDbContext>().OidcProviders.SingleAsync(p => p.Slug == slug, TestContext.Current.CancellationToken);
            Assert.True(stored.Enabled);

            // A provider added on the page starts not making accounts, and its domains are stored cleaned up.
            Assert.False(stored.CreateAccounts);
            Assert.Equal("example.org\nexample.net", stored.AllowedEmailDomains);
            Assert.DoesNotContain(FakeOidcProvider.ClientSecret, stored.ProtectedClientSecret, StringComparison.Ordinal);
            Assert.Equal(FakeOidcProvider.ClientSecret, scope.ServiceProvider.GetRequiredService<ISecretProtector>().Unprotect(stored.ProtectedClientSecret));
        }

        using var stranger = CreateBrowser();
        var login = await HttpAssert.SuccessBodyAsync(await stranger.GetAsync("/account/login"));
        Assert.Contains($"action=\"/account/external/{slug}\"", login, StringComparison.Ordinal);
        Assert.Contains("href=\"/account/login/local\"", login, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"password\"", login, StringComparison.Ordinal);

        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISettingStore>().SetAsync(SettingKeys.SignInMode, SignInModes.ProvidersOnly, "test", CancellationToken.None);
        }

        try
        {
            login = await HttpAssert.SuccessBodyAsync(await stranger.GetAsync("/account/login"));
            Assert.DoesNotContain("href=\"/account/login/local\"", login, StringComparison.Ordinal);
            Assert.Contains("id=\"password\"", await HttpAssert.SuccessBodyAsync(await stranger.GetAsync("/account/login/local")), StringComparison.Ordinal);
        }
        finally
        {
            await using var scope = server.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ISettingStore>().SetAsync(SettingKeys.SignInMode, SignInModes.ProvidersAndLocal, "test", CancellationToken.None);
        }

        var plainAdmin = await CreateLocalUserAsync(UserRole.Admin);
        using var adminBrowser = await SignedInLocallyAsync(plainAdmin);
        HttpAssert.Status(HttpStatusCode.Redirect, await adminBrowser.GetAsync("/admin/providers"));
        Assert.DoesNotContain("href=\"/admin/providers\"", await HttpAssert.SuccessBodyAsync(await adminBrowser.GetAsync("/admin/users")), StringComparison.Ordinal);
    }

    /// <summary>The user name comes from the claim, else the email, reduced to what account names allow.</summary>
    [Theory]
    [InlineData("jane.doe", "jane@example.org", "jane.doe")]
    [InlineData("Jane Doe+x", null, "Jane.Doe.x")]
    [InlineData(null, "j.doe@example.org", "j.doe")]
    [InlineData("--", null, "user")]
    public void User_names_are_taken_from_the_claim_or_the_email(string? claim, string? email, string expected) =>
        Assert.Equal(expected, ExternalAccountService.UserNameFrom(new ExternalIdentity("s", claim, email, null, null)));

    /// <summary>The whole browser round trip: FiGet's button, the provider's authorize, the callback, and the complete step.</summary>
    private static async Task<HttpResponseMessage> ProviderSignInAsync(HttpClient browser, string slug)
    {
        var page = await HttpAssert.SuccessBodyAsync(await browser.GetAsync("/account/login"));
        using var content = new FormUrlEncodedContent(Hidden(FormWithAction(page, $"/account/external/{slug}")));
        return await FollowProviderAsync(browser, await browser.PostAsync($"/account/external/{slug}", content));
    }

    private static async Task<HttpResponseMessage> ProviderLinkAsync(HttpClient browser, string slug)
    {
        var page = await HttpAssert.SuccessBodyAsync(await browser.GetAsync("/account/profile"));
        using var content = new FormUrlEncodedContent(Hidden(FormWithAction(page, $"/account/external/{slug}/link")));
        return await FollowProviderAsync(browser, await browser.PostAsync($"/account/external/{slug}/link", content));
    }

    /// <summary>Follows redirects until FiGet's complete step has answered, and returns that answer.</summary>
    private static async Task<HttpResponseMessage> FollowProviderAsync(HttpClient browser, HttpResponseMessage response)
    {
        for (var hop = 0; hop < 6; hop++)
        {
            HttpAssert.Status(HttpStatusCode.Redirect, response);
            var location = response.Headers.Location!;
            var target = location.IsAbsoluteUri ? location : new Uri(browser.BaseAddress!, location);
            if (target.AbsolutePath == "/account/external/complete")
            {
                return await browser.GetAsync(target);
            }

            if (hop > 0 && !target.AbsolutePath.StartsWith("/signin-oidc/", StringComparison.Ordinal) && !target.AbsolutePath.StartsWith("/authorize", StringComparison.Ordinal))
            {
                return response;
            }

            response = await browser.GetAsync(target);
        }

        throw new InvalidOperationException("The sign-in did not come back to FiGet.");
    }

    private static async Task<HttpResponseMessage> PostProfileFormAsync(HttpClient browser, string actionPrefix)
    {
        var page = await HttpAssert.SuccessBodyAsync(await browser.GetAsync("/account/profile"));
        var form = FormElement().Matches(page).Select(m => m.Value).First(f => f.Contains($"action=\"{actionPrefix}", StringComparison.Ordinal));
        var action = Regex.Match(form, "action=\"(?<a>[^\"]+)\"").Groups["a"].Value;
        using var content = new FormUrlEncodedContent(Hidden(form));
        return await browser.PostAsync(action, content);
    }

    private async Task<string> AddProviderAsync(string groupsClaim = "")
    {
        var slug = "p" + Guid.NewGuid().ToString("N")[..10];
        await using var scope = server.Services.CreateAsyncScope();
        var secrets = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IOidcProviderStore>().AddAsync(
            new OidcProvider
            {
                Slug = slug,
                DisplayName = "Provider " + slug,
                Authority = idp.Authority,
                ClientId = FakeOidcProvider.ClientId,
                ProtectedClientSecret = secrets.Protect(FakeOidcProvider.ClientSecret),
                GroupsClaim = groupsClaim,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            },
            CancellationToken.None));
        return slug;
    }

    private Task SetEnabledAsync(string slug, bool enabled) => ChangeProviderAsync(slug, p => p.Enabled = enabled);

    private Task SetSecretAsync(string slug, string secret) => ChangeProviderAsync(slug, p => p.ProtectedClientSecret = server.Services.GetRequiredService<ISecretProtector>().Protect(secret));

    private async Task ChangeProviderAsync(string slug, Action<OidcProvider> change)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOidcProviderStore>();
        var provider = (await store.FindBySlugAsync(slug, CancellationToken.None))!;
        change(provider);

        // Past the stored stamp for certain, as a save through the page moves it.
        provider.UpdatedUtc = provider.UpdatedUtc.AddSeconds(1);
        Assert.True(await store.UpdateAsync(provider, CancellationToken.None));
    }

    private async Task<string> CreateLocalUserAsync(UserRole role = UserRole.User, string email = "")
    {
        var name = "l" + Guid.NewGuid().ToString("N")[..10];
        await using var scope = server.Services.CreateAsyncScope();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IUserStore>().AddAsync(
            new User { UserName = name, UserNameLower = name, Email = email, Role = role, PasswordHash = hasher.Hash(Password), SecurityStamp = AccountService.NewStamp(), CreatedUtc = DateTime.UtcNow },
            CancellationToken.None));
        return name;
    }

    private async Task<int> CreateGroupAsync()
    {
        var name = "g" + Guid.NewGuid().ToString("N")[..10];
        await using var scope = server.Services.CreateAsyncScope();
        var groups = scope.ServiceProvider.GetRequiredService<IGroupStore>();
        Assert.True(await groups.AddAsync(new FiGet.Domain.Entities.Group { Name = name, NameLower = name, CreatedUtc = DateTime.UtcNow }, CancellationToken.None));
        return (await groups.ListAsync(CancellationToken.None)).Single(g => g.Name == name).Key;
    }

    private async Task<User?> FindUserAsync(string name)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IUserStore>().FindByUserNameAsync(name, CancellationToken.None);
    }

    private async Task<int> CountUsersAsync(string prefix)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<FiGetDbContext>().Users.CountAsync(u => u.UserNameLower.StartsWith(prefix), TestContext.Current.CancellationToken);
    }

    private async Task<IReadOnlyList<ExternalLogin>> LoginsOfAsync(string userName)
    {
        var user = await FindUserAsync(userName);
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IExternalLoginStore>().ListForUserAsync(user!.Key, CancellationToken.None);
    }

    private async Task<HttpClient> SignedInLocallyAsync(string user)
    {
        var browser = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(browser, user, Password));
        return browser;
    }

    private HttpClient CreateBrowser() =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = server.BaseAddress };

    private static string FormWith(string page, string handler) =>
        FormElement().Matches(page).Select(m => m.Value).First(f => f.Contains($"value=\"{handler}\"", StringComparison.Ordinal));

    private static string FormWithAction(string page, string action) =>
        FormElement().Matches(page).Select(m => m.Value).First(f => f.Contains($"action=\"{action}\"", StringComparison.Ordinal));

    private static string InputNameOfCheckbox(string form, string property) =>
        Regex.Matches(form, "<input[^>]*type=\"checkbox\"[^>]*name=\"(?<n>[^\"]+)\"|<input[^>]*name=\"(?<n>[^\"]+)\"[^>]*type=\"checkbox\"")
            .Select(m => WebUtility.HtmlDecode(m.Groups["n"].Value))
            .Single(n => n.EndsWith("." + property, StringComparison.Ordinal));

    private static Dictionary<string, string> Hidden(string html) =>
        HiddenInput().Matches(html)
            .GroupBy(m => WebUtility.HtmlDecode(m.Groups["name"].Value))
            .ToDictionary(g => g.Key, g => WebUtility.HtmlDecode(g.First().Groups["value"].Value));

    [GeneratedRegex("<form[^>]*>.*?</form>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex FormElement();

    [GeneratedRegex("<input[^>]*type=\"hidden\"[^>]*name=\"(?<name>[^\"]+)\"[^>]*value=\"(?<value>[^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex HiddenInput();
}
