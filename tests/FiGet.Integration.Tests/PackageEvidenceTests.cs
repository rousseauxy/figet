using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FiGet.Application.Tokens;
using FiGet.Domain.Entities;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

public sealed class SqlitePackageEvidenceTests(SqliteServerFixture fixture) : PackageEvidenceTests(fixture), IClassFixture<SqliteServerFixture>;

public sealed class SqlServerPackageEvidenceTests(SqlServerServerFixture fixture) : PackageEvidenceTests(fixture), IClassFixture<SqlServerServerFixture>;

/// <summary>
/// Behaviour other package servers' issue trackers showed going wrong elsewhere, where FiGet's code was judged right but no
/// test proved it (cross-check of 2026-09-15). Each test is the evidence for one of those judgements.
/// </summary>
public abstract class PackageEvidenceTests
{
    private readonly FiGetServerFixture server;

    protected PackageEvidenceTests(FiGetServerFixture server)
    {
        this.server = server;
        server.SkipIfUnavailable();
    }

    /// <summary>
    /// A version with build metadata is the same version as without it: pushed once, downloaded by either spelling, stored
    /// under the normalised name, and a second push with other metadata is the same version again.
    /// </summary>
    [Fact]
    public async Task A_version_with_build_metadata_is_stored_and_served_under_its_normalised_version()
    {
        var id = FiGetServerFixture.UniqueId("Evidence.BuildMetadata");
        using (var package = TestPackages.Create(id, "1.0.0+build.5"))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushAsync("public", package));
        }

        using (var again = TestPackages.Create(id, "1.0.0+build.6"))
        {
            HttpAssert.Status(HttpStatusCode.Conflict, await PushAsync("public", again));
        }

        using var client = server.CreateClient();
        var idLower = id.ToLowerInvariant();
        var index = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/public/v3/flatcontainer/{idLower}/index.json")))!;
        Assert.Equal(["1.0.0"], index["versions"]!.AsArray().Select(v => (string?)v).ToArray());

        HttpAssert.Status(HttpStatusCode.OK, await client.GetAsync($"nuget/public/v3/flatcontainer/{idLower}/1.0.0/{idLower}.1.0.0.nupkg"));
        foreach (var spelling in new[] { "1.0.0", Uri.EscapeDataString("1.0.0+build.5"), "1.0.0.0" })
        {
            using var download = await client.GetAsync($"nuget/public/package/{id}/{spelling}");
            HttpAssert.Status(HttpStatusCode.OK, download);
            Assert.Equal($"{idLower}.1.0.0.nupkg", download.Content.Headers.ContentDisposition?.FileNameStar ?? download.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        }
    }

    /// <summary>
    /// Pushing the same bytes again puts back the file of a version whose file is gone; other bytes for that version are still
    /// a conflict. Found cross-checking Gitea's NuGet issues (#39215, 2026-09-15).
    /// </summary>
    [Fact]
    public async Task Pushing_the_same_package_again_restores_a_missing_file()
    {
        var id = FiGetServerFixture.UniqueId("Evidence.Restore");
        var bytes = TestPackages.Create(id, "1.0.0").ToArray();
        using (var package = new MemoryStream(bytes))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushAsync("public", package));
        }

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var feedKey = (await scope.ServiceProvider.GetRequiredService<FiGet.Application.Ports.IFeedStore>().FindAsync("public", CancellationToken.None))!.Key;
            await scope.ServiceProvider.GetRequiredService<FiGet.Application.Ports.IPackageStorage>().DeletePackageAsync(new FiGet.Application.Ports.PackageStorageKey(feedKey, id.ToLowerInvariant(), "1.0.0"), CancellationToken.None);
        }

        using var client = server.CreateClient();
        HttpAssert.Status(HttpStatusCode.NotFound, await client.GetAsync($"nuget/public/package/{id}/1.0.0"));

        using (var other = TestPackages.Create(id, "1.0.0", b => b.Description = "Something else"))
        {
            HttpAssert.Status(HttpStatusCode.Conflict, await PushAsync("public", other));
        }

        using (var same = new MemoryStream(bytes))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushAsync("public", same));
        }

        Assert.Equal(bytes, await client.GetByteArrayAsync($"nuget/public/package/{id}/1.0.0"));
    }

    /// <summary>nuget.exe over v2 may send the package as the raw request body instead of multipart.</summary>
    [Fact]
    public async Task A_v2_push_with_the_package_as_the_raw_body_is_stored()
    {
        var id = FiGetServerFixture.UniqueId("Evidence.RawPush");
        using var package = TestPackages.Create(id, "1.0.0");
        using var client = server.CreateClient();
        using var content = new ByteArrayContent(package.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var request = new HttpRequestMessage(HttpMethod.Put, "nuget/public/") { Content = content };
        request.Headers.Add("X-NuGet-ApiKey", FiGetServerFixture.AdminToken);

        HttpAssert.Status(HttpStatusCode.Created, await client.SendAsync(request));
        HttpAssert.Status(HttpStatusCode.OK, await client.GetAsync($"nuget/public/package/{id}/1.0.0"));
    }

    /// <summary>Metadata outside ASCII comes back exactly as pushed, on both database providers.</summary>
    [Fact]
    public async Task Metadata_outside_ascii_round_trips()
    {
        const string Text = "Überprüfung der Zugänge – 日本語のテスト ✓ ñ";
        var id = FiGetServerFixture.UniqueId("Evidence.Unicode");
        using (var package = TestPackages.Create(id, "1.0.0", b =>
        {
            b.Description = Text;
            b.Title = "Zugänge ✓";
            b.Authors.Clear();
            b.Authors.Add("Jürgen Ødegård");
        }))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushAsync("public", package));
        }

        using var client = server.CreateClient();
        var v2 = await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/public/FindPackagesById()?id='{id}'"));
        Assert.Contains(Text, v2, StringComparison.Ordinal);
        Assert.Contains("Jürgen Ødegård", v2, StringComparison.Ordinal);

        var registration = await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/public/v3/registration/{id.ToLowerInvariant()}/index.json"));
        Assert.Equal(Text, (string?)JsonNode.Parse(registration)!["items"]![0]!["items"]![0]!["catalogEntry"]!["description"]);
    }

    /// <summary>A prerelease label is matched ignoring case in a delete and in a symbol push, as it is in downloads.</summary>
    [Fact]
    public async Task An_upper_case_prerelease_label_matches_in_delete_and_symbol_push()
    {
        var id = FiGetServerFixture.UniqueId("Evidence.PrereleaseCase");
        using (var package = TestPackages.Create(id, "2.0.0-beta1"))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushAsync("public", package));
        }

        using (var symbols = TestPackages.CreateSymbols(id, "2.0.0-BETA1", TestPackages.PortablePdb(), "Lib.pdb"))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushAsync("public", symbols, "v3/symbolpublish"));
        }

        using var admin = server.CreateClient();
        admin.DefaultRequestHeaders.Add("X-NuGet-ApiKey", FiGetServerFixture.AdminToken);
        var deleted = await admin.DeleteAsync($"nuget/public/v3/publish/{id}/2.0.0-BETA1");
        Assert.True(deleted.IsSuccessStatusCode, $"Delete answered {(int)deleted.StatusCode}.");
    }

    /// <summary>A symbol package carrying the same PDB twice, for two frameworks, is accepted and served.</summary>
    [Fact]
    public async Task A_symbol_package_with_the_same_pdb_twice_is_accepted()
    {
        var id = FiGetServerFixture.UniqueId("Evidence.TwinPdb");
        using (var package = TestPackages.Create(id, "1.0.0"))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushAsync("public", package));
        }

        var pdb = TestPackages.PortablePdb();
        using (var symbols = TestPackages.CreateSymbols(id, "1.0.0", [("lib/net8.0/Lib.pdb", pdb), ("lib/netstandard2.0/Lib.pdb", pdb)]))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushAsync("public", symbols, "v3/symbolpublish"));
        }
    }

    /// <summary>A symbol client probing for the two-tier layout gets 404, so it keeps asking by file name and key.</summary>
    [Fact]
    public async Task The_two_tier_symbol_index_is_not_found()
    {
        using var client = server.CreateClient();
        HttpAssert.Status(HttpStatusCode.NotFound, await client.GetAsync("nuget/public/symbols/index2.txt"));
    }

    /// <summary>A garbage API key header does not spoil valid Basic credentials in the same request.</summary>
    [Fact]
    public async Task A_garbage_key_beside_valid_basic_credentials_is_ignored()
    {
        using var client = server.CreateClient(FiGetServerFixture.AdminToken);
        client.DefaultRequestHeaders.Add("X-NuGet-ApiKey", "figet_not_a_real_key");
        HttpAssert.Status(HttpStatusCode.OK, await client.GetAsync("nuget/private/v3/query"));
    }

    /// <summary>A key with an expiry date works until then and is refused after it.</summary>
    [Fact]
    public async Task A_key_is_refused_after_its_expiry_date()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<AccessTokenService>();
        var expired = (await tokens.CreateServiceTokenAsync(FiGetServerFixture.SuperAdminActor, "expired", TokenScopes.Read, null, DateTime.UtcNow.AddMinutes(-1), CancellationToken.None)).Created!.Secret;
        var current = (await tokens.CreateServiceTokenAsync(FiGetServerFixture.SuperAdminActor, "current", TokenScopes.Read, null, DateTime.UtcNow.AddDays(1), CancellationToken.None)).Created!.Secret;

        using var client = server.CreateClient(expired);
        HttpAssert.Status(HttpStatusCode.Unauthorized, await client.GetAsync("nuget/private/v3/query"));
        using var valid = server.CreateClient(current);
        HttpAssert.Status(HttpStatusCode.OK, await valid.GetAsync("nuget/private/v3/query"));
    }

    /// <summary>The flat container index of an id nobody pushed is 404, which is how a v3 client learns it does not exist.</summary>
    [Fact]
    public async Task The_flat_container_index_of_an_unknown_id_is_not_found()
    {
        using var client = server.CreateClient();
        HttpAssert.Status(HttpStatusCode.NotFound, await client.GetAsync($"nuget/public/v3/flatcontainer/{FiGetServerFixture.UniqueId("Evidence.Nobody").ToLowerInvariant()}/index.json"));
    }

    /// <summary>Concurrent pushes of the same new version store it once: one is created, the others conflict.</summary>
    [Fact]
    public async Task Concurrent_pushes_of_the_same_version_store_it_once()
    {
        var id = FiGetServerFixture.UniqueId("Evidence.Race");
        var bytes = TestPackages.Create(id, "1.0.0").ToArray();
        var answers = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            using var package = new MemoryStream(bytes);
            return (await PushAsync("public", package)).StatusCode;
        }));

        Assert.Single(answers, a => a == HttpStatusCode.Created);
        Assert.All(answers.Where(a => a != HttpStatusCode.Created), a => Assert.Equal(HttpStatusCode.Conflict, a));

        using var client = server.CreateClient();
        var index = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/public/v3/flatcontainer/{id.ToLowerInvariant()}/index.json")))!;
        Assert.Single(index["versions"]!.AsArray());
    }

    private async Task<HttpResponseMessage> PushAsync(string feed, Stream package, string resource = "v3/publish")
    {
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-NuGet-ApiKey", FiGetServerFixture.AdminToken);
        using var content = new MultipartFormDataContent();
        var file = new StreamContent(package);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "package", "package.nupkg");
        var response = await client.PutAsync($"nuget/{feed}/{resource}", content);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }
}

public sealed class SmallPackageLimitServerFixture() : FiGetServerFixture(TestDatabase.Sqlite)
{
    protected override void Configure(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSetting("FiGet:Limits:MaxPackageSizeMB", "1");
    }
}

/// <summary>A package over the size limit is refused with 413 before it is stored.</summary>
public sealed class PackageSizeLimitTests(SmallPackageLimitServerFixture server) : IClassFixture<SmallPackageLimitServerFixture>
{
    [Fact]
    public async Task A_package_over_the_size_limit_is_413()
    {
        var id = FiGetServerFixture.UniqueId("Evidence.TooLarge");
        var noise = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(noise);
        using var package = TestPackages.Create(id, "1.0.0", b => b.AddContent("lib/netstandard2.0/noise.bin", noise));
        // The client waits for the go-ahead before sending the body, so the server's early 413 is read rather than lost to a
        // connection it closed mid-upload. HttpClient stops waiting after one second by default and sends the body anyway,
        // which a loaded test run outlasts.
        using var client = new HttpClient(new SocketsHttpHandler { Expect100ContinueTimeout = TimeSpan.FromSeconds(60) }) { BaseAddress = server.BaseAddress };
        client.DefaultRequestHeaders.Add("X-NuGet-ApiKey", FiGetServerFixture.AdminToken);
        client.DefaultRequestHeaders.ExpectContinue = true;
        using var content = new MultipartFormDataContent();
        content.Add(new StreamContent(package) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } }, "package", "package.nupkg");

        HttpAssert.Status(HttpStatusCode.RequestEntityTooLarge, await client.PutAsync("nuget/public/v3/publish", content));
        HttpAssert.Status(HttpStatusCode.NotFound, await client.GetAsync($"nuget/public/v3/flatcontainer/{id.ToLowerInvariant()}/index.json"));
    }
}

/// <summary>With a public base URL set, every URL a protocol answer hands out uses it, not the address the request came in on.</summary>
public sealed class PublicBaseUrlProtocolTests(HttpsPublicAddressServerFixture server) : IClassFixture<HttpsPublicAddressServerFixture>
{
    [Fact]
    public async Task Protocol_answers_use_the_public_base_url()
    {
        using var client = server.CreateClient();
        var index = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync("nuget/public/v3/index.json")))!;
        var ids = index["resources"]!.AsArray().Select(r => (string?)r!["@id"]).ToList();
        Assert.NotEmpty(ids);
        Assert.All(ids, url => Assert.StartsWith("https://packages.example.test/nuget/public", url, StringComparison.Ordinal));

        var service = await HttpAssert.SuccessBodyAsync(await client.GetAsync("nuget/public/"));
        Assert.Contains("https://packages.example.test/nuget/public", service, StringComparison.Ordinal);
        Assert.DoesNotContain(server.BaseAddress.Authority, service, StringComparison.Ordinal);
    }
}

/// <summary>
/// A push whose client authenticates by challenge: the first attempt carries no credentials and a large body, gets 401, and
/// is sent again with Basic credentials. Found cross-checking Gitea's NuGet issues (#21864, #33671; 2026-09-15): past 30 MB,
/// or after 5 seconds, the unread body of the refused attempt had the connection reset, and the client never saw the 401.
/// </summary>
public sealed class ChallengedPushTests(SqliteServerFixture server) : IClassFixture<SqliteServerFixture>
{
    [Fact]
    public async Task A_large_push_answering_a_challenge_succeeds_and_a_refused_one_is_told_why()
    {
        var id = FiGetServerFixture.UniqueId("Evidence.Challenged");
        var noise = new byte[40 * 1024 * 1024];
        Random.Shared.NextBytes(noise);
        var bytes = TestPackages.Create(id, "1.0.0", b => b.AddContent("tools/noise.bin", noise)).ToArray();

        using (var challenged = new HttpClient(new HttpClientHandler { Credentials = new NetworkCredential("ci", FiGetServerFixture.AdminToken), PreAuthenticate = false }) { BaseAddress = server.BaseAddress, Timeout = TimeSpan.FromMinutes(2) })
        {
            foreach (var (path, version) in new[] { ("nuget/public/v3/publish", "1.0.0"), ("nuget/public/", "2.0.0") })
            {
                var body = version == "1.0.0" ? bytes : TestPackages.Create(id, version, b => b.AddContent("tools/noise.bin", noise)).ToArray();
                using var content = new MultipartFormDataContent();
                content.Add(new StreamContent(new MemoryStream(body)) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } }, "package", "package.nupkg");
                using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = content };
                request.Headers.TransferEncodingChunked = true;
                HttpAssert.Status(HttpStatusCode.Created, await challenged.SendAsync(request));
            }
        }

        using var wrongKey = new HttpClient { BaseAddress = server.BaseAddress, Timeout = TimeSpan.FromMinutes(2) };
        wrongKey.DefaultRequestHeaders.Add("X-NuGet-ApiKey", "figet_not_a_real_key");
        using var refusedContent = new MultipartFormDataContent();
        refusedContent.Add(new StreamContent(new MemoryStream(bytes)) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } }, "package", "package.nupkg");
        HttpAssert.Status(HttpStatusCode.Forbidden, await wrongKey.PutAsync("nuget/public/v3/publish", refusedContent));
    }
}
