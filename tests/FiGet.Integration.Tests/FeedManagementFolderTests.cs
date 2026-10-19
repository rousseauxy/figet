using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FiGet.Integration.Tests.Infrastructure;

namespace FiGet.Integration.Tests;

/// <summary>
/// A directory's content chosen over the API: the same rules as on the pages. A share is named, never a path; anything
/// not on the mount is refused; a directory whose folder configuration sets is left to configuration.
/// </summary>
public sealed class FeedManagementFolderTests(SharesRootServerFixture server) : IClassFixture<SharesRootServerFixture>
{
    private const string Endpoint = "api/management/feeds";

    [Fact]
    public async Task A_directory_made_on_a_share_serves_the_share_and_never_shows_its_path()
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        var name = Unique();

        using var response = await admin.PostAsJsonAsync(Endpoint, new { name, kind = "directory", folder = new { share = "intune", inside = "scripts" } });
        HttpAssert.Status(HttpStatusCode.Created, response);
        var body = await response.Content.ReadAsStringAsync();
        var folder = JsonNode.Parse(body)!["folder"]!;
        Assert.Equal("intune", folder["share"]!.GetValue<string>());
        Assert.Equal("scripts", folder["inside"]!.GetValue<string>());
        Assert.False(folder["writes"]!.GetValue<bool>());
        Assert.DoesNotContain(server.SharesRoot.Replace("\\", "\\\\", StringComparison.Ordinal), body, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("from intune/scripts", await HttpAssert.SuccessBodyAsync(await admin.GetAsync($"endpoints/{name}/content/run.ps1")));
    }

    [Theory]
    [InlineData("nosuchshare")]
    [InlineData("..")]
    [InlineData(".dotted")]
    [InlineData("notes.txt")]
    public async Task Anything_that_is_not_a_share_on_the_mount_makes_nothing(string share)
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        var name = Unique();

        HttpAssert.Status(HttpStatusCode.BadRequest, await admin.PostAsJsonAsync(Endpoint, new { name, kind = "directory", folder = new { share } }));
        HttpAssert.Status(HttpStatusCode.NotFound, await admin.GetAsync($"{Endpoint}/{name}"));
    }

    [Fact]
    public async Task A_directory_moves_between_a_share_and_its_own_storage()
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        var name = Unique();
        HttpAssert.Status(HttpStatusCode.Created, await admin.PostAsJsonAsync(Endpoint, new { name, kind = "directory" }));

        using var onShare = await admin.PutAsJsonAsync($"{Endpoint}/{name}/folder", new { share = "intune", writes = true });
        var moved = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(onShare))!["folder"]!;
        Assert.Equal("intune", moved["share"]!.GetValue<string>());
        Assert.True(moved["writes"]!.GetValue<bool>());
        Assert.Equal("from intune", await HttpAssert.SuccessBodyAsync(await admin.GetAsync($"endpoints/{name}/content/setup.txt")));

        HttpAssert.Status(HttpStatusCode.BadRequest, await admin.PutAsJsonAsync($"{Endpoint}/{name}/folder", new { share = "intune", inside = "../crm" }));

        using var back = await admin.PutAsJsonAsync($"{Endpoint}/{name}/folder", new { share = "" });
        Assert.Null(JsonNode.Parse(await HttpAssert.SuccessBodyAsync(back))!["folder"]);
        HttpAssert.Status(HttpStatusCode.NotFound, await admin.GetAsync($"endpoints/{name}/content/setup.txt"));
    }

    [Fact]
    public async Task A_folder_set_by_configuration_or_a_package_feed_has_none_to_choose()
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);

        var configured = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await admin.GetAsync($"{Endpoint}/cfgshare")))!["folder"]!;
        Assert.True(configured["fromConfiguration"]!.GetValue<bool>());
        HttpAssert.Status(HttpStatusCode.Conflict, await admin.PutAsJsonAsync($"{Endpoint}/cfgshare/folder", new { share = "crm" }));
        Assert.Equal("from configuration", await HttpAssert.SuccessBodyAsync(await admin.GetAsync("endpoints/cfgshare/content/configured.txt")));

        HttpAssert.Status(HttpStatusCode.Conflict, await admin.PutAsJsonAsync($"{Endpoint}/public/folder", new { share = "crm" }));
    }

    private static string Unique() => "api-" + Guid.NewGuid().ToString("N")[..10];
}
