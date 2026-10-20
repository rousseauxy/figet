using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

public sealed class SqliteUpstreamManagementTests(SqliteServerFixture fixture) : UpstreamManagementTests(fixture), IClassFixture<SqliteServerFixture>;

public sealed class SqlServerUpstreamManagementTests(SqlServerServerFixture fixture) : UpstreamManagementTests(fixture), IClassFixture<SqlServerServerFixture>;

/// <summary>
/// A feed's upstreams added, changed, ordered and removed by a script holding an admin token:
/// <c>/api/management/feeds/{name}/upstreams</c>. Nothing here fetches from an upstream; the addresses are never asked.
/// </summary>
public abstract class UpstreamManagementTests
{
    private readonly FiGetServerFixture server;

    protected UpstreamManagementTests(FiGetServerFixture server)
    {
        this.server = server;
        server.SkipIfUnavailable();
    }

    [Fact]
    public async Task A_gallery_added_to_a_curated_feed_makes_it_a_proxy_and_removing_it_undoes_that()
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        var feed = await NewFeedAsync(admin);

        using var added = await admin.PostAsJsonAsync(Upstreams(feed), new { name = "gallery", gallery = "PowerShell Gallery" });
        HttpAssert.Status(HttpStatusCode.Created, added);
        var upstream = JsonNode.Parse(await added.Content.ReadAsStringAsync())!;
        Assert.Equal("gallery", upstream["name"]!.GetValue<string>());
        Assert.Equal("https://www.powershellgallery.com/api/v2", upstream["url"]!.GetValue<string>());
        Assert.Equal("v2", upstream["protocol"]!.GetValue<string>());
        Assert.True(upstream["enabled"]!.GetValue<bool>());
        Assert.True((await FeedAsync(admin, feed))["proxy"]!.GetValue<bool>());
        Assert.Equal("gallery", (await WaitForAuditAsync("upstream.add", "gallery", feed)).Subject);

        HttpAssert.Status(HttpStatusCode.NoContent, await admin.DeleteAsync($"{Upstreams(feed)}/{upstream["id"]!.GetValue<int>()}"));
        Assert.Empty(JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await admin.GetAsync(Upstreams(feed))))!.AsArray());
        Assert.False((await FeedAsync(admin, feed))["proxy"]!.GetValue<bool>());
        HttpAssert.Status(HttpStatusCode.NotFound, await admin.DeleteAsync($"{Upstreams(feed)}/{upstream["id"]!.GetValue<int>()}"));
    }

    [Fact]
    public async Task An_address_of_its_own_with_a_credential_is_an_administrators_to_add()
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        var feed = await NewFeedAsync(admin);

        using var added = await admin.PostAsJsonAsync(Upstreams(feed), new
        {
            name = "internal",
            url = "https://packages.internal.example.test/v3/index.json",
            allow = "Contoso.*",
            credentialRef = "FIGET_UPSTREAM_INTERNAL",
        });
        HttpAssert.Status(HttpStatusCode.Created, added);
        var upstream = JsonNode.Parse(await added.Content.ReadAsStringAsync())!;
        Assert.Equal("v3", upstream["protocol"]!.GetValue<string>());
        Assert.Equal("Contoso.*", upstream["allow"]!.GetValue<string>());
        Assert.Equal("FIGET_UPSTREAM_INTERNAL", upstream["credentialRef"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{\"name\":\"x\",\"url\":\"https://ok.example.test/\",\"credentialRef\":\"FIGET_DATABASE__CONNECTIONSTRING\"}", HttpStatusCode.BadRequest)]
    [InlineData("{\"name\":\"x\",\"url\":\"http://169.254.169.254/latest/\"}", HttpStatusCode.BadRequest)]
    [InlineData("{\"name\":\"x\",\"url\":\"http://[fe80::1]/\"}", HttpStatusCode.BadRequest)]
    [InlineData("{\"name\":\"x\",\"url\":\"ftp://files.example.test/\"}", HttpStatusCode.BadRequest)]
    [InlineData("{\"name\":\"x\",\"url\":\"packages.example.test\"}", HttpStatusCode.BadRequest)]
    [InlineData("{\"name\":\"x\",\"gallery\":\"Maven Central\"}", HttpStatusCode.BadRequest)]
    [InlineData("{\"name\":\"\",\"gallery\":\"nuget.org\"}", HttpStatusCode.BadRequest)]
    [InlineData("{\"name\":\"x\",\"url\":\"https://ok.example.test/\",\"protocol\":\"v4\"}", HttpStatusCode.BadRequest)]
    public async Task An_upstream_the_server_must_not_have_is_refused_and_nothing_is_added(string body, HttpStatusCode expected)
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        var feed = await NewFeedAsync(admin);

        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        HttpAssert.Status(expected, await admin.PostAsync(Upstreams(feed), content));
        Assert.Empty(JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await admin.GetAsync(Upstreams(feed))))!.AsArray());
    }

    [Fact]
    public async Task Two_upstreams_cannot_share_a_name_and_a_directory_has_none()
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        var feed = await NewFeedAsync(admin);
        HttpAssert.Status(HttpStatusCode.Created, await admin.PostAsJsonAsync(Upstreams(feed), new { name = "one", gallery = "nuget.org" }));
        HttpAssert.Status(HttpStatusCode.Conflict, await admin.PostAsJsonAsync(Upstreams(feed), new { name = "ONE", gallery = "nuget.org" }));

        var directory = await NewFeedAsync(admin, "directory");
        HttpAssert.Status(HttpStatusCode.Conflict, await admin.PostAsJsonAsync(Upstreams(directory), new { name = "one", gallery = "nuget.org" }));
    }

    [Fact]
    public async Task A_change_keeps_what_it_does_not_mention_and_an_empty_credential_takes_it_away()
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        var feed = await NewFeedAsync(admin);
        var id = await AddAsync(admin, feed, new { name = "internal", url = "https://a.example.test/v3/index.json", deny = "Bad.*", credentialRef = "FIGET_UPSTREAM_A" });

        using var renamed = await admin.PatchAsJsonAsync($"{Upstreams(feed)}/{id}", new { name = "renamed", enabled = false });
        var changed = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(renamed))!;
        Assert.Equal("renamed", changed["name"]!.GetValue<string>());
        Assert.False(changed["enabled"]!.GetValue<bool>());
        Assert.Equal("https://a.example.test/v3/index.json", changed["url"]!.GetValue<string>());
        Assert.Equal("Bad.*", changed["deny"]!.GetValue<string>());
        Assert.Equal("FIGET_UPSTREAM_A", changed["credentialRef"]!.GetValue<string>());

        using var uncredentialled = await admin.PatchAsJsonAsync($"{Upstreams(feed)}/{id}", new { credentialRef = "" });
        Assert.Null(JsonNode.Parse(await HttpAssert.SuccessBodyAsync(uncredentialled))!["credentialRef"]);

        HttpAssert.Status(HttpStatusCode.BadRequest, await admin.PatchAsJsonAsync($"{Upstreams(feed)}/{id}", new { url = "http://169.254.169.254/" }));
        HttpAssert.Status(HttpStatusCode.NotFound, await admin.PatchAsJsonAsync($"{Upstreams(feed)}/{id + 100000}", new { enabled = true }));
    }

    [Fact]
    public async Task The_order_moves_one_place_at_a_time_and_the_ends_stay_put()
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        var feed = await NewFeedAsync(admin);
        var first = await AddAsync(admin, feed, new { name = "first", gallery = "nuget.org" });
        var second = await AddAsync(admin, feed, new { name = "second", url = "https://b.example.test/v3/index.json" });

        using var moved = await admin.PostAsJsonAsync($"{Upstreams(feed)}/{second}/move", new { direction = "up" });
        Assert.Equal(["second", "first"], Names(await HttpAssert.SuccessBodyAsync(moved)));

        using var already = await admin.PostAsJsonAsync($"{Upstreams(feed)}/{second}/move", new { direction = "up" });
        Assert.Equal(["second", "first"], Names(await HttpAssert.SuccessBodyAsync(already)));

        HttpAssert.Status(HttpStatusCode.BadRequest, await admin.PostAsJsonAsync($"{Upstreams(feed)}/{first}/move", new { direction = "sideways" }));
    }

    [Fact]
    public async Task Only_a_service_token_with_the_admin_scope_manages_upstreams()
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        var feed = await NewFeedAsync(admin);
        using var made = await admin.PostAsJsonAsync("api/management/tokens", new { name = Unique(), scopes = new[] { "read", "push", "delete" } });
        var everyFeed = JsonNode.Parse(await made.Content.ReadAsStringAsync())!["token"]!.GetValue<string>();

        using var holder = server.CreateClient(everyFeed);
        HttpAssert.Status(HttpStatusCode.Forbidden, await holder.GetAsync(Upstreams(feed)));
        HttpAssert.Status(HttpStatusCode.Forbidden, await holder.PostAsJsonAsync(Upstreams(feed), new { name = "x", gallery = "nuget.org" }));
        using var stranger = server.CreateClient();
        HttpAssert.Status(HttpStatusCode.Unauthorized, await stranger.GetAsync(Upstreams(feed)));
        HttpAssert.Status(HttpStatusCode.NotFound, await admin.GetAsync(Upstreams(Unique())));
    }

    private static string Upstreams(string feed) => $"api/management/feeds/{feed}/upstreams";

    private static async Task<string> NewFeedAsync(HttpClient admin, string kind = "package")
    {
        var name = Unique();
        HttpAssert.Status(HttpStatusCode.Created, await admin.PostAsJsonAsync("api/management/feeds", new { name, kind }));
        return name;
    }

    private static async Task<JsonNode> FeedAsync(HttpClient admin, string feed) =>
        JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await admin.GetAsync($"api/management/feeds/{feed}")))!;

    private static async Task<int> AddAsync(HttpClient admin, string feed, object body)
    {
        using var response = await admin.PostAsJsonAsync(Upstreams(feed), body);
        HttpAssert.Status(HttpStatusCode.Created, response);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["id"]!.GetValue<int>();
    }

    private static string[] Names(string json) => [.. JsonNode.Parse(json)!.AsArray().Select(u => u!["name"]!.GetValue<string>())];

    /// <summary>Waits for the background writer: a request never waits on the audit table, so a test has to.</summary>
    private async Task<AuditEntry> WaitForAuditAsync(string action, string subject, string feed)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using (var scope = server.Services.CreateAsyncScope())
            {
                var found = (await scope.ServiceProvider.GetRequiredService<IAuditStore>().QueryAsync(new AuditQuery(Action: action, Take: 200), CancellationToken.None))
                    .FirstOrDefault(e => e.Subject == subject && e.Detail.Contains($"feed={feed}", StringComparison.Ordinal));
                if (found is not null)
                {
                    return found;
                }
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new Xunit.Sdk.XunitException($"No {action} entry for {subject} on {feed} arrived within five seconds.");
    }

    private static string Unique() => "up-" + Guid.NewGuid().ToString("N")[..10];
}
