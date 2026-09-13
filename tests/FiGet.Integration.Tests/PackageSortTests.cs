using System.Net;
using System.Net.Http.Headers;
using FiGet.Application.Ports;
using FiGet.Domain.Search;
using FiGet.Infrastructure.Persistence;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

public sealed class SqlitePackageSortTests(SqliteServerFixture fixture) : PackageSortTests(fixture), IClassFixture<SqliteServerFixture>;

public sealed class SqlServerPackageSortTests(SqlServerServerFixture fixture) : PackageSortTests(fixture), IClassFixture<SqlServerServerFixture>;

/// <summary>
/// Ordering a feed's package list by a column, done by the database on every provider: the aggregates over versions
/// translate differently in SQLite and SQL Server, and a page of fifty is only right if the whole list was ordered.
/// </summary>
public abstract class PackageSortTests
{
    private readonly FiGetServerFixture server;

    protected PackageSortTests(FiGetServerFixture server)
    {
        this.server = server;
        server.SkipIfUnavailable();
    }

    [Fact]
    public async Task Packages_order_by_each_column_in_both_directions()
    {
        var token = "sort" + Guid.NewGuid().ToString("N")[..8];
        // a: 3 versions, 5 downloads, newest 2026-03; b: 1 version, 50 downloads, newest 2026-01; c: 2 versions, 20 downloads, newest 2026-05.
        await SeedAsync($"{token}.a", ["1.0.0", "1.1.0", "1.2.0"], downloads: 5, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        await SeedAsync($"{token}.b", ["1.0.0"], downloads: 50, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await SeedAsync($"{token}.c", ["1.0.0", "2.0.0"], downloads: 20, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(["a", "b", "c"], await OrderAsync(token, new PackageSort(PackageSortField.Id, false)));
        Assert.Equal(["c", "b", "a"], await OrderAsync(token, new PackageSort(PackageSortField.Id, true)));
        Assert.Equal(["b", "c", "a"], await OrderAsync(token, new PackageSort(PackageSortField.Versions, false)));
        Assert.Equal(["a", "c", "b"], await OrderAsync(token, new PackageSort(PackageSortField.Versions, true)));
        Assert.Equal(["a", "c", "b"], await OrderAsync(token, new PackageSort(PackageSortField.Downloads, false)));
        Assert.Equal(["b", "c", "a"], await OrderAsync(token, new PackageSort(PackageSortField.Downloads, true)));
        Assert.Equal(["b", "a", "c"], await OrderAsync(token, new PackageSort(PackageSortField.LastPublished, false)));
        Assert.Equal(["c", "a", "b"], await OrderAsync(token, new PackageSort(PackageSortField.LastPublished, true)));
    }

    /// <summary>Equal values fall back to the id, so a page boundary between them lands in the same place every time.</summary>
    [Fact]
    public async Task Equal_values_are_ordered_by_id_so_pages_are_stable()
    {
        var token = "tie" + Guid.NewGuid().ToString("N")[..8];
        foreach (var name in new[] { "c", "a", "b" })
        {
            await SeedAsync($"{token}.{name}", ["1.0.0"], downloads: 7, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        }

        Assert.Equal(["a", "b", "c"], await OrderAsync(token, new PackageSort(PackageSortField.Downloads, true)));

        await using var scope = server.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IPackageStore>();
        var feed = await scope.ServiceProvider.GetRequiredService<IFeedStore>().FindAsync("public", TestContext.Current.CancellationToken);
        var filter = new PackageSearchFilter(SearchQueryParser.Parse(token), true, true, null, new PackageSort(PackageSortField.Downloads, true));
        var first = await store.SearchAsync(feed!.Key, filter, 0, 2, TestContext.Current.CancellationToken);
        var second = await store.SearchAsync(feed.Key, filter, 2, 2, TestContext.Current.CancellationToken);
        Assert.Equal(3, first.PackageKeys.Concat(second.PackageKeys).Distinct().Count());
    }

    /// <summary>The page a reader without a token sees sorts through its header links, and says which column is in use.</summary>
    [Fact]
    public async Task The_anonymous_package_table_sorts_from_its_headers()
    {
        var token = "hdr" + Guid.NewGuid().ToString("N")[..8];
        await SeedAsync($"{token}.small", ["1.0.0"], downloads: 1, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await SeedAsync($"{token}.large", ["1.0.0"], downloads: 99, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        using var client = server.CreateClient();
        var page = await HttpAssert.SuccessBodyAsync(await client.GetAsync($"feeds/public?q={token}&sort=downloads&dir=desc", TestContext.Current.CancellationToken));

        Assert.True(page.IndexOf($"{token}.large", StringComparison.OrdinalIgnoreCase) < page.IndexOf($"{token}.small", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("aria-sort=\"descending\"", page, StringComparison.Ordinal);

        // Clicking the column in use reverses it; the search is kept.
        Assert.Contains($"q={token}&amp;sort=downloads\"", page, StringComparison.Ordinal);

        var ascending = await HttpAssert.SuccessBodyAsync(await client.GetAsync($"feeds/public?q={token}&sort=downloads", TestContext.Current.CancellationToken));
        Assert.True(ascending.IndexOf($"{token}.small", StringComparison.OrdinalIgnoreCase) < ascending.IndexOf($"{token}.large", StringComparison.OrdinalIgnoreCase));

        // A sort nobody offers is simply no sort.
        await HttpAssert.SuccessBodyAsync(await client.GetAsync($"feeds/public?q={token}&sort=nonsense", TestContext.Current.CancellationToken));
    }

    private async Task<string[]> OrderAsync(string token, PackageSort sort)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IPackageStore>();
        var feed = await scope.ServiceProvider.GetRequiredService<IFeedStore>().FindAsync("public", TestContext.Current.CancellationToken);
        var filter = new PackageSearchFilter(SearchQueryParser.Parse(token), IncludePrerelease: true, IncludeSemVer2: true, PackageTypeLower: null, sort);
        var page = await store.SearchAsync(feed!.Key, filter, 0, 10, TestContext.Current.CancellationToken);
        var packages = (await store.GetPackagesAsync(page.PackageKeys, TestContext.Current.CancellationToken)).ToDictionary(p => p.Key);
        return page.PackageKeys.Select(k => packages[k].Id[(token.Length + 1)..]).ToArray();
    }

    /// <summary>Pushes the versions, then sets the numbers a push cannot choose: downloads and publish dates.</summary>
    private async Task SeedAsync(string id, string[] versions, long downloads, DateTime newest)
    {
        using var client = server.CreateClient();
        foreach (var version in versions)
        {
            using var package = TestPackages.Create(id, version);
            using var content = new MultipartFormDataContent();
            using var file = new StreamContent(package);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(file, "package", "package.nupkg");
            using var request = new HttpRequestMessage(HttpMethod.Put, "nuget/public/") { Content = content };
            request.Headers.Add("X-NuGet-ApiKey", FiGetServerFixture.AdminToken);
            HttpAssert.Status(HttpStatusCode.Created, await client.SendAsync(request, TestContext.Current.CancellationToken));
        }

        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FiGetDbContext>();
        var idLower = id.ToLowerInvariant();
        var rows = await db.PackageVersions.Where(v => v.Package!.IdLower == idLower).OrderBy(v => v.Key).ToListAsync(TestContext.Current.CancellationToken);

        // Downloads spread over the versions, so the sort has to sum them; dates a day apart ending on the newest, so
        // it has to take the maximum rather than any one row.
        for (var i = 0; i < rows.Count; i++)
        {
            rows[i].Downloads = i == 0 ? downloads - (rows.Count - 1) : 1;
            rows[i].PublishedUtc = newest.AddDays(i - (rows.Count - 1));
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
