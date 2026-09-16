using System.Net;
using System.Net.Http.Headers;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

public sealed class SqliteChangeReportQueryTests(SqliteServerFixture fixture) : ChangeReportQueryTests(fixture), IClassFixture<SqliteServerFixture>;

public sealed class SqlServerChangeReportQueryTests(SqlServerServerFixture fixture) : ChangeReportQueryTests(fixture), IClassFixture<SqlServerServerFixture>;

/// <summary>
/// The reads a change report is built from. Each test works in a feed of its own, so what another test pushes - or
/// what a fresh clone's compile order happens to run first - cannot reach these assertions.
/// </summary>
public abstract class ChangeReportQueryTests
{
    private readonly FiGetServerFixture server;

    protected ChangeReportQueryTests(FiGetServerFixture server)
    {
        this.server = server;
        server.SkipIfUnavailable();
    }

    [Fact]
    public async Task Only_versions_published_inside_the_window_are_listed()
    {
        var feed = await CreateFeedAsync();
        var id = FiGetServerFixture.UniqueId("Query.Window");
        await PushAsync(feed, id, "1.0.0");
        await PushAsync(feed, id, "1.1.0");

        // Push time is the publish time for a pushed version, so the window is moved rather than the rows.
        var (key, now) = await FeedKeyAndNowAsync(feed);
        await using var scope = server.Services.CreateAsyncScope();
        var packages = scope.ServiceProvider.GetRequiredService<IPackageStore>();

        var inside = await packages.ListPublishedBetweenAsync(key, now.AddMinutes(-10), now.AddMinutes(10), 100, TestContext.Current.CancellationToken);
        Assert.Equal(["1.1.0", "1.0.0"], inside.Select(v => v.NormalizedVersion).ToArray());
        Assert.All(inside, v => Assert.Equal(PackageOrigin.Pushed, v.Origin));
        Assert.All(inside, v => Assert.Equal(id, v.Id));

        // Half-open: a window that ends before now holds nothing, and one that starts after now holds nothing.
        Assert.Empty(await packages.ListPublishedBetweenAsync(key, now.AddHours(-2), now.AddHours(-1), 100, TestContext.Current.CancellationToken));
        Assert.Empty(await packages.ListPublishedBetweenAsync(key, now.AddHours(1), now.AddHours(2), 100, TestContext.Current.CancellationToken));
    }

    /// <summary>A version the feed no longer offers is not news; it is the opposite.</summary>
    [Fact]
    public async Task An_unlisted_version_is_not_listed()
    {
        var feed = await CreateFeedAsync();
        var id = FiGetServerFixture.UniqueId("Query.Unlisted");
        await PushAsync(feed, id, "1.0.0");
        await PushAsync(feed, id, "2.0.0");

        using (var client = server.CreateClient(FiGetServerFixture.AdminToken))
        {
            HttpAssert.Status(HttpStatusCode.NoContent, await client.DeleteAsync($"nuget/{feed}/v3/publish/{id}/2.0.0"));
        }

        var (key, now) = await FeedKeyAndNowAsync(feed);
        await using var scope = server.Services.CreateAsyncScope();
        var rows = await scope.ServiceProvider.GetRequiredService<IPackageStore>()
            .ListPublishedBetweenAsync(key, now.AddMinutes(-10), now.AddMinutes(10), 100, TestContext.Current.CancellationToken);

        Assert.Equal(["1.0.0"], rows.Select(v => v.NormalizedVersion).ToArray());
    }

    [Fact]
    public async Task The_versions_a_feed_holds_come_back_as_strings()
    {
        var feed = await CreateFeedAsync();
        var id = FiGetServerFixture.UniqueId("Query.Held");
        var other = FiGetServerFixture.UniqueId("Query.Other");
        await PushAsync(feed, id, "1.0.0");
        await PushAsync(feed, id, "1.9.4");
        await PushAsync(feed, other, "3.0.0");

        var (key, _) = await FeedKeyAndNowAsync(feed);
        await using var scope = server.Services.CreateAsyncScope();
        var packages = scope.ServiceProvider.GetRequiredService<IPackageStore>();

        var held = await packages.ListHeldVersionsAsync(key, [id.ToLowerInvariant()], TestContext.Current.CancellationToken);
        Assert.Equal(["1.0.0", "1.9.4"], held.Select(v => v.NormalizedVersion).Order(StringComparer.Ordinal).ToArray());
        Assert.All(held, v => Assert.True(v.Listed));

        // The other id is in the same feed and must not answer a question about this one.
        Assert.Empty(await packages.ListHeldVersionsAsync(key, [FiGetServerFixture.UniqueId("Query.Absent").ToLowerInvariant()], TestContext.Current.CancellationToken));
        Assert.Equal(
            [id.ToLowerInvariant(), other.ToLowerInvariant()],
            (await packages.ListIdsAsync(key, TestContext.Current.CancellationToken)).Order(StringComparer.Ordinal).ToArray());
    }

    private async Task<string> CreateFeedAsync()
    {
        var name = FiGetServerFixture.UniqueId("query").ToLowerInvariant().Replace('.', '-');
        await using var scope = server.Services.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<IFeedStore>().CreateAsync(
            new Feed { Name = name, NameLower = name, AnonymousRead = true, CreatedUtc = DateTime.UtcNow },
            TestContext.Current.CancellationToken);
        Assert.True(created, $"Could not create the feed '{name}'.");
        return name;
    }

    private async Task<(int Key, DateTime Now)> FeedKeyAndNowAsync(string name)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var feed = await scope.ServiceProvider.GetRequiredService<IFeedStore>().FindAsync(name, TestContext.Current.CancellationToken);
        return (feed!.Key, DateTime.UtcNow);
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
