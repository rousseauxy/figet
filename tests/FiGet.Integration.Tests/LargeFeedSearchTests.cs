using System.Globalization;
using System.Net;
using System.Xml.Linq;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Infrastructure.Persistence;
using FiGet.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

public sealed class SqliteLargeFeedSearchTests(SqliteServerFixture fixture) : LargeFeedSearchTests(fixture), IClassFixture<SqliteServerFixture>;

public sealed class SqlServerLargeFeedSearchTests(SqlServerServerFixture fixture) : LargeFeedSearchTests(fixture), IClassFixture<SqlServerServerFixture>;

/// <summary>
/// v2 listings on a feed larger than the 2,000 packages a listing used to read. Found cross-checking other package servers'
/// issue trackers: on a larger feed, <c>startswith(Id,'…')</c> for ids past the first 2,000 answered an empty 200 with a
/// count of 2,000 - an unparsed-looking empty answer to a valid question. Rows are inserted directly: the listing is under
/// test, not the push.
/// </summary>
public abstract class LargeFeedSearchTests
{
    private const int PackageCount = 2_501;

    /// <summary>How many of them carry the tag <c>Big</c>.</summary>
    private const int TaggedCount = 250;

    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Data = "http://schemas.microsoft.com/ado/2007/08/dataservices";
    private static readonly XNamespace Meta = "http://schemas.microsoft.com/ado/2007/08/dataservices/metadata";

    private readonly FiGetServerFixture server;

    protected LargeFeedSearchTests(FiGetServerFixture server)
    {
        this.server = server;
        server.SkipIfUnavailable();
    }

    [Fact]
    public async Task A_listing_reads_past_two_thousand_packages_and_counts_them_all()
    {
        var feed = await CreateLargeFeedAsync();
        using var client = server.CreateClient();

        // The last package by id, found by the filter PSResourceGet sends for a wildcard with no search term.
        var last = $"Scan.P{PackageCount - 1:D4}";
        var found = await EntriesAsync(client, $"nuget/{feed}/Search()?$filter=IsLatestVersion and startswith(Id, '{last}')&$inlinecount=allpages&$skip=0&$top=100");
        Assert.Equal([last], found.Entries.Select(e => Property(e, "Id")).ToArray());
        Assert.Equal("1", found.Count);

        // Every package, counted, on both routes that list without one id.
        Assert.Equal(PackageCount.ToString(CultureInfo.InvariantCulture), await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/{feed}/Packages()/$count?$filter=IsLatestVersion")));
        var all = await EntriesAsync(client, $"nuget/{feed}/Search()?$filter=IsLatestVersion&searchTerm=''&$inlinecount=allpages&$skip=0&$top=40");
        Assert.Equal(PackageCount.ToString(CultureInfo.InvariantCulture), all.Count);
        Assert.Equal(40, all.Entries.Count);
        Assert.NotNull(all.Next);

        // PowerShellGet pages by $skip until a page comes back short: the last page is short and has no next link, and the
        // pages meet without a gap or a repeat.
        var tail = await EntriesAsync(client, $"nuget/{feed}/Search()?$filter=IsLatestVersion&searchTerm=''&targetFramework=''&includePrerelease=false&$skip=2480&$top=40");
        Assert.Equal(PackageCount - 2480, tail.Entries.Count);
        Assert.Null(tail.Next);
        Assert.Equal($"Scan.P{PackageCount - 1:D4}", Property(tail.Entries[^1], "Id"));

        var before = await EntriesAsync(client, $"nuget/{feed}/Search()?$filter=IsLatestVersion&searchTerm=''&$skip=1990&$top=20");
        Assert.Equal(Enumerable.Range(1990, 20).Select(i => $"Scan.P{i:D4}").ToArray(), before.Entries.Select(e => Property(e, "Id")).ToArray());

        // A free-text term and a substring filter on the id narrow in the database, past the old window as well.
        var middle = await EntriesAsync(client, $"nuget/{feed}/Search()?$filter=IsLatestVersion and substringof('P2345', Id)&$inlinecount=allpages");
        Assert.Equal(["Scan.P2345"], middle.Entries.Select(e => Property(e, "Id")).ToArray());
    }

    /// <summary>
    /// Pages sized for the way PSResourceGet steps through them: <c>Find-PSResource -Name *</c> asks for 6000 and steps by
    /// 6000, so it must get every package in one page; a tag search asks for 6000 and steps by 100, so it must get pages of
    /// 100 or see packages again. Found cross-checking PSResourceGet's issues (#1016, 2026-09-15).
    /// </summary>
    [Fact]
    public async Task Pages_fit_the_steps_PSResourceGet_takes()
    {
        var feed = await CreateLargeFeedAsync();
        using var client = server.CreateClient();

        var listing = await EntriesAsync(client, $"nuget/{feed}/Search()?$filter=IsLatestVersion&$inlinecount=allpages&$skip=0&$top=6000");
        Assert.Equal(PackageCount, listing.Entries.Count);

        foreach (var query in new[]
        {
            "$filter=IsLatestVersion and substringof('PSModule', Tags) eq true and substringof('Big', Tags) eq true&$inlinecount=allpages",
            "$filter=IsLatestVersion&searchTerm='tag:Big'&$inlinecount=allpages",
        })
        {
            var ids = new List<string>();
            var first = await EntriesAsync(client, $"nuget/{feed}/Search()?{query}&$skip=0&$top=6000");
            var total = int.Parse(first.Count!, CultureInfo.InvariantCulture);
            ids.AddRange(first.Entries.Select(e => Property(e, "Id")));
            for (var skip = 100; skip < total; skip += 100)
            {
                ids.AddRange((await EntriesAsync(client, $"nuget/{feed}/Search()?{query}&$skip={skip}&$top=6000")).Entries.Select(e => Property(e, "Id")));
            }

            Assert.Equal(TaggedCount, total);
            Assert.Equal(TaggedCount, ids.Count);
            Assert.Equal(TaggedCount, ids.Distinct(StringComparer.Ordinal).Count());
        }
    }

    private async Task<string> CreateLargeFeedAsync()
    {
        var name = "large" + Guid.NewGuid().ToString("N")[..8];
        await using var scope = server.Services.CreateAsyncScope();
        var feeds = scope.ServiceProvider.GetRequiredService<IFeedStore>();
        Assert.True(await feeds.CreateAsync(new Feed { Name = name, NameLower = name, Kind = FeedKind.Curated, AnonymousRead = true, CreatedUtc = DateTime.UtcNow }, CancellationToken.None));
        var feedKey = (await feeds.FindAsync(name, CancellationToken.None))!.Key;

        var db = scope.ServiceProvider.GetRequiredService<FiGetDbContext>();
        var now = DateTime.UtcNow;

        // Inserted out of id order, so the listing's order comes from the query, not from the insert.
        foreach (var chunk in Enumerable.Range(0, PackageCount).OrderByDescending(i => i % 7).ThenBy(i => i).Chunk(500))
        {
            foreach (var i in chunk)
            {
                var id = $"Scan.P{i:D4}";
                db.Packages.Add(new Package
                {
                    FeedKey = feedKey,
                    Id = id,
                    IdLower = id.ToLowerInvariant(),
                    Versions =
                    [
                        new PackageVersion
                        {
                            OriginalVersion = "1.0.0",
                            NormalizedVersion = "1.0.0",
                            NormalizedVersionLower = "1.0.0",
                            Listed = true,
                            Origin = PackageOrigin.Pushed,
                            Description = "A package of the large feed.",
                            Tags = i < TaggedCount ? "PSModule Big" : "PSModule",
                            SearchTextLower = id.ToLowerInvariant() + "\na package of the large feed.\n" + (i < TaggedCount ? "psmodule big" : "psmodule"),
                            TagsLower = i < TaggedCount ? " psmodule big " : " psmodule ",
                            PackageTypes = "|Dependency|",
                            PackageTypesLower = "|dependency|",
                            PublishedUtc = now,
                            LastUpdatedUtc = now,
                            LastUsedUtc = now,
                        },
                    ],
                });
            }

            await db.SaveChangesAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
        }

        return name;
    }

    private static async Task<(IReadOnlyList<XElement> Entries, string? Count, string? Next)> EntriesAsync(HttpClient client, string path)
    {
        var root = XDocument.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync(path))).Root!;
        return (
            root.Elements(Atom + "entry").ToList(),
            root.Element(Meta + "count")?.Value,
            root.Elements(Atom + "link").FirstOrDefault(l => (string?)l.Attribute("rel") == "next")?.Attribute("href")?.Value);
    }

    private static string Property(XElement entry, string name) =>
        entry.Element(Meta + "properties")?.Element(Data + name)?.Value ?? "";
}
