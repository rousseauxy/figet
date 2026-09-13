using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Web.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

/// <summary>
/// Two asset directories on top of the usual feeds: <c>files</c> reads anonymously, <c>vault</c> needs a
/// token. The asset limit is lowered to one megabyte so the size refusal can be tested without a gigabyte.
/// </summary>
public abstract class AssetServerFixture(TestDatabase database) : FiGetServerFixture(database)
{
    protected override void Configure(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSetting("FiGet:Feeds:3:Name", "files");
        builder.UseSetting("FiGet:Feeds:3:Kind", "Assets");
        builder.UseSetting("FiGet:Feeds:3:AnonymousRead", "true");
        builder.UseSetting("FiGet:Feeds:4:Name", "vault");
        builder.UseSetting("FiGet:Feeds:4:Kind", "Assets");
        builder.UseSetting("FiGet:Limits:MaxAssetSizeMB", "1");
        builder.UseSetting("FiGet:Limits:MaxImportSizeMB", "2");
    }
}

public sealed class SqliteAssetServerFixture() : AssetServerFixture(TestDatabase.Sqlite);

public sealed class SqlServerAssetServerFixture() : AssetServerFixture(TestDatabase.SqlServer);

public sealed class SqliteAssetDirectoryTests(SqliteAssetServerFixture fixture) : AssetDirectoryTests(fixture), IClassFixture<SqliteAssetServerFixture>;

public sealed class SqlServerAssetDirectoryTests(SqlServerAssetServerFixture fixture) : AssetDirectoryTests(fixture), IClassFixture<SqlServerAssetServerFixture>;

/// <summary>
/// The asset directory API, against the shapes docs/protocol-assets.md records from the server being
/// replaced: a missing file's exact body, PUT that never replaces, listings that answer an empty array for a
/// folder that is not there.
/// </summary>
public abstract partial class AssetDirectoryTests
{
    private readonly FiGetServerFixture server;

    protected AssetDirectoryTests(FiGetServerFixture server)
    {
        this.server = server;
        server.SkipIfUnavailable();
    }

    [Fact]
    public async Task A_stored_file_downloads_byte_for_byte_with_its_hash_as_etag()
    {
        var folder = Unique();
        var bytes = Encoding.UTF8.GetBytes("installer stand-in");
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);

        // curl's --data-binary labels a body as a web form; the extension has to win over that.
        using var body = new ByteArrayContent(bytes);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync($"endpoints/files/content/{folder}/readme.txt", body));

        using var anonymous = server.CreateClient();
        using var response = await anonymous.GetAsync($"endpoints/files/content/{folder}/readme.txt");
        HttpAssert.Status(HttpStatusCode.OK, response);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"\"{Convert.ToHexStringLower(SHA256.HashData(bytes))}\"", response.Headers.ETag?.Tag);
        Assert.Equal("bytes", response.Headers.AcceptRanges.Single());
    }

    /// <summary>
    /// Found fetching a package from the gallery's CDN, which labels files <c>binary/octet-stream</c>: that
    /// says as little as <c>application/octet-stream</c> does, so the extension decides for both.
    /// </summary>
    [Theory]
    [InlineData("application/octet-stream")]
    [InlineData("binary/octet-stream")]
    public async Task A_content_type_that_says_nothing_lets_the_extension_decide(string sent)
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        using var body = new ByteArrayContent([1, 2, 3]);
        body.Headers.ContentType = new MediaTypeHeaderValue(sent);
        var path = $"endpoints/files/content/{Unique()}/bundle.zip";
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync(path, body));

        using var response = await admin.GetAsync(path);
        Assert.Equal("application/x-zip-compressed", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Put_never_replaces_post_does_and_patch_needs_something_to_replace()
    {
        var path = $"endpoints/files/content/{Unique()}/tool.bin";
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);

        HttpAssert.Status(HttpStatusCode.NotFound, await admin.PatchAsync(path, new ByteArrayContent([9])));
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync(path, new ByteArrayContent([1])));
        HttpAssert.Status(HttpStatusCode.Conflict, await admin.PutAsync(path, new ByteArrayContent([2])));
        Assert.Equal([1], await admin.GetByteArrayAsync(path));

        HttpAssert.Status(HttpStatusCode.Created, await admin.PostAsync(path, new ByteArrayContent([3])));
        Assert.Equal([3], await admin.GetByteArrayAsync(path));

        HttpAssert.Status(HttpStatusCode.Created, await admin.PatchAsync(path, new ByteArrayContent([4])));
        Assert.Equal([4], await admin.GetByteArrayAsync(path));
    }

    /// <summary>A resumed <c>win_get_url</c> or <c>Invoke-WebRequest</c> asks for the rest of a file, not all of it.</summary>
    [Fact]
    public async Task A_range_request_gets_only_that_range()
    {
        var path = $"endpoints/files/content/{Unique()}/data.bin";
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync(path, new ByteArrayContent([0, 1, 2, 3, 4, 5, 6, 7, 8, 9])));

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Range = new RangeHeaderValue(4, 6);
        using var response = await admin.SendAsync(request);

        HttpAssert.Status(HttpStatusCode.PartialContent, response);
        Assert.Equal([4, 5, 6], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Head_answers_the_headers_of_a_file_without_its_body()
    {
        var path = $"endpoints/files/content/{Unique()}/setup.exe";
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync(path, new ByteArrayContent(new byte[1234])));

        using var response = await admin.SendAsync(new HttpRequestMessage(HttpMethod.Head, path));
        HttpAssert.Status(HttpStatusCode.OK, response);
        Assert.Equal(1234, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary>Recorded from the reference server: plain text, and this exact sentence.</summary>
    [Fact]
    public async Task A_missing_file_is_a_plain_text_404()
    {
        using var client = server.CreateClient();
        using var response = await client.GetAsync($"endpoints/files/content/{Unique()}/nothing.txt");

        HttpAssert.Status(HttpStatusCode.NotFound, response);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("The specified asset was not found.", await response.Content.ReadAsStringAsync());

        using var metadata = await client.GetAsync($"endpoints/files/metadata/{Unique()}/nothing.txt");
        HttpAssert.Status(HttpStatusCode.NotFound, metadata);
        Assert.Equal("Asset not found.", await metadata.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_listing_describes_files_and_the_folders_an_upload_created()
    {
        var folder = Unique();
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync($"endpoints/files/content/{folder}/win/app.zip", new ByteArrayContent([1, 2, 3])));

        var top = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await admin.GetAsync($"endpoints/files/dir/{folder}")))!.AsArray();
        var win = Assert.Single(top)!;
        Assert.Equal("win", (string?)win["name"]);
        Assert.Equal("dir", (string?)win["type"]);
        Assert.Equal(folder, (string?)win["parent"]);
        Assert.Null(win["content"]);

        var inner = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await admin.GetAsync($"endpoints/files/dir/{folder}/win/")))!.AsArray();
        var file = Assert.Single(inner)!;
        Assert.Equal("app.zip", (string?)file["name"]);
        Assert.Equal($"{folder}/win", (string?)file["parent"]);
        Assert.Equal(3, (long?)file["size"]);
        Assert.Equal("application/x-zip-compressed", (string?)file["type"]);
        Assert.EndsWith($"/endpoints/files/content/{folder}/win/app.zip", (string?)file["content"], StringComparison.Ordinal);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData([1, 2, 3])), (string?)file["sha256"]);
        Assert.Equal(Convert.ToHexStringLower(SHA512.HashData([1, 2, 3])), (string?)file["sha512"]);
        Assert.NotNull(file["md5"]);
        Assert.NotNull(file["sha1"]);

        var recursive = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await admin.GetAsync($"endpoints/files/dir/{folder}?recursive=true")))!.AsArray();
        Assert.Equal(["win", "app.zip"], recursive.Select(i => (string?)i!["name"]));
    }

    /// <summary>
    /// Recorded from the reference server, with and without the trailing slash: a folder that is not there
    /// lists as an empty array, never a 404. A script that lists before it uploads depends on that.
    /// </summary>
    [Fact]
    public async Task A_folder_that_does_not_exist_lists_as_an_empty_array()
    {
        using var client = server.CreateClient();
        foreach (var path in new[] { $"endpoints/files/dir/{Unique()}", "endpoints/vault/dir" })
        {
            using var authorised = server.CreateClient(FiGetServerFixture.AdminToken);
            using var response = await authorised.GetAsync(path);
            Assert.Equal("[]", await HttpAssert.SuccessBodyAsync(response));
        }
    }

    /// <summary>
    /// A wildcard in a folder name must stay a character. Recursive listing is a prefix match in SQL, and
    /// an unescaped <c>%</c> would pull in a sibling folder it merely resembles.
    /// </summary>
    [Fact]
    public async Task A_recursive_listing_takes_a_percent_in_a_folder_name_literally()
    {
        var folder = Unique();
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync($"endpoints/files/content/{folder}/50%25/inside.txt", new ByteArrayContent([1])));
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync($"endpoints/files/content/{folder}/50x/outside.txt", new ByteArrayContent([1])));

        var listed = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await admin.GetAsync($"endpoints/files/dir/{folder}/50%25?recursive=true")))!.AsArray();
        Assert.Equal(["inside.txt"], listed.Select(i => (string?)i!["name"]));
    }

    /// <summary>
    /// Found by running the reference client: it asks for <c>dir/{path}?recursive=false)</c>, parenthesis and
    /// all. A strict boolean binding answered every listing it made with 400.
    /// </summary>
    [Fact]
    public async Task A_listing_accepts_the_reference_clients_malformed_recursive_flag()
    {
        var folder = Unique();
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync($"endpoints/files/content/{folder}/sub/x.txt", new ByteArrayContent([1])));

        var shallow = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await admin.GetAsync($"endpoints/files/dir/{folder}?recursive=false)")))!.AsArray();
        Assert.Equal(["sub"], shallow.Select(i => (string?)i!["name"]));
        var deep = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await admin.GetAsync($"endpoints/files/dir/{folder}?recursive=true)")))!.AsArray();
        Assert.Equal(2, deep.Count);
    }

    [Fact]
    public async Task Paths_are_case_insensitive_and_keep_the_case_they_were_written_with()
    {
        var folder = Unique();
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync($"endpoints/files/content/{folder}/Tools/Setup.EXE", new ByteArrayContent([7])));

        Assert.Equal([7], await admin.GetByteArrayAsync($"endpoints/files/content/{folder.ToUpperInvariant()}/tools/setup.exe"));
        var listed = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await admin.GetAsync($"endpoints/files/dir/{folder}/TOOLS")))!.AsArray();
        Assert.Equal("Setup.EXE", (string?)Assert.Single(listed)!["name"]);
    }

    /// <summary>
    /// Anonymous read is a read setting. Writing always needs a token, and a token needs the right scope,
    /// whatever the directory lets strangers see.
    /// </summary>
    [Fact]
    public async Task Writing_needs_a_token_with_the_scope_even_where_reading_is_anonymous()
    {
        var path = $"endpoints/files/content/{Unique()}/x.txt";
        using var anonymous = server.CreateClient();
        HttpAssert.Status(HttpStatusCode.Unauthorized, await anonymous.PutAsync(path, new ByteArrayContent([1])));
        HttpAssert.Status(HttpStatusCode.Unauthorized, await anonymous.DeleteAsync(path));
        HttpAssert.Status(HttpStatusCode.Unauthorized, await anonymous.PostAsync($"endpoints/files/dir/{Unique()}", null));

        var reader = await CreateTokenAsync(FiGet.Domain.Entities.TokenScopes.Read);
        using var readOnly = server.CreateClient(reader);
        HttpAssert.Status(HttpStatusCode.Forbidden, await readOnly.PutAsync(path, new ByteArrayContent([1])));

        HttpAssert.Status(HttpStatusCode.Unauthorized, await anonymous.GetAsync("endpoints/vault/dir/"));
        HttpAssert.Status(HttpStatusCode.OK, await readOnly.GetAsync("endpoints/vault/dir/"));
    }

    /// <summary>Each surface sees only its own kind of feed, so neither answers as if the other were an empty one.</summary>
    [Fact]
    public async Task Package_endpoints_and_asset_endpoints_do_not_see_each_others_feeds()
    {
        using var client = server.CreateClient(FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.NotFound, await client.GetAsync("nuget/files/v3/index.json"));
        HttpAssert.Status(HttpStatusCode.NotFound, await client.GetAsync("nuget/files/FindPackagesById()?id='x'"));
        HttpAssert.Status(HttpStatusCode.NotFound, await client.GetAsync("endpoints/public/dir/"));
        HttpAssert.Status(HttpStatusCode.NotFound, await client.PutAsync("endpoints/public/content/x.txt", new ByteArrayContent([1])));
    }

    [Fact]
    public async Task Deleting_a_file_is_idempotent_and_removes_its_bytes()
    {
        var folder = Unique();
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync($"endpoints/files/content/{folder}/gone.txt", new ByteArrayContent([1, 2])));
        var before = StoredBlobCount();

        HttpAssert.Status(HttpStatusCode.OK, await admin.DeleteAsync($"endpoints/files/content/{folder}/gone.txt"));
        HttpAssert.Status(HttpStatusCode.NotFound, await admin.GetAsync($"endpoints/files/content/{folder}/gone.txt"));
        Assert.Equal(before - 1, StoredBlobCount());

        // Documented as not an error: what the caller wanted gone is gone.
        HttpAssert.Status(HttpStatusCode.OK, await admin.DeleteAsync($"endpoints/files/content/{folder}/gone.txt"));
    }

    [Fact]
    public async Task A_full_folder_is_only_deleted_when_asked_to_recurse()
    {
        var folder = Unique();
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync($"endpoints/files/content/{folder}/a/one.txt", new ByteArrayContent([1])));
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync($"endpoints/files/content/{folder}/a/b/two.txt", new ByteArrayContent([2])));
        var before = StoredBlobCount();

        HttpAssert.Status(HttpStatusCode.BadRequest, await admin.PostAsync($"endpoints/files/delete/{folder}/a", null));
        Assert.Equal([1], await admin.GetByteArrayAsync($"endpoints/files/content/{folder}/a/one.txt"));

        HttpAssert.Status(HttpStatusCode.OK, await admin.PostAsync($"endpoints/files/delete/{folder}/a?recursive=true", null));
        Assert.Equal("[]", await HttpAssert.SuccessBodyAsync(await admin.GetAsync($"endpoints/files/dir/{folder}?recursive=true")));
        Assert.Equal(before - 2, StoredBlobCount());
    }

    [Fact]
    public async Task A_folder_can_be_created_empty_and_creating_it_again_is_not_an_error()
    {
        var folder = Unique();
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.Created, await admin.PostAsync($"endpoints/files/dir/{folder}/empty", null));
        HttpAssert.Status(HttpStatusCode.Created, await admin.PostAsync($"endpoints/files/dir/{folder}/empty", null));

        var listed = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await admin.GetAsync($"endpoints/files/dir/{folder}")))!.AsArray();
        Assert.Equal("dir", (string?)Assert.Single(listed)!["type"]);

        // An empty folder goes without being told to recurse.
        HttpAssert.Status(HttpStatusCode.OK, await admin.PostAsync($"endpoints/files/delete/{folder}/empty", null));
    }

    [Fact]
    public async Task A_file_cannot_have_something_inside_it()
    {
        var folder = Unique();
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync($"endpoints/files/content/{folder}/plain.txt", new ByteArrayContent([1])));

        HttpAssert.Status(HttpStatusCode.BadRequest, await admin.PutAsync($"endpoints/files/content/{folder}/plain.txt/inner.txt", new ByteArrayContent([1])));
        HttpAssert.Status(HttpStatusCode.BadRequest, await admin.PostAsync($"endpoints/files/dir/{folder}/plain.txt/sub", null));
        HttpAssert.Status(HttpStatusCode.BadRequest, await admin.PostAsync($"endpoints/files/content/{folder}", new ByteArrayContent([1])));
    }

    [Fact]
    public async Task Metadata_sets_the_content_type_custom_fields_and_a_cache_lifetime()
    {
        var path = $"{Unique()}/config.dat";
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync($"endpoints/files/content/{path}", new ByteArrayContent([1])));

        using var update = new StringContent(
            """{"type":"application/json","userMetadataUpdateMode":"update","userMetadata":{"owner":"platform","shown":{"value":"yes","includeInResponseHeader":true}},"cacheHeader":{"type":"ttl","value":60}}""",
            Encoding.UTF8,
            "application/json");
        HttpAssert.Status(HttpStatusCode.OK, await admin.PostAsync($"endpoints/files/metadata/{path}", update));

        var item = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await admin.GetAsync($"endpoints/files/metadata/{path}")))!;
        Assert.Equal("application/json", (string?)item["type"]);
        // The reference client's own shapes, both ways: a plain string, and an object only when the value is
        // also meant for a response header. Its reader expects exactly that mix back.
        Assert.Equal("platform", (string?)item["userMetadata"]!["owner"]);
        Assert.Equal("yes", (string?)item["userMetadata"]!["shown"]!["value"]);
        Assert.True((bool?)item["userMetadata"]!["shown"]!["includeInResponseHeader"]);
        Assert.Equal("ttl", (string?)item["cacheHeader"]!["type"]);
        Assert.Equal("60", (string?)item["cacheHeader"]!["value"]);

        using var download = await admin.GetAsync($"endpoints/files/content/{path}");
        Assert.Equal("application/json", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(TimeSpan.FromSeconds(60), download.Headers.CacheControl?.MaxAge);
    }

    [Fact]
    public async Task A_file_over_the_limit_is_refused_and_leaves_nothing_behind()
    {
        var folder = Unique();
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        var before = StoredBlobCount();

        using var response = await admin.PutAsync($"endpoints/files/content/{folder}/big.iso", new ByteArrayContent(new byte[(1024 * 1024) + 1]));

        HttpAssert.Status(HttpStatusCode.RequestEntityTooLarge, response);
        Assert.Equal(before, StoredBlobCount());
        Assert.Equal("[]", await HttpAssert.SuccessBodyAsync(await admin.GetAsync($"endpoints/files/dir/{folder}")));
    }

    // ── the browse page and its upload ──────────────────────────────────────────

    [Fact]
    public async Task The_browse_page_lists_files_for_a_reader_without_offering_to_upload()
    {
        var folder = Unique();
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync($"endpoints/files/content/{folder}/listed.msi", new ByteArrayContent([1])));

        using var anonymous = server.CreateClient();
        var page = await HttpAssert.SuccessBodyAsync(await anonymous.GetAsync($"assets/files?path={folder}"));

        Assert.Contains($"/endpoints/files/content/{folder}/listed.msi", page, StringComparison.Ordinal);
        Assert.DoesNotContain("data-asset-upload", page, StringComparison.Ordinal);

        // A directory that needs a token is not confirmed to exist to a stranger.
        HttpAssert.Status(HttpStatusCode.NotFound, await anonymous.GetAsync("assets/vault"));
    }

    /// <summary>
    /// Asset directories have their own list and their own address; the feed list and the feed pages are
    /// package feeds only, the same split the protocol endpoints make.
    /// </summary>
    [Fact]
    public async Task Asset_directories_are_listed_under_assets_and_not_among_the_feeds()
    {
        using var anonymous = server.CreateClient();
        var assets = await HttpAssert.SuccessBodyAsync(await anonymous.GetAsync("assets"));
        Assert.Contains("href=\"/assets/files\"", assets, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/assets/public\"", assets, StringComparison.Ordinal);

        var feeds = await HttpAssert.SuccessBodyAsync(await anonymous.GetAsync("/"));
        Assert.Contains("href=\"/feeds/public\"", feeds, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/feeds/files\"", feeds, StringComparison.Ordinal);

        HttpAssert.Status(HttpStatusCode.NotFound, await anonymous.GetAsync("feeds/files"));

        HttpAssert.Status(HttpStatusCode.NotFound, await anonymous.GetAsync("assets/public"));
    }

    [Fact]
    public async Task An_admin_uploads_from_the_page_and_replaces_only_when_asked()
    {
        var folder = Unique();
        using var browser = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await SignInAsync(browser, FiGetServerFixture.AdminToken));

        // The API itself takes tokens only. A cookie is sent by the browser on its own, from any page.
        HttpAssert.Status(HttpStatusCode.Unauthorized, await browser.PostAsync($"endpoints/files/dir/{folder}", null));
        using (var admin = server.CreateClient(FiGetServerFixture.AdminToken))
        {
            HttpAssert.Status(HttpStatusCode.Created, await admin.PostAsync($"endpoints/files/dir/{folder}", null));
        }

        var page = await HttpAssert.SuccessBodyAsync(await browser.GetAsync($"assets/files?path={folder}"));
        Assert.Contains("data-asset-upload", page, StringComparison.Ordinal);
        var token = UploadToken().Match(page);
        Assert.True(token.Success, "The drop zone carries no antiforgery token.");

        async Task<HttpResponseMessage> UploadAsync(byte[] bytes, bool overwrite, bool withToken = true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"admin/assets/files/upload?path={Uri.EscapeDataString(folder + "/dropped.txt")}{(overwrite ? "&overwrite=true" : "")}")
            {
                Content = new ByteArrayContent(bytes),
            };
            if (withToken)
            {
                request.Headers.Add("RequestVerificationToken", WebUtility.HtmlDecode(token.Groups["value"].Value));
            }

            return await browser.SendAsync(request);
        }

        HttpAssert.Status(HttpStatusCode.BadRequest, await UploadAsync([0], overwrite: false, withToken: false));
        HttpAssert.Status(HttpStatusCode.Created, await UploadAsync([1], overwrite: false));
        HttpAssert.Status(HttpStatusCode.Conflict, await UploadAsync([2], overwrite: false));

        using var reader = server.CreateClient();
        Assert.Equal([1], await reader.GetByteArrayAsync($"endpoints/files/content/{folder}/dropped.txt"));

        HttpAssert.Status(HttpStatusCode.Created, await UploadAsync([3], overwrite: true));
        Assert.Equal([3], await reader.GetByteArrayAsync($"endpoints/files/content/{folder}/dropped.txt"));
    }

    /// <summary>
    /// The admin buttons are plain form posts, which a browser will send from any page the admin has open.
    /// The antiforgery token is what makes such a post come from this server's own page.
    /// </summary>
    [Fact]
    public async Task An_admin_form_post_without_its_antiforgery_token_changes_nothing()
    {
        var folder = Unique();
        using var browser = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await SignInAsync(browser, FiGetServerFixture.AdminToken));

        using var forged = new FormUrlEncodedContent(new Dictionary<string, string> { ["parent"] = "", ["name"] = folder });
        using var response = await browser.PostAsync("admin/assets/files/folders", forged);

        Assert.False(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Redirect, $"A forged post was answered {(int)response.StatusCode}.");
        using var reader = server.CreateClient();
        Assert.DoesNotContain(folder, await HttpAssert.SuccessBodyAsync(await reader.GetAsync("endpoints/files/dir/")), StringComparison.Ordinal);
    }

    private static string Unique() => "t" + Guid.NewGuid().ToString("N")[..10];

    private int StoredBlobCount()
    {
        var root = Path.Combine(server.Services.GetRequiredService<StoragePaths>().Root, "files", "assets");
        return Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count(f => !f.EndsWith(".tmp", StringComparison.Ordinal)) : 0;
    }

    private async Task<string> CreateTokenAsync(FiGet.Domain.Entities.TokenScopes scopes)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<FiGet.Application.Tokens.AccessTokenService>();
        return (await tokens.CreateAsync("assets-" + scopes, scopes, null, null, CancellationToken.None)).Secret;
    }

    private HttpClient CreateBrowser() =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = server.BaseAddress };

    private static async Task<HttpResponseMessage> SignInAsync(HttpClient client, string token)
    {
        var form = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/account/login"));
        var fields = new Dictionary<string, string>();
        foreach (Match hidden in HiddenInput().Matches(form))
        {
            fields[WebUtility.HtmlDecode(hidden.Groups["name"].Value)] = WebUtility.HtmlDecode(hidden.Groups["value"].Value);
        }

        var tokenField = TokenInputName().Match(form);
        Assert.True(tokenField.Success, "The login form has no token input.");
        fields[WebUtility.HtmlDecode(tokenField.Groups["name"].Value)] = token;

        using var content = new FormUrlEncodedContent(fields);
        return await client.PostAsync("/account/login", content);
    }

    [GeneratedRegex("<input[^>]*type=\"hidden\"[^>]*name=\"(?<name>[^\"]+)\"[^>]*value=\"(?<value>[^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex HiddenInput();

    [GeneratedRegex("<input[^>]*id=\"token\"[^>]*name=\"(?<name>[^\"]+)\"|<input[^>]*name=\"(?<name>[^\"]+)\"[^>]*id=\"token\"", RegexOptions.CultureInvariant)]
    private static partial Regex TokenInputName();

    [GeneratedRegex("data-asset-upload.*?name=\"__RequestVerificationToken\"[^>]*value=\"(?<value>[^\"]+)\"", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex UploadToken();
}
