using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Testing;

namespace FiGet.Integration.Tests;

/// <summary>
/// Protocol listings are compressed for a client that accepts it; what is already compressed, and what a browser
/// reads, is left as it is.
/// </summary>
public sealed class CompressionTests(SqliteServerFixture server) : IClassFixture<SqliteServerFixture>
{
    [Theory]
    [InlineData("gzip")]
    [InlineData("br")]
    public async Task A_v2_listing_is_compressed_when_the_client_accepts_it(string encoding)
    {
        var id = await PushAsync();
        using var client = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"nuget/public/FindPackagesById()?id='{id}'");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue(encoding));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        HttpAssert.Status(HttpStatusCode.OK, response);
        Assert.Equal([encoding], response.Content.Headers.ContentEncoding);
        Assert.Contains(id, await DecompressAsync(response, encoding), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_v3_registration_and_the_management_api_are_compressed()
    {
        var id = await PushAsync();
        using var client = server.CreateClient();
        foreach (var path in new[] { $"nuget/public/v3/registration/{id.ToLowerInvariant()}/index.json", $"api/packages/public/versions?name={id}" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

            HttpAssert.Status(HttpStatusCode.OK, response);
            Assert.Equal(["gzip"], response.Content.Headers.ContentEncoding);
        }
    }

    [Fact]
    public async Task A_listing_is_plain_for_a_client_that_does_not_ask()
    {
        var id = await PushAsync();
        using var client = server.CreateClient();
        using var response = await client.GetAsync($"nuget/public/FindPackagesById()?id='{id}'", TestContext.Current.CancellationToken);

        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Contains(id, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    /// <summary>A nupkg is a zip already; a browser page is not a protocol answer.</summary>
    [Fact]
    public async Task A_package_download_and_a_page_are_not_compressed()
    {
        var id = await PushAsync();
        using var client = server.CreateClient();
        foreach (var path in new[] { $"nuget/public/package/{id}/1.0.0", "/" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

            HttpAssert.Status(HttpStatusCode.OK, response);
            Assert.Empty(response.Content.Headers.ContentEncoding);
        }
    }

    private async Task<string> PushAsync()
    {
        var id = FiGetServerFixture.UniqueId("Compress");
        using var package = TestPackages.Create(id, "1.0.0");
        using var client = server.CreateClient();
        using var content = new MultipartFormDataContent();
        using var file = new StreamContent(package);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "package", "package.nupkg");
        using var request = new HttpRequestMessage(HttpMethod.Put, "nuget/public/") { Content = content };
        request.Headers.Add("X-NuGet-ApiKey", FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.Created, await client.SendAsync(request, TestContext.Current.CancellationToken));
        return id;
    }

    private static async Task<string> DecompressAsync(HttpResponseMessage response, string encoding)
    {
        await using var body = await response.Content.ReadAsStreamAsync();
        await using Stream decoded = encoding == "br" ? new BrotliStream(body, CompressionMode.Decompress) : new GZipStream(body, CompressionMode.Decompress);
        using var reader = new StreamReader(decoded, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }
}
