using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using FiGet.Application.Accounts;
using FiGet.Application.Ports;
using FiGet.Application.Tokens;
using FiGet.Domain.Entities;
using FiGet.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

public sealed class SqliteTokenManagementTests(SqliteServerFixture fixture) : TokenManagementTests(fixture), IClassFixture<SqliteServerFixture>;

public sealed class SqlServerTokenManagementTests(SqlServerServerFixture fixture) : TokenManagementTests(fixture), IClassFixture<SqlServerServerFixture>;

/// <summary>
/// Service tokens made, listed and revoked by a script holding an admin token: <c>/api/management/tokens</c>. The feed
/// <c>private</c> asks for a token for everything, so whether a key works is read off its answers.
/// </summary>
public abstract class TokenManagementTests
{
    private const string Endpoint = "api/management/tokens";

    private readonly FiGetServerFixture server;

    protected TokenManagementTests(FiGetServerFixture server)
    {
        this.server = server;
        server.SkipIfUnavailable();
    }

    [Fact]
    public async Task A_token_made_over_the_api_works_and_shows_its_secret_once()
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        var name = Unique();

        using var response = await admin.PostAsJsonAsync(Endpoint, new { name, scopes = new[] { "read" }, feed = "private", expiresInDays = 30 });
        HttpAssert.Status(HttpStatusCode.Created, response);
        var made = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        var secret = made["token"]!.GetValue<string>();
        Assert.StartsWith("figet_", secret, StringComparison.Ordinal);
        Assert.StartsWith(made["prefix"]!.GetValue<string>(), secret, StringComparison.Ordinal);
        Assert.Equal(name, made["name"]!.GetValue<string>());
        Assert.Equal("private", made["feed"]!.GetValue<string>());
        Assert.Equal(["read"], made["scopes"]!.AsArray().Select(s => s!.GetValue<string>()));
        Assert.True(made["active"]!.GetValue<bool>());
        Assert.EndsWith("Z", made["expiresUtc"]!.ToJsonString().Trim('"'), StringComparison.Ordinal);
        Assert.InRange(made["expiresUtc"]!.GetValue<DateTime>().ToUniversalTime(), DateTime.UtcNow.AddDays(29), DateTime.UtcNow.AddDays(31));

        // It reads the feed it was made for.
        using var holder = server.CreateClient(secret);
        HttpAssert.Status(HttpStatusCode.OK, await holder.GetAsync("api/packages/private"));

        // The list knows the token and never its secret.
        var listed = (await ListAsync(admin)).Single(t => t!["name"]!.GetValue<string>() == name)!;
        Assert.Equal(made["id"]!.GetValue<int>(), listed["id"]!.GetValue<int>());
        Assert.Null(listed["token"]);
        Assert.DoesNotContain(secret, listed.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_token_limited_to_one_feed_does_nothing_on_another()
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        var secret = await CreateAsync(admin, new { name = Unique(), scopes = new[] { "read", "push" }, feed = "public" });

        using var holder = server.CreateClient(secret);
        HttpAssert.Status(HttpStatusCode.OK, await holder.GetAsync("api/packages/public"));
        HttpAssert.Status(HttpStatusCode.Forbidden, await holder.GetAsync("api/packages/private"));
    }

    [Fact]
    public async Task A_token_without_a_feed_or_an_expiry_applies_everywhere_and_does_not_end()
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        using var response = await admin.PostAsJsonAsync(Endpoint, new { name = Unique(), scopes = new[] { "READ", " Delete " } });
        HttpAssert.Status(HttpStatusCode.Created, response);
        var made = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Null(made["feed"]);
        Assert.Null(made["expiresUtc"]);
        Assert.Equal(["read", "delete"], made["scopes"]!.AsArray().Select(s => s!.GetValue<string>()));

        using var holder = server.CreateClient(made["token"]!.GetValue<string>());
        HttpAssert.Status(HttpStatusCode.OK, await holder.GetAsync("api/packages/private"));
    }

    [Fact]
    public async Task A_revoked_token_stops_working_at_once()
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        var name = Unique();
        var secret = await CreateAsync(admin, new { name, scopes = new[] { "read" } });
        var id = (await ListAsync(admin)).Single(t => t!["name"]!.GetValue<string>() == name)!["id"]!.GetValue<int>();

        using var holder = server.CreateClient(secret);
        HttpAssert.Status(HttpStatusCode.OK, await holder.GetAsync("api/packages/private"));

        HttpAssert.Status(HttpStatusCode.NoContent, await admin.DeleteAsync($"{Endpoint}/{id}"));
        HttpAssert.Status(HttpStatusCode.Unauthorized, await holder.GetAsync("api/packages/private"));

        var listed = (await ListAsync(admin)).Single(t => t!["id"]!.GetValue<int>() == id)!;
        Assert.False(listed["active"]!.GetValue<bool>());
        Assert.NotNull(listed["revokedUtc"]);

        // Asking again for what is already done is not an error.
        HttpAssert.Status(HttpStatusCode.NoContent, await admin.DeleteAsync($"{Endpoint}/{id}"));
        HttpAssert.Status(HttpStatusCode.NotFound, await admin.DeleteAsync($"{Endpoint}/2000000000"));
    }

    [Fact]
    public async Task The_api_never_makes_or_revokes_a_token_with_the_admin_scope()
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);

        using var refused = await admin.PostAsJsonAsync(Endpoint, new { name = Unique(), scopes = new[] { "read", "admin" } });
        HttpAssert.Status(HttpStatusCode.Forbidden, refused);
        Assert.Contains("super administrator", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The token this test itself calls with is one: listed as what it is, and not the API's to take away.
        var own = (await ListAsync(admin)).First(t => t!["scopes"]!.AsArray().Any(s => s!.GetValue<string>() == "admin"))!;
        HttpAssert.Status(HttpStatusCode.Forbidden, await admin.DeleteAsync($"{Endpoint}/{own["id"]!.GetValue<int>()}"));
        HttpAssert.Status(HttpStatusCode.OK, await admin.GetAsync(Endpoint));
    }

    [Fact]
    public async Task Only_a_service_token_with_the_admin_scope_manages_tokens()
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        var everything = await CreateAsync(admin, new { name = Unique(), scopes = new[] { "read", "push", "delete" } });

        using var anonymous = server.CreateClient();
        using var challenged = await anonymous.GetAsync(Endpoint);
        HttpAssert.Status(HttpStatusCode.Unauthorized, challenged);
        Assert.Contains("Basic", challenged.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);

        // A key that may do everything on every feed still manages nothing.
        using var holder = server.CreateClient(everything);
        HttpAssert.Status(HttpStatusCode.Forbidden, await holder.GetAsync(Endpoint));
        HttpAssert.Status(HttpStatusCode.Forbidden, await holder.PostAsJsonAsync(Endpoint, new { name = Unique(), scopes = new[] { "read" } }));
        HttpAssert.Status(HttpStatusCode.Forbidden, await holder.DeleteAsync($"{Endpoint}/1"));

        using var garbage = server.CreateClient();
        garbage.DefaultRequestHeaders.Add("X-ApiKey", "figet_not_a_key_at_all");
        HttpAssert.Status(HttpStatusCode.Forbidden, await garbage.GetAsync(Endpoint));

        // A stranger who sends a body learns only that credentials are required, not what was wrong with the body.
        using var broken = new StringContent("{ this is not json", Encoding.UTF8, "application/json");
        HttpAssert.Status(HttpStatusCode.Unauthorized, await anonymous.PostAsync(Endpoint, broken));
    }

    [Fact]
    public async Task A_personal_key_is_not_the_apis_to_show_or_revoke()
    {
        string secret;
        int key;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var owner = (await scope.ServiceProvider.GetRequiredService<IUserStore>().FindByUserNameAsync(FiGetServerFixture.AdminUserName, CancellationToken.None))!;
            var created = await scope.ServiceProvider.GetRequiredService<AccessTokenService>()
                .CreatePersonalKeyAsync(new AccountActor(owner.Key, owner.Role), Unique(), TokenScopes.Read, feed: null, expiresUtc: null, CancellationToken.None);
            secret = created.Created!.Secret;
            key = created.Created.Token.Key;
        }

        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        Assert.DoesNotContain(await ListAsync(admin), t => t!["id"]!.GetValue<int>() == key);
        HttpAssert.Status(HttpStatusCode.NotFound, await admin.DeleteAsync($"{Endpoint}/{key}"));

        using var holder = server.CreateClient(secret);
        HttpAssert.Status(HttpStatusCode.OK, await holder.GetAsync("api/packages/private"));
    }

    [Theory]
    [InlineData("""{"scopes":["read"]}""", "name is required")]
    [InlineData("""{"name":"   ","scopes":["read"]}""", "name is required")]
    [InlineData("""{"name":"x"}""", "scopes is required")]
    [InlineData("""{"name":"x","scopes":[]}""", "scopes is required")]
    [InlineData("""{"name":"x","scopes":["write"]}""", "is not a scope")]
    [InlineData("""{"name":"x","scopes":["read"],"feed":"no-such-feed"}""", "There is no feed")]
    [InlineData("""{"name":"x","scopes":["read"],"expiresInDays":0}""", "expiresInDays")]
    [InlineData("""{"name":"x","scopes":["read"],"expiresInDays":3651}""", "expiresInDays")]
    [InlineData("""{ this is not json""", "Send a JSON object")]
    [InlineData("null", "Send a JSON object")]
    public async Task A_request_that_cannot_be_honoured_is_refused_with_the_reason(string body, string reason)
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await admin.PostAsync(Endpoint, content);

        HttpAssert.Status(HttpStatusCode.BadRequest, response);
        Assert.Contains(reason, JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Making_and_revoking_a_token_is_in_the_audit_log()
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        var name = Unique();
        await CreateAsync(admin, new { name, scopes = new[] { "read", "push" }, feed = "private", expiresInDays = 7 });
        var id = (await ListAsync(admin)).Single(t => t!["name"]!.GetValue<string>() == name)!["id"]!.GetValue<int>();
        HttpAssert.Status(HttpStatusCode.NoContent, await admin.DeleteAsync($"{Endpoint}/{id}"));

        var made = await AuditWait.ForAsync(server, "token.create", name);
        Assert.Contains("feed=private", made.Detail, StringComparison.Ordinal);
        Assert.Contains("scopes=Read, Push", made.Detail, StringComparison.Ordinal);
        Assert.StartsWith("token:", made.Actor, StringComparison.Ordinal);

        var revoked = await AuditWait.ForAsync(server, "token.revoke", name);
        Assert.StartsWith("token:", revoked.Actor, StringComparison.Ordinal);
    }

    private static async Task<string> CreateAsync(HttpClient admin, object request)
    {
        using var response = await admin.PostAsJsonAsync(Endpoint, request);
        HttpAssert.Status(HttpStatusCode.Created, response);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["token"]!.GetValue<string>();
    }

    private static async Task<JsonArray> ListAsync(HttpClient admin)
    {
        using var response = await admin.GetAsync(Endpoint);
        return JsonNode.Parse(await HttpAssert.SuccessBodyAsync(response))!.AsArray();
    }

    private static string Unique() => "api-" + Guid.NewGuid().ToString("N")[..10];
}
