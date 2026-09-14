using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FiGet.Integration.Tests.Infrastructure;

namespace FiGet.Integration.Tests;

public abstract partial class AssetDirectoryTests
{
    /// <summary>
    /// A content type set through the metadata call becomes the download's header, so it is checked as a media type before
    /// it is stored: a line break in it failed every download of the file with a 500, and one longer than the column
    /// failed the save the same way (review S6.4). A refusal leaves the stored type as it was.
    /// </summary>
    [Theory]
    [InlineData("text/plain\r\nX-Injected: 1")]
    [InlineData("not a media type")]
    [InlineData("application/")]
    public async Task A_content_type_that_is_not_a_media_type_is_refused_and_the_old_one_kept(string type)
    {
        var folder = Unique();
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        using (var body = new ByteArrayContent(Encoding.UTF8.GetBytes("hello")))
        {
            body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
            HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync($"endpoints/files/content/{folder}/note.txt", body));
        }

        var update = new JsonObject { ["type"] = type }.ToJsonString();
        using (var content = new StringContent(update, Encoding.UTF8, "application/json"))
        {
            HttpAssert.Status(HttpStatusCode.BadRequest, await admin.PostAsync($"endpoints/files/metadata/{folder}/note.txt", content));
        }

        var described = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await admin.GetAsync($"endpoints/files/metadata/{folder}/note.txt")))!;
        Assert.Equal("text/plain", (string?)described["type"]);
        var download = await admin.GetAsync($"endpoints/files/content/{folder}/note.txt");
        HttpAssert.Status(HttpStatusCode.OK, download);
    }

    [Fact]
    public async Task A_content_type_longer_than_the_column_is_refused()
    {
        var folder = Unique();
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync($"endpoints/files/content/{folder}/note.txt", new ByteArrayContent([1])));

        var update = new JsonObject { ["type"] = "application/x-" + new string('a', 300) }.ToJsonString();
        using var content = new StringContent(update, Encoding.UTF8, "application/json");
        HttpAssert.Status(HttpStatusCode.BadRequest, await admin.PostAsync($"endpoints/files/metadata/{folder}/note.txt", content));
    }
}
