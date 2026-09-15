using System.Net;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Testing;
using NuGet.Frameworks;
using NuGet.Packaging;

namespace FiGet.Integration.Tests;

/// <summary>
/// Client behaviours confirmed by hand against the running server while cross-checking other package servers' trackers
/// (2026-09-15), each of which had no test that would catch a regression. The report's list, in its order.
/// </summary>
public abstract partial class PackageEvidenceTests
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Data = "http://schemas.microsoft.com/ado/2007/08/dataservices";
    private static readonly XNamespace Meta = "http://schemas.microsoft.com/ado/2007/08/dataservices/metadata";

    /// <summary>Chocolatey's id lookup: an exact id, never the packages whose id merely contains it (Gitea #31168).</summary>
    [Fact]
    public async Task A_lower_cased_id_comparison_finds_that_id_only()
    {
        var id = FiGetServerFixture.UniqueId("Evidence.Git").ToLowerInvariant();
        var neighbour = id + ".portable";
        foreach (var (name, version) in new[] { (id, "1.0.0"), (id, "2.0.0"), (neighbour, "3.0.0") })
        {
            using var package = TestPackages.Create(name, version);
            HttpAssert.Status(HttpStatusCode.Created, await PushAsync("public", package));
        }

        using var client = server.CreateClient();
        var lookup = await Entries($"nuget/public/Packages()?$filter=(tolower(Id) eq '{id}') and IsLatestVersion&semVerLevel=2.0.0", client);
        Assert.Equal([(id, "2.0.0")], lookup.Select(e => (Property(e, "Id"), Property(e, "Version"))).ToArray());

        // choco search: the term matches both ids, and each is at its latest version only.
        var search = await Entries($"nuget/public/Search()?$filter=IsLatestVersion&$orderby=Id&searchTerm='{id}'&targetFramework=''&includePrerelease=false&$skip=0&$top=30", client);
        Assert.Equal([(id, "2.0.0"), (neighbour, "3.0.0")], search.Select(e => (Property(e, "Id"), Property(e, "Version"))).OrderBy(p => p.Item1, StringComparer.Ordinal).ToArray());
    }

    /// <summary>A count of a search ordered by downloads, with the empty arguments the clients send (Gitea #22838).</summary>
    [Fact]
    public async Task A_search_count_ordered_by_downloads_is_a_number()
    {
        var id = FiGetServerFixture.UniqueId("Evidence.Counted");
        using (var package = TestPackages.Create(id, "1.0.0"))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushAsync("public", package));
        }

        using var client = server.CreateClient();
        var body = await HttpAssert.SuccessBodyAsync(await client.GetAsync(
            $"nuget/public/Search()/$count?$filter=IsLatestVersion&$orderby=DownloadCount desc&searchTerm='{id}'&targetFramework=''&includePrerelease=false"));

        Assert.Equal(1, int.Parse(body.Trim(), System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>A v3 search that excludes prereleases: neither the top version nor the version list holds one (Gitea).</summary>
    [Fact]
    public async Task A_v3_search_without_prereleases_answers_only_stable_versions()
    {
        var id = FiGetServerFixture.UniqueId("Evidence.Stable");
        foreach (var version in new[] { "1.0.0", "1.1.0", "2.0.0-beta1" })
        {
            using var package = TestPackages.Create(id, version);
            HttpAssert.Status(HttpStatusCode.Created, await PushAsync("public", package));
        }

        using var client = server.CreateClient();
        var body = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/public/v3/query?q={id}&prerelease=false")))!;
        var hit = body["data"]!.AsArray().Single(d => string.Equals((string?)d!["id"], id, StringComparison.OrdinalIgnoreCase))!;

        Assert.Equal("1.1.0", (string?)hit["version"]);
        Assert.Equal(["1.0.0", "1.1.0"], hit["versions"]!.AsArray().Select(v => (string?)v!["version"]).ToArray());
    }

    /// <summary>Versions pushed interleaved still group under their own package (Gitea #21434).</summary>
    [Fact]
    public async Task Interleaved_pushes_group_under_their_own_package()
    {
        var prefix = FiGetServerFixture.UniqueId("Evidence.Group");
        var first = prefix + ".First";
        var second = prefix + ".Second";
        foreach (var (id, version) in new[] { (first, "1.0.0"), (second, "1.0.0"), (first, "1.1.0"), (second, "1.1.0") })
        {
            using var package = TestPackages.Create(id, version);
            HttpAssert.Status(HttpStatusCode.Created, await PushAsync("public", package));
        }

        using var client = server.CreateClient();
        var body = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/public/v3/query?q={prefix}")))!;
        var hits = body["data"]!.AsArray()
            .Where(d => ((string?)d!["id"])?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        Assert.Equal(2, hits.Count);
        Assert.All(hits, hit => Assert.Equal(["1.0.0", "1.1.0"], hit!["versions"]!.AsArray().Select(v => (string?)v!["version"]).ToArray()));
    }

    /// <summary>The NuGet provider 3.0.0.1 asks for a leaf by a two-part version before the normalised one.</summary>
    [Fact]
    public async Task A_registration_leaf_answers_a_shortened_version_spelling()
    {
        var id = FiGetServerFixture.UniqueId("Evidence.Leaf");
        using (var package = TestPackages.Create(id, "1.0.0"))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushAsync("public", package));
        }

        using var client = server.CreateClient();
        var idLower = id.ToLowerInvariant();
        foreach (var spelling in new[] { "1.0.0", "1.0", "1.0.0.0" })
        {
            var leaf = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/public/v3/registration/{idLower}/{spelling}.json")))!;
            Assert.EndsWith($"/v3/catalog/{idLower}/1.0.0.json", (string?)leaf["catalogEntry"], StringComparison.Ordinal);
            Assert.True((bool?)leaf["listed"]);

            // The document that leaf points to is what the provider reads the version from, and it answers the
            // shortened spellings too: the provider asks for 1.0 before it asks for 1.0.0.
            var entry = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/public/v3/catalog/{idLower}/{spelling}.json")))!;
            Assert.Equal("1.0.0", (string?)entry["version"]);
        }
    }

    /// <summary>
    /// Dependencies declared outside any group, and an empty group that names a framework: two shapes real packages have
    /// and the clients read differently (Gitea #30265, #28678).
    /// </summary>
    [Fact]
    public async Task Dependency_groups_are_served_as_the_clients_read_them()
    {
        var id = FiGetServerFixture.UniqueId("Evidence.Deps");
        using (var package = TestPackages.Create(id, "1.0.0", b =>
        {
            b.AddDependency("any", "Evidence.A", "1.1.0");
            b.AddDependency("any", "Evidence.B", "[1.0.0, 2.0.0)");
            b.DependencyGroups.Add(new PackageDependencyGroup(NuGetFramework.Parse("net472"), []));
        }))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushAsync("public", package));
        }

        using var client = server.CreateClient();
        var entry = (await Entries($"nuget/public/FindPackagesById()?id='{id}'", client)).Single();
        Assert.Equal("Evidence.A:[1.1.0, ):|Evidence.B:[1.0.0, 2.0.0):|::net472", Property(entry, "Dependencies"));

        var catalogEntry = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/public/v3/catalog/{id.ToLowerInvariant()}/1.0.0.json")))!;
        var groups = catalogEntry["dependencyGroups"]!.AsArray();
        var any = groups.Single(g => string.IsNullOrEmpty((string?)g!["targetFramework"]))!;
        Assert.Equal(["Evidence.A", "Evidence.B"], any["dependencies"]!.AsArray().Select(d => (string?)d!["id"]).ToArray());
        var empty = groups.Single(g => (string?)g!["targetFramework"] is { Length: > 0 })!;
        Assert.Empty(empty["dependencies"]?.AsArray() ?? []);
    }

    /// <summary>Visual Studio reads these five fields off a v3 search hit (Gitea #21291).</summary>
    [Fact]
    public async Task A_v3_search_hit_carries_the_fields_visual_studio_reads()
    {
        var id = FiGetServerFixture.UniqueId("Evidence.Fields");
        using (var package = TestPackages.Create(id, "1.0.0", b =>
        {
            b.Summary = "A short summary.";
            b.IconUrl = new Uri("https://example.org/icon.png");
            b.LicenseUrl = new Uri("https://example.org/licence");
            b.ProjectUrl = new Uri("https://example.org/project");
            b.Tags.Add("evidence");
            b.Tags.Add("fields");
        }))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushAsync("public", package));
        }

        using var client = server.CreateClient();
        var body = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/public/v3/query?q={id}")))!;
        var hit = body["data"]!.AsArray().Single(d => string.Equals((string?)d!["id"], id, StringComparison.OrdinalIgnoreCase))!;

        Assert.Equal("A short summary.", (string?)hit["summary"]);
        Assert.Equal("https://example.org/icon.png", (string?)hit["iconUrl"]);
        Assert.Equal("https://example.org/licence", (string?)hit["licenseUrl"]);
        Assert.Equal("https://example.org/project", (string?)hit["projectUrl"]);
        Assert.Equal(["evidence", "fields"], hit["tags"]!.AsArray().Select(t => (string?)t).ToArray());
    }

    /// <summary>A symbol package with no PDB is refused with the reason dotnet prints (Gitea #32133).</summary>
    [Fact]
    public async Task A_symbol_package_without_a_pdb_is_refused_with_the_reason()
    {
        var id = FiGetServerFixture.UniqueId("Evidence.NoPdb");
        using (var package = TestPackages.Create(id, "1.0.0"))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushAsync("public", package));
        }

        using var symbols = TestPackages.CreateSymbols(id, "1.0.0", [("lib/netstandard2.0/readme.txt", "not a pdb"u8.ToArray())]);
        var response = await PushAsync("public", symbols, "v3/symbolpublish");

        HttpAssert.Status(HttpStatusCode.BadRequest, response);
        Assert.Contains("no PDB files", response.ReasonPhrase + await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<IReadOnlyList<XElement>> Entries(string url, HttpClient client) =>
        [.. XDocument.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync(url))).Root!.Elements(Atom + "entry")];

    private static string Property(XElement entry, string name) =>
        entry.Element(Meta + "properties")?.Element(Data + name)?.Value ?? "";
}
