using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FiGet.Application.Ports;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Web.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

public sealed class SqliteAssetTransferTests(SqliteAssetServerFixture fixture) : AssetTransferTests(fixture), IClassFixture<SqliteAssetServerFixture>;

public sealed class SqlServerAssetTransferTests(SqlServerAssetServerFixture fixture) : AssetTransferTests(fixture), IClassFixture<SqlServerAssetServerFixture>;

/// <summary>
/// The ways a file reaches an asset directory besides one plain upload - in parts, inside an archive, from a
/// URL - and a folder leaving it as an archive. The fixture's limits are one megabyte per file and two per
/// import, so every limit can be crossed with small bodies.
/// </summary>
public abstract partial class AssetTransferTests
{
    private readonly FiGetServerFixture server;

    protected AssetTransferTests(FiGetServerFixture server)
    {
        this.server = server;
        server.SkipIfUnavailable();
    }

    // ── multipart ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_multipart_upload_appears_only_once_completed_and_leaves_no_parts_behind()
    {
        var folder = Unique();
        var bytes = Enumerable.Range(0, 25).Select(i => (byte)i).ToArray();
        var path = $"endpoints/files/content/{folder}/big.iso";
        var id = Guid.NewGuid().ToString("N");
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);

        // Compared with what was there before, not with zero: other tests in this class leave abandoned
        // uploads behind on purpose, and in which order they run is not this test's to decide.
        var unfinishedBefore = UnfinishedUploadCount();

        // In the order the reference client sends: every part but the last at the full part size.
        foreach (var (index, offset, size) in new[] { (0, 0, 10), (1, 10, 10), (2, 20, 5) })
        {
            HttpAssert.Status(HttpStatusCode.OK, await admin.PostAsync(
                $"{path}?id={id}&multipart=upload&index={index}&offset={offset}&totalSize=25&partSize={size}&totalParts=3",
                new ByteArrayContent(bytes[offset..(offset + size)])));

            HttpAssert.Status(HttpStatusCode.NotFound, await admin.GetAsync(path));
        }

        HttpAssert.Status(HttpStatusCode.OK, await admin.PostAsync($"{path}?id={id}&multipart=complete", null));
        Assert.Equal(bytes, await admin.GetByteArrayAsync(path));
        Assert.Equal(unfinishedBefore, UnfinishedUploadCount());
    }

    [Fact]
    public async Task A_multipart_upload_with_a_part_missing_stores_nothing()
    {
        var path = $"endpoints/files/content/{Unique()}/gap.bin";
        var id = Guid.NewGuid().ToString("N");
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);

        HttpAssert.Status(HttpStatusCode.OK, await admin.PostAsync($"{path}?id={id}&multipart=upload&index=0&offset=0&totalSize=20&partSize=10&totalParts=2", new ByteArrayContent(new byte[10])));
        HttpAssert.Status(HttpStatusCode.BadRequest, await admin.PostAsync($"{path}?id={id}&multipart=complete", null));
        HttpAssert.Status(HttpStatusCode.NotFound, await admin.GetAsync(path));

        // The part is kept for a retry that sends what was missing.
        HttpAssert.Status(HttpStatusCode.OK, await admin.PostAsync($"{path}?id={id}&multipart=upload&index=1&offset=10&totalSize=20&partSize=10&totalParts=2", new ByteArrayContent(new byte[10])));
        HttpAssert.Status(HttpStatusCode.OK, await admin.PostAsync($"{path}?id={id}&multipart=complete", null));
        Assert.Equal(20, (await admin.GetByteArrayAsync(path)).Length);
    }

    [Fact]
    public async Task A_part_that_is_not_the_size_it_claims_or_a_total_over_the_limit_is_refused()
    {
        var path = $"endpoints/files/content/{Unique()}/wrong.bin";
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);

        HttpAssert.Status(HttpStatusCode.BadRequest, await admin.PostAsync($"{path}?id=a&multipart=upload&index=0&offset=0&totalSize=20&partSize=10&totalParts=2", new ByteArrayContent(new byte[11])));
        HttpAssert.Status(HttpStatusCode.BadRequest, await admin.PostAsync($"{path}?id=b&multipart=upload&index=2&offset=0&totalSize=20&partSize=10&totalParts=2", new ByteArrayContent(new byte[10])));

        // Refused on the first part, before anyone sends the rest of a file that could never be stored.
        HttpAssert.Status(HttpStatusCode.RequestEntityTooLarge, await admin.PostAsync($"{path}?id=c&multipart=upload&index=0&offset=0&totalSize={(2 * 1024 * 1024) + 1}&partSize=10&totalParts=2", new ByteArrayContent(new byte[10])));
    }

    [Fact]
    public async Task Abandoned_uploads_are_swept_and_active_ones_are_not()
    {
        var path = $"endpoints/files/content/{Unique()}/abandoned.bin";
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.OK, await admin.PostAsync($"{path}?id={Guid.NewGuid():N}&multipart=upload&index=0&offset=0&totalSize=20&partSize=10&totalParts=2", new ByteArrayContent(new byte[10])));
        var storage = server.Services.GetRequiredService<IAssetStorage>();

        Assert.Equal(0, await storage.PruneUploadsAsync(DateTime.UtcNow.AddHours(-1), CancellationToken.None));
        Assert.True(UnfinishedUploadCount() > 0);

        Assert.True(await storage.PruneUploadsAsync(DateTime.UtcNow.AddMinutes(1), CancellationToken.None) > 0);
        Assert.Equal(0, UnfinishedUploadCount());
    }

    // ── archives ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_zip_imports_with_its_folders_and_refuses_an_entry_that_climbs_out()
    {
        var folder = Unique();
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        var zip = Zip(("readme.txt", "top"), ("tools/setup.exe", "setup"), ("windows\\made\\on.txt", "backslashes"), ("../escape.txt", "no"), ("empty/", null));

        using var response = await admin.PostAsync($"endpoints/files/import/{folder}?format=zip", new ByteArrayContent(zip));
        var result = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(response))!;

        Assert.Equal(3, (int?)result["imported"]);
        Assert.Contains("../escape.txt", (string?)Assert.Single(result["failed"]!.AsArray()), StringComparison.Ordinal);
        Assert.Equal("setup", await admin.GetStringAsync($"endpoints/files/content/{folder}/tools/setup.exe"));
        Assert.Equal("backslashes", await admin.GetStringAsync($"endpoints/files/content/{folder}/windows/made/on.txt"));
        var top = JsonNode.Parse(await admin.GetStringAsync($"endpoints/files/dir/{folder}"))!.AsArray().Select(i => (string?)i!["name"]).ToList();
        Assert.Equal(["empty", "tools", "windows", "readme.txt"], top);
        HttpAssert.Status(HttpStatusCode.NotFound, await admin.GetAsync("endpoints/files/content/escape.txt"));
    }

    [Fact]
    public async Task An_import_skips_existing_files_unless_told_to_overwrite()
    {
        var folder = Unique();
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync($"endpoints/files/content/{folder}/keep.txt", new StringContent("original")));

        var first = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await admin.PostAsync($"endpoints/files/import/{folder}?format=zip", new ByteArrayContent(Zip(("keep.txt", "from archive"), ("new.txt", "new"))))))!;
        Assert.Equal(1, (int?)first["imported"]);
        Assert.Equal(1, (int?)first["skipped"]);
        Assert.Equal("original", await admin.GetStringAsync($"endpoints/files/content/{folder}/keep.txt"));

        await HttpAssert.SuccessBodyAsync(await admin.PostAsync($"endpoints/files/import/{folder}?format=zip&overwrite=true", new ByteArrayContent(Zip(("keep.txt", "from archive")))));
        Assert.Equal("from archive", await admin.GetStringAsync($"endpoints/files/content/{folder}/keep.txt"));
    }

    /// <summary>
    /// The limit is on bytes actually unpacked, not on what an archive says about itself, so a small archive
    /// of zeros that unpacks past it is stopped - and what came before the limit stays.
    /// </summary>
    [Fact]
    public async Task An_archive_that_unpacks_past_the_import_limit_is_stopped()
    {
        var folder = Unique();
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        var zeros = new string('\0', 900 * 1024);
        var zip = Zip(("a.bin", zeros), ("b.bin", zeros), ("c.bin", zeros));
        Assert.True(zip.Length < 100 * 1024, "The archive should be small; it is its content that is large.");

        using var response = await admin.PostAsync($"endpoints/files/import/{folder}?format=zip", new ByteArrayContent(zip));

        HttpAssert.Status(HttpStatusCode.RequestEntityTooLarge, response);
        Assert.Equal(2, (int?)JsonNode.Parse(await response.Content.ReadAsStringAsync())!["imported"]);
    }

    [Fact]
    public async Task A_folder_exported_as_tgz_imports_back_identically()
    {
        var source = Unique();
        var copy = Unique();
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync($"endpoints/files/content/{source}/one.txt", new StringContent("1")));
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync($"endpoints/files/content/{source}/deep/er/two.bin", new ByteArrayContent([2, 2])));

        using var export = await admin.GetAsync($"endpoints/files/export/{source}?format=tgz&recursive=true");
        HttpAssert.Status(HttpStatusCode.OK, export);
        Assert.Equal("application/gzip", export.Content.Headers.ContentType?.MediaType);
        var archive = await export.Content.ReadAsByteArrayAsync();

        await HttpAssert.SuccessBodyAsync(await admin.PostAsync($"endpoints/files/import/{copy}?format=tgz", new ByteArrayContent(archive)));

        Assert.Equal("1", await admin.GetStringAsync($"endpoints/files/content/{copy}/one.txt"));
        Assert.Equal([2, 2], await admin.GetByteArrayAsync($"endpoints/files/content/{copy}/deep/er/two.bin"));
        var hashes = async (string folder) => JsonNode.Parse(await admin.GetStringAsync($"endpoints/files/dir/{folder}?recursive=true"))!.AsArray()
            .Select(i => $"{(string?)i!["name"]}:{(string?)i["sha256"]}").ToList();
        Assert.Equal(await hashes(source), await hashes(copy));
    }

    [Fact]
    public async Task A_zip_export_holds_only_the_top_files_unless_recursive()
    {
        var folder = Unique();
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync($"endpoints/files/content/{folder}/top.txt", new StringContent("t")));
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync($"endpoints/files/content/{folder}/sub/inner.txt", new StringContent("i")));

        Assert.Equal(["top.txt"], await ZipEntriesAsync(admin, $"endpoints/files/export/{folder}?format=zip"));
        Assert.Equal(["sub/", "sub/inner.txt", "top.txt"], await ZipEntriesAsync(admin, $"endpoints/files/export/{folder}?format=zip&recursive=true"));

        HttpAssert.Status(HttpStatusCode.NotFound, await admin.GetAsync($"endpoints/files/export/{Unique()}?format=zip"));
        HttpAssert.Status(HttpStatusCode.BadRequest, await admin.GetAsync($"endpoints/files/export/{folder}?format=rar"));
    }

    [Fact]
    public async Task Importing_needs_a_token_and_exporting_follows_read_access()
    {
        using var anonymous = server.CreateClient();
        HttpAssert.Status(HttpStatusCode.Unauthorized, await anonymous.PostAsync($"endpoints/files/import/{Unique()}?format=zip", new ByteArrayContent(Zip(("x.txt", "x")))));
        HttpAssert.Status(HttpStatusCode.OK, await anonymous.GetAsync("endpoints/files/export/?format=zip"));
        HttpAssert.Status(HttpStatusCode.Unauthorized, await anonymous.GetAsync("endpoints/vault/export/?format=zip"));
    }

    // ── fetching from a URL ─────────────────────────────────────────────────────

    /// <summary>
    /// By default the server does not fetch from its own host or a private network: an upload token must not
    /// become a way to read what only the server can reach. The URL here is this very server.
    /// </summary>
    [Fact]
    public async Task Fetching_from_a_local_address_is_refused_by_default()
    {
        var folder = Unique();
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync($"endpoints/files/content/{folder}/source.txt", new StringContent("secret-ish")));

        using var request = new HttpRequestMessage(HttpMethod.Put, $"endpoints/files/content/{folder}/copy.txt");
        request.Headers.Add("X-Source-Url", new Uri(server.BaseAddress, $"endpoints/files/content/{folder}/source.txt").ToString());
        using var response = await admin.SendAsync(request);

        HttpAssert.Status(HttpStatusCode.BadRequest, response);
        Assert.Contains("does not fetch from", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        HttpAssert.Status(HttpStatusCode.NotFound, await admin.GetAsync($"endpoints/files/content/{folder}/copy.txt"));
    }

    // ── the page ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_admin_imports_exports_and_is_told_why_a_fetch_was_refused_from_the_page()
    {
        var folder = Unique();
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.Created, await admin.PostAsync($"endpoints/files/dir/{folder}", null));

        using var browser = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(browser));
        var page = await HttpAssert.SuccessBodyAsync(await browser.GetAsync($"assets/files?path={folder}"));
        var token = WebUtility.HtmlDecode(UploadToken().Match(page).Groups["value"].Value);

        using (var import = new HttpRequestMessage(HttpMethod.Post, $"admin/assets/files/import?format=zip&path={folder}") { Content = new ByteArrayContent(Zip(("page.txt", "from the page"))) })
        {
            import.Headers.Add("RequestVerificationToken", token);
            Assert.Equal(1, (int?)JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await browser.SendAsync(import)))!["imported"]);
        }

        Assert.Equal(["page.txt"], await ZipEntriesAsync(browser, $"admin/assets/files/export?format=zip&path={folder}"));

        var fetchForm = FormElement().Matches(await HttpAssert.SuccessBodyAsync(await browser.GetAsync($"assets/files?path={folder}")))
            .Single(f => f.Value.Contains("/fetch\"", StringComparison.Ordinal)).Value;
        var fields = HiddenInput().Matches(fetchForm).ToDictionary(m => WebUtility.HtmlDecode(m.Groups["name"].Value), m => WebUtility.HtmlDecode(m.Groups["value"].Value));
        fields["url"] = new Uri(server.BaseAddress, $"endpoints/files/content/{folder}/page.txt").ToString();
        using var fetch = await browser.PostAsync("admin/assets/files/fetch", new FormUrlEncodedContent(fields));

        HttpAssert.Status(HttpStatusCode.Redirect, fetch);
        var back = fetch.Headers.Location!.ToString();
        Assert.Contains("fetch=refused", back, StringComparison.Ordinal);
        Assert.Contains("points at a private or local address", await HttpAssert.SuccessBodyAsync(await browser.GetAsync(back)), StringComparison.Ordinal);
    }

    private static byte[] Zip(params (string Name, string? Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                if (content is not null)
                {
                    using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                    writer.Write(content);
                }
            }
        }

        return buffer.ToArray();
    }

    private static async Task<List<string>> ZipEntriesAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        HttpAssert.Status(HttpStatusCode.OK, response);
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        return [.. zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal)];
    }

    private int UnfinishedUploadCount()
    {
        var root = Path.Combine(server.Services.GetRequiredService<StoragePaths>().Root, "files", "asset-uploads", "files");
        return Directory.Exists(root) ? Directory.EnumerateDirectories(root).Count() : 0;
    }

    private static string Unique() => "x" + Guid.NewGuid().ToString("N")[..10];

    private HttpClient CreateBrowser() =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = server.BaseAddress };

    [GeneratedRegex("<input[^>]*type=\"hidden\"[^>]*name=\"(?<name>[^\"]+)\"[^>]*value=\"(?<value>[^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex HiddenInput();


    [GeneratedRegex("data-asset-upload.*?name=\"__RequestVerificationToken\"[^>]*value=\"(?<value>[^\"]+)\"", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex UploadToken();

    [GeneratedRegex("<form[^>]*>.*?</form>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex FormElement();
}

/// <summary>A server that may fetch from private networks, so fetching can be tested against itself.</summary>
public sealed class AssetFetchServerFixture() : FiGetServerFixture(TestDatabase.Sqlite)
{
    protected override void Configure(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSetting("FiGet:Feeds:3:Name", "files");
        builder.UseSetting("FiGet:Feeds:3:Kind", "Assets");
        builder.UseSetting("FiGet:Feeds:3:AnonymousRead", "true");
        builder.UseSetting("FiGet:Limits:MaxAssetSizeMB", "1");
        builder.UseSetting("FiGet:Assets:RemoteFetch:AllowPrivateNetworks", "true");
    }
}

/// <summary>Fetching by URL where private networks are allowed, with this server playing the remote one.</summary>
public sealed class AssetFetchTests(AssetFetchServerFixture server) : IClassFixture<AssetFetchServerFixture>
{
    [Fact]
    public async Task A_file_is_fetched_once_and_stored_with_its_own_hashes()
    {
        var folder = "f" + Guid.NewGuid().ToString("N")[..10];
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        var bytes = Encoding.UTF8.GetBytes("vendor installer stand-in");
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync($"endpoints/files/content/{folder}/vendor/setup.msi", new ByteArrayContent(bytes)));

        using var request = new HttpRequestMessage(HttpMethod.Put, $"endpoints/files/content/{folder}/pinned/setup.msi");
        request.Headers.Add("X-Source-Url", new Uri(server.BaseAddress, $"endpoints/files/content/{folder}/vendor/setup.msi").ToString());
        HttpAssert.Status(HttpStatusCode.Created, await admin.SendAsync(request));

        Assert.Equal(bytes, await admin.GetByteArrayAsync($"endpoints/files/content/{folder}/pinned/setup.msi"));
        var item = JsonNode.Parse(await admin.GetStringAsync($"endpoints/files/metadata/{folder}/pinned/setup.msi"))!;
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), (string?)item["sha256"]);
    }

    [Fact]
    public async Task A_remote_error_is_a_bad_gateway_and_stores_nothing()
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        using var request = new HttpRequestMessage(HttpMethod.Put, "endpoints/files/content/remote-error/nothing.bin");
        request.Headers.Add("X-Source-Url", new Uri(server.BaseAddress, "endpoints/files/content/does/not/exist.bin").ToString());

        using var response = await admin.SendAsync(request);
        HttpAssert.Status(HttpStatusCode.BadGateway, response);
        Assert.Contains("404", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        HttpAssert.Status(HttpStatusCode.NotFound, await admin.GetAsync("endpoints/files/content/remote-error/nothing.bin"));
    }

    /// <summary>
    /// Allowing private networks does not open the metadata service: that address is where a cloud instance
    /// hands out its credentials, and nothing an asset directory needs lives there.
    /// </summary>
    [Fact]
    public async Task The_cloud_metadata_address_is_refused_even_where_private_networks_are_allowed()
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        foreach (var url in new[] { "http://169.254.169.254/latest/meta-data/", "http://[64:ff9b::a9fe:a9fe]/", "file:///etc/passwd" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, "endpoints/files/content/metadata/stolen.txt");
            request.Headers.Add("X-Source-Url", url);
            HttpAssert.Status(HttpStatusCode.BadRequest, await admin.SendAsync(request));
        }

        HttpAssert.Status(HttpStatusCode.NotFound, await admin.GetAsync("endpoints/files/content/metadata/stolen.txt"));
    }
}
