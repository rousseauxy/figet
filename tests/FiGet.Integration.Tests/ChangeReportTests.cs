using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FiGet.Application.Ports;
using FiGet.Application.Reports;
using FiGet.Domain.Entities;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

/// <summary>
/// What a change report says, against a stub gallery. On the proxy fixture, so the upstream half is exercised with the
/// same rules a listing uses; each test works with ids of its own, so nothing another test pushes reaches it.
/// </summary>
public sealed class ChangeReportTests(ProxyServerFixture server) : IClassFixture<ProxyServerFixture>
{
    /// <summary>A version pushed here is news, and it knows what it replaced.</summary>
    [Fact]
    public async Task A_pushed_version_is_reported_with_what_came_before_it()
    {
        var id = FiGetServerFixture.UniqueId("Report.Pushed");
        await PushAsync("proxy", id, "1.9.4");
        await PushAsync("proxy", id, "2.0.0");

        var report = await BuildAsync("proxy", 1);
        var change = Assert.Single(report.Of(PackageChangeKind.Pushed), c => c.Id == id && c.Version == "2.0.0");

        Assert.Equal("1.9.4", change.PreviousVersion);
        Assert.True(change.Breaking, "A major version grew, which is the one thing a reader wants marked.");
        Assert.Equal("", change.Upstream);

        var first = Assert.Single(report.Of(PackageChangeKind.Pushed), c => c.Id == id && c.Version == "1.9.4");
        Assert.Null(first.PreviousVersion);
        Assert.False(first.Breaking);
    }

    /// <summary>A copy fetched from a gallery is reported with the gallery's publish date, not the day it was fetched.</summary>
    [Fact]
    public async Task A_cached_copy_is_reported_with_the_gallerys_publish_date()
    {
        var id = FiGetServerFixture.UniqueId("Report.Cached");
        var published = DateTime.UtcNow.AddHours(-3);
        await SeedAsync(id, "proxy", ("1.0.0", published, true));

        var change = Assert.Single((await BuildAsync("proxy", 1)).Of(PackageChangeKind.Cached), c => c.Id == id);

        Assert.Equal("1.0.0", change.Version);
        Assert.Equal(published, change.PublishedUtc, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// The early warning: the gallery has something newer than anything here, nobody has fetched it, and that is
    /// precisely when somebody wants to check their scripts.
    /// </summary>
    [Fact]
    public async Task A_newer_upstream_version_nobody_fetched_is_reported()
    {
        var id = FiGetServerFixture.UniqueId("Report.Upstream");
        await SeedAsync(id, "proxy", ("1.0.0", DateTime.UtcNow.AddDays(-30), true), ("2.0.0", DateTime.UtcNow.AddHours(-2), false));

        var change = Assert.Single((await BuildAsync("proxy", 1)).Of(PackageChangeKind.Upstream), c => c.Id == id);

        Assert.Equal("2.0.0", change.Version);
        Assert.Equal("1.0.0", change.PreviousVersion);
        Assert.True(change.Breaking);
        Assert.Equal("stub", change.Upstream);
        Assert.Equal("", change.ReleaseNotes);
    }

    /// <summary>
    /// The premise of the whole report: a gallery publishing something nobody here uses is not this feed's news,
    /// however new it is. Without this line the report is the gallery's changelog again.
    /// </summary>
    [Fact]
    public async Task An_id_the_feed_does_not_hold_is_never_reported()
    {
        var id = FiGetServerFixture.UniqueId("Report.Stranger");
        await SeedAsync(id, "proxy", ("1.0.0", DateTime.UtcNow.AddMinutes(-5), false));

        var report = await BuildAsync("proxy", 1);

        Assert.DoesNotContain(report.Changes, c => c.Id == id);
    }

    /// <summary>A backport below what we already hold is not an update to anything.</summary>
    [Fact]
    public async Task An_upstream_version_below_the_one_we_hold_is_not_reported()
    {
        var id = FiGetServerFixture.UniqueId("Report.Backport");
        await SeedAsync(id, "proxy", ("2.0.0", DateTime.UtcNow.AddDays(-30), true), ("1.9.5", DateTime.UtcNow.AddHours(-1), false));

        Assert.DoesNotContain((await BuildAsync("proxy", 1)).Of(PackageChangeKind.Upstream), c => c.Id == id);
    }

    /// <summary>
    /// One row per id, the highest. A gallery that has been ahead of us for a year publishes most weeks, and a row
    /// for each of them every day is a report nobody reads.
    /// </summary>
    [Fact]
    public async Task Only_the_highest_upstream_version_in_the_window_is_reported()
    {
        var id = FiGetServerFixture.UniqueId("Report.Highest");
        await SeedAsync(
            id,
            "proxy",
            ("1.0.0", DateTime.UtcNow.AddDays(-30), true),
            ("2.0.0", DateTime.UtcNow.AddHours(-5), false),
            ("2.1.0", DateTime.UtcNow.AddHours(-4), false));

        var change = Assert.Single((await BuildAsync("proxy", 1)).Of(PackageChangeKind.Upstream), c => c.Id == id);

        Assert.Equal("2.1.0", change.Version);
    }

    /// <summary>A version published before the window did not become news by our asking about it later.</summary>
    [Fact]
    public async Task A_version_published_before_the_window_is_not_reported()
    {
        var id = FiGetServerFixture.UniqueId("Report.Old");
        await SeedAsync(id, "proxy", ("1.0.0", DateTime.UtcNow.AddDays(-30), true), ("2.0.0", DateTime.UtcNow.AddDays(-20), false));

        Assert.DoesNotContain((await BuildAsync("proxy", 1)).Changes, c => c.Id == id);
        Assert.Contains((await BuildAsync("proxy", 30)).Of(PackageChangeKind.Upstream), c => c.Id == id && c.Version == "2.0.0");
    }

    /// <summary>
    /// An id pushed here is served from here alone, so a gallery's package of the same name is not an update to it -
    /// it is somebody else's package. The report obeys that rule because it asks the connector, rather than comparing
    /// version numbers on its own; the feed next door, which merges the two, does report it.
    /// </summary>
    [Fact]
    public async Task A_gallery_version_of_a_pushed_id_is_reported_only_where_the_feed_merges_them()
    {
        var id = FiGetServerFixture.UniqueId("Report.SameName");

        // On the gallery before the push: a push asks the upstreams whether they hold the id, to warn about pushing
        // over a name a gallery already uses, and "no such package" is remembered for minutes afterwards.
        using (var package = TestPackages.Create(id, "2.0.0"))
        {
            server.Upstream.Add(id, "2.0.0", package.ToArray(), DateTime.UtcNow.AddHours(-1));
        }

        await PushAsync("proxy", id, "1.0.0");
        await PushAsync("merging", id, "1.0.0");

        // Listed on the feed that merges the two: the other one serves a pushed id from here alone and never asks.
        await SeedAsync(id, "merging");

        Assert.DoesNotContain((await BuildAsync("proxy", 1)).Of(PackageChangeKind.Upstream), c => c.Id == id);
        Assert.Contains((await BuildAsync("merging", 1)).Of(PackageChangeKind.Upstream), c => c.Id == id && c.Version == "2.0.0");
    }

    /// <summary>A feed without upstreams has only its own pushes to report, and says so rather than failing.</summary>
    [Fact]
    public async Task A_feed_without_upstreams_reports_only_what_was_pushed()
    {
        var id = FiGetServerFixture.UniqueId("Report.Curated");
        await PushAsync("public", id, "1.0.0");

        var report = await BuildAsync("public", 1);

        Assert.Equal("public", report.Feed);
        Assert.Contains(report.Of(PackageChangeKind.Pushed), c => c.Id == id);
        Assert.Empty(report.Of(PackageChangeKind.Upstream));
    }

    /// <summary>The route a script calls, answering the same report the page shows.</summary>
    [Fact]
    public async Task The_changes_route_answers_the_report_as_json()
    {
        var id = FiGetServerFixture.UniqueId("Report.Api");
        await PushAsync("proxy", id, "1.0.0");
        await PushAsync("proxy", id, "2.0.0");

        using var client = server.CreateClient();
        var body = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync("api/packages/proxy/changes?days=1")))!;

        Assert.Equal("proxy", (string?)body["feed"]);
        Assert.Equal(1, (int?)body["days"]);
        Assert.False((bool?)body["truncated"]);

        var change = body["changes"]!.AsArray().Single(c => (string?)c!["name"] == id && (string?)c["version"] == "2.0.0")!;
        Assert.Equal("pushed", (string?)change["kind"]);
        Assert.Equal("1.0.0", (string?)change["previousVersion"]);
        Assert.True((bool?)change["breaking"]);

        // Absent rather than empty: the reader of a JSON document should not have to tell "" from "nothing to say".
        Assert.Null(change["upstream"]);
    }

    /// <summary>A hand-edited window is clamped, never refused: the worst it should do is show a different week.</summary>
    [Theory]
    [InlineData("?days=100000", 90)]
    [InlineData("?days=-5", 1)]
    [InlineData("?days=abc", 7)]
    [InlineData("", 7)]
    public async Task The_window_is_clamped_rather_than_refused(string query, int days)
    {
        using var client = server.CreateClient();
        var body = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync("api/packages/proxy/changes" + query)))!;

        Assert.Equal(days, (int?)body["days"]);
    }

    /// <summary>The same gate as browsing the feed: what a reader may not see, a script may not read either.</summary>
    [Fact]
    public async Task The_changes_route_needs_read_on_the_feed()
    {
        using (var anonymous = server.CreateClient())
        {
            HttpAssert.Status(HttpStatusCode.Unauthorized, await anonymous.GetAsync("api/packages/private/changes"));
        }

        using var admin = server.CreateClient(FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.OK, await admin.GetAsync("api/packages/private/changes"));
    }

    private async Task<ChangeReport> BuildAsync(string feed, int days)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var feeds = scope.ServiceProvider.GetRequiredService<IFeedStore>();
        var reports = scope.ServiceProvider.GetRequiredService<ChangeReportService>();
        var found = await feeds.FindAsync(feed, TestContext.Current.CancellationToken);
        return await reports.BuildAsync(found!, days, DateTime.UtcNow, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Puts versions on the gallery, fetches the ones marked cached, and then lists the id once so the catalogue and
    /// its descriptions are stored - which is where the report reads them, and why it costs a gallery nothing.
    ///
    /// Every version is seeded before anything asks: a catalogue is cached for minutes, so a version added after the
    /// first listing would not be fetched again inside a test, and the report would rightly not know about it.
    /// </summary>
    private async Task SeedAsync(string id, string feed, params (string Version, DateTime Published, bool Cached)[] versions)
    {
        foreach (var (version, published, _) in versions)
        {
            using var package = TestPackages.Create(id, version);
            server.Upstream.Add(id, version, package.ToArray(), published);
        }

        using var client = server.CreateClient();
        foreach (var (version, _, _) in versions.Where(v => v.Cached))
        {
            HttpAssert.Status(HttpStatusCode.OK, await client.GetAsync($"nuget/{feed}/package/{id}/{version}"));
        }

        HttpAssert.Status(HttpStatusCode.OK, await client.GetAsync($"nuget/{feed}/FindPackagesById()?id='{id}'"));
    }

    private async Task PushAsync(string feed, string id, string version)
    {
        using var client = server.CreateClient(FiGetServerFixture.AdminToken);
        using var package = TestPackages.Create(id, version);
        using var content = new MultipartFormDataContent();
        var file = new StreamContent(package);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "package", "package.nupkg");
        HttpAssert.Status(HttpStatusCode.Created, await client.PutAsync($"nuget/{feed}/v3/publish", content));
    }
}
