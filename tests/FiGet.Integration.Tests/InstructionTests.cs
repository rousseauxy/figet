using System.Net;
using System.Text.RegularExpressions;
using FiGet.Application.Packages;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

public sealed class SqliteInstructionTests(SqliteServerFixture fixture) : InstructionTests(fixture), IClassFixture<SqliteServerFixture>;

public sealed class SqlServerInstructionTests(SqlServerServerFixture fixture) : InstructionTests(fixture), IClassFixture<SqlServerServerFixture>;

/// <summary>
/// Per-feed instructions: the defaults on a feed nobody configured, and a feed's own client address and commands once a
/// manager saves them - on the package, feed and asset directory pages, while protocol answers keep the public address.
/// </summary>
public abstract partial class InstructionTests
{
    private readonly FiGetServerFixture server;

    protected InstructionTests(FiGetServerFixture server)
    {
        this.server = server;
        server.SkipIfUnavailable();
    }

    [Fact]
    public async Task A_feed_shows_the_defaults_and_then_its_own_address_and_commands()
    {
        var feed = await CreateFeedAsync(FeedKind.Curated);
        var id = FiGetServerFixture.UniqueId("Instr.Pkg");
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var target = (await scope.ServiceProvider.GetRequiredService<IFeedStore>().FindAsync(feed, CancellationToken.None))!;
            using var package = TestPackages.Create(id, "1.2.3");
            await scope.ServiceProvider.GetRequiredService<PackageIngestionService>().PushAsync(target, package, CancellationToken.None);
        }

        using var anyone = server.CreateClient();
        var packagePage = await HttpAssert.SuccessBodyAsync(await anyone.GetAsync($"/feeds/{feed}/packages/{id}"));
        Assert.Contains($"Install-Module -Name {id} -RequiredVersion 1.2.3 -Repository {feed}", packagePage, StringComparison.Ordinal);
        Assert.Contains($"Register-PSResourceRepository -Name {feed} -Uri {server.BaseAddress.ToString().TrimEnd('/')}/nuget/{feed}/v3/index.json", await HttpAssert.SuccessBodyAsync(await anyone.GetAsync($"/feeds/{feed}")), StringComparison.Ordinal);

        // A manager saves a client address and an own install command through the settings page.
        using var admin = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(admin));
        var settings = await HttpAssert.SuccessBodyAsync(await admin.GetAsync($"/admin/feeds/{feed}/instructions"));
        var form = FormWith(settings, "feed-instructions");
        var fields = Hidden(form);
        fields[BrowserSignIn.InputName(form, "client-base")] = "https://packages.internal.example/";
        fields[TextAreaName(form, "package-instructions")] = "# Our servers\nInstall-Module {id} -RequiredVersion {version} -Repository Internal";
        fields[TextAreaName(form, "feed-instructions")] = TextAreaValue(form, "feed-instructions");
        using (var content = new FormUrlEncodedContent(fields))
        {
            Assert.Contains("Instructions saved.", await HttpAssert.SuccessBodyAsync(await admin.PostAsync($"/admin/feeds/{feed}/instructions", content)), StringComparison.Ordinal);
        }

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var stored = (await scope.ServiceProvider.GetRequiredService<IFeedStore>().FindAsync(feed, CancellationToken.None))!;
            Assert.Equal("https://packages.internal.example", stored.ClientBaseUrl);
            Assert.NotNull(stored.PackageInstructions);
            Assert.Null(stored.FeedInstructions);
        }

        packagePage = await HttpAssert.SuccessBodyAsync(await anyone.GetAsync($"/feeds/{feed}/packages/{id}"));
        Assert.Contains("Our servers", packagePage, StringComparison.Ordinal);
        Assert.Contains($"Install-Module {id} -RequiredVersion 1.2.3 -Repository Internal", packagePage, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet add package", packagePage, StringComparison.Ordinal);
        Assert.Contains($"https://packages.internal.example/nuget/{feed}/v3/index.json", await HttpAssert.SuccessBodyAsync(await anyone.GetAsync($"/feeds/{feed}")), StringComparison.Ordinal);

        // What clients are told by the protocol keeps the server's own address.
        var index = await HttpAssert.SuccessBodyAsync(await anyone.GetAsync($"nuget/{feed}/v3/index.json"));
        Assert.DoesNotContain("packages.internal.example", index, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_asset_directory_shows_its_download_commands()
    {
        var directory = await CreateFeedAsync(FeedKind.Assets);
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var feeds = scope.ServiceProvider.GetRequiredService<IFeedStore>();
            var target = (await feeds.FindAsync(directory, CancellationToken.None))!;
            Assert.True(await feeds.UpdateInstructionsAsync(target.Key, "https://files.internal.example", null, null, "Start-BitsTransfer -Source {folderUrl}<file>", CancellationToken.None));
        }

        using var anyone = server.CreateClient();
        var page = await HttpAssert.SuccessBodyAsync(await anyone.GetAsync($"/assets/{directory}"));
        Assert.Contains($"Start-BitsTransfer -Source https://files.internal.example/endpoints/{directory}/content/&lt;file&gt;", page, StringComparison.Ordinal);
    }

    private async Task<string> CreateFeedAsync(FeedKind kind)
    {
        var name = (kind == FeedKind.Assets ? "idir" : "ifeed") + Guid.NewGuid().ToString("N")[..8];
        await using var scope = server.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IFeedStore>().CreateAsync(
            new Feed { Name = name, NameLower = name, Kind = kind, AnonymousRead = true, AnonymousList = kind == FeedKind.Assets, CreatedUtc = DateTime.UtcNow },
            CancellationToken.None));
        return name;
    }

    private HttpClient CreateBrowser() =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = server.BaseAddress };

    private static string FormWith(string page, string handler) =>
        FormElement().Matches(page).Select(m => m.Value).First(f => f.Contains($"value=\"{handler}\"", StringComparison.Ordinal));

    private static Dictionary<string, string> Hidden(string form) =>
        HiddenInput().Matches(form).ToDictionary(m => WebUtility.HtmlDecode(m.Groups["name"].Value), m => WebUtility.HtmlDecode(m.Groups["value"].Value));

    private static string TextAreaName(string form, string id) =>
        WebUtility.HtmlDecode(Regex.Match(form, $"<textarea[^>]*id=\"{id}\"[^>]*name=\"(?<n>[^\"]+)\"|<textarea[^>]*name=\"(?<n>[^\"]+)\"[^>]*id=\"{id}\"").Groups["n"].Value);

    private static string TextAreaValue(string form, string id) =>
        WebUtility.HtmlDecode(Regex.Match(form, $"<textarea[^>]*id=\"{id}\"[^>]*>(?<v>.*?)</textarea>", RegexOptions.Singleline).Groups["v"].Value);

    [GeneratedRegex("<form[^>]*>.*?</form>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex FormElement();

    [GeneratedRegex("<input[^>]*type=\"hidden\"[^>]*name=\"(?<name>[^\"]+)\"[^>]*value=\"(?<value>[^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex HiddenInput();
}
