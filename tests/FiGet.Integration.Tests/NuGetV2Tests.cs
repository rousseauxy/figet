using System.Net;
using System.Net.Http.Headers;
using System.Xml.Linq;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Testing;

namespace FiGet.Integration.Tests;

public sealed class SqliteNuGetV2Tests(SqliteServerFixture fixture) : NuGetV2Tests(fixture), IClassFixture<SqliteServerFixture>;

public sealed class SqlServerNuGetV2Tests(SqlServerServerFixture fixture) : NuGetV2Tests(fixture), IClassFixture<SqlServerServerFixture>;

/// <summary>
/// The v2 OData surface, driven with the request shapes recorded from the real clients in phase 0
/// (docs/protocol-v2.md). Raw HTTP on purpose: these clients are the reason v2 exists, and their requests
/// are what must keep working.
/// </summary>
public abstract class NuGetV2Tests
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Data = "http://schemas.microsoft.com/ado/2007/08/dataservices";
    private static readonly XNamespace Meta = "http://schemas.microsoft.com/ado/2007/08/dataservices/metadata";
    private static readonly XNamespace App = "http://www.w3.org/2007/app";

    private readonly FiGetServerFixture server;

    protected NuGetV2Tests(FiGetServerFixture server)
    {
        this.server = server;
        server.SkipIfUnavailable();
    }

    [Fact]
    public async Task The_feed_root_serves_a_service_document_with_and_without_a_trailing_slash()
    {
        using var client = server.CreateClient();

        foreach (var path in new[] { "nuget/public", "nuget/public/", "nuget/public/api/v2", "nuget/public/api/v2/" })
        {
            var document = XDocument.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync(path)));
            var collection = document.Root!.Element(App + "workspace")!.Element(App + "collection")!;
            Assert.Equal("Packages", collection.Attribute("href")!.Value);
        }
    }

    /// <summary>
    /// How the NuGet providers decide a source is a v2 source: anything but a success makes the source
    /// invalid, so an unknown id must be an empty feed and not a 404.
    /// </summary>
    [Fact]
    public async Task The_source_validation_probe_answers_an_empty_feed()
    {
        using var client = server.CreateClient();

        foreach (var path in new[] { "nuget/public/FindPackagesById()?id='FoooBarr'", "nuget/public/api/v2/FindPackagesById()?id='FoooBarr'" })
        {
            var body = await HttpAssert.SuccessBodyAsync(await client.GetAsync(path));
            Assert.Empty(Entries(body));
        }
    }

    [Fact]
    public async Task Metadata_describes_the_package_entity()
    {
        using var client = server.CreateClient();
        var document = XDocument.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync("nuget/public/$metadata")));

        var names = document.Descendants().Where(e => e.Name.LocalName == "EntityType").Select(e => e.Attribute("Name")!.Value);
        Assert.Contains("V2FeedPackage", names);
        var imports = document.Descendants().Where(e => e.Name.LocalName == "FunctionImport").Select(e => e.Attribute("Name")!.Value).ToList();
        Assert.Contains("Search", imports);
        Assert.Contains("FindPackagesById", imports);
        Assert.Contains("GetUpdates", imports);
    }

    /// <summary>
    /// The PowerShellGet round trip: push three versions over v2, then look the package up the way
    /// Find-Module does. Exactly one version may claim each latest flag, whatever the paging.
    /// </summary>
    [Fact]
    public async Task Push_over_v2_then_find_by_id_flags_exactly_one_latest_version()
    {
        var id = FiGetServerFixture.UniqueId("V2.RoundTrip");
        await SeedAsync("public", id);

        using var client = server.CreateClient();
        var body = await HttpAssert.SuccessBodyAsync(
            await client.GetAsync($"nuget/public/FindPackagesById()?id='{id}'&$skip=0&$top=40"));

        var entries = Entries(body);
        Assert.Equal(3, entries.Count);
        Assert.Equal(["1.0.0", "1.1.0", "2.0.0-beta1"], entries.Select(e => Property(e, "Version")).ToArray());
        Assert.Single(entries, e => Property(e, "IsLatestVersion") == "true");
        Assert.Single(entries, e => Property(e, "IsAbsoluteLatestVersion") == "true");
        Assert.Equal("1.1.0", Property(entries.Single(e => Property(e, "IsLatestVersion") == "true"), "Version"));
        Assert.Equal("2.0.0-beta1", Property(entries.Single(e => Property(e, "IsAbsoluteLatestVersion") == "true"), "Version"));

        var content = entries[0].Element(Atom + "content")!;
        Assert.Equal("application/zip", content.Attribute("type")!.Value);
        Assert.EndsWith($"/nuget/public/package/{id}/1.0.0", content.Attribute("src")!.Value, StringComparison.Ordinal);
        Assert.Equal("SHA512", Property(entries[0], "PackageHashAlgorithm"));
        Assert.NotEmpty(Property(entries[0], "PackageHash"));
        Assert.True(long.Parse(Property(entries[0], "PackageSize"), System.Globalization.CultureInfo.InvariantCulture) > 0);
    }

    [Fact]
    public async Task A_single_package_is_addressable_by_id_and_version()
    {
        var id = FiGetServerFixture.UniqueId("V2.ByKey");
        await SeedAsync("public", id);

        using var client = server.CreateClient();
        var body = await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/public/Packages(Id='{id}',Version='1.1.0')"));
        var entry = XDocument.Parse(body).Root!;

        Assert.Equal("entry", entry.Name.LocalName);
        Assert.Equal(id, Property(entry, "Id"));
        Assert.Equal("1.1.0", Property(entry, "Version"));

        HttpAssert.Status(HttpStatusCode.NotFound, await client.GetAsync($"nuget/public/Packages(Id='{id}',Version='9.9.9')"));
    }

    [Fact]
    public async Task Search_finds_by_tag_the_way_Find_Module_asks()
    {
        var id = FiGetServerFixture.UniqueId("V2.Tagged");
        var tag = "figettag" + Guid.NewGuid().ToString("N")[..6];
        using (var package = TestPackages.Create(id, "1.0.0", b => b.Tags.Add(tag)))
        {
            await PushAsync("public", package);
        }

        using var client = server.CreateClient();
        var body = await HttpAssert.SuccessBodyAsync(await client.GetAsync(
            $"nuget/public/Search()?$filter=IsLatestVersion&searchTerm='%20tag:{tag}'&targetFramework=''&includePrerelease=false&$skip=0&$top=40"));

        var entries = Entries(body);
        Assert.Single(entries);
        Assert.Equal(id, Property(entries[0], "Id"));
    }

    /// <summary>The dialect PSResourceGet sends in v2 mode: the whole query inside $filter.</summary>
    [Fact]
    public async Task The_PSResourceGet_filter_dialect_is_understood()
    {
        var id = FiGetServerFixture.UniqueId("V2.Filter");
        await SeedAsync("public", id);
        using var client = server.CreateClient();

        var latest = Entries(await HttpAssert.SuccessBodyAsync(await client.GetAsync(
            $"nuget/public/api/v2/FindPackagesById()?$filter=Id eq '{id}' and IsLatestVersion eq true&$inlinecount=allpages&id='{id}'")));
        Assert.Single(latest);
        Assert.Equal("1.1.0", Property(latest[0], "Version"));

        var prerelease = Entries(await HttpAssert.SuccessBodyAsync(await client.GetAsync(
            $"nuget/public/api/v2/FindPackagesById()?$filter=Id eq '{id}' and IsAbsoluteLatestVersion eq true&id='{id}'")));
        Assert.Single(prerelease);
        Assert.Equal("2.0.0-beta1", Property(prerelease[0], "Version"));

        var exact = Entries(await HttpAssert.SuccessBodyAsync(await client.GetAsync(
            $"nuget/public/api/v2/FindPackagesById()?$filter=Id eq '{id}' and NormalizedVersion eq '1.0.0'&id='{id}'")));
        Assert.Single(exact);
        Assert.Equal("1.0.0", Property(exact[0], "Version"));

        // A range, newest first: string order would put 1.1.0 before 1.0.0 only by luck, version order must decide.
        var range = Entries(await HttpAssert.SuccessBodyAsync(await client.GetAsync(
            $"nuget/public/api/v2/FindPackagesById()?$filter=NormalizedVersion ge '1.0.0' and NormalizedVersion le '1.1.0' and IsPrerelease eq false and Id eq '{id}'&$orderby=NormalizedVersion desc&id='{id}'")));
        Assert.Equal(["1.1.0", "1.0.0"], range.Select(e => Property(e, "Version")).ToArray());

        var prefix = Entries(await HttpAssert.SuccessBodyAsync(await client.GetAsync(
            $"nuget/public/api/v2/Search()?$filter=IsLatestVersion and startswith(Id, '{id[..8]}')&$inlinecount=allpages&$skip=0&$top=100")));
        Assert.Contains(prefix, e => Property(e, "Id") == id);
    }

    [Fact]
    public async Task Inlinecount_reports_the_total_and_the_next_link_continues_the_pages()
    {
        var id = FiGetServerFixture.UniqueId("V2.Paging");
        await SeedAsync("public", id);
        using var client = server.CreateClient();

        var body = await HttpAssert.SuccessBodyAsync(await client.GetAsync(
            $"nuget/public/FindPackagesById()?id='{id}'&$inlinecount=allpages&$top=2"));
        var feed = XDocument.Parse(body).Root!;

        Assert.Equal("3", feed.Element(Meta + "count")!.Value);
        Assert.Equal(2, Entries(body).Count);

        var next = feed.Elements(Atom + "link").Single(l => l.Attribute("rel")!.Value == "next").Attribute("href")!.Value;
        var rest = Entries(await HttpAssert.SuccessBodyAsync(await client.GetAsync(next)));
        Assert.Single(rest);
        Assert.Equal("2.0.0-beta1", Property(rest[0], "Version"));

        var count = await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/public/FindPackagesById()/$count?id='{id}'"));
        Assert.Equal("3", count.Trim());
    }

    /// <summary>
    /// The failure mode this server must not have: an unparsable filter is a 400 naming the expression,
    /// never an empty 200 that makes a client report "no packages found".
    /// </summary>
    [Fact]
    public async Task An_unsupported_filter_is_a_400_that_names_the_expression()
    {
        using var client = server.CreateClient();

        var response = await client.GetAsync("nuget/public/Search()?$filter=SomethingUnknown eq 'x'");
        HttpAssert.Status(HttpStatusCode.BadRequest, response);
        Assert.Contains("SomethingUnknown", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var broken = await client.GetAsync("nuget/public/Search()?$filter=Id eq");
        HttpAssert.Status(HttpStatusCode.BadRequest, broken);
    }

    [Fact]
    public async Task Packages_are_downloaded_by_their_normalised_version()
    {
        var id = FiGetServerFixture.UniqueId("V2.Download");
        await SeedAsync("public", id);
        using var client = server.CreateClient();

        var response = await client.GetAsync($"nuget/public/package/{id}/1.0.0");
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {response.ReasonPhrase}");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 0);
        Assert.Equal(0x50, bytes[0]);
        Assert.Equal(0x4b, bytes[1]);

        HttpAssert.Status(HttpStatusCode.NotFound, await client.GetAsync($"nuget/public/package/{id}/9.9.9"));
    }

    [Fact]
    public async Task Delete_over_v2_unlists_and_moves_the_latest_flag()
    {
        var id = FiGetServerFixture.UniqueId("V2.Delete");
        await SeedAsync("public", id);

        using var client = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"nuget/public/{id}/1.1.0");
        request.Headers.Add("X-NuGet-ApiKey", FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.NoContent, await client.SendAsync(request));

        var entries = Entries(await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/public/FindPackagesById()?id='{id}'")));
        var unlisted = entries.Single(e => Property(e, "Version") == "1.1.0");
        Assert.Equal("false", Property(unlisted, "Listed"));
        Assert.Equal("1.0.0", Property(entries.Single(e => Property(e, "IsLatestVersion") == "true"), "Version"));
    }

    [Fact]
    public async Task A_private_feed_challenges_and_then_serves_with_credentials()
    {
        using var anonymous = server.CreateClient();
        var refused = await anonymous.GetAsync("nuget/private/FindPackagesById()?id='anything'");
        HttpAssert.Status(HttpStatusCode.Unauthorized, refused);
        Assert.Equal("Basic", refused.Headers.WwwAuthenticate.Single().Scheme);

        using var authenticated = server.CreateClient(FiGetServerFixture.AdminToken);
        await HttpAssert.SuccessBodyAsync(await authenticated.GetAsync("nuget/private/FindPackagesById()?id='anything'"));
    }

    [Fact]
    public async Task Pushing_an_existing_version_is_refused_unless_the_feed_allows_overwrite()
    {
        var id = FiGetServerFixture.UniqueId("V2.Conflict");
        using (var first = TestPackages.Create(id, "1.0.0"))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushAsync("public", first));
        }

        using (var again = TestPackages.Create(id, "1.0.0"))
        {
            HttpAssert.Status(HttpStatusCode.Conflict, await PushAsync("public", again));
        }

        using (var overwrite = TestPackages.Create(id, "1.0.0"))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushAsync("overwrite", overwrite));
        }

        using (var replaced = TestPackages.Create(id, "1.0.0"))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushAsync("overwrite", replaced));
        }
    }

    private async Task SeedAsync(string feed, string id)
    {
        foreach (var version in new[] { "1.0.0", "1.1.0", "2.0.0-beta1" })
        {
            using var package = TestPackages.Create(id, version);
            HttpAssert.Status(HttpStatusCode.Created, await PushAsync(feed, package));
        }
    }

    /// <summary>Pushes the way nuget.exe does over v2: a multipart body to the source URL itself.</summary>
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

    private static IReadOnlyList<XElement> Entries(string xml)
    {
        var root = XDocument.Parse(xml).Root!;
        return root.Name.LocalName == "entry" ? [root] : root.Elements(Atom + "entry").ToList();
    }

    private static string Property(XElement entry, string name) =>
        entry.Element(Meta + "properties")?.Element(Data + name)?.Value ?? "";
}
