using System.Net;
using System.Net.Http.Headers;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Http;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Testing;
using FiGet.Web.Components.Shared;
using FiGet.Web.Connectors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FiGet.Integration.Tests;

/// <summary>A server of its own, so no other test's traffic is counted with these.</summary>
public sealed class FeedUsageServerFixture() : FiGetServerFixture(TestDatabase.Sqlite);

/// <summary>
/// A server for the counting test alone. Its siblings in <see cref="FeedUsageTests"/> read and search the same feed in
/// the same hour, and which of them runs first is not fixed: a fresh clone compiles the test files in another order
/// than a working tree that grew them one at a time, so the counts were right here and wrong in CI.
/// </summary>
public sealed class FeedUsageCountingServerFixture() : FiGetServerFixture(TestDatabase.Sqlite);

/// <summary>
/// The usage graph on the feed lists, asked for by the tester: every download and search counted per feed and hour, drawn
/// as a line per feed in the feed's colour. Anonymous by construction.
/// </summary>
public sealed class FeedUsageTests(FeedUsageServerFixture server) : IClassFixture<FeedUsageServerFixture>
{
    /// <summary>The feed list draws a line and a legend entry per feed, in the feed's colour, for the period asked.</summary>
    [Fact]
    public async Task The_feed_list_draws_a_line_per_feed_in_its_colour()
    {
        var feed = await FeedAsync("public");
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var now = DateTime.UtcNow;
            var hour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
            await scope.ServiceProvider.GetRequiredService<IFeedUsageStore>().AddAsync(
                [new FeedUsageCount(feed.Key, hour, FeedUsageKind.Search, 7), new FeedUsageCount(feed.Key, hour.AddHours(-3), FeedUsageKind.Search, 5)],
                CancellationToken.None);
        }

        using var client = server.CreateClient();
        var page = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/?usage=24h&metric=searches"));
        var color = FeedColors.Of(feed);
        Assert.Contains($"class=\"fg-usage-line\" style=\"--series: var(--fg-series-{color})\"", page, StringComparison.Ordinal);
        Assert.Matches($"--fg-series-{color}\\)\" aria-hidden=\"true\"></span>\\s*<span>public</span>\\s*<span class=\"fg-num fg-muted\">\\d", page);
        Assert.Contains("class=\"fg-pill active\" href=\"/?usage=24h&amp;metric=searches#usage-title\"", page, StringComparison.Ordinal);

        // A feed the reader may not read is neither listed nor drawn.
        Assert.DoesNotContain("<span>private</span>", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Hours_fall_into_whole_buckets_that_end_with_the_current_one()
    {
        var feed = new Feed { Key = 3, Name = "f", NameLower = "f" };
        var now = new DateTime(2026, 9, 14, 13, 25, 0, DateTimeKind.Utc);
        var hours = new[]
        {
            new FeedUsageCount(3, new DateTime(2026, 9, 14, 13, 0, 0, DateTimeKind.Utc), FeedUsageKind.Download, 4),
            new FeedUsageCount(3, new DateTime(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc), FeedUsageKind.Download, 1),
            new FeedUsageCount(3, new DateTime(2026, 9, 14, 11, 0, 0, DateTimeKind.Utc), FeedUsageKind.Download, 2),
            new FeedUsageCount(3, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), FeedUsageKind.Download, 99),
        };

        var day = UsageSeries.Build([feed], hours, UsageRange.Parse("24h"), now);
        Assert.Equal(24, day.Starts.Count);
        Assert.Equal(new DateTime(2026, 9, 14, 13, 0, 0, DateTimeKind.Utc), day.Starts[^1]);
        Assert.Equal([2L, 1L, 4L], day.Lines[0].Points.TakeLast(3));
        Assert.Equal(7, day.Lines[0].Total);

        // Seven days in six-hour points: 11:00, 12:00 and 13:00 share the one that starts at 12:00, bar 11:00.
        var week = UsageSeries.Build([feed], hours, UsageRange.Parse("7d"), now);
        Assert.Equal(28, week.Starts.Count);
        Assert.Equal(new DateTime(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc), week.Starts[^1]);
        Assert.Equal([2L, 5L], week.Lines[0].Points.TakeLast(2));

        Assert.Equal(10, new UsageSeries([], [], 7).ScaleTop());
        Assert.Equal(200, new UsageSeries([], [], 130).ScaleTop());
    }

    private async Task<Feed> FeedAsync(string name)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IFeedStore>().FindAsync(name, CancellationToken.None))!;
    }

    /// <summary>
    /// Flushes and reads back, waiting a little: a request is counted after its response is written, which the client may
    /// already have read.
    /// </summary>
    private async Task<(long Downloads, long Searches, long Private)> CountsAsync(int feedKey, int privateKey, (long Downloads, long Searches) expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            await FeedUsageWriterService.FlushAsync(server.Services.GetRequiredService<FeedUsageCounter>(), server.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, CancellationToken.None);
            await using var scope = server.Services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IFeedUsageStore>();
            var from = DateTime.UtcNow.AddHours(-2);
            var downloads = (await store.ListAsync([feedKey], FeedUsageKind.Download, from, CancellationToken.None)).Sum(c => c.Count);
            var searches = (await store.ListAsync([feedKey], FeedUsageKind.Search, from, CancellationToken.None)).Sum(c => c.Count);
            var refused = (await store.ListAsync([privateKey], FeedUsageKind.Search, from, CancellationToken.None)).Sum(c => c.Count);
            if ((downloads, searches) == expected || DateTime.UtcNow > deadline)
            {
                // A moment more, so a count that should not be there has had the time to arrive too.
                await Task.Delay(300);
                await FeedUsageWriterService.FlushAsync(server.Services.GetRequiredService<FeedUsageCounter>(), server.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, CancellationToken.None);
                downloads = (await store.ListAsync([feedKey], FeedUsageKind.Download, from, CancellationToken.None)).Sum(c => c.Count);
                searches = (await store.ListAsync([feedKey], FeedUsageKind.Search, from, CancellationToken.None)).Sum(c => c.Count);
                refused = (await store.ListAsync([privateKey], FeedUsageKind.Search, from, CancellationToken.None)).Sum(c => c.Count);
                return (downloads, searches, refused);
            }

            await Task.Delay(100);
        }
    }
}

/// <summary>
/// Counting, on a server nothing else touches. Its assertions are exact totals for one feed in one hour, so a sibling
/// test searching the same feed would break them; that is what happened when a fresh clone compiled the test files in
/// a different order from the working tree they were written in (2026-09-16).
/// </summary>
public sealed class FeedUsageCountingTests(FeedUsageCountingServerFixture server) : IClassFixture<FeedUsageCountingServerFixture>
{
    /// <summary>
    /// What counts: a download, and a search's first page. What does not: later pages, a count-only request, HEAD, a
    /// resumed download, and anything refused.
    /// </summary>
    [Fact]
    public async Task Downloads_and_searches_are_counted_once_each_and_refused_requests_not_at_all()
    {
        var id = FiGetServerFixture.UniqueId("Usage.Counted");
        using (var admin = server.CreateClient(FiGetServerFixture.AdminToken))
        using (var package = TestPackages.Create(id, "1.0.0"))
        using (var content = new MultipartFormDataContent())
        using (var file = new StreamContent(package))
        {
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(file, "package", "package.nupkg");
            HttpAssert.Status(HttpStatusCode.Created, await admin.PutAsync("nuget/public/", content));
        }

        var lower = id.ToLowerInvariant();
        var nupkg = $"nuget/public/v3/flatcontainer/{lower}/1.0.0/{lower}.1.0.0.nupkg";
        using var client = server.CreateClient();

        // Two downloads, one v3 and one v2.
        HttpAssert.Status(HttpStatusCode.OK, await client.GetAsync(nupkg));
        HttpAssert.Status(HttpStatusCode.OK, await client.GetAsync($"nuget/public/package/{id}/1.0.0"));

        // Not downloads: HEAD, and the rest of a download that was resumed.
        using (await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, nupkg)))
        {
        }

        using (var resumed = new HttpRequestMessage(HttpMethod.Get, nupkg))
        {
            resumed.Headers.Range = new RangeHeaderValue(10, null);
            HttpAssert.Status(HttpStatusCode.PartialContent, await client.SendAsync(resumed));
        }

        // Three searches: a v2 search's first page, a v3 search, a version look-up. Not searches: a later page, a count.
        HttpAssert.Status(HttpStatusCode.OK, await client.GetAsync("nuget/public/Search()?searchTerm=''&$skip=0&$top=40"));
        HttpAssert.Status(HttpStatusCode.OK, await client.GetAsync("nuget/public/Search()?searchTerm=''&$skip=40&$top=40"));
        HttpAssert.Status(HttpStatusCode.OK, await client.GetAsync("nuget/public/v3/query?q=usage"));
        HttpAssert.Status(HttpStatusCode.OK, await client.GetAsync($"nuget/public/v3/flatcontainer/{lower}/index.json"));
        HttpAssert.Status(HttpStatusCode.OK, await client.GetAsync($"nuget/public/FindPackagesById()/$count?id='{id}'"));

        // Refused: a feed that needs credentials, asked without any.
        HttpAssert.Status(HttpStatusCode.Unauthorized, await client.GetAsync("nuget/private/v3/query?q=usage"));

        var publicKey = (await FeedAsync("public")).Key;
        var privateKey = (await FeedAsync("private")).Key;
        Assert.Equal((2L, 3L, 0L), await CountsAsync(publicKey, privateKey, expected: (2, 3)));
    }

    private async Task<Feed> FeedAsync(string name)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IFeedStore>().FindAsync(name, CancellationToken.None))!;
    }

    /// <summary>
    /// Flushes and reads back, waiting a little: a request is counted after its response is written, which the client may
    /// already have read.
    /// </summary>
    private async Task<(long Downloads, long Searches, long Private)> CountsAsync(int feedKey, int privateKey, (long Downloads, long Searches) expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            await FeedUsageWriterService.FlushAsync(server.Services.GetRequiredService<FeedUsageCounter>(), server.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, CancellationToken.None);
            await using var scope = server.Services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IFeedUsageStore>();
            var from = DateTime.UtcNow.AddHours(-2);
            var downloads = (await store.ListAsync([feedKey], FeedUsageKind.Download, from, CancellationToken.None)).Sum(c => c.Count);
            var searches = (await store.ListAsync([feedKey], FeedUsageKind.Search, from, CancellationToken.None)).Sum(c => c.Count);
            var refused = (await store.ListAsync([privateKey], FeedUsageKind.Search, from, CancellationToken.None)).Sum(c => c.Count);
            if ((downloads, searches) == expected || DateTime.UtcNow > deadline)
            {
                // A moment more, so a count that should not be there has had the time to arrive too.
                await Task.Delay(300);
                await FeedUsageWriterService.FlushAsync(server.Services.GetRequiredService<FeedUsageCounter>(), server.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, CancellationToken.None);
                downloads = (await store.ListAsync([feedKey], FeedUsageKind.Download, from, CancellationToken.None)).Sum(c => c.Count);
                searches = (await store.ListAsync([feedKey], FeedUsageKind.Search, from, CancellationToken.None)).Sum(c => c.Count);
                refused = (await store.ListAsync([privateKey], FeedUsageKind.Search, from, CancellationToken.None)).Sum(c => c.Count);
                return (downloads, searches, refused);
            }

            await Task.Delay(100);
        }
    }
}
