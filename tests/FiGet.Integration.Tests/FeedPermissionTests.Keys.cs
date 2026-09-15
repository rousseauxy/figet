using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using FiGet.Application.Accounts;
using FiGet.Application.Ports;
using FiGet.Application.Tokens;
using FiGet.Domain.Entities;
using FiGet.Infrastructure.Persistence;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

/// <summary>
/// Personal API keys (docs/auth-plan.md, phase 3): a key acts as its owner and never more, follows the owner's access as
/// it changes, dies with the owner, and nobody creates a key above their own rights.
/// </summary>
public abstract partial class FeedPermissionTests
{
    /// <summary>A key allowed to publish publishes only while its owner may; the owner's grant decides, request by request.</summary>
    [Fact]
    public async Task A_personal_key_does_what_its_owner_may_do_now()
    {
        var feed = await CreateFeedAsync(FeedKind.Curated);
        var user = await CreateUserAsync();
        await GrantUserAsync(feed, user, FeedAccessLevel.Read);
        var key = await CreatePersonalKeyAsync(user, TokenScopes.Push | TokenScopes.Delete, feed: null);

        HttpAssert.Status(HttpStatusCode.OK, await ReadIndexAsync(feed, key));
        HttpAssert.Status(HttpStatusCode.Forbidden, await PushAsync(feed, key));

        await GrantUserAsync(feed, user, FeedAccessLevel.Publish);
        HttpAssert.Status(HttpStatusCode.Created, await PushAsync(feed, key));

        await GrantUserAsync(feed, user, FeedAccessLevel.None);
        HttpAssert.Status(HttpStatusCode.Forbidden, await ReadIndexAsync(feed, key));
    }

    /// <summary>The key's own limits hold even for an admin, who may do everything: read-only stays read-only, one feed stays one feed.</summary>
    [Fact]
    public async Task A_personal_key_keeps_its_own_limits_under_an_admin()
    {
        var feed = await CreateFeedAsync(FeedKind.Curated);
        var other = await CreateFeedAsync(FeedKind.Curated);
        var user = await CreateUserAsync(UserRole.Admin);
        var key = await CreatePersonalKeyAsync(user, TokenScopes.Read, feed);

        HttpAssert.Status(HttpStatusCode.OK, await ReadIndexAsync(feed, key));
        HttpAssert.Status(HttpStatusCode.Forbidden, await PushAsync(feed, key));
        HttpAssert.Status(HttpStatusCode.Forbidden, await ReadIndexAsync(other, key));
    }

    /// <summary>A disabled owner's key reads as no key at all; deleting the owner deletes the key.</summary>
    [Fact]
    public async Task A_personal_key_stops_with_its_owner()
    {
        var feed = await CreateFeedAsync(FeedKind.Curated);
        var user = await CreateUserAsync();
        await GrantUserAsync(feed, user, FeedAccessLevel.Read);
        var key = await CreatePersonalKeyAsync(user, TokenScopes.Read, feed: null);
        HttpAssert.Status(HttpStatusCode.OK, await ReadIndexAsync(feed, key));

        await using var scope = server.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserStore>();
        var account = (await FindUserAsync(user))!;
        account.IsDisabled = true;
        Assert.True(await users.UpdateAsync(account, CancellationToken.None));
        HttpAssert.Status(HttpStatusCode.Unauthorized, await ReadIndexAsync(feed, key));

        Assert.True(await users.DeleteAsync(account.Key, CancellationToken.None));
        var db = scope.ServiceProvider.GetRequiredService<FiGetDbContext>();
        Assert.False(await db.AccessTokens.AnyAsync(t => t.UserKey == account.Key, TestContext.Current.CancellationToken));
    }

    /// <summary>The ceiling rule, asked of the service directly, because every page that creates a key goes through it.</summary>
    [Fact]
    public async Task Nobody_creates_a_key_above_their_own_rights()
    {
        var feed = await CreateFeedAsync(FeedKind.Curated);
        var user = await CreateUserAsync();
        await GrantUserAsync(feed, user, FeedAccessLevel.Read);
        var owner = new AccountActor((await FindUserAsync(user))!.Key, UserRole.User);

        await using var scope = server.Services.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<AccessTokenService>();
        var feedEntity = (await scope.ServiceProvider.GetRequiredService<IFeedStore>().FindAsync(feed, CancellationToken.None))!;
        var none = CancellationToken.None;

        Assert.Equal(TokenCreateStatus.AboveYourRights, (await tokens.CreatePersonalKeyAsync(owner, "k", TokenScopes.Push, feedEntity, null, none)).Status);
        Assert.Equal(TokenCreateStatus.AboveYourRights, (await tokens.CreatePersonalKeyAsync(owner, "k", TokenScopes.Admin, null, null, none)).Status);
        Assert.Equal(TokenCreateStatus.Created, (await tokens.CreatePersonalKeyAsync(owner, "k", TokenScopes.Read, feedEntity, null, none)).Status);

        Assert.Equal(TokenCreateStatus.AboveYourRights, (await tokens.CreateServiceTokenAsync(owner, "s", TokenScopes.Read, null, null, none)).Status);
        var admin = new AccountActor(owner.Key, UserRole.Admin);
        Assert.Equal(TokenCreateStatus.AboveYourRights, (await tokens.CreateServiceTokenAsync(admin, "s", TokenScopes.Admin, null, null, none)).Status);
        Assert.Equal(TokenCreateStatus.Created, (await tokens.CreateServiceTokenAsync(admin, "s", TokenScopes.Push, null, null, none)).Status);
        Assert.Equal(TokenCreateStatus.Created, (await tokens.CreateServiceTokenAsync(FiGetServerFixture.SuperAdminActor, "s", TokenScopes.Admin, null, null, none)).Status);
    }

    /// <summary>The profile page makes a key that works, and revokes it; nobody revokes a key that is not theirs.</summary>
    [Fact]
    public async Task The_profile_page_creates_and_revokes_a_personal_key()
    {
        var feed = await CreateFeedAsync(FeedKind.Curated);
        var user = await CreateUserAsync();
        await GrantUserAsync(feed, user, FeedAccessLevel.Read);
        using var browser = await SignedInAsync(user);

        var page = await HttpAssert.SuccessBodyAsync(await browser.GetAsync("/account/profile"));
        Assert.Contains($"<option value=\"{feed}\">", page, StringComparison.Ordinal);
        var form = FormWith(page, "create-key");
        var fields = HiddenFields(form);
        fields[BrowserSignIn.InputName(form, "key-name")] = "laptop";
        string created;
        using (var content = new FormUrlEncodedContent(fields))
        {
            created = await HttpAssert.SuccessBodyAsync(await browser.PostAsync("/account/profile", content));
        }

        var secret = NewSecret().Match(created);
        Assert.True(secret.Success, "The new key is not on the page.");
        HttpAssert.Status(HttpStatusCode.OK, await ReadIndexAsync(feed, secret.Value));

        var stranger = await CreateUserAsync();
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FiGetDbContext>();
            var keyKey = await db.AccessTokens.Where(t => t.Hash == AccessTokenService.HashSecret(secret.Value)).Select(t => t.Key).SingleAsync(TestContext.Current.CancellationToken);
            var tokens = scope.ServiceProvider.GetRequiredService<AccessTokenService>();
            Assert.False(await tokens.RevokeOwnAsync(new AccountActor((await FindUserAsync(stranger))!.Key, UserRole.User), keyKey, CancellationToken.None));
        }

        HttpAssert.Status(HttpStatusCode.OK, await ReadIndexAsync(feed, secret.Value));
        var revokeForm = FormWith(await HttpAssert.SuccessBodyAsync(await browser.GetAsync("/account/profile")), "revoke-key-");
        using (var content = new FormUrlEncodedContent(HiddenFields(revokeForm)))
        {
            Assert.Contains("Key revoked.", await HttpAssert.SuccessBodyAsync(await browser.PostAsync("/account/profile", content)), StringComparison.Ordinal);
        }

        HttpAssert.Status(HttpStatusCode.Unauthorized, await ReadIndexAsync(feed, secret.Value));
    }

    private async Task<string> CreateUserAsync(UserRole role)
    {
        var name = await CreateUserAsync();
        await using var scope = server.Services.CreateAsyncScope();
        var account = (await FindUserAsync(name))!;
        account.Role = role;
        Assert.True(await scope.ServiceProvider.GetRequiredService<IUserStore>().UpdateAsync(account, CancellationToken.None));
        return name;
    }

    private async Task<string> CreatePersonalKeyAsync(string user, TokenScopes scopes, string? feed)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var account = (await FindUserAsync(user))!;
        var feedEntity = feed is null ? null : await scope.ServiceProvider.GetRequiredService<IFeedStore>().FindAsync(feed, CancellationToken.None);
        var result = await scope.ServiceProvider.GetRequiredService<AccessTokenService>()
            .CreatePersonalKeyAsync(new AccountActor(account.Key, account.Role), "key", scopes, feedEntity, null, CancellationToken.None);
        Assert.Equal(TokenCreateStatus.Created, result.Status);
        return result.Created!.Secret;
    }

    /// <summary>Reads the way PowerShell and NuGet clients do: the key as the password of Basic authentication.</summary>
    private async Task<HttpResponseMessage> ReadIndexAsync(string feed, string key)
    {
        using var client = new HttpClient { BaseAddress = server.BaseAddress };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("any:" + key)));
        return await client.GetAsync($"nuget/{feed}/v3/query");
    }

    private async Task<HttpResponseMessage> PushAsync(string feed, string key)
    {
        using var client = new HttpClient { BaseAddress = server.BaseAddress };
        client.DefaultRequestHeaders.Add("X-NuGet-ApiKey", key);
        using var package = TestPackages.Create(FiGetServerFixture.UniqueId("Personal.Key"), "1.0.0");
        using var content = new MultipartFormDataContent();
        var file = new StreamContent(package);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "package", "package.nupkg");
        return await client.PutAsync($"nuget/{feed}/v3/publish", content);
    }

    private static string FormWith(string page, string handlerPrefix) =>
        FormElement().Matches(page).Select(m => m.Value).First(f => f.Contains($"value=\"{handlerPrefix}", StringComparison.Ordinal));

    private static Dictionary<string, string> HiddenFields(string form) =>
        HiddenInput().Matches(form).ToDictionary(m => WebUtility.HtmlDecode(m.Groups["name"].Value), m => WebUtility.HtmlDecode(m.Groups["value"].Value));

    [GeneratedRegex("<form[^>]*>.*?</form>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex FormElement();

    [GeneratedRegex("<input[^>]*type=\"hidden\"[^>]*name=\"(?<name>[^\"]+)\"[^>]*value=\"(?<value>[^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex HiddenInput();

    [GeneratedRegex("figet_[A-Za-z0-9_-]{40,}", RegexOptions.CultureInvariant)]
    private static partial Regex NewSecret();
}
