using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

public sealed class SqlitePackageManagementTests(SqliteServerFixture fixture) : PackageManagementTests(fixture), IClassFixture<SqliteServerFixture>;

public sealed class SqlServerPackageManagementTests(SqlServerServerFixture fixture) : PackageManagementTests(fixture), IClassFixture<SqlServerServerFixture>;

/// <summary>
/// <c>/api/packages/{feed}</c>, the management API scripts written against the server being replaced call:
/// the CI version fallback reads <c>versions</c> sorted by <c>published</c>, and clean-up scripts pair
/// <c>latest</c> with <c>delete</c>. The shapes are the reference client's (docs/protocol-management.md).
/// </summary>
public abstract class PackageManagementTests
{
    private readonly FiGetServerFixture server;

    protected PackageManagementTests(FiGetServerFixture server)
    {
        this.server = server;
        server.SkipIfUnavailable();
    }

    [Fact]
    public async Task Versions_lists_every_version_of_a_package_with_the_fields_scripts_read()
    {
        var id = FiGetServerFixture.UniqueId("Mgmt.Versions");
        var bytes = await PushAsync("public", id, "1.0.0");
        await PushAsync("public", id, "1.2.0");
        await PushAsync("public", id, "2.0.0-beta1");

        using var client = server.CreateClient();
        var versions = await JsonArrayAsync(client, $"api/packages/public/versions?name={id}");

        // Newest first, which is also what "Sort-Object published -Descending" would leave in front.
        Assert.Equal(["2.0.0-beta1", "1.2.0", "1.0.0"], versions.Select(v => (string?)v!["version"]));
        var oldest = versions[^1]!;
        Assert.Equal(id, (string?)oldest["name"]);
        Assert.Equal($"pkg:nuget/{id}@1.0.0", (string?)oldest["purl"]);
        Assert.True((bool?)oldest["listed"]);
        Assert.Equal(bytes.Length, (long?)oldest["size"]);
        Assert.Equal(Convert.ToHexStringLower(SHA512.HashData(bytes)), (string?)oldest["sha512"]);
        Assert.True(DateTime.TryParse((string?)oldest["published"], out _));

        // A pushed version has no recorded publisher; the field is left out rather than invented.
        Assert.Null(oldest["publishedBy"]);

        Assert.Equal(["1.2.0"], (await JsonArrayAsync(client, $"api/packages/public/versions?name={id}&version=1.2")).Select(v => (string?)v!["version"]));
        Assert.Empty(await JsonArrayAsync(client, $"api/packages/public/versions?name={id}.missing"));
    }

    [Fact]
    public async Task Versions_without_a_name_lists_the_whole_feed()
    {
        var first = FiGetServerFixture.UniqueId("Mgmt.FeedA");
        var second = FiGetServerFixture.UniqueId("Mgmt.FeedB");
        await PushAsync("public", first, "1.0.0");
        await PushAsync("public", second, "3.0.0");

        using var client = server.CreateClient();
        var all = await JsonArrayAsync(client, "api/packages/public/versions");

        Assert.Contains(all, v => (string?)v!["name"] == first && (string?)v["version"] == "1.0.0");
        Assert.Contains(all, v => (string?)v!["name"] == second && (string?)v["version"] == "3.0.0");
    }

    [Fact]
    public async Task Latest_is_a_list_with_one_entry_per_package_following_the_listed_and_stable_rules()
    {
        var id = FiGetServerFixture.UniqueId("Mgmt.Latest");
        await PushAsync("public", id, "1.0.0");
        await PushAsync("public", id, "1.5.0");
        await PushAsync("public", id, "2.0.0-rc1");

        using var client = server.CreateClient();
        Assert.Equal(["2.0.0-rc1"], (await JsonArrayAsync(client, $"api/packages/public/latest?name={id}")).Select(v => (string?)v!["version"]));
        Assert.Equal(["1.5.0"], (await JsonArrayAsync(client, $"api/packages/public/latest?name={id}&stableOnly=true")).Select(v => (string?)v!["version"]));

        // Unlisting the newest stable version hands "latest" to the one before it.
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.OK, await admin.PostAsync($"api/packages/public/status?name={id}&version=1.5.0", Json("""{"listed":false}""")));
        Assert.Equal(["1.0.0"], (await JsonArrayAsync(client, $"api/packages/public/latest?name={id}&stableOnly=true")).Select(v => (string?)v!["version"]));

        var feedWide = await JsonArrayAsync(client, "api/packages/public/latest");
        Assert.Single(feedWide, v => (string?)v!["name"] == id);
    }

    [Fact]
    public async Task Delete_removes_the_version_and_its_file_whatever_the_feed_delete_setting_says()
    {
        // The public feed only unlists on a NuGet delete; this call is a delete.
        var id = FiGetServerFixture.UniqueId("Mgmt.Delete");
        await PushAsync("public", id, "1.0.0");
        await PushAsync("public", id, "1.1.0");

        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.OK, await admin.PostAsync($"api/packages/public/delete?name={id}&version=1.0.0", null));

        using var client = server.CreateClient();
        Assert.Equal(["1.1.0"], (await JsonArrayAsync(client, $"api/packages/public/versions?name={id}")).Select(v => (string?)v!["version"]));
        HttpAssert.Status(HttpStatusCode.NotFound, await client.GetAsync($"nuget/public/v3/flatcontainer/{id.ToLowerInvariant()}/1.0.0/{id.ToLowerInvariant()}.1.0.0.nupkg"));
        HttpAssert.Status(HttpStatusCode.NotFound, await admin.PostAsync($"api/packages/public/delete?name={id}&version=1.0.0", null));
    }

    [Fact]
    public async Task Changing_a_feed_needs_a_token_with_the_scope()
    {
        var id = FiGetServerFixture.UniqueId("Mgmt.Scope");
        await PushAsync("public", id, "1.0.0");

        using var anonymous = server.CreateClient();
        HttpAssert.Status(HttpStatusCode.Unauthorized, await anonymous.PostAsync($"api/packages/public/delete?name={id}&version=1.0.0", null));

        var reader = await CreateTokenAsync(FiGet.Domain.Entities.TokenScopes.Read);
        using var readOnly = server.CreateClient(reader);
        HttpAssert.Status(HttpStatusCode.Forbidden, await readOnly.PostAsync($"api/packages/public/delete?name={id}&version=1.0.0", null));
        HttpAssert.Status(HttpStatusCode.Forbidden, await readOnly.PostAsync($"api/packages/public/status?name={id}&version=1.0.0", Json("""{"listed":false}""")));

        // A feed that needs a token to read needs one to list, too; the scripts send X-ApiKey.
        HttpAssert.Status(HttpStatusCode.Unauthorized, await anonymous.GetAsync("api/packages/private/versions"));
        using var keyed = new HttpClient { BaseAddress = server.BaseAddress };
        keyed.DefaultRequestHeaders.Add("X-ApiKey", reader);
        HttpAssert.Status(HttpStatusCode.OK, await keyed.GetAsync("api/packages/private/versions"));
    }

    [Fact]
    public async Task Status_lists_and_unlists_and_refuses_what_it_cannot_do()
    {
        var id = FiGetServerFixture.UniqueId("Mgmt.Status");
        await PushAsync("public", id, "1.0.0");
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);

        HttpAssert.Status(HttpStatusCode.OK, await admin.PostAsync($"api/packages/public/status?name={id}&version=1.0.0", Json("""{"listed":false}""")));
        Assert.False((bool?)(await JsonArrayAsync(admin, $"api/packages/public/versions?name={id}"))[0]!["listed"]);

        HttpAssert.Status(HttpStatusCode.OK, await admin.PostAsync($"api/packages/public/status?name={id}&version=1.0.0", Json("""{"listed":true}""")));
        Assert.True((bool?)(await JsonArrayAsync(admin, $"api/packages/public/versions?name={id}"))[0]!["listed"]);

        HttpAssert.Status(HttpStatusCode.BadRequest, await admin.PostAsync($"api/packages/public/status?name={id}&version=1.0.0", Json("""{"deprecated":true,"deprecationReason":"old"}""")));
        HttpAssert.Status(HttpStatusCode.NotFound, await admin.PostAsync($"api/packages/public/status?name={id}&version=9.9.9", Json("""{"listed":false}""")));
    }

    [Fact]
    public async Task Upload_and_download_round_trip_a_package()
    {
        var id = FiGetServerFixture.UniqueId("Mgmt.Upload");
        using var package = TestPackages.Create(id, "1.0.0");
        var bytes = package.ToArray();
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);

        // The reference client puts the raw package, with the file name in the path.
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync($"api/packages/overwrite/upload/{id}.1.0.0.nupkg", new ByteArrayContent(bytes)));
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync("api/packages/public/upload", new ByteArrayContent(bytes)));
        HttpAssert.Status(HttpStatusCode.Conflict, await admin.PutAsync("api/packages/public/upload", new ByteArrayContent(bytes)));

        using var download = await server.CreateClient().GetAsync($"api/packages/public/download?name={id}&version=1.0.0");
        HttpAssert.Status(HttpStatusCode.OK, download);
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
        HttpAssert.Status(HttpStatusCode.NotFound, await server.CreateClient().GetAsync($"api/packages/public/download?name={id}&version=2.0.0"));
    }

    /// <summary>
    /// Found by running the reference client: before a download or a delete it asks what kind of feed this
    /// is, and without an answer it stops with 404 before sending the request it was asked to make.
    /// </summary>
    [Fact]
    public async Task The_feed_describes_itself_as_a_nuget_feed()
    {
        using var client = server.CreateClient();
        var info = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync("api/packages/public")))!;
        Assert.Equal("public", (string?)info["name"]);
        Assert.Equal("nuget", (string?)info["packageType"]);
        Assert.Equal("nuget", (string?)info["feedType"]);
        HttpAssert.Status(HttpStatusCode.NotFound, await client.GetAsync("api/packages/nosuchfeed"));
    }

    [Fact]
    public async Task An_asset_directory_is_not_a_package_feed_here_either()
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        await using var scope = server.Services.CreateAsyncScope();
        var feeds = scope.ServiceProvider.GetRequiredService<FiGet.Application.Ports.IFeedStore>();
        var name = "mgmt-assets-" + Guid.NewGuid().ToString("N")[..6];
        Assert.True(await feeds.CreateAsync(new FiGet.Domain.Entities.Feed { Name = name, NameLower = name, Kind = FiGet.Domain.Entities.FeedKind.Assets, AnonymousRead = true, CreatedUtc = DateTime.UtcNow }, CancellationToken.None));

        HttpAssert.Status(HttpStatusCode.NotFound, await admin.GetAsync($"api/packages/{name}/versions"));
    }

    private async Task<byte[]> PushAsync(string feed, string id, string version)
    {
        using var package = TestPackages.Create(id, version);
        var bytes = package.ToArray();
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync($"api/packages/{feed}/upload", new ByteArrayContent(bytes)));
        return bytes;
    }

    private static async Task<JsonArray> JsonArrayAsync(HttpClient client, string url) =>
        JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync(url)))!.AsArray();

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private async Task<string> CreateTokenAsync(FiGet.Domain.Entities.TokenScopes scopes)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<FiGet.Application.Tokens.AccessTokenService>();
        return (await tokens.CreateAsync("mgmt-" + scopes, scopes, null, null, CancellationToken.None)).Secret;
    }
}
