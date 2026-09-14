using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FiGet.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace FiGet.Integration.Tests;

/// <summary>
/// Two asset directories backed by folders on this machine, as a mounted share would be on the cluster: <c>share</c>
/// downloads anonymously and lists only with credentials, and is never written through FiGet; <c>share-rw</c> lists
/// anonymously too and takes writes. The folders are made before the server starts and seeded from configuration.
/// </summary>
public sealed class SharedFolderServerFixture() : FiGetServerFixture(TestDatabase.Sqlite)
{
    public string ReadOnlyRoot { get; } = Path.Combine(Path.GetTempPath(), "figet-share-ro-" + Guid.NewGuid().ToString("N"));

    public string WritableRoot { get; } = Path.Combine(Path.GetTempPath(), "figet-share-rw-" + Guid.NewGuid().ToString("N"));

    protected override void Configure(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        Directory.CreateDirectory(Path.Combine(ReadOnlyRoot, "tools"));
        File.WriteAllText(Path.Combine(ReadOnlyRoot, "readme.txt"), "hello from the share");
        File.WriteAllBytes(Path.Combine(ReadOnlyRoot, "tools", "setup.exe"), [77, 90, 1, 2, 3]);
        File.WriteAllText(Path.Combine(ReadOnlyRoot, "desktop.ini"), "[.ShellClassInfo]");
        File.WriteAllText(Path.Combine(ReadOnlyRoot, "~$notes.docx"), "lock");
        File.WriteAllText(Path.Combine(ReadOnlyRoot, "web.config"), "<configuration />");
        Directory.CreateDirectory(WritableRoot);

        builder.UseSetting("FiGet:Feeds:3:Name", "share");
        builder.UseSetting("FiGet:Feeds:3:Kind", "Assets");
        builder.UseSetting("FiGet:Feeds:3:AnonymousRead", "true");
        builder.UseSetting("FiGet:Feeds:3:AnonymousList", "false");
        builder.UseSetting("FiGet:Feeds:3:Folder", ReadOnlyRoot);

        builder.UseSetting("FiGet:Feeds:4:Name", "share-rw");
        builder.UseSetting("FiGet:Feeds:4:Kind", "Assets");
        builder.UseSetting("FiGet:Feeds:4:AnonymousRead", "true");
        builder.UseSetting("FiGet:Feeds:4:AnonymousList", "true");
        builder.UseSetting("FiGet:Feeds:4:Folder", WritableRoot);
        builder.UseSetting("FiGet:Feeds:4:FolderWrites", "true");
    }
}

/// <summary>
/// Asset directories backed by a shared folder (docs/backlog.md, designed 2026-09-14): the folder is the content, with no
/// copy and no index; downloads and listings have separate anonymous switches; writes are off unless turned on; and
/// cache modes per folder are kept in FiGet, since nothing can be written beside the files.
/// </summary>
public sealed partial class SharedFolderTests(SharedFolderServerFixture server) : IClassFixture<SharedFolderServerFixture>
{
    [Fact]
    public async Task A_file_on_the_folder_is_served_as_it_is_and_a_new_one_at_once()
    {
        using var anonymous = server.CreateClient();
        var readme = await anonymous.GetAsync("endpoints/share/content/readme.txt");
        HttpAssert.Status(HttpStatusCode.OK, readme);
        Assert.Equal("hello from the share", await readme.Content.ReadAsStringAsync());
        Assert.Equal("text/plain", readme.Content.Headers.ContentType?.MediaType);
        Assert.Null(readme.Content.Headers.ContentDisposition);

        // Size and modified time stand in for a hash nobody computed: a second request with the tag is a 304.
        var etag = readme.Headers.ETag;
        Assert.NotNull(etag);
        using var conditional = new HttpRequestMessage(HttpMethod.Get, "endpoints/share/content/readme.txt");
        conditional.Headers.IfNoneMatch.Add(etag);
        HttpAssert.Status(HttpStatusCode.NotModified, await anonymous.SendAsync(conditional));

        // An installer downloads rather than opens, like every non-inert type, and takes its type from its extension.
        var installer = await anonymous.GetAsync("endpoints/share/content/tools/setup.exe");
        HttpAssert.Status(HttpStatusCode.OK, installer);
        Assert.Equal("attachment", installer.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal([77, 90, 1, 2, 3], await installer.Content.ReadAsByteArrayAsync());

        // Placed on the share directly, served at once; removed, gone at once.
        var later = Path.Combine(server.ReadOnlyRoot, "tools", "later.msi");
        await File.WriteAllBytesAsync(later, [1, 2, 3, 4], TestContext.Current.CancellationToken);
        HttpAssert.Status(HttpStatusCode.OK, await anonymous.GetAsync("endpoints/share/content/tools/later.msi"));
        File.Delete(later);
        HttpAssert.Status(HttpStatusCode.NotFound, await anonymous.GetAsync("endpoints/share/content/tools/later.msi"));
    }

    [Fact]
    public async Task Folders_missing_files_and_names_never_served_all_answer_the_same_404()
    {
        using var anonymous = server.CreateClient();
        // No "..": the client normalises it away before sending, and AssetPathTests already refuses it on arrival.
        foreach (var path in (string[])["tools", "tools/", "nothing.here", "desktop.ini", "web.config", "~$notes.docx"])
        {
            var response = await anonymous.GetAsync($"endpoints/share/content/{path}");
            Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, $"{path}: {(int)response.StatusCode}");
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Assert.Equal(FiGet.Protocol.Assets.AssetEndpoints.FileNotFound, await response.Content.ReadAsStringAsync());
            }
        }

        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        var listed = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await admin.GetAsync("endpoints/share/dir/?recursive=true")))!.AsArray();
        var names = listed.Select(item => (string?)item!["name"]).ToList();
        Assert.Contains("readme.txt", names);
        Assert.Contains("tools", names);
        Assert.Contains("setup.exe", names);
        Assert.DoesNotContain("desktop.ini", names);
        Assert.DoesNotContain("web.config", names);
        Assert.DoesNotContain("~$notes.docx", names);

        // A folder entry has no hashes and no size; a file has a size and a type from its extension, and no hashes.
        var folder = listed.First(item => (string?)item!["name"] == "tools")!;
        Assert.Equal("dir", (string?)folder["type"]);
        var file = listed.First(item => (string?)item!["name"] == "readme.txt")!;
        Assert.Equal("text/plain", (string?)file["type"]);
        Assert.Null(file["sha256"]);
        Assert.Equal(20, (long?)file["size"]);
    }

    [Fact]
    public async Task Listing_browsing_and_exporting_need_credentials_unless_the_directory_allows_them()
    {
        using var anonymous = server.CreateClient();
        var refused = await anonymous.GetAsync("endpoints/share/dir/");
        HttpAssert.Status(HttpStatusCode.Unauthorized, refused);
        Assert.Contains("Basic", refused.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
        HttpAssert.Status(HttpStatusCode.Unauthorized, await anonymous.GetAsync("endpoints/share/export/?format=zip"));

        // A known file's metadata is a download; a folder's is a listing.
        HttpAssert.Status(HttpStatusCode.OK, await anonymous.GetAsync("endpoints/share/metadata/readme.txt"));
        HttpAssert.Status(HttpStatusCode.NotFound, await anonymous.GetAsync("endpoints/share/metadata/tools"));

        HttpAssert.Status(HttpStatusCode.NotFound, await anonymous.GetAsync("/assets/share"));
        var assets = await HttpAssert.SuccessBodyAsync(await anonymous.GetAsync("/assets"));
        Assert.DoesNotContain("/assets/share\"", assets, StringComparison.Ordinal);
        Assert.Contains("/assets/share-rw\"", assets, StringComparison.Ordinal);

        // The other directory lists to anyone.
        HttpAssert.Status(HttpStatusCode.OK, await anonymous.GetAsync("endpoints/share-rw/dir/"));
        HttpAssert.Status(HttpStatusCode.OK, await anonymous.GetAsync("/assets/share-rw"));

        // A key with Read, or a signed-in account, lists what a stranger cannot.
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.OK, await admin.GetAsync("endpoints/share/dir/"));
        HttpAssert.Status(HttpStatusCode.OK, await admin.GetAsync("endpoints/share/metadata/tools"));
        using var browser = Browser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(browser));
        var page = await HttpAssert.SuccessBodyAsync(await browser.GetAsync("/assets/share"));
        Assert.Contains("readme.txt", page, StringComparison.Ordinal);
        Assert.Contains("From a folder on the server", page, StringComparison.Ordinal);
        Assert.DoesNotContain("data-asset-upload", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writes_reach_the_folder_only_where_they_are_turned_on()
    {
        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.Forbidden, await admin.PutAsync("endpoints/share/content/new.txt", new StringContent("x")));
        HttpAssert.Status(HttpStatusCode.Forbidden, await admin.PostAsync("endpoints/share/dir/made", null));
        HttpAssert.Status(HttpStatusCode.Forbidden, await admin.PostAsync("endpoints/share/delete/tools?recursive=true", null));
        Assert.True(File.Exists(Path.Combine(server.ReadOnlyRoot, "tools", "setup.exe")));

        // Metadata and multipart parts have nowhere to live beside a share's files, writes on or off.
        using (var metadata = new StringContent("{\"type\":\"text/plain\"}", Encoding.UTF8, "application/json"))
        {
            HttpAssert.Status(HttpStatusCode.Forbidden, await admin.PostAsync("endpoints/share-rw/metadata/anything.txt", metadata));
        }

        HttpAssert.Status(HttpStatusCode.Forbidden, await admin.PostAsync("endpoints/share-rw/content/big.bin?multipart=upload&id=x&index=0&offset=0&totalSize=1&partSize=1&totalParts=1", new ByteArrayContent([1])));

        var folder = "w" + Guid.NewGuid().ToString("N")[..8];
        HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync($"endpoints/share-rw/content/{folder}/file.txt", new StringContent("first")));
        var onDisk = Path.Combine(server.WritableRoot, folder, "file.txt");
        Assert.Equal("first", await File.ReadAllTextAsync(onDisk, TestContext.Current.CancellationToken));
        HttpAssert.Status(HttpStatusCode.Conflict, await admin.PutAsync($"endpoints/share-rw/content/{folder}/file.txt", new StringContent("second")));
        HttpAssert.Status(HttpStatusCode.Created, await admin.PostAsync($"endpoints/share-rw/content/{folder}/file.txt", new StringContent("second")));
        Assert.Equal("second", await File.ReadAllTextAsync(onDisk, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(Path.Combine(server.WritableRoot, folder), "*.figet-tmp"));

        HttpAssert.Status(HttpStatusCode.Created, await admin.PostAsync($"endpoints/share-rw/dir/{folder}/sub", null));
        Assert.True(Directory.Exists(Path.Combine(server.WritableRoot, folder, "sub")));
        HttpAssert.Status(HttpStatusCode.BadRequest, await admin.PostAsync($"endpoints/share-rw/delete/{folder}", null));
        HttpAssert.Status(HttpStatusCode.OK, await admin.DeleteAsync($"endpoints/share-rw/content/{folder}/file.txt"));
        Assert.False(File.Exists(onDisk));
        HttpAssert.Status(HttpStatusCode.OK, await admin.PostAsync($"endpoints/share-rw/delete/{folder}?recursive=true", null));
        Assert.False(Directory.Exists(Path.Combine(server.WritableRoot, folder)));

        // Names a web server never served cannot be written either.
        HttpAssert.Status(HttpStatusCode.BadRequest, await admin.PutAsync("endpoints/share-rw/content/web.config", new StringContent("x")));
    }

    [Fact]
    public async Task A_link_that_leaves_the_folder_is_not_followed()
    {
        var outside = Path.Combine(server.ReadOnlyRoot, "readme.txt");
        var link = Path.Combine(server.WritableRoot, "escape.txt");
        try
        {
            File.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Creating a symbolic link needs a privilege this account does not have: " + ex.Message);
        }

        try
        {
            using var anonymous = server.CreateClient();
            HttpAssert.Status(HttpStatusCode.NotFound, await anonymous.GetAsync("endpoints/share-rw/content/escape.txt"));
            var listed = await HttpAssert.SuccessBodyAsync(await anonymous.GetAsync("endpoints/share-rw/dir/"));
            Assert.Contains("escape.txt", listed, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(link);
        }
    }

    [Fact]
    public async Task Cache_modes_apply_to_a_folder_and_the_folders_below_it()
    {
        using var browser = Browser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(browser));
        using var anonymous = server.CreateClient();

        await SetCacheAsync(browser, "", "no-store", null);
        var readme = await anonymous.GetAsync("endpoints/share/content/readme.txt");
        Assert.Equal("must-revalidate, no-cache, no-store", Directives(readme));
        Assert.Equal("no-cache", readme.Headers.Pragma.ToString());
        Assert.Equal("-1", readme.Content.Headers.GetValues("Expires").Single());

        await SetCacheAsync(browser, "tools", "max-age", 60);
        Assert.Equal("max-age=60, public", Directives(await anonymous.GetAsync("endpoints/share/content/tools/setup.exe")));
        Assert.Equal("must-revalidate, no-cache, no-store", Directives(await anonymous.GetAsync("endpoints/share/content/readme.txt")));

        // The page names what the folder inherits and what it set.
        var page = await HttpAssert.SuccessBodyAsync(await browser.GetAsync("/assets/share?path=tools"));
        Assert.Contains("Caching: cached for 60 seconds", page, StringComparison.Ordinal);

        await SetCacheAsync(browser, "tools", "inherit", null);
        Assert.Equal("must-revalidate, no-cache, no-store", Directives(await anonymous.GetAsync("endpoints/share/content/tools/setup.exe")));

        await SetCacheAsync(browser, "", "inherit", null);
        Assert.Null((await anonymous.GetAsync("endpoints/share/content/readme.txt")).Headers.CacheControl);
    }

    /// <summary>The Cache-Control directives, sorted: the client library reorders them when it parses the header.</summary>
    private static string Directives(HttpResponseMessage response) =>
        string.Join(", ", response.Headers.GetValues("Cache-Control").SelectMany(v => v.Split(',')).Select(v => v.Trim()).Order(StringComparer.Ordinal));

    private static async Task SetCacheAsync(HttpClient browser, string path, string mode, int? seconds)
    {
        var page = await HttpAssert.SuccessBodyAsync(await browser.GetAsync("/assets/share" + (path.Length == 0 ? "" : "?path=" + Uri.EscapeDataString(path))));
        var fields = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(Antiforgery().Match(page).Groups["value"].Value),
            ["path"] = path,
            ["mode"] = mode,
            ["maxAge"] = seconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            ["returnUrl"] = "/assets/share",
        };
        using var content = new FormUrlEncodedContent(fields);
        HttpAssert.Status(HttpStatusCode.Redirect, await browser.PostAsync("/admin/assets/share/cache", content));
    }

    private HttpClient Browser() =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = server.BaseAddress };

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<value>[^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex Antiforgery();
}
