using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using FiGet.Application.Accounts;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Infrastructure.Persistence;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

public sealed class SqliteFeedPermissionTests(SqliteServerFixture fixture) : FeedPermissionTests(fixture), IClassFixture<SqliteServerFixture>;

public sealed class SqlServerFeedPermissionTests(SqlServerServerFixture fixture) : FeedPermissionTests(fixture), IClassFixture<SqlServerServerFixture>;

/// <summary>
/// Per-feed permissions (docs/auth-plan.md): Read, Publish and Manage, granted to accounts and to groups, applied to
/// pages, the admin endpoints and signed-in protocol reads. Every test makes its own feed and accounts, so a grant in
/// one cannot leak into another.
/// </summary>
public abstract partial class FeedPermissionTests
{
    private const string Password = "permission-test-pass-01";
    private readonly FiGetServerFixture server;

    protected FeedPermissionTests(FiGetServerFixture server)
    {
        this.server = server;
        server.SkipIfUnavailable();
    }

    [Fact]
    public async Task Without_a_grant_a_private_feed_does_not_exist_for_a_user()
    {
        var feed = await CreateFeedAsync(FeedKind.Curated);
        var user = await CreateUserAsync();
        using var browser = await SignedInAsync(user);

        Assert.DoesNotContain($">{feed}<", await HttpAssert.SuccessBodyAsync(await browser.GetAsync("/")), StringComparison.Ordinal);
        HttpAssert.Status(HttpStatusCode.NotFound, await browser.GetAsync($"/feeds/{feed}"));
        HttpAssert.Status(HttpStatusCode.Unauthorized, await browser.GetAsync($"nuget/{feed}/v3/query"));
        HttpAssert.Status(HttpStatusCode.NotFound, await browser.GetAsync($"/admin/feeds/{feed}"));
    }

    /// <summary>Read shows the feed, and downloads through the browser's own sign-in, but changes nothing.</summary>
    [Fact]
    public async Task Read_shows_a_private_feed_and_nothing_more()
    {
        var feed = await CreateFeedAsync(FeedKind.Curated);
        var user = await CreateUserAsync();
        await GrantUserAsync(feed, user, FeedAccessLevel.Read);
        using var browser = await SignedInAsync(user);

        Assert.Contains($">{feed}<", await HttpAssert.SuccessBodyAsync(await browser.GetAsync("/")), StringComparison.Ordinal);
        var page = await HttpAssert.SuccessBodyAsync(await browser.GetAsync($"/feeds/{feed}"));
        Assert.DoesNotContain($"href=\"/admin/feeds/{feed}\"", page, StringComparison.Ordinal);
        HttpAssert.Status(HttpStatusCode.OK, await browser.GetAsync($"nuget/{feed}/v3/query"));

        HttpAssert.Status(HttpStatusCode.Forbidden, await PostAsync(browser, $"/admin/feeds/{feed}/pull", ("id", "Some.Package"), ("version", "1.0.0")));
        HttpAssert.Status(HttpStatusCode.NotFound, await browser.GetAsync($"/admin/feeds/{feed}"));
        HttpAssert.Status(HttpStatusCode.NotFound, await browser.GetAsync($"/admin/feeds/{feed}/unlisted"));
    }

    /// <summary>
    /// A group's grant reaches its members, and leaving the group takes it away on the next request. Publish opens the
    /// publishing pages and endpoints but not the feed's settings.
    /// </summary>
    [Fact]
    public async Task Publish_through_a_group_follows_membership()
    {
        var feed = await CreateFeedAsync(FeedKind.Curated);
        var user = await CreateUserAsync();
        var group = await CreateGroupAsync();
        await AddMemberAsync(group, user);
        await GrantGroupAsync(feed, group, FeedAccessLevel.Publish);
        using var browser = await SignedInAsync(user);

        await HttpAssert.SuccessBodyAsync(await browser.GetAsync($"/admin/feeds/{feed}/unlisted"));
        HttpAssert.Status(HttpStatusCode.Redirect, await PostAsync(browser, $"/admin/feeds/{feed}/versions/relist", ("id", "No.Such"), ("version", "1.0.0")));
        HttpAssert.Status(HttpStatusCode.NotFound, await browser.GetAsync($"/admin/feeds/{feed}"));
        HttpAssert.Status(HttpStatusCode.Forbidden, await PostAsync(browser, $"/admin/feeds/{feed}/upstreams/add", ("name", "x"), ("url", "https://example.invalid/v3/index.json")));

        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IGroupStore>().RemoveMemberAsync(group, (await FindUserAsync(user))!.Key, CancellationToken.None);
        }

        HttpAssert.Status(HttpStatusCode.NotFound, await browser.GetAsync($"/admin/feeds/{feed}/unlisted"));
        HttpAssert.Status(HttpStatusCode.NotFound, await browser.GetAsync($"/feeds/{feed}"));
    }

    /// <summary>A group's page lists every feed that grants it something, so the effect of changing the group is visible there.</summary>
    [Fact]
    public async Task A_group_page_lists_the_feeds_it_has_access_to()
    {
        var feed = await CreateFeedAsync(FeedKind.Curated);
        var directory = await CreateFeedAsync(FeedKind.Assets);
        var group = await CreateGroupAsync();
        await GrantGroupAsync(feed, group, FeedAccessLevel.Publish);
        await GrantGroupAsync(directory, group, FeedAccessLevel.Read);

        using var admin = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(admin));
        var page = await HttpAssert.SuccessBodyAsync(await admin.GetAsync($"/admin/groups/{group}"));

        Assert.Contains($"href=\"/admin/feeds/{feed}\"><strong>{feed}</strong>", page, StringComparison.Ordinal);
        Assert.Contains($"href=\"/admin/assets/{directory}\"><strong>{directory}</strong>", page, StringComparison.Ordinal);
        Assert.Contains(">Publish</span>", page, StringComparison.Ordinal);
    }

    /// <summary>The sign-in cookie reads, but is never what lets a push through: publishing over a protocol needs a key.</summary>
    [Fact]
    public async Task A_signed_in_browser_cannot_push_with_its_cookie()
    {
        var feed = await CreateFeedAsync(FeedKind.Curated);
        var user = await CreateUserAsync();
        await GrantUserAsync(feed, user, FeedAccessLevel.Manage);
        using var browser = await SignedInAsync(user);

        using var package = TestPackages.Create(FiGetServerFixture.UniqueId("Cookie.Push"), "1.0.0");
        using var content = new MultipartFormDataContent();
        using var file = new StreamContent(package);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "package", "package.nupkg");
        var response = await browser.PutAsync($"nuget/{feed}/", content);

        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"{(int)response.StatusCode}");
    }

    /// <summary>
    /// Manage opens the feed's settings and its access list - enough to grant someone else - but not deleting the feed,
    /// and not the rest of the admin area.
    /// </summary>
    [Fact]
    public async Task Manage_runs_the_feed_and_its_access_list_but_not_the_admin_area()
    {
        var feed = await CreateFeedAsync(FeedKind.Curated);
        var manager = await CreateUserAsync();
        var colleague = await CreateUserAsync();
        await GrantUserAsync(feed, manager, FeedAccessLevel.Manage);
        using var browser = await SignedInAsync(manager);

        var settings = await HttpAssert.SuccessBodyAsync(await browser.GetAsync($"/admin/feeds/{feed}/access"));
        Assert.Contains($"action=\"/admin/feeds/{feed}/access/set\"", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Name and deletion", settings, StringComparison.Ordinal);

        // Renaming and deleting are not what a grant of Manage covers: the page is not there for this account.
        HttpAssert.Status(HttpStatusCode.NotFound, await browser.GetAsync($"/admin/feeds/{feed}/name"));
        Assert.DoesNotContain("href=\"/admin/users\"", settings, StringComparison.Ordinal);

        var colleagueKey = (await FindUserAsync(colleague))!.Key;
        HttpAssert.Status(HttpStatusCode.Redirect, await PostAsync(browser, $"/admin/feeds/{feed}/access/set", ("who", $"user:{colleagueKey}"), ("level", nameof(FeedAccessLevel.Read))));
        Assert.Equal(FeedAccessLevel.Read, await GrantedAsync(feed, colleague));

        foreach (var path in new[] { "/admin/feeds", "/admin/users", "/admin/groups" })
        {
            var refused = await browser.GetAsync(path);
            HttpAssert.Status(HttpStatusCode.Redirect, refused);
            Assert.Contains("/account/login", refused.Headers.Location!.ToString(), StringComparison.Ordinal);
        }
    }

    /// <summary>An asset directory: Publish shows the upload and delete controls, Read shows the files only.</summary>
    [Fact]
    public async Task Asset_directory_controls_follow_the_level()
    {
        var directory = await CreateFeedAsync(FeedKind.Assets);
        var reader = await CreateUserAsync();
        var publisher = await CreateUserAsync();
        await GrantUserAsync(directory, reader, FeedAccessLevel.Read);
        await GrantUserAsync(directory, publisher, FeedAccessLevel.Publish);

        using (var browser = await SignedInAsync(reader))
        {
            var page = await HttpAssert.SuccessBodyAsync(await browser.GetAsync($"/assets/{directory}"));
            Assert.DoesNotContain("data-asset-upload", page, StringComparison.Ordinal);
        }

        using (var browser = await SignedInAsync(publisher))
        {
            var page = await HttpAssert.SuccessBodyAsync(await browser.GetAsync($"/assets/{directory}"));
            Assert.Contains("data-asset-upload", page, StringComparison.Ordinal);
            Assert.DoesNotContain($"href=\"/admin/assets/{directory}\"", page, StringComparison.Ordinal);
        }
    }

    /// <summary>Anonymous read still grants Read to everyone, and an admin manages every feed without a grant.</summary>
    [Fact]
    public async Task Anonymous_read_and_admins_need_no_grant()
    {
        var open = await CreateFeedAsync(FeedKind.Curated, anonymousRead: true);
        var closed = await CreateFeedAsync(FeedKind.Curated);
        var user = await CreateUserAsync();
        using (var browser = await SignedInAsync(user))
        {
            await HttpAssert.SuccessBodyAsync(await browser.GetAsync($"/feeds/{open}"));
            HttpAssert.Status(HttpStatusCode.NotFound, await browser.GetAsync($"/admin/feeds/{open}"));
        }

        using var admin = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(admin));
        await HttpAssert.SuccessBodyAsync(await admin.GetAsync($"/admin/feeds/{closed}"));
    }

    /// <summary>Deleting an account or a feed takes its grants with it, so none is left to attach to a later one.</summary>
    [Fact]
    public async Task Deleting_an_account_or_a_feed_removes_its_grants()
    {
        var feed = await CreateFeedAsync(FeedKind.Curated);
        var user = await CreateUserAsync();
        var group = await CreateGroupAsync();
        await AddMemberAsync(group, user);
        await GrantUserAsync(feed, user, FeedAccessLevel.Publish);
        await GrantGroupAsync(feed, group, FeedAccessLevel.Read);
        var userKey = (await FindUserAsync(user))!.Key;

        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FiGetDbContext>();
        var feeds = scope.ServiceProvider.GetRequiredService<IFeedStore>();
        var feedKey = (await feeds.FindAsync(feed, CancellationToken.None))!.Key;

        Assert.True(await scope.ServiceProvider.GetRequiredService<IUserStore>().DeleteAsync(userKey, CancellationToken.None));
        Assert.False(await db.FeedPermissions.AnyAsync(p => p.UserKey == userKey, TestContext.Current.CancellationToken));
        Assert.False(await db.GroupMembers.AnyAsync(m => m.UserKey == userKey, TestContext.Current.CancellationToken));

        Assert.True(await feeds.DeleteAsync(feedKey, CancellationToken.None));
        Assert.False(await db.FeedPermissions.AnyAsync(p => p.FeedKey == feedKey, TestContext.Current.CancellationToken));
    }

    private async Task<string> CreateFeedAsync(FeedKind kind, bool anonymousRead = false)
    {
        var name = (kind == FeedKind.Assets ? "dir" : "feed") + Guid.NewGuid().ToString("N")[..8];
        await using var scope = server.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IFeedStore>().CreateAsync(
            new Feed { Name = name, NameLower = name, Kind = kind, AnonymousRead = anonymousRead, CreatedUtc = DateTime.UtcNow },
            CancellationToken.None));
        return name;
    }

    private async Task<string> CreateUserAsync()
    {
        var name = "u" + Guid.NewGuid().ToString("N")[..10];
        await using var scope = server.Services.CreateAsyncScope();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IUserStore>().AddAsync(
            new User { UserName = name, UserNameLower = name, PasswordHash = hasher.Hash(Password), SecurityStamp = AccountService.NewStamp(), CreatedUtc = DateTime.UtcNow },
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

    private async Task AddMemberAsync(int group, string user)
    {
        await using var scope = server.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IGroupStore>().AddMemberAsync(group, (await FindUserAsync(user))!.Key, CancellationToken.None));
    }

    private async Task GrantUserAsync(string feed, string user, FeedAccessLevel level)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var feedKey = (await scope.ServiceProvider.GetRequiredService<IFeedStore>().FindAsync(feed, CancellationToken.None))!.Key;
        await scope.ServiceProvider.GetRequiredService<IFeedPermissionStore>().SetAsync(feedKey, (await FindUserAsync(user))!.Key, null, level, CancellationToken.None);
    }

    private async Task GrantGroupAsync(string feed, int group, FeedAccessLevel level)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var feedKey = (await scope.ServiceProvider.GetRequiredService<IFeedStore>().FindAsync(feed, CancellationToken.None))!.Key;
        await scope.ServiceProvider.GetRequiredService<IFeedPermissionStore>().SetAsync(feedKey, null, group, level, CancellationToken.None);
    }

    private async Task<FeedAccessLevel> GrantedAsync(string feed, string user)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var feedKey = (await scope.ServiceProvider.GetRequiredService<IFeedStore>().FindAsync(feed, CancellationToken.None))!.Key;
        return await scope.ServiceProvider.GetRequiredService<IFeedPermissionStore>().GrantedAsync(feedKey, (await FindUserAsync(user))!.Key, CancellationToken.None);
    }

    private async Task<User?> FindUserAsync(string name)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IUserStore>().FindByUserNameAsync(name, CancellationToken.None);
    }

    private async Task<HttpClient> SignedInAsync(string user)
    {
        var browser = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(browser, user, Password));
        return browser;
    }

    /// <summary>A form post with the antiforgery token of a page the account can open.</summary>
    private static async Task<HttpResponseMessage> PostAsync(HttpClient browser, string path, params (string Name, string Value)[] fields)
    {
        var page = await HttpAssert.SuccessBodyAsync(await browser.GetAsync("/account/profile"));
        var token = AntiforgeryToken().Match(page);
        Assert.True(token.Success, "No antiforgery token on the profile page.");
        var form = new Dictionary<string, string> { ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups["value"].Value) };
        foreach (var (name, value) in fields)
        {
            form[name] = value;
        }

        using var content = new FormUrlEncodedContent(form);
        return await browser.PostAsync(path, content);
    }

    private HttpClient CreateBrowser() =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = server.BaseAddress };

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<value>[^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex AntiforgeryToken();
}
