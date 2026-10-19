using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

public sealed class SqliteFeedManagementTests(SqliteServerFixture fixture) : FeedManagementTests(fixture), IClassFixture<SqliteServerFixture>;

public sealed class SqlServerFeedManagementTests(SqlServerServerFixture fixture) : FeedManagementTests(fixture), IClassFixture<SqlServerServerFixture>;

/// <summary>
/// Feeds and asset directories made, changed, renamed and deleted by a script holding an admin token:
/// <c>/api/management/feeds</c>. Whether a change took is read off the feed's own protocol answers where it can be, not
/// only off the API's description of it.
/// </summary>
public abstract class FeedManagementTests
{
    private const string Endpoint = "api/management/feeds";

    private readonly FiGetServerFixture server;

    protected FeedManagementTests(FiGetServerFixture server)
    {
        this.server = server;
        server.SkipIfUnavailable();
    }

    [Fact]
    public async Task A_package_feed_made_over_the_api_serves_and_is_recorded()
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        var name = Unique();

        using var response = await admin.PostAsJsonAsync(Endpoint, new { name, purpose = "PowerShell", allowOverwrite = true, onDelete = "delete" });
        HttpAssert.Status(HttpStatusCode.Created, response);
        var made = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(name, made["name"]!.GetValue<string>());
        Assert.Equal("package", made["kind"]!.GetValue<string>());
        Assert.False(made["proxy"]!.GetValue<bool>());
        Assert.Equal("powershell", made["purpose"]!.GetValue<string>());
        Assert.True(made["allowOverwrite"]!.GetValue<bool>());
        Assert.Equal("delete", made["onDelete"]!.GetValue<string>());
        Assert.False(made["anonymousRead"]!.GetValue<bool>());
        Assert.Null(made["anonymousList"]);
        Assert.EndsWith($"/nuget/{name}/v3/index.json", made["url"]!.GetValue<string>(), StringComparison.Ordinal);

        // A real feed: it answers a key, and a stranger is asked for one. Not the service index: that is discovery, served to
        // anyone on any feed.
        HttpAssert.Status(HttpStatusCode.OK, await admin.GetAsync($"api/packages/{name}"));
        using var stranger = server.CreateClient();
        HttpAssert.Status(HttpStatusCode.Unauthorized, await stranger.GetAsync($"api/packages/{name}"));

        // Listed, and recorded under the action the create form records.
        var listed = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await admin.GetAsync(Endpoint)))!.AsArray();
        Assert.Contains(listed, f => f!["name"]!.GetValue<string>() == name);
        Assert.Equal(name, (await WaitForAuditAsync("feed.create", name)).Subject);
    }

    [Fact]
    public async Task A_feed_made_with_its_gallery_is_a_proxy_from_the_start()
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        var made = await CreateAsync(admin, new { name = Unique(), purpose = "nuget", gallery = true });

        Assert.True(made["proxy"]!.GetValue<bool>());
        Assert.Single(made["upstreams"]!.AsArray());
    }

    [Fact]
    public async Task A_directory_made_over_the_api_takes_files_and_keeps_only_its_own_settings()
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        var name = Unique();
        var made = await CreateAsync(admin, new { name, kind = "directory", anonymousRead = true, anonymousList = true, allowOverwrite = true, purpose = "nuget" });

        Assert.Equal("directory", made["kind"]!.GetValue<string>());
        Assert.True(made["anonymousList"]!.GetValue<bool>());
        Assert.Equal("any", made["purpose"]!.GetValue<string>());
        Assert.Null(made["allowOverwrite"]);
        Assert.Null(made["onDelete"]);
        Assert.Null(made["folder"]);
        Assert.EndsWith($"/endpoints/{name}/content/", made["url"]!.GetValue<string>(), StringComparison.Ordinal);

        using var upload = new ByteArrayContent(Encoding.UTF8.GetBytes("hello"));
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync($"endpoints/{name}/content/hello.txt", upload));
        using var stranger = server.CreateClient();
        Assert.Equal("hello", await HttpAssert.SuccessBodyAsync(await stranger.GetAsync($"endpoints/{name}/content/hello.txt")));
    }

    [Fact]
    public async Task Settings_change_only_what_is_sent_and_take_effect()
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        var name = Unique();
        await CreateAsync(admin, new { name, allowOverwrite = true });
        using (var before = server.CreateClient())
        {
            HttpAssert.Status(HttpStatusCode.Unauthorized, await before.GetAsync($"api/packages/{name}"));
        }

        using var response = await admin.PatchAsJsonAsync($"{Endpoint}/{name}", new { anonymousRead = true, chartColor = 3 });
        var changed = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(response))!;
        Assert.True(changed["anonymousRead"]!.GetValue<bool>());
        Assert.Equal(3, changed["chartColor"]!.GetValue<int>());
        Assert.True(changed["allowOverwrite"]!.GetValue<bool>());

        using var stranger = server.CreateClient();
        HttpAssert.Status(HttpStatusCode.OK, await stranger.GetAsync($"api/packages/{name}"));
        Assert.Equal(name, (await WaitForAuditAsync("feed.settings", name)).Subject);
    }

    [Fact]
    public async Task A_refused_network_list_changes_nothing_else_in_the_same_request()
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        var name = Unique();
        await CreateAsync(admin, new { name });

        using var refused = await admin.PatchAsJsonAsync($"{Endpoint}/{name}", new { anonymousRead = true, allowedNetworks = new[] { "10.0.0.0/8", "not-an-address" } });
        HttpAssert.Status(HttpStatusCode.BadRequest, refused);
        Assert.Contains("not-an-address", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var unchanged = await GetAsync(admin, name);
        Assert.False(unchanged["anonymousRead"]!.GetValue<bool>());
        Assert.Null(unchanged["allowedNetworks"]);

        using var accepted = await admin.PatchAsJsonAsync($"{Endpoint}/{name}", new { allowedNetworks = new[] { "10.0.0.0/8", "192.168.1.5" } });
        var limited = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(accepted))!;
        Assert.Equal(["10.0.0.0/8", "192.168.1.5"], limited["allowedNetworks"]!.AsArray().Select(n => n!.GetValue<string>()));

        using var opened = await admin.PatchAsJsonAsync($"{Endpoint}/{name}", new { allowedNetworks = Array.Empty<string>() });
        Assert.Null(JsonNode.Parse(await HttpAssert.SuccessBodyAsync(opened))!["allowedNetworks"]);
    }

    [Fact]
    public async Task A_rename_that_keeps_the_old_name_keeps_old_clients_working()
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        var old = Unique();
        var renamed = Unique();
        await CreateAsync(admin, new { name = old });

        using var response = await admin.PostAsJsonAsync($"{Endpoint}/{old}/rename", new { name = renamed, keepOldName = true });
        var after = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(response))!;
        Assert.Equal(renamed, after["name"]!.GetValue<string>());
        Assert.Equal([old], after["aliases"]!.AsArray().Select(a => a!.GetValue<string>()));

        HttpAssert.Status(HttpStatusCode.OK, await admin.GetAsync($"api/packages/{old}"));
        Assert.Equal(renamed, (await GetAsync(admin, old))["name"]!.GetValue<string>());

        HttpAssert.Status(HttpStatusCode.Conflict, await admin.PostAsJsonAsync($"{Endpoint}/{renamed}/rename", new { name = "public" }));
        HttpAssert.Status(HttpStatusCode.BadRequest, await admin.PostAsJsonAsync($"{Endpoint}/{renamed}/rename", new { name = "no spaces" }));
    }

    [Fact]
    public async Task A_delete_needs_the_name_repeated_and_then_takes_everything()
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        var name = Unique();
        await CreateAsync(admin, new { name, kind = "directory" });
        using var upload = new ByteArrayContent([1, 2, 3]);
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync($"endpoints/{name}/content/a.bin", upload));

        HttpAssert.Status(HttpStatusCode.BadRequest, await admin.DeleteAsync($"{Endpoint}/{name}"));
        HttpAssert.Status(HttpStatusCode.BadRequest, await admin.DeleteAsync($"{Endpoint}/{name}?confirm=public"));
        HttpAssert.Status(HttpStatusCode.OK, await admin.GetAsync($"{Endpoint}/{name}"));

        HttpAssert.Status(HttpStatusCode.NoContent, await admin.DeleteAsync($"{Endpoint}/{name}?confirm={name.ToUpperInvariant()}"));
        HttpAssert.Status(HttpStatusCode.NotFound, await admin.GetAsync($"{Endpoint}/{name}"));
        HttpAssert.Status(HttpStatusCode.NotFound, await admin.GetAsync($"endpoints/{name}/content/a.bin"));
        Assert.Equal(name, (await WaitForAuditAsync("feed.delete", name)).Subject);
    }

    [Fact]
    public async Task Requests_that_cannot_be_right_are_refused_with_what_is_wrong()
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);

        HttpAssert.Status(HttpStatusCode.BadRequest, await admin.PostAsJsonAsync(Endpoint, new { name = "-starts-with-a-dash" }));
        HttpAssert.Status(HttpStatusCode.Conflict, await admin.PostAsJsonAsync(Endpoint, new { name = "PUBLIC" }));
        HttpAssert.Status(HttpStatusCode.BadRequest, await admin.PostAsJsonAsync(Endpoint, new { name = Unique(), kind = "proxy" }));
        HttpAssert.Status(HttpStatusCode.BadRequest, await admin.PostAsJsonAsync(Endpoint, new { name = Unique(), purpose = "maven" }));
        HttpAssert.Status(HttpStatusCode.BadRequest, await admin.PostAsJsonAsync(Endpoint, new { name = Unique(), purpose = "1" }));
        HttpAssert.Status(HttpStatusCode.BadRequest, await admin.PostAsJsonAsync(Endpoint, new { name = Unique(), onDelete = "purge" }));
        HttpAssert.Status(HttpStatusCode.BadRequest, await admin.PostAsJsonAsync(Endpoint, new { name = Unique(), folder = new { share = "x" } }));
        HttpAssert.Status(HttpStatusCode.BadRequest, await admin.PostAsync(Endpoint, new StringContent("name=x", Encoding.UTF8, "application/x-www-form-urlencoded")));
        HttpAssert.Status(HttpStatusCode.BadRequest, await admin.PatchAsJsonAsync($"{Endpoint}/public", new { chartColor = 99 }));
        HttpAssert.Status(HttpStatusCode.NotFound, await admin.GetAsync($"{Endpoint}/{Unique()}"));
    }

    /// <summary>The same gate as the token routes: a stranger is asked for credentials, a key without the admin scope is refused.</summary>
    [Fact]
    public async Task Only_a_service_token_with_the_admin_scope_manages_feeds()
    {
        using var stranger = server.CreateClient();
        using var asked = await stranger.GetAsync(Endpoint);
        HttpAssert.Status(HttpStatusCode.Unauthorized, asked);
        Assert.NotEmpty(asked.Headers.WwwAuthenticate);

        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        using var made = await admin.PostAsJsonAsync("api/management/tokens", new { name = Unique(), scopes = new[] { "read", "push", "delete" } });
        var everyFeed = JsonNode.Parse(await made.Content.ReadAsStringAsync())!["token"]!.GetValue<string>();

        using var holder = server.CreateClient(everyFeed);
        HttpAssert.Status(HttpStatusCode.Forbidden, await holder.GetAsync(Endpoint));
        HttpAssert.Status(HttpStatusCode.Forbidden, await holder.PostAsJsonAsync(Endpoint, new { name = Unique() }));
        HttpAssert.Status(HttpStatusCode.Forbidden, await holder.DeleteAsync($"{Endpoint}/public?confirm=public"));
        HttpAssert.Status(HttpStatusCode.OK, await admin.GetAsync($"{Endpoint}/public"));
    }

    private static async Task<JsonNode> CreateAsync(HttpClient admin, object body)
    {
        using var response = await admin.PostAsJsonAsync(Endpoint, body);
        HttpAssert.Status(HttpStatusCode.Created, response);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static async Task<JsonNode> GetAsync(HttpClient admin, string name) =>
        JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await admin.GetAsync($"{Endpoint}/{name}")))!;

    /// <summary>Waits for the background writer: a request never waits on the audit table, so a test has to.</summary>
    private async Task<AuditEntry> WaitForAuditAsync(string action, string subject)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using (var scope = server.Services.CreateAsyncScope())
            {
                var found = (await scope.ServiceProvider.GetRequiredService<IAuditStore>().QueryAsync(new AuditQuery(Action: action, Take: 200), CancellationToken.None))
                    .FirstOrDefault(e => e.Subject == subject);
                if (found is not null)
                {
                    return found;
                }
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new Xunit.Sdk.XunitException($"No {action} entry for {subject} arrived within five seconds.");
    }

    private static string Unique() => "api-" + Guid.NewGuid().ToString("N")[..10];
}
