using System.Net;
using System.Text.RegularExpressions;
using FiGet.Application.Packages;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Infrastructure.Persistence;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

public sealed class SqliteRetentionTests(SqliteServerFixture fixture) : RetentionTests(fixture), IClassFixture<SqliteServerFixture>;

public sealed class SqlServerRetentionTests(SqlServerServerFixture fixture) : RetentionTests(fixture), IClassFixture<SqlServerServerFixture>;

/// <summary>
/// Retention and cache pruning against stored packages and files: the feed page previews, "Run now" removes what the
/// preview listed, deleted versions lose their files, and a download keeps a cached copy from being pruned.
/// </summary>
public abstract partial class RetentionTests
{
    private readonly FiGetServerFixture server;

    protected RetentionTests(FiGetServerFixture server)
    {
        this.server = server;
        server.SkipIfUnavailable();
    }

    [Fact]
    public async Task Run_now_deletes_what_the_preview_listed_and_keeps_the_newest()
    {
        var feed = await CreateFeedAsync(PackageDeletionBehavior.HardDelete);
        var id = FiGetServerFixture.UniqueId("Retain.Me");
        foreach (var version in new[] { "1.0.0", "1.1.0", "1.2.0" })
        {
            await StoreAsync(feed, id, version, PackageOrigin.Pushed);
        }

        await SetRulesAsync(feed, new RetentionRules(KeepStable: 1));

        using var admin = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(admin));
        var page = await HttpAssert.SuccessBodyAsync(await admin.GetAsync($"/admin/feeds/{feed}/retention"));
        Assert.Contains("2 version(s) would go", page, StringComparison.Ordinal);

        using (var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = Antiforgery(page) }))
        {
            var ran = await admin.PostAsync($"/admin/feeds/{feed}/retention/run", content);
            HttpAssert.Status(HttpStatusCode.Redirect, ran);
            Assert.StartsWith($"/admin/feeds/{feed}/retention?retention=0.2.0.", ran.Headers.Location!.OriginalString, StringComparison.Ordinal);
        }

        Assert.Equal(["1.2.0"], await VersionsAsync(feed, id));
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-NuGet-ApiKey", FiGetServerFixture.AdminToken);
        var lower = id.ToLowerInvariant();
        HttpAssert.Status(HttpStatusCode.NotFound, await client.GetAsync($"nuget/{feed}/v3/flatcontainer/{lower}/1.0.0/{lower}.1.0.0.nupkg"));
        HttpAssert.Status(HttpStatusCode.OK, await client.GetAsync($"nuget/{feed}/v3/flatcontainer/{lower}/1.2.0/{lower}.1.2.0.nupkg"));
    }

    [Fact]
    public async Task Cached_copies_nobody_downloads_are_pruned_and_a_download_keeps_one()
    {
        var feed = await CreateFeedAsync(PackageDeletionBehavior.Unlist);
        var id = FiGetServerFixture.UniqueId("Prune.Me");
        await StoreAsync(feed, id, "1.0.0", PackageOrigin.Cached);
        await StoreAsync(feed, id, "2.0.0", PackageOrigin.Cached);
        await StoreAsync(feed, id, "3.0.0", PackageOrigin.Pushed);
        await AgeAsync(feed, id, days: 60);
        await SetRulesAsync(feed, new RetentionRules(PruneCachedAfterDays: 30));

        // Downloading 2.0.0 marks it used today.
        using (var client = server.CreateClient())
        {
            client.DefaultRequestHeaders.Add("X-NuGet-ApiKey", FiGetServerFixture.AdminToken);
            var lower = id.ToLowerInvariant();
            HttpAssert.Status(HttpStatusCode.OK, await client.GetAsync($"nuget/{feed}/v3/flatcontainer/{lower}/2.0.0/{lower}.2.0.0.nupkg"));
        }

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var target = (await scope.ServiceProvider.GetRequiredService<IFeedStore>().FindAsync(feed, CancellationToken.None))!;
            var report = await scope.ServiceProvider.GetRequiredService<RetentionService>().RunAsync(target, CancellationToken.None);
            Assert.Equal(1, report.Pruned);
            Assert.True(report.FreedBytes > 0);
        }

        Assert.Equal(["2.0.0", "3.0.0"], await VersionsAsync(feed, id));
    }

    private async Task<string> CreateFeedAsync(PackageDeletionBehavior deletion)
    {
        var name = "ret" + Guid.NewGuid().ToString("N")[..8];
        await using var scope = server.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IFeedStore>().CreateAsync(
            new Feed { Name = name, NameLower = name, Kind = FeedKind.Curated, DeletionBehavior = deletion, CreatedUtc = DateTime.UtcNow },
            CancellationToken.None));
        return name;
    }

    private async Task StoreAsync(string feed, string id, string version, PackageOrigin origin)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var target = (await scope.ServiceProvider.GetRequiredService<IFeedStore>().FindAsync(feed, CancellationToken.None))!;
        using var package = TestPackages.Create(id, version);
        var result = await scope.ServiceProvider.GetRequiredService<PackageIngestionService>().PushAsync(target, package, origin, CancellationToken.None);
        Assert.Equal(PushOutcome.Created, result.Outcome);
    }

    private async Task SetRulesAsync(string feed, RetentionRules rules)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var feeds = scope.ServiceProvider.GetRequiredService<IFeedStore>();
        Assert.True(await feeds.UpdateRetentionAsync((await feeds.FindAsync(feed, CancellationToken.None))!.Key, rules, CancellationToken.None));
    }

    private async Task AgeAsync(string feed, string id, int days)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FiGetDbContext>();
        var lower = id.ToLowerInvariant();
        var feedLower = feed.ToLowerInvariant();
        var old = DateTime.UtcNow.AddDays(-days);
        await db.PackageVersions
            .Where(v => v.Package!.IdLower == lower && v.Package.Feed!.NameLower == feedLower)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.LastUsedUtc, old), TestContext.Current.CancellationToken);
    }

    private async Task<string[]> VersionsAsync(string feed, string id)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var target = (await scope.ServiceProvider.GetRequiredService<IFeedStore>().FindAsync(feed, CancellationToken.None))!;
        var package = await scope.ServiceProvider.GetRequiredService<IPackageStore>().GetPackageAsync(target.Key, id.ToLowerInvariant(), false, CancellationToken.None);
        return package?.Versions.Select(v => v.NormalizedVersion).Order(StringComparer.Ordinal).ToArray() ?? [];
    }

    private HttpClient CreateBrowser() =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = server.BaseAddress };

    private static string Antiforgery(string page) =>
        WebUtility.HtmlDecode(AntiforgeryPattern().Match(page).Groups["value"].Value);

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<value>[^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex AntiforgeryPattern();
}
