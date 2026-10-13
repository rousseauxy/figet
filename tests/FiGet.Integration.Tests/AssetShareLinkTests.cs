using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FiGet.Application.Assets;
using FiGet.Application.Ports;
using FiGet.Application.Tokens;
using FiGet.Domain.Assets;
using FiGet.Domain.Entities;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Protocol.Assets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

public sealed class SqliteAssetShareLinkTests(SqliteAssetServerFixture fixture) : AssetShareLinkTests(fixture), IClassFixture<SqliteAssetServerFixture>;

public sealed class SqlServerAssetShareLinkTests(SqlServerAssetServerFixture fixture) : AssetShareLinkTests(fixture), IClassFixture<SqlServerAssetServerFixture>;

/// <summary>
/// Download links that expire. <c>vault</c> asks for a token for everything, so a file that arrives without one arrived
/// through its link and nothing else.
/// </summary>
public abstract partial class AssetShareLinkTests
{
    private readonly FiGetServerFixture server;

    protected AssetShareLinkTests(FiGetServerFixture server)
    {
        this.server = server;
        server.SkipIfUnavailable();
    }

    [Fact]
    public async Task A_link_downloads_a_file_from_a_directory_that_asks_for_a_token()
    {
        var path = $"{Unique()}/handover.zip";
        var bytes = Encoding.UTF8.GetBytes("what the other machine needs");
        await StoreAsync(path, bytes);

        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        var link = await LinkAsync(admin, path);
        Assert.Equal(path, link["path"]!.GetValue<string>());
        Assert.Equal(24, link["hours"]!.GetValue<int>());
        var expires = link["expiresUtc"]!.GetValue<DateTime>();
        Assert.InRange(expires, DateTime.UtcNow.AddHours(23), DateTime.UtcNow.AddHours(25));

        using var anonymous = server.CreateClient();
        HttpAssert.Status(HttpStatusCode.Unauthorized, await anonymous.GetAsync($"endpoints/vault/content/{path}"));

        using var response = await anonymous.GetAsync(Relative(link));
        HttpAssert.Status(HttpStatusCode.OK, response);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("handover.zip", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());

        // A link names no directory and no path: holding one says nothing about where the file lives.
        Assert.DoesNotContain("vault", Relative(link), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("handover", Relative(link), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_link_asks_for_the_lifetime_it_was_given()
    {
        var path = $"{Unique()}/short.txt";
        await StoreAsync(path, "short-lived"u8.ToArray());
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);

        var link = await LinkAsync(admin, path, "?hours=2");
        Assert.Equal(2, link["hours"]!.GetValue<int>());
        Assert.InRange(link["expiresUtc"]!.GetValue<DateTime>(), DateTime.UtcNow.AddMinutes(110), DateTime.UtcNow.AddMinutes(130));
    }

    [Theory]
    [InlineData("?hours=0")]
    [InlineData("?hours=169")]
    [InlineData("?hours=-3")]
    [InlineData("?hours=soon")]
    [InlineData("?hours=1.5")]
    public async Task A_lifetime_outside_the_limit_makes_no_link(string query)
    {
        var path = $"{Unique()}/limits.txt";
        await StoreAsync(path, "limits"u8.ToArray());
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);

        using var response = await admin.PostAsync($"endpoints/vault/link/{path}{query}", content: null);
        HttpAssert.Status(HttpStatusCode.BadRequest, response);
        Assert.Contains("1 to 168", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Making_a_link_takes_the_right_to_publish()
    {
        var path = $"{Unique()}/guarded.txt";
        await StoreAsync(path, "guarded"u8.ToArray());

        using var anonymous = server.CreateClient();
        HttpAssert.Status(HttpStatusCode.Unauthorized, await anonymous.PostAsync($"endpoints/vault/link/{path}", content: null));

        // A key that may read the file may not hand it to someone else.
        using var reader = server.CreateClient(await TokenAsync(TokenScopes.Read));
        HttpAssert.Status(HttpStatusCode.OK, await reader.GetAsync($"endpoints/vault/content/{path}"));
        HttpAssert.Status(HttpStatusCode.Forbidden, await reader.PostAsync($"endpoints/vault/link/{path}", content: null));

        using var publisher = server.CreateClient(await TokenAsync(TokenScopes.Push));
        HttpAssert.Status(HttpStatusCode.OK, await publisher.PostAsync($"endpoints/vault/link/{path}", content: null));
    }

    [Fact]
    public async Task A_link_is_for_a_file_not_a_folder_or_nothing()
    {
        var folder = Unique();
        await StoreAsync($"{folder}/inside.txt", "inside"u8.ToArray());
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);

        HttpAssert.Status(HttpStatusCode.NotFound, await admin.PostAsync($"endpoints/vault/link/{folder}", content: null));
        HttpAssert.Status(HttpStatusCode.NotFound, await admin.PostAsync($"endpoints/vault/link/{folder}/missing.txt", content: null));
        HttpAssert.Status(HttpStatusCode.NotFound, await admin.PostAsync("endpoints/no-such-directory/link/a.txt", content: null));
    }

    [Fact]
    public async Task A_link_ends_when_its_file_is_replaced()
    {
        var path = $"{Unique()}/release-notes.txt";
        await StoreAsync(path, "first edition"u8.ToArray());
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        var link = await LinkAsync(admin, path);

        using var anonymous = server.CreateClient();
        HttpAssert.Status(HttpStatusCode.OK, await anonymous.GetAsync(Relative(link)));

        // The link was given for the first edition; whoever holds it was never given the second.
        await StoreAsync(path, "second edition"u8.ToArray());
        using var after = await anonymous.GetAsync(Relative(link));
        HttpAssert.Status(HttpStatusCode.NotFound, after);
        Assert.Equal(AssetEndpoints.LinkNotValid, await after.Content.ReadAsStringAsync());

        // Putting the same bytes back makes it the same file again.
        await StoreAsync(path, "first edition"u8.ToArray());
        HttpAssert.Status(HttpStatusCode.OK, await anonymous.GetAsync(Relative(link)));
    }

    [Fact]
    public async Task A_link_ends_when_its_file_is_deleted()
    {
        var path = $"{Unique()}/gone.txt";
        await StoreAsync(path, "soon gone"u8.ToArray());
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        var link = await LinkAsync(admin, path);

        HttpAssert.Status(HttpStatusCode.OK, await admin.DeleteAsync($"endpoints/vault/content/{path}"));

        using var anonymous = server.CreateClient();
        HttpAssert.Status(HttpStatusCode.NotFound, await anonymous.GetAsync(Relative(link)));
    }

    [Fact]
    public async Task An_expired_link_opens_nothing()
    {
        var path = $"{Unique()}/yesterday.txt";
        await StoreAsync(path, "yesterday"u8.ToArray());

        await using var scope = server.Services.CreateAsyncScope();
        var directory = (await scope.ServiceProvider.GetRequiredService<IFeedStore>().FindAsync("vault", CancellationToken.None))!;
        Assert.True(AssetPath.TryParse(path, out var parsed));
        var file = (await scope.ServiceProvider.GetRequiredService<AssetService>().FindAsync(directory, parsed, CancellationToken.None))!;
        var links = server.Services.GetRequiredService<AssetShareLinks>();

        using var anonymous = server.CreateClient();

        // The same way of making a link, ending in the future: proof that what is refused below is refused for its date.
        var (current, _) = links.CreateUntil(directory, file, DateTimeOffset.UtcNow.AddMinutes(5));
        HttpAssert.Status(HttpStatusCode.OK, await anonymous.GetAsync($"share/{current}"));

        var (expired, _) = links.CreateUntil(directory, file, DateTimeOffset.UtcNow.AddMinutes(-1));
        using var response = await anonymous.GetAsync($"share/{expired}");
        HttpAssert.Status(HttpStatusCode.NotFound, response);
        Assert.Equal(AssetEndpoints.LinkNotValid, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_link_that_was_altered_or_invented_opens_nothing()
    {
        var path = $"{Unique()}/sealed.txt";
        await StoreAsync(path, "sealed"u8.ToArray());
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        var token = Relative(await LinkAsync(admin, path))["share/".Length..];

        using var anonymous = server.CreateClient();
        var middle = token.Length / 2;
        var altered = token[..middle] + (token[middle] == 'A' ? 'B' : 'A') + token[(middle + 1)..];
        foreach (var candidate in new[] { altered, token[..^4], "not-a-link", new string('A', 200) })
        {
            using var response = await anonymous.GetAsync($"share/{candidate}");
            HttpAssert.Status(HttpStatusCode.NotFound, response);
            Assert.Equal(AssetEndpoints.LinkNotValid, await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Making_and_using_a_link_is_in_the_audit_log()
    {
        var path = $"{Unique()}/audited.txt";
        await StoreAsync(path, "audited"u8.ToArray());
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        var link = await LinkAsync(admin, path, "?hours=3");

        using var anonymous = server.CreateClient();
        HttpAssert.Status(HttpStatusCode.OK, await anonymous.GetAsync(Relative(link)));

        var made = await AuditWait.ForAsync(server, "asset.link.create", path);
        Assert.Contains("directory=vault", made.Detail, StringComparison.Ordinal);
        Assert.Contains("hours=3", made.Detail, StringComparison.Ordinal);

        var used = await AuditWait.ForAsync(server, "asset.link.use", path);
        Assert.Contains("directory=vault", used.Detail, StringComparison.Ordinal);

        // The log names the link by a short hash. The link itself is a credential and is written nowhere.
        var token = Relative(link)["share/".Length..];
        Assert.DoesNotContain(token, made.Detail + used.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_browse_page_makes_a_link_and_shows_it_once_made()
    {
        var folder = Unique();
        var bytes = Encoding.UTF8.GetBytes("made from the page");
        await StoreAsync($"{folder}/from-the-page.txt", bytes);

        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = server.BaseAddress };
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(browser));
        var page = await HttpAssert.SuccessBodyAsync(await browser.GetAsync($"assets/vault?path={folder}"));

        var form = FormElement().Matches(page).Single(f => f.Value.Contains("/link\"", StringComparison.Ordinal)).Value;
        var fields = HiddenInput().Matches(form).ToDictionary(m => WebUtility.HtmlDecode(m.Groups["name"].Value), m => WebUtility.HtmlDecode(m.Groups["value"].Value));
        using var posted = await browser.PostAsync("admin/assets/vault/link", new FormUrlEncodedContent(fields));
        HttpAssert.Status(HttpStatusCode.Redirect, posted);
        var back = posted.Headers.Location!.ToString();
        Assert.Contains($"path={folder}", back, StringComparison.Ordinal);

        var after = await HttpAssert.SuccessBodyAsync(await browser.GetAsync(back));
        Assert.Contains($"A download link for <code>{folder}/from-the-page.txt</code>", after, StringComparison.Ordinal);
        var url = new Uri(WebUtility.HtmlDecode(ShownLink().Match(after).Groups["url"].Value));

        using var anonymous = server.CreateClient();
        using var download = await anonymous.GetAsync(url.AbsolutePath.TrimStart('/'));
        HttpAssert.Status(HttpStatusCode.OK, download);
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());

        // A file that went away between the page and the button: told so, and no link.
        fields["path"] = $"{folder}/not-there.txt";
        using var refused = await browser.PostAsync("admin/assets/vault/link", new FormUrlEncodedContent(fields));
        Assert.Contains("link=refused", refused.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Contains("No download link was made", await HttpAssert.SuccessBodyAsync(await browser.GetAsync(refused.Headers.Location!.ToString())), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_visitor_who_may_only_read_is_offered_no_link_and_shown_none()
    {
        var folder = Unique();
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        using var body = new ByteArrayContent("readable by anyone"u8.ToArray());
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync($"endpoints/files/content/{folder}/open.txt", body));
        using var made = await admin.PostAsync($"endpoints/files/link/{folder}/open.txt", content: null);
        var token = new Uri(JsonNode.Parse(await HttpAssert.SuccessBodyAsync(made))!["url"]!.GetValue<string>()).AbsolutePath["/share/".Length..];

        using var visitor = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = server.BaseAddress };
        var page = await HttpAssert.SuccessBodyAsync(await visitor.GetAsync($"assets/files?path={folder}"));
        Assert.Contains("open.txt", page, StringComparison.Ordinal);
        Assert.DoesNotContain("/link\"", page, StringComparison.Ordinal);

        // A link put into the address by hand tells a reader nothing either, real as it is.
        var withLink = await HttpAssert.SuccessBodyAsync(await visitor.GetAsync($"assets/files?path={folder}&link={token}"));
        Assert.DoesNotContain("A download link for", withLink, StringComparison.Ordinal);
        Assert.DoesNotContain("No download link was made", withLink, StringComparison.Ordinal);
    }

    [GeneratedRegex("""<form\b[^>]*>.*?</form>""", RegexOptions.Singleline)]
    private static partial Regex FormElement();

    [GeneratedRegex("""<input type="hidden" name="(?<name>[^"]*)" value="(?<value>[^"]*)"\s*/?>""")]
    private static partial Regex HiddenInput();

    [GeneratedRegex("""<code>(?<url>https?://[^<]*/share/[^<]+)</code>""")]
    private static partial Regex ShownLink();

    private async Task StoreAsync(string path, byte[] bytes)
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        using var body = new ByteArrayContent(bytes);
        using var response = await admin.PostAsync($"endpoints/vault/content/{path}", body);
        Assert.True(response.IsSuccessStatusCode, $"Storing {path}: {(int)response.StatusCode}");
    }

    private static async Task<JsonNode> LinkAsync(HttpClient client, string path, string query = "")
    {
        using var response = await client.PostAsync($"endpoints/vault/link/{path}{query}", content: null);
        return JsonNode.Parse(await HttpAssert.SuccessBodyAsync(response))!;
    }

    /// <summary>The link's address relative to the server, checked to be one of this server's own.</summary>
    private string Relative(JsonNode link)
    {
        var url = new Uri(link["url"]!.GetValue<string>());
        Assert.Equal(server.BaseAddress.Authority, url.Authority);
        return url.AbsolutePath.TrimStart('/');
    }

    private async Task<string> TokenAsync(TokenScopes scopes)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<AccessTokenService>()
            .CreateServiceTokenAsync(FiGetServerFixture.SuperAdminActor, "links-" + Guid.NewGuid().ToString("N")[..8], scopes, feedKey: null, expiresUtc: null, CancellationToken.None);
        return created.Created!.Secret;
    }

    private static string Unique() => "links-" + Guid.NewGuid().ToString("N")[..10];
}

public sealed class LinksOffFixture() : AssetServerFixture(TestDatabase.Sqlite)
{
    protected override void Configure(IWebHostBuilder builder)
    {
        base.Configure(builder);
        builder.UseSetting("FiGet:Assets:ShareLinks:MaxHours", "0");
    }
}

/// <summary>
/// The switch: with <c>MaxHours</c> at 0 no link is made and none works. One database is enough - nothing here is stored.
/// </summary>
public sealed class AssetShareLinksOffTests(LinksOffFixture server) : IClassFixture<LinksOffFixture>
{
    [Fact]
    public async Task With_links_switched_off_none_is_made_and_none_works()
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        using var body = new ByteArrayContent("off"u8.ToArray());
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync("endpoints/vault/content/off/file.txt", body));

        using var refused = await admin.PostAsync("endpoints/vault/link/off/file.txt", content: null);
        HttpAssert.Status(HttpStatusCode.NotFound, refused);
        Assert.Contains("switched off", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // A link from before the switch: made by the same key ring, refused all the same.
        await using var scope = server.Services.CreateAsyncScope();
        var directory = (await scope.ServiceProvider.GetRequiredService<IFeedStore>().FindAsync("vault", CancellationToken.None))!;
        Assert.True(AssetPath.TryParse("off/file.txt", out var parsed));
        var file = (await scope.ServiceProvider.GetRequiredService<AssetService>().FindAsync(directory, parsed, CancellationToken.None))!;
        var (token, _) = server.Services.GetRequiredService<AssetShareLinks>().CreateUntil(directory, file, DateTimeOffset.UtcNow.AddHours(1));

        using var anonymous = server.CreateClient();
        HttpAssert.Status(HttpStatusCode.NotFound, await anonymous.GetAsync($"share/{token}"));
    }
}
