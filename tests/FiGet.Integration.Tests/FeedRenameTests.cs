using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

public sealed class SqliteFeedRenameTests(SqliteServerFixture fixture) : FeedRenameTests(fixture), IClassFixture<SqliteServerFixture>;

public sealed class SqlServerFeedRenameTests(SqlServerServerFixture fixture) : FeedRenameTests(fixture), IClassFixture<SqlServerServerFixture>;

/// <summary>
/// Renaming a feed (asked by the tester: a feed created as "Test" had to be recreated to go into production). The files
/// stay where they are, because they are stored by key, and the old name keeps answering when it is kept.
/// </summary>
public abstract partial class FeedRenameTests
{
    private readonly FiGetServerFixture server;

    protected FeedRenameTests(FiGetServerFixture server)
    {
        this.server = server;
        server.SkipIfUnavailable();
    }

    [Fact]
    public async Task A_renamed_feed_keeps_its_packages_and_answers_to_its_old_name()
    {
        var old = Unique("before");
        var renamed = Unique("after");
        await CreateFeedAsync(old, FeedKind.Curated);
        var id = FiGetServerFixture.UniqueId("Renamed.Feed");
        using (var package = TestPackages.Create(id, "1.0.0"))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushAsync(old, package));
        }

        using var admin = await AdminAsync();
        using (var response = await PostAsync(admin, $"/admin/feeds/{old}", $"/admin/feeds/{old}/rename", ("name", renamed), ("keepOldName", "true")))
        {
            HttpAssert.Status(HttpStatusCode.Redirect, response);
            Assert.Equal($"/admin/feeds/{renamed}/name?naming=renamed", response.Headers.Location?.OriginalString);
        }

        var lower = id.ToLowerInvariant();
        using var anonymous = server.CreateClient();
        foreach (var name in (string[])[renamed, old])
        {
            HttpAssert.Status(HttpStatusCode.OK, await anonymous.GetAsync($"nuget/{name}/v3/flatcontainer/{lower}/1.0.0/{lower}.1.0.0.nupkg"));
            HttpAssert.Status(HttpStatusCode.OK, await anonymous.GetAsync($"nuget/{name}/v3/index.json"));
        }

        // The old name is still the feed's: nothing else can be created under it.
        Assert.False(await CreateFeedAsync(old, FeedKind.Curated));

        var settings = await HttpAssert.SuccessBodyAsync(await admin.GetAsync($"/admin/feeds/{renamed}/name"));
        Assert.Contains($"<code>{old}</code>", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("not since it was added", settings, StringComparison.Ordinal);

        // Written in the background, so waited for.
        for (var attempt = 0; ; attempt++)
        {
            await using (var scope = server.Services.CreateAsyncScope())
            {
                var entries = await scope.ServiceProvider.GetRequiredService<IAuditStore>().QueryAsync(new AuditQuery(Action: "feed.alias.used", Feed: renamed), CancellationToken.None);
                if (entries.Any(e => e.Detail?.Contains($"alias={old}", StringComparison.Ordinal) == true))
                {
                    break;
                }
            }

            Assert.True(attempt < 100, "No feed.alias.used entry arrived within five seconds.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Without_its_old_name_kept_the_name_is_free_again()
    {
        var old = Unique("dropped");
        var renamed = Unique("kept");
        await CreateFeedAsync(old, FeedKind.Curated);

        using var admin = await AdminAsync();
        HttpAssert.Status(HttpStatusCode.Redirect, await PostAsync(admin, $"/admin/feeds/{old}", $"/admin/feeds/{old}/rename", ("name", renamed)));

        using var anonymous = server.CreateClient();
        HttpAssert.Status(HttpStatusCode.NotFound, await anonymous.GetAsync($"nuget/{old}/v3/index.json"));
        HttpAssert.Status(HttpStatusCode.OK, await anonymous.GetAsync($"nuget/{renamed}/v3/index.json"));
        Assert.True(await CreateFeedAsync(old, FeedKind.Curated));
    }

    /// <summary>One set of names: another feed's name, and another feed's alternate name, are both taken.</summary>
    [Fact]
    public async Task A_name_another_feed_answers_to_is_refused()
    {
        var first = Unique("first");
        var second = Unique("second");
        var alternate = Unique("alternate");
        await CreateFeedAsync(first, FeedKind.Curated);
        await CreateFeedAsync(second, FeedKind.Curated);

        using var admin = await AdminAsync();
        using (var added = await PostAsync(admin, $"/admin/feeds/{first}", $"/admin/feeds/{first}/aliases/add", ("name", alternate)))
        {
            Assert.EndsWith("/name?naming=alias-added", added.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        foreach (var taken in (string[])[first, alternate.ToUpperInvariant()])
        {
            using var response = await PostAsync(admin, $"/admin/feeds/{second}", $"/admin/feeds/{second}/rename", ("name", taken), ("keepOldName", "true"));
            Assert.Equal($"/admin/feeds/{second}/name?naming=taken", response.Headers.Location?.OriginalString);
        }

        using (var response = await PostAsync(admin, $"/admin/feeds/{second}", $"/admin/feeds/{second}/aliases/add", ("name", first)))
        {
            Assert.EndsWith("/name?naming=taken", response.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        using (var response = await PostAsync(admin, $"/admin/feeds/{second}", $"/admin/feeds/{second}/rename", ("name", "not a name")))
        {
            Assert.EndsWith("/name?naming=invalid", response.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        Assert.Equal(first, (await FindAsync(alternate))!.Name);
        Assert.NotNull(await FindAsync(second));
    }

    /// <summary>Going back to the old name takes it off the list, and the name left behind takes its place.</summary>
    [Fact]
    public async Task Renaming_back_swaps_the_name_and_the_alternate()
    {
        var a = Unique("there");
        var b = Unique("back");
        await CreateFeedAsync(a, FeedKind.Curated);
        await using var scope = server.Services.CreateAsyncScope();
        var feeds = scope.ServiceProvider.GetRequiredService<IFeedStore>();
        var key = (await feeds.FindAsync(a, CancellationToken.None))!.Key;

        Assert.Equal(FeedNameChange.Done, await feeds.RenameAsync(key, b, keepOldName: true, DateTime.UtcNow, CancellationToken.None));
        Assert.Equal(FeedNameChange.Done, await feeds.RenameAsync(key, a, keepOldName: true, DateTime.UtcNow, CancellationToken.None));

        Assert.Equal(a, (await feeds.FindAsync(b, CancellationToken.None))!.Name);
        Assert.Equal(b, Assert.Single(await feeds.ListAliasesAsync(key, CancellationToken.None)).Name);

        // A change of case is a rename, and adds no alternate name: names are case-insensitive.
        Assert.Equal(FeedNameChange.Done, await feeds.RenameAsync(key, a.ToUpperInvariant(), keepOldName: true, DateTime.UtcNow, CancellationToken.None));
        Assert.Single(await feeds.ListAliasesAsync(key, CancellationToken.None));
        Assert.Equal(FeedNameChange.Unchanged, await feeds.RenameAsync(key, a.ToUpperInvariant(), keepOldName: true, DateTime.UtcNow, CancellationToken.None));
    }

    [Fact]
    public async Task A_renamed_asset_directory_serves_its_files_by_both_names()
    {
        var old = Unique("dir");
        var renamed = Unique("folder");
        await CreateFeedAsync(old, FeedKind.Assets);
        using (var uploader = server.CreateClient(FiGetServerFixture.AdminToken))
        using (var body = new ByteArrayContent([1, 2, 3]))
        {
            body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            HttpAssert.Status(HttpStatusCode.Created, await uploader.PutAsync($"endpoints/{old}/content/tools/setup.bin", body));
        }

        using var admin = await AdminAsync();
        using (var response = await PostAsync(admin, $"/admin/assets/{old}", $"/admin/feeds/{old}/rename", ("name", renamed), ("keepOldName", "true")))
        {
            Assert.Equal($"/admin/assets/{renamed}/name?naming=renamed", response.Headers.Location?.OriginalString);
        }

        using var anonymous = server.CreateClient();
        foreach (var name in (string[])[renamed, old])
        {
            using var file = await anonymous.GetAsync($"endpoints/{name}/content/tools/setup.bin");
            HttpAssert.Status(HttpStatusCode.OK, file);
            Assert.Equal([1, 2, 3], await file.Content.ReadAsByteArrayAsync());
        }
    }

    [Fact]
    public async Task A_removed_alternate_name_stops_answering()
    {
        var old = Unique("gone");
        var renamed = Unique("stays");
        await CreateFeedAsync(old, FeedKind.Curated);
        using var admin = await AdminAsync();
        HttpAssert.Status(HttpStatusCode.Redirect, await PostAsync(admin, $"/admin/feeds/{old}", $"/admin/feeds/{old}/rename", ("name", renamed), ("keepOldName", "true")));

        int aliasKey;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var feeds = scope.ServiceProvider.GetRequiredService<IFeedStore>();
            aliasKey = Assert.Single(await feeds.ListAliasesAsync((await feeds.FindAsync(renamed, CancellationToken.None))!.Key, CancellationToken.None)).Key;
        }

        using (var response = await PostAsync(admin, $"/admin/feeds/{renamed}", $"/admin/feeds/{renamed}/aliases/remove", ("key", aliasKey.ToString(System.Globalization.CultureInfo.InvariantCulture))))
        {
            Assert.EndsWith("/name?naming=alias-removed", response.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        using var anonymous = server.CreateClient();
        HttpAssert.Status(HttpStatusCode.NotFound, await anonymous.GetAsync($"nuget/{old}/v3/index.json"));
    }

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private async Task<bool> CreateFeedAsync(string name, FeedKind kind)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IFeedStore>().CreateAsync(
            new Feed { Name = name, NameLower = name.ToLowerInvariant(), Kind = kind, AnonymousRead = true, CreatedUtc = DateTime.UtcNow },
            CancellationToken.None);
    }

    private async Task<Feed?> FindAsync(string name)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IFeedStore>().FindAsync(name, CancellationToken.None);
    }

    private async Task<HttpClient> AdminAsync()
    {
        var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = server.BaseAddress };
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(browser));
        return browser;
    }

    /// <summary>Posts a form with the antiforgery token of the page it would be sent from.</summary>
    private static async Task<HttpResponseMessage> PostAsync(HttpClient browser, string page, string action, params (string Name, string Value)[] fields)
    {
        var html = await HttpAssert.SuccessBodyAsync(await browser.GetAsync(page));
        var values = fields.ToDictionary(f => f.Name, f => f.Value);
        values["__RequestVerificationToken"] = WebUtility.HtmlDecode(AntiforgeryPattern().Match(html).Groups["value"].Value);
        using var content = new FormUrlEncodedContent(values);
        return await browser.PostAsync(action, content);
    }

    private async Task<HttpResponseMessage> PushAsync(string feed, Stream package)
    {
        using var client = server.CreateClient();
        using var content = new MultipartFormDataContent();
        using var file = new StreamContent(package);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "package", "package.nupkg");

        using var request = new HttpRequestMessage(HttpMethod.Put, $"nuget/{feed}/") { Content = content };
        request.Headers.Add("X-NuGet-ApiKey", FiGetServerFixture.AdminToken);
        var response = await client.SendAsync(request);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<value>[^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex AntiforgeryPattern();
}
