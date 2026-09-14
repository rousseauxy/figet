using System.Net;
using System.Text.RegularExpressions;
using FiGet.Application.Accounts;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

/// <summary>
/// A shares mount with two real folders, a dot-folder and a file under it, made before the server starts; a separate
/// folder named in configuration as the content of <c>cfgshare</c>; and <c>plainshare</c>, named in configuration
/// without a folder, so the pages own its content.
/// </summary>
public sealed class SharesRootServerFixture() : FiGetServerFixture(TestDatabase.Sqlite)
{
    public string SharesRoot { get; } = Path.Combine(Path.GetTempPath(), "figet-shares-" + Guid.NewGuid().ToString("N"));

    public string ConfiguredRoot { get; } = Path.Combine(Path.GetTempPath(), "figet-cfgshare-" + Guid.NewGuid().ToString("N"));

    protected override void Configure(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        Directory.CreateDirectory(Path.Combine(SharesRoot, "intune"));
        File.WriteAllText(Path.Combine(SharesRoot, "intune", "setup.txt"), "from intune");
        Directory.CreateDirectory(Path.Combine(SharesRoot, "crm"));
        Directory.CreateDirectory(Path.Combine(SharesRoot, ".dotted"));
        File.WriteAllText(Path.Combine(SharesRoot, "notes.txt"), "not a folder");
        Directory.CreateDirectory(ConfiguredRoot);
        File.WriteAllText(Path.Combine(ConfiguredRoot, "configured.txt"), "from configuration");

        builder.UseSetting("FiGet:Assets:SharesRoot", SharesRoot);
        builder.UseSetting("FiGet:Feeds:3:Name", "cfgshare");
        builder.UseSetting("FiGet:Feeds:3:Kind", "Assets");
        builder.UseSetting("FiGet:Feeds:3:AnonymousRead", "true");
        builder.UseSetting("FiGet:Feeds:3:Folder", ConfiguredRoot);
        builder.UseSetting("FiGet:Feeds:4:Name", "plainshare");
        builder.UseSetting("FiGet:Feeds:4:Kind", "Assets");
    }
}

/// <summary>
/// An asset directory's content chosen on the pages: one of the sub-folders of the shares mount, by name, admins only.
/// The pages never take a path, the resolver refuses everything that is not on the list, and a directory whose folder
/// configuration sets is left to configuration.
/// </summary>
public sealed partial class ShareFolderChoiceTests(SharesRootServerFixture server) : IClassFixture<SharesRootServerFixture>
{
    [Fact]
    public async Task The_create_form_offers_the_folders_under_the_mount_and_nothing_else()
    {
        using var admin = await AdminAsync();
        var form = FormWith(await HttpAssert.SuccessBodyAsync(await admin.GetAsync("/admin/assets")), "create-feed");
        Assert.Contains("FiGet's own storage (uploads)</option>", form, StringComparison.Ordinal);
        Assert.Contains("<option value=\"intune\"", form, StringComparison.Ordinal);
        Assert.Contains("<option value=\"crm\"", form, StringComparison.Ordinal);
        Assert.DoesNotContain(".dotted", form, StringComparison.Ordinal);
        Assert.DoesNotContain("notes.txt", form, StringComparison.Ordinal);
        Assert.DoesNotContain("FiGet:Assets:SharesRoot", form, StringComparison.Ordinal);

        // Package feeds have no content to choose.
        Assert.DoesNotContain("id=\"feed-folder\"", FormWith(await HttpAssert.SuccessBodyAsync(await admin.GetAsync("/admin/feeds")), "create-feed"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_directory_created_on_a_folder_serves_the_folder_and_refuses_writes_until_told_otherwise()
    {
        using var admin = await AdminAsync();
        var name = "share-" + Guid.NewGuid().ToString("N")[..8];
        var created = await CreateAsync(admin, name, "intune", writes: false);
        Assert.Contains($"Asset directory &#x27;{name}&#x27; created.", created, StringComparison.Ordinal);

        var feed = (await FindAsync(name))!;
        Assert.Equal(Path.Combine(Path.GetFullPath(server.SharesRoot), "intune"), feed.FolderRoot);
        Assert.False(feed.FolderWritable);

        using var withKey = server.CreateClient(FiGetServerFixture.AdminToken);
        Assert.Equal("from intune", await HttpAssert.SuccessBodyAsync(await withKey.GetAsync($"endpoints/{name}/content/setup.txt")));
        HttpAssert.Status(HttpStatusCode.Forbidden, await withKey.PutAsync($"endpoints/{name}/content/new.txt", new StringContent("x")));
        Assert.False(File.Exists(Path.Combine(server.SharesRoot, "intune", "new.txt")));

        var entry = await AuditWait.ForAsync(server, "feed.create");
        Assert.Equal(name, entry.Subject);
        Assert.Contains("folder=intune folderWrites=False", entry.Detail, StringComparison.Ordinal);

        // The settings page says where the content is, and shows the chooser to an admin.
        var settings = await HttpAssert.SuccessBodyAsync(await admin.GetAsync($"/admin/assets/{name}"));
        Assert.Contains("Content: folder intune on the shares mount", settings, StringComparison.Ordinal);
        Assert.Contains("chosen under <em>Content</em> below", settings, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("other")]
    [InlineData(".dotted")]
    [InlineData("notes.txt")]
    [InlineData("intune/sub")]
    [InlineData("..\\..")]
    [InlineData("INTUNE")]
    public async Task A_name_that_is_not_on_the_list_is_refused_and_creates_nothing(string folder)
    {
        using var admin = await AdminAsync();
        var name = "refused-" + Guid.NewGuid().ToString("N")[..8];
        var before = Directory.GetFileSystemEntries(server.SharesRoot).Length;

        var refused = await CreateAsync(admin, name, folder, writes: true);
        Assert.Contains("is not a folder on the shares mount", refused, StringComparison.Ordinal);
        Assert.Null(await FindAsync(name));
        Assert.Equal(before, Directory.GetFileSystemEntries(server.SharesRoot).Length);
    }

    [Fact]
    public async Task The_settings_page_moves_a_directory_between_folders_and_its_own_storage()
    {
        using var admin = await AdminAsync();
        var name = "moving-" + Guid.NewGuid().ToString("N")[..8];
        Assert.Contains("created.", await CreateAsync(admin, name, "", writes: false), StringComparison.Ordinal);
        Assert.Null((await FindAsync(name))!.FolderRoot);

        var page = await HttpAssert.SuccessBodyAsync(await admin.GetAsync($"/admin/assets/{name}"));
        Assert.Contains("<details class=\"fg-accordion\" id=\"folder\">", page, StringComparison.Ordinal);
        Assert.Contains("Content: FiGet&#x27;s own storage", page, StringComparison.Ordinal);

        // On to a folder, with writes: a PUT then lands in the folder.
        var moved = await SaveContentAsync(admin, name, page, "crm", writes: true);
        Assert.Contains("Content saved.", moved, StringComparison.Ordinal);
        Assert.Contains("Content: folder crm on the shares mount", moved, StringComparison.Ordinal);
        var feed = (await FindAsync(name))!;
        Assert.Equal(Path.Combine(Path.GetFullPath(server.SharesRoot), "crm"), feed.FolderRoot);
        Assert.True(feed.FolderWritable);
        var entry = await AuditWait.ForAsync(server, "feed.folder");
        Assert.Equal(name, entry.Subject);
        Assert.Contains("folder=crm folderWrites=True", entry.Detail, StringComparison.Ordinal);

        using var withKey = server.CreateClient(FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.Created, await withKey.PutAsync($"endpoints/{name}/content/written.txt", new StringContent("written through FiGet")));
        Assert.Equal("written through FiGet", await File.ReadAllTextAsync(Path.Combine(server.SharesRoot, "crm", "written.txt"), TestContext.Current.CancellationToken));

        // A name that is not on the list changes nothing and reopens the form with the reason.
        var refused = await SaveContentAsync(admin, name, moved, "other", writes: false);
        Assert.Contains("&#x27;other&#x27; is not a folder on the shares mount.", refused, StringComparison.Ordinal);
        Assert.Contains("<details class=\"fg-accordion\" id=\"folder\" open>", refused, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(Path.GetFullPath(server.SharesRoot), "crm"), (await FindAsync(name))!.FolderRoot);

        // Back to FiGet's own storage: the folder and its file are untouched.
        var back = await SaveContentAsync(admin, name, moved, "", writes: false);
        Assert.Contains("Content saved.", back, StringComparison.Ordinal);
        Assert.Contains("Content: FiGet&#x27;s own storage", back, StringComparison.Ordinal);
        Assert.Null((await FindAsync(name))!.FolderRoot);
        Assert.True(File.Exists(Path.Combine(server.SharesRoot, "crm", "written.txt")));
        HttpAssert.Status(HttpStatusCode.NotFound, await withKey.GetAsync($"endpoints/{name}/content/written.txt"));
    }

    [Fact]
    public async Task A_directory_whose_folder_configuration_sets_is_not_chosen_on_the_page()
    {
        using var admin = await AdminAsync();
        var page = await HttpAssert.SuccessBodyAsync(await admin.GetAsync("/admin/assets/cfgshare"));
        Assert.DoesNotContain("value=\"feed-folder\"", page, StringComparison.Ordinal);
        Assert.Contains("set in the server configuration", page, StringComparison.Ordinal);

        // Posted by hand anyway, with the networks form's antiforgery token: refused, nothing changes.
        var fields = HiddenFields(FormWith(page, "feed-networks"));
        fields["_handler"] = "feed-folder";
        fields["Folder.Share"] = "crm";
        using (var content = new FormUrlEncodedContent(fields))
        {
            var answered = await admin.PostAsync("/admin/assets/cfgshare", content);
            Assert.True(answered.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest, $"{(int)answered.StatusCode}");
        }

        Assert.Equal(server.ConfiguredRoot, (await FindAsync("cfgshare"))!.FolderRoot);

        // Named in configuration without a folder: the pages own its content.
        Assert.Contains("value=\"feed-folder\"", await HttpAssert.SuccessBodyAsync(await admin.GetAsync("/admin/assets/plainshare")), StringComparison.Ordinal);
    }

    /// <summary>A manager sees the page but not the chooser, and cannot post it.</summary>
    [Fact]
    public async Task A_feed_manager_does_not_choose_the_content()
    {
        using var admin = await AdminAsync();
        var name = "managed-" + Guid.NewGuid().ToString("N")[..8];
        Assert.Contains("created.", await CreateAsync(admin, name, "", writes: false), StringComparison.Ordinal);

        var user = "mgr" + Guid.NewGuid().ToString("N")[..8];
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            var users = scope.ServiceProvider.GetRequiredService<IUserStore>();
            Assert.True(await users.AddAsync(
                new User { UserName = user, UserNameLower = user, PasswordHash = hasher.Hash("manager-pass-word-01"), SecurityStamp = AccountService.NewStamp(), CreatedUtc = DateTime.UtcNow },
                CancellationToken.None));
            var feed = (await FindAsync(name))!;
            var account = (await users.FindByUserNameAsync(user, CancellationToken.None))!;
            await scope.ServiceProvider.GetRequiredService<IFeedPermissionStore>().SetAsync(feed.Key, account.Key, null, FeedAccessLevel.Manage, CancellationToken.None);
        }

        using var manager = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(manager, user, "manager-pass-word-01"));
        var page = await HttpAssert.SuccessBodyAsync(await manager.GetAsync($"/admin/assets/{name}"));
        Assert.DoesNotContain("value=\"feed-folder\"", page, StringComparison.Ordinal);

        var fields = HiddenFields(FormWith(page, "feed-networks"));
        fields["_handler"] = "feed-folder";
        fields["Folder.Share"] = "crm";
        using var content = new FormUrlEncodedContent(fields);
        await manager.PostAsync($"/admin/assets/{name}", content);
        Assert.Null((await FindAsync(name))!.FolderRoot);
    }

    private static async Task<string> CreateAsync(HttpClient admin, string name, string folder, bool writes)
    {
        var form = FormWith(await HttpAssert.SuccessBodyAsync(await admin.GetAsync("/admin/assets")), "create-feed");
        var fields = HiddenFields(form);
        fields[BrowserSignIn.InputName(form, "feed-name")] = name;
        fields[BrowserSignIn.InputName(form, "feed-folder")] = folder;
        fields[BrowserSignIn.InputName(form, "feed-folder-writes")] = writes ? "true" : "false";
        using var content = new FormUrlEncodedContent(fields);
        return await HttpAssert.SuccessBodyAsync(await admin.PostAsync("/admin/assets", content));
    }

    private static async Task<string> SaveContentAsync(HttpClient admin, string name, string page, string share, bool writes)
    {
        var form = FormWith(page, "feed-folder");
        var fields = HiddenFields(form);
        fields[BrowserSignIn.InputName(form, "folder-share")] = share;
        fields[BrowserSignIn.InputName(form, "folder-writes")] = writes ? "true" : "false";
        using var content = new FormUrlEncodedContent(fields);
        return await HttpAssert.SuccessBodyAsync(await admin.PostAsync($"/admin/assets/{name}", content));
    }

    private async Task<HttpClient> AdminAsync()
    {
        var admin = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(admin));
        return admin;
    }

    private async Task<Feed?> FindAsync(string name)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IFeedStore>().FindAsync(name, CancellationToken.None);
    }

    private HttpClient CreateBrowser() =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = server.BaseAddress };

    private static string FormWith(string page, string handler) =>
        FormElement().Matches(page).Select(m => m.Value).First(f => f.Contains($"value=\"{handler}\"", StringComparison.Ordinal));

    private static Dictionary<string, string> HiddenFields(string form) =>
        HiddenInput().Matches(form).ToDictionary(m => WebUtility.HtmlDecode(m.Groups["name"].Value), m => WebUtility.HtmlDecode(m.Groups["value"].Value), StringComparer.Ordinal);

    [GeneratedRegex("<input[^>]*type=\"hidden\"[^>]*name=\"(?<name>[^\"]+)\"[^>]*value=\"(?<value>[^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex HiddenInput();

    [GeneratedRegex("<form[^>]*>.*?</form>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex FormElement();
}
