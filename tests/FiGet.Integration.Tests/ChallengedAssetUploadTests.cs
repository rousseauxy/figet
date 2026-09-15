using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using FiGet.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace FiGet.Integration.Tests;

/// <summary>An asset directory at the default size limits, so an upload can be larger than Kestrel's 30 MB default.</summary>
public sealed class LargeAssetServerFixture() : FiGetServerFixture(TestDatabase.Sqlite)
{
    protected override void Configure(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSetting("FiGet:Feeds:3:Name", "installers");
        builder.UseSetting("FiGet:Feeds:3:Kind", "Assets");
    }
}

/// <summary>
/// The asset routes' side of the challenged push (ChallengedPushTests): a client that sends a large body without credentials,
/// is answered 401 and sends it again with them. Left open by the cross-check of 2026-09-15 as "not traced"; the refused
/// attempt's unread body had the connection reset past Kestrel's 30 MB default, so the client never saw the 401.
/// </summary>
public sealed class ChallengedAssetUploadTests(LargeAssetServerFixture server) : IClassFixture<LargeAssetServerFixture>
{
    [Fact]
    public async Task A_large_upload_and_import_answering_a_challenge_succeed()
    {
        var folder = "c" + Guid.NewGuid().ToString("N")[..10];
        var noise = new byte[40 * 1024 * 1024];
        Random.Shared.NextBytes(noise);

        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using var entry = await zip.CreateEntry("setup.bin", CompressionLevel.NoCompression).OpenAsync(TestContext.Current.CancellationToken);
            await entry.WriteAsync(noise, TestContext.Current.CancellationToken);
        }

        using var challenged = new HttpClient(new HttpClientHandler { Credentials = new NetworkCredential("ci", FiGetServerFixture.AdminToken), PreAuthenticate = false })
        {
            BaseAddress = server.BaseAddress,
            Timeout = TimeSpan.FromMinutes(2),
        };

        foreach (var (method, path, body) in new[]
        {
            (HttpMethod.Put, $"endpoints/installers/content/{folder}/setup.bin", noise),
            (HttpMethod.Post, $"endpoints/installers/import/{folder}/unpacked?format=zip", archive.ToArray()),
        })
        {
            using var content = new StreamContent(new MemoryStream(body));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var request = new HttpRequestMessage(method, path) { Content = content };
            request.Headers.TransferEncodingChunked = true;
            using var response = await challenged.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.True(response.IsSuccessStatusCode, $"{method} {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        }

        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        Assert.Equal(noise.Length, (await admin.GetByteArrayAsync($"endpoints/installers/content/{folder}/setup.bin", TestContext.Current.CancellationToken)).Length);
        Assert.Equal(noise.Length, (await admin.GetByteArrayAsync($"endpoints/installers/content/{folder}/unpacked/setup.bin", TestContext.Current.CancellationToken)).Length);
    }
}
