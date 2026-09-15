using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Testing;
using Microsoft.Extensions.DependencyInjection;
using NuGet.Common;
using NuGet.Packaging;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace FiGet.Integration.Tests;

public sealed class SqliteNuGetV3Tests(SqliteServerFixture fixture) : NuGetV3Tests(fixture), IClassFixture<SqliteServerFixture>;

public sealed class SqlServerNuGetV3Tests(SqlServerServerFixture fixture) : NuGetV3Tests(fixture), IClassFixture<SqlServerServerFixture>;

/// <summary>The v3 surface, exercised both raw and through NuGet's own client library, on every database provider.</summary>
public abstract class NuGetV3Tests
{
    private readonly FiGetServerFixture server;

    protected NuGetV3Tests(FiGetServerFixture server)
    {
        this.server = server;
        server.SkipIfUnavailable();
    }

    private static SourceCacheContext NoCache => new() { NoCache = true, DirectDownload = true };

    [Fact]
    public async Task Service_index_lists_every_resource_type()
    {
        using var client = server.CreateClient();
        var json = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync("nuget/public/v3/index.json")))!;

        Assert.Equal("3.0.0", (string?)json["version"]);
        var types = json["resources"]!.AsArray().Select(r => (string?)r!["@type"]).ToHashSet();
        foreach (var type in new[]
        {
            "SearchQueryService", "SearchQueryService/3.0.0-beta", "SearchQueryService/3.0.0-rc", "SearchQueryService/3.5.0",
            "SearchAutocompleteService", "SearchAutocompleteService/3.0.0-beta", "SearchAutocompleteService/3.0.0-rc",
            "RegistrationsBaseUrl", "RegistrationsBaseUrl/3.0.0-beta", "RegistrationsBaseUrl/3.4.0", "RegistrationsBaseUrl/3.6.0",
            "PackageBaseAddress/3.0.0", "PackagePublish/2.0.0", "SymbolPackagePublish/4.9.0",
        })
        {
            Assert.Contains(type, types);
        }

        Assert.All(json["resources"]!.AsArray(), r => Assert.StartsWith(server.BaseAddress + "nuget/public/v3/", (string?)r!["@id"]));
    }

    [Fact]
    public async Task Unknown_feed_is_404()
    {
        using var client = server.CreateClient();
        HttpAssert.Status(HttpStatusCode.NotFound, await client.GetAsync("nuget/does-not-exist/v3/index.json"));
    }

    [Fact]
    public async Task Push_then_metadata_versions_download_search_and_autocomplete_through_the_NuGet_client()
    {
        var id = FiGetServerFixture.UniqueId("Client.RoundTrip");
        await PushWithClientAsync("public", id, "1.0.0", b =>
        {
            b.Tags.Add("roundtrip");
            b.AddDependency("netstandard2.0", "Newtonsoft.Json", "[13.0.1, )");
        });
        byte[] second;
        using (var stream = TestPackages.Create(id, "1.1.0-beta.1"))
        {
            second = stream.ToArray();
            await PushWithClientAsync("public", stream);
        }

        var repository = server.Repository("public");
        using var cache = NoCache;

        var metadata = await (await repository.GetResourceAsync<PackageMetadataResource>()).GetMetadataAsync(id, includePrerelease: true, includeUnlisted: false, cache, NullLogger.Instance, CancellationToken.None);
        Assert.Equal(["1.0.0", "1.1.0-beta.1"], metadata.Select(m => m.Identity.Version.ToNormalizedString()).Order());
        var stable = metadata.Single(m => !m.Identity.Version.IsPrerelease);
        Assert.Equal("Newtonsoft.Json", Assert.Single(Assert.Single(stable.DependencySets).Packages).Id);
        Assert.Contains("roundtrip", stable.Tags, StringComparison.Ordinal);

        var findById = await repository.GetResourceAsync<FindPackageByIdResource>();
        var versions = await findById.GetAllVersionsAsync(id, cache, NullLogger.Instance, CancellationToken.None);
        Assert.Equal(2, versions.Count());

        using var downloaded = new MemoryStream();
        Assert.True(await findById.CopyNupkgToStreamAsync(id, NuGetVersion.Parse("1.1.0-beta.1"), downloaded, cache, NullLogger.Instance, CancellationToken.None));
        Assert.Equal(SHA512.HashData(second), SHA512.HashData(downloaded.ToArray()));

        var search = await repository.GetResourceAsync<PackageSearchResource>();
        var stableHits = (await search.SearchAsync(id, new SearchFilter(includePrerelease: false), 0, 10, NullLogger.Instance, CancellationToken.None)).ToList();
        Assert.Equal("1.0.0", Assert.Single(stableHits).Identity.Version.ToNormalizedString());
        var prereleaseHits = (await search.SearchAsync(id, new SearchFilter(includePrerelease: true), 0, 10, NullLogger.Instance, CancellationToken.None)).ToList();
        Assert.Equal("1.1.0-beta.1", Assert.Single(prereleaseHits).Identity.Version.ToNormalizedString());

        var autocomplete = await repository.GetResourceAsync<AutoCompleteResource>();
        Assert.Contains(id, await autocomplete.IdStartsWith(id[..10], includePrerelease: false, NullLogger.Instance, CancellationToken.None));
        var autoVersions = await autocomplete.VersionStartsWith(id, "1.", includePrerelease: true, cache, NullLogger.Instance, CancellationToken.None);
        Assert.Equal(2, autoVersions.Count());
    }

    [Fact]
    public async Task Registration_json_carries_every_type_marker_the_PowerShell_clients_parse()
    {
        var id = FiGetServerFixture.UniqueId("Type.Markers");
        using (var stream = TestPackages.Create(id, "2.0.0", b => b.AddDependency("netstandard2.0", "Dep.A", "[1.0.0, )")))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushRawAsync("public", stream, FiGetServerFixture.AdminToken));
        }

        using var client = server.CreateClient();
        var index = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/public/v3/registration/{id.ToLowerInvariant()}/index.json")))!;

        Assert.Contains("PackageRegistration", index["@type"]!.AsArray().Select(t => (string?)t));
        var page = index["items"]![0]!;
        Assert.Equal("catalog:CatalogPage", (string?)page["@type"]);
        var leaf = page["items"]![0]!;
        Assert.Equal("Package", (string?)leaf["@type"]);
        var entry = leaf["catalogEntry"]!;
        Assert.Equal("PackageDetails", (string?)entry["@type"]);
        Assert.Equal(id, (string?)entry["id"]);
        Assert.Equal("2.0.0", (string?)entry["version"]);
        Assert.True((bool)entry["listed"]!);
        var group = entry["dependencyGroups"]![0]!;
        Assert.Equal("PackageDependencyGroup", (string?)group["@type"]);
        Assert.Equal("PackageDependency", (string?)group["dependencies"]![0]!["@type"]);
        Assert.Equal("netstandard2.0", (string?)group["targetFramework"]);

        var leafDocument = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/public/v3/registration/{id.ToLowerInvariant()}/2.0.0.json")))!;
        Assert.Contains("Package", leafDocument["@type"]!.AsArray().Select(t => (string?)t));
        Assert.EndsWith($"/v3/flatcontainer/{id.ToLowerInvariant()}/2.0.0/{id.ToLowerInvariant()}.2.0.0.nupkg", (string?)leafDocument["packageContent"]);

        // PackageManagement's NuGet provider 3.x follows the leaf's catalogEntry URL and reads version and metadata
        // from that document. It must be the package details, and the inline catalogEntry must name the same URL.
        var catalogUrl = (string)leafDocument["catalogEntry"]!;
        Assert.Equal(catalogUrl, (string?)entry["@id"]);
        var details = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync(new Uri(catalogUrl))))!;
        Assert.Equal("PackageDetails", (string?)details["@type"]);
        Assert.Equal(id, (string?)details["id"]);
        Assert.Equal("2.0.0", (string?)details["version"]);
        Assert.Equal("Dep.A", (string?)details["dependencyGroups"]![0]!["dependencies"]![0]!["id"]);
        HttpAssert.Status(HttpStatusCode.NotFound, await client.GetAsync($"nuget/public/v3/catalog/{id.ToLowerInvariant()}/9.9.9.json"));
    }

    [Fact]
    public async Task Pushing_an_existing_version_is_409_and_an_overwrite_feed_replaces_it()
    {
        var id = FiGetServerFixture.UniqueId("Duplicate");
        using (var first = TestPackages.Create(id, "1.0.0"))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushRawAsync("public", first, FiGetServerFixture.AdminToken));
        }

        using (var again = TestPackages.Create(id, "1.0.0", b => b.Description = "changed"))
        {
            var conflict = await PushRawAsync("public", again, FiGetServerFixture.AdminToken);
            HttpAssert.Status(HttpStatusCode.Conflict, conflict);
            Assert.Contains("already exists", await conflict.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using (var original = TestPackages.Create(id, "1.0.0"))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushRawAsync("overwrite", original, FiGetServerFixture.AdminToken));
        }

        using (var replacement = TestPackages.Create(id, "1.0.0", b => b.Description = "replaced"))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushRawAsync("overwrite", replacement, FiGetServerFixture.AdminToken));
        }

        using var client = server.CreateClient();
        var entry = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/overwrite/v3/registration/{id.ToLowerInvariant()}/index.json")))!["items"]![0]!["items"]!;
        Assert.Equal("replaced", (string?)Assert.Single(entry.AsArray())!["catalogEntry"]!["description"]);
    }

    [Fact]
    public async Task Push_needs_a_valid_key_with_push_scope()
    {
        var id = FiGetServerFixture.UniqueId("Auth.Push");

        using (var noKey = TestPackages.Create(id, "1.0.0"))
        {
            var response = await PushRawAsync("public", noKey, apiKey: null);
            HttpAssert.Status(HttpStatusCode.Unauthorized, response);
            Assert.Equal("Basic", response.Headers.WwwAuthenticate.Single().Scheme);
        }

        using (var badKey = TestPackages.Create(id, "1.0.0"))
        {
            HttpAssert.Status(HttpStatusCode.Forbidden, await PushRawAsync("public", badKey, "figet_not_a_real_token"));
        }

        var readOnly = await CreateTokenAsync(FiGet.Domain.Entities.TokenScopes.Read, feed: null);
        using (var readKey = TestPackages.Create(id, "1.0.0"))
        {
            HttpAssert.Status(HttpStatusCode.Forbidden, await PushRawAsync("public", readKey, readOnly));
        }

        var pushPrivateOnly = await CreateTokenAsync(FiGet.Domain.Entities.TokenScopes.Push, feed: "private");
        using (var wrongFeed = TestPackages.Create(id, "1.0.0"))
        {
            HttpAssert.Status(HttpStatusCode.Forbidden, await PushRawAsync("public", wrongFeed, pushPrivateOnly));
        }

        using (var rightFeed = TestPackages.Create(id, "1.0.0"))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushRawAsync("private", rightFeed, pushPrivateOnly));
        }
    }

    [Fact]
    public async Task A_private_feed_challenges_and_accepts_Basic_credentials_through_the_NuGet_client()
    {
        var id = FiGetServerFixture.UniqueId("Private.Read");
        using (var stream = TestPackages.Create(id, "1.0.0"))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushRawAsync("private", stream, FiGetServerFixture.AdminToken));
        }

        using (var anonymous = server.CreateClient())
        {
            var challenge = await anonymous.GetAsync("nuget/private/v3/index.json");
            HttpAssert.Status(HttpStatusCode.Unauthorized, challenge);
            Assert.Contains(challenge.Headers.WwwAuthenticate, h => h.Scheme == "Basic");
            HttpAssert.Status(HttpStatusCode.Unauthorized, await anonymous.GetAsync($"nuget/private/v3/flatcontainer/{id.ToLowerInvariant()}/index.json"));
        }

        var reader = await CreateTokenAsync(FiGet.Domain.Entities.TokenScopes.Read, feed: "private");
        using (var authenticated = server.CreateClient(reader))
        {
            await HttpAssert.SuccessBodyAsync(await authenticated.GetAsync($"nuget/private/v3/flatcontainer/{id.ToLowerInvariant()}/index.json"));
        }

        using var withHeader = server.CreateClient();
        withHeader.DefaultRequestHeaders.Add("X-NuGet-ApiKey", reader);
        await HttpAssert.SuccessBodyAsync(await withHeader.GetAsync("nuget/private/v3/index.json"));

        using var cache = NoCache;
        var metadata = await (await server.Repository("private", reader).GetResourceAsync<PackageMetadataResource>())
            .GetMetadataAsync(id, includePrerelease: true, includeUnlisted: true, cache, NullLogger.Instance, CancellationToken.None);
        Assert.Single(metadata);
    }

    [Fact]
    public async Task Delete_on_an_unlist_feed_hides_the_version_from_search_but_keeps_it_downloadable_and_relist_restores_it()
    {
        var id = FiGetServerFixture.UniqueId("Unlist");
        foreach (var version in new[] { "1.0.0", "2.0.0" })
        {
            using var stream = TestPackages.Create(id, version);
            HttpAssert.Status(HttpStatusCode.Created, await PushRawAsync("public", stream, FiGetServerFixture.AdminToken));
        }

        using var admin = server.CreateClient();
        admin.DefaultRequestHeaders.Add("X-NuGet-ApiKey", FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.NoContent, await admin.DeleteAsync($"nuget/public/v3/publish/{id}/2.0.0"));

        using var client = server.CreateClient();
        var search = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/public/v3/query?q=packageid:{id}")))!;
        var hit = search["data"]![0]!;
        Assert.Equal("1.0.0", (string?)hit["version"]);
        Assert.Single(hit["versions"]!.AsArray());

        var registration = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/public/v3/registration/{id.ToLowerInvariant()}/index.json")))!;
        var unlisted = registration["items"]![0]!["items"]!.AsArray().Single(i => (string?)i!["catalogEntry"]!["version"] == "2.0.0")!["catalogEntry"]!;
        Assert.False((bool)unlisted["listed"]!);
        Assert.StartsWith("1900-01-01", (string?)unlisted["published"]);

        var flat = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/public/v3/flatcontainer/{id.ToLowerInvariant()}/index.json")))!;
        Assert.Equal(["1.0.0", "2.0.0"], flat["versions"]!.AsArray().Select(v => (string?)v));
        await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/public/v3/flatcontainer/{id.ToLowerInvariant()}/2.0.0/{id.ToLowerInvariant()}.2.0.0.nupkg"));

        HttpAssert.Status(HttpStatusCode.OK, await admin.PostAsync($"nuget/public/v3/publish/{id}/2.0.0", null));
        search = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/public/v3/query?q=packageid:{id}")))!;
        Assert.Equal("2.0.0", (string?)search["data"]![0]!["version"]);

        HttpAssert.Status(HttpStatusCode.NotFound, await admin.DeleteAsync($"nuget/public/v3/publish/{id}/9.9.9"));
        using var anonymous = server.CreateClient();
        HttpAssert.Status(HttpStatusCode.Unauthorized, await anonymous.DeleteAsync($"nuget/public/v3/publish/{id}/1.0.0"));
    }

    [Fact]
    public async Task Hard_delete_removes_the_version_and_the_file()
    {
        var id = FiGetServerFixture.UniqueId("HardDelete");
        using (var stream = TestPackages.Create(id, "1.0.0"))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushRawAsync("overwrite", stream, FiGetServerFixture.AdminToken));
        }

        using var admin = server.CreateClient();
        admin.DefaultRequestHeaders.Add("X-NuGet-ApiKey", FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.NoContent, await admin.DeleteAsync($"nuget/overwrite/v3/publish/{id}/1.0.0"));

        using var client = server.CreateClient();
        HttpAssert.Status(HttpStatusCode.NotFound, await client.GetAsync($"nuget/overwrite/v3/flatcontainer/{id.ToLowerInvariant()}/1.0.0/{id.ToLowerInvariant()}.1.0.0.nupkg"));
        HttpAssert.Status(HttpStatusCode.NotFound, await client.GetAsync($"nuget/overwrite/v3/registration/{id.ToLowerInvariant()}/index.json"));

        using var again = TestPackages.Create(id, "1.0.0");
        HttpAssert.Status(HttpStatusCode.Created, await PushRawAsync("overwrite", again, FiGetServerFixture.AdminToken));
    }

    [Fact]
    public async Task More_than_128_versions_page_the_registration_like_nuget_org()
    {
        var id = FiGetServerFixture.UniqueId("Many.Versions");
        for (var i = 1; i <= 130; i++)
        {
            using var stream = TestPackages.Create(id, $"1.0.{i}");
            HttpAssert.Status(HttpStatusCode.Created, await PushRawAsync("public", stream, FiGetServerFixture.AdminToken));
        }

        using var client = server.CreateClient();
        var index = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/public/v3/registration/{id.ToLowerInvariant()}/index.json")))!;
        var pages = index["items"]!.AsArray();
        Assert.Equal(3, (int)index["count"]!);
        Assert.All(pages, p => Assert.Null(p!["items"]));
        Assert.Equal("1.0.1", (string?)pages[0]!["lower"]);
        Assert.Equal("1.0.64", (string?)pages[0]!["upper"]);

        var page = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync(new Uri((string)pages[2]!["@id"]!))))!;
        Assert.Equal(2, page["items"]!.AsArray().Count);

        using var cache = NoCache;
        var metadata = await (await server.Repository("public").GetResourceAsync<PackageMetadataResource>())
            .GetMetadataAsync(id, includePrerelease: true, includeUnlisted: false, cache, NullLogger.Instance, CancellationToken.None);
        Assert.Equal(130, metadata.Count());
    }

    [Fact]
    public async Task SemVer2_versions_are_hidden_from_clients_that_do_not_ask_for_them()
    {
        var id = FiGetServerFixture.UniqueId("SemVer.Levels");
        foreach (var version in new[] { "1.0.0", "2.0.0-beta.1" })
        {
            using var stream = TestPackages.Create(id, version);
            HttpAssert.Status(HttpStatusCode.Created, await PushRawAsync("public", stream, FiGetServerFixture.AdminToken));
        }

        using var client = server.CreateClient();
        var semVer1 = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/public/v3/query?q=packageid:{id}&prerelease=true")))!;
        Assert.Equal("1.0.0", (string?)semVer1["data"]![0]!["version"]);
        Assert.Single(semVer1["data"]![0]!["versions"]!.AsArray());

        var semVer2 = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/public/v3/query?q=packageid:{id}&prerelease=true&semVerLevel=2.0.0")))!;
        Assert.Equal("2.0.0-beta.1", (string?)semVer2["data"]![0]!["version"]);
    }

    [Fact]
    public async Task Four_part_versions_are_normalised_in_urls_and_reachable_by_either_spelling()
    {
        var id = FiGetServerFixture.UniqueId("Four.Part");
        foreach (var version in new[] { "1.2.3.0", "1.2.3.4" })
        {
            using var stream = TestPackages.Create(id, version);
            HttpAssert.Status(HttpStatusCode.Created, await PushRawAsync("public", stream, FiGetServerFixture.AdminToken));
        }

        var lower = id.ToLowerInvariant();
        using var client = server.CreateClient();
        var flat = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/public/v3/flatcontainer/{lower}/index.json")))!;
        Assert.Equal(["1.2.3", "1.2.3.4"], flat["versions"]!.AsArray().Select(v => (string?)v));

        await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/public/v3/flatcontainer/{lower}/1.2.3/{lower}.1.2.3.nupkg"));
        await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/public/v3/flatcontainer/{lower}/1.2.3.0/{lower}.1.2.3.0.nupkg"));
        var nuspec = await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/public/v3/flatcontainer/{lower}/1.2.3.4/{lower}.nuspec"));
        Assert.Contains("<version>1.2.3.4</version>", nuspec, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_understands_packageid_id_wildcards_tags_and_package_types()
    {
        var prefix = FiGetServerFixture.UniqueId("Search");
        using (var a = TestPackages.Create(prefix + ".Alpha", "1.0.0", b => { b.Tags.Add("psmodule"); b.Title = "Alpha title"; }))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushRawAsync("public", a, FiGetServerFixture.AdminToken));
        }

        using (var b = TestPackages.Create(prefix + ".Beta", "1.0.0", b => { b.Tags.Add("psscript"); b.PackageTypes.Add(new NuGet.Packaging.Core.PackageType("DotnetTool", new Version(0, 0))); }))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushRawAsync("public", b, FiGetServerFixture.AdminToken));
        }

        using var client = server.CreateClient();
        async Task<string[]> Ids(string query) =>
            JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync("nuget/public/v3/query?take=50&" + query)))!["data"]!.AsArray().Select(d => (string)d!["id"]!).ToArray();

        Assert.Equal([prefix + ".Alpha", prefix + ".Beta"], await Ids("q=" + Uri.EscapeDataString(prefix)));
        Assert.Equal([prefix + ".Alpha"], await Ids("q=" + Uri.EscapeDataString("packageid:" + prefix + ".alpha")));
        Assert.Equal([prefix + ".Alpha", prefix + ".Beta"], await Ids("q=" + Uri.EscapeDataString("id:" + prefix + ".*")));
        Assert.Equal([prefix + ".Beta"], await Ids("q=" + Uri.EscapeDataString(prefix + " tags:psscript")));
        Assert.Equal([prefix + ".Alpha"], await Ids("q=" + Uri.EscapeDataString(prefix + " tag:PSModule")));
        Assert.Equal([prefix + ".Beta"], await Ids("q=" + Uri.EscapeDataString(prefix) + "&packageType=DotnetTool"));
        Assert.Empty(await Ids("q=" + Uri.EscapeDataString(prefix + " tags:psmod")));

        var paged = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync("nuget/public/v3/query?take=1&skip=1&q=" + Uri.EscapeDataString(prefix))))!;
        Assert.Equal(2, (int)paged["totalHits"]!);
        Assert.Equal(prefix + ".Beta", (string?)Assert.Single(paged["data"]!.AsArray())!["id"]);
    }

    [Fact]
    public async Task Symbol_packages_are_accepted_for_existing_versions_and_served_by_symbol_key()
    {
        var id = FiGetServerFixture.UniqueId("Symbols");
        using (var package = TestPackages.Create(id, "1.0.0"))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushRawAsync("public", package, FiGetServerFixture.AdminToken));
        }

        var pdb = TestPackages.PortablePdb();
        using (var orphan = TestPackages.CreateSymbols(id, "9.0.0", pdb, "Lib.pdb"))
        {
            HttpAssert.Status(HttpStatusCode.NotFound, await PushRawAsync("public", orphan, FiGetServerFixture.AdminToken, "symbolpublish"));
        }

        using (var windowsPdb = TestPackages.CreateSymbols(id, "1.0.0", "Microsoft C/C++ MSF 7.00\r\n"u8.ToArray(), "Lib.pdb"))
        {
            HttpAssert.Status(HttpStatusCode.BadRequest, await PushRawAsync("public", windowsPdb, FiGetServerFixture.AdminToken, "symbolpublish"));
        }

        using (var symbols = TestPackages.CreateSymbols(id, "1.0.0", pdb, "Lib.pdb"))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushRawAsync("public", symbols, FiGetServerFixture.AdminToken, "symbolpublish"));
        }

        using (var symbolsToPackageEndpoint = TestPackages.CreateSymbols(id, "1.0.0", pdb, "Lib.pdb"))
        {
            HttpAssert.Status(HttpStatusCode.BadRequest, await PushRawAsync("public", symbolsToPackageEndpoint, FiGetServerFixture.AdminToken));
        }

        var key = PortablePdbKey(pdb);
        using var client = server.CreateClient();
        var served = await client.GetAsync($"nuget/public/symbols/Lib.pdb/{key.ToUpperInvariant()}/lib.pdb");
        await HttpAssert.SuccessBodyAsync(served);
        Assert.Equal(pdb, await served.Content.ReadAsByteArrayAsync());
        HttpAssert.Status(HttpStatusCode.NotFound, await client.GetAsync($"nuget/public/symbols/Lib.pdb/{new string('0', 40)}/Lib.pdb"));
    }

    /// <summary>
    /// A second symbol package for a version follows the feed's overwrite setting, as the package does: refused where
    /// overwriting is not allowed, and where it is, the new symbols replace the old ones and the old file is gone. Found
    /// cross-checking other package servers' issue trackers (BaGet #688).
    /// </summary>
    [Fact]
    public async Task A_second_symbol_package_follows_the_overwrite_setting()
    {
        var first = TestPackages.PortablePdb();
        var second = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "FiGet.Integration.Tests.pdb"));
        Assert.NotEqual(PortablePdbKey(first), PortablePdbKey(second));
        using var client = server.CreateClient();

        foreach (var (feed, overwrite) in new[] { ("public", false), ("overwrite", true) })
        {
            var id = FiGetServerFixture.UniqueId("Symbols.Twice");
            using (var package = TestPackages.Create(id, "1.0.0"))
            {
                HttpAssert.Status(HttpStatusCode.Created, await PushRawAsync(feed, package, FiGetServerFixture.AdminToken));
            }

            using (var symbols = TestPackages.CreateSymbols(id, "1.0.0", first, "Lib.pdb"))
            {
                HttpAssert.Status(HttpStatusCode.Created, await PushRawAsync(feed, symbols, FiGetServerFixture.AdminToken, "symbolpublish"));
            }

            using (var again = TestPackages.CreateSymbols(id, "1.0.0", second, "Lib.pdb"))
            {
                HttpAssert.Status(overwrite ? HttpStatusCode.Created : HttpStatusCode.Conflict, await PushRawAsync(feed, again, FiGetServerFixture.AdminToken, "symbolpublish"));
            }

            var kept = overwrite ? second : first;
            var gone = overwrite ? first : second;
            var served = await client.GetAsync($"nuget/{feed}/symbols/Lib.pdb/{PortablePdbKey(kept)}/Lib.pdb");
            HttpAssert.Status(HttpStatusCode.OK, served);
            Assert.Equal(kept, await served.Content.ReadAsByteArrayAsync());
            HttpAssert.Status(HttpStatusCode.NotFound, await client.GetAsync($"nuget/{feed}/symbols/Lib.pdb/{PortablePdbKey(gone)}/Lib.pdb"));

            // Not only unlisted: the replaced PDB's file is removed from storage.
            await using var scope = server.Services.CreateAsyncScope();
            var feedKey = (await scope.ServiceProvider.GetRequiredService<FiGet.Application.Ports.IFeedStore>().FindAsync(feed, CancellationToken.None))!.Key;
            await using var file = await scope.ServiceProvider.GetRequiredService<FiGet.Application.Ports.IPackageStorage>()
                .OpenSymbolAsync(new FiGet.Application.Ports.SymbolStorageKey(feedKey, "lib.pdb", PortablePdbKey(gone)), CancellationToken.None);
            Assert.True(file is null, $"The {(overwrite ? "replaced" : "refused")} PDB's file is in storage.");
        }
    }

    [Fact]
    public async Task Garbage_and_empty_uploads_are_400()
    {
        using (var garbage = new MemoryStream("not a zip"u8.ToArray()))
        {
            HttpAssert.Status(HttpStatusCode.BadRequest, await PushRawAsync("public", garbage, FiGetServerFixture.AdminToken));
        }

        using var admin = server.CreateClient();
        admin.DefaultRequestHeaders.Add("X-NuGet-ApiKey", FiGetServerFixture.AdminToken);
        HttpAssert.Status(HttpStatusCode.BadRequest, await admin.PutAsync("nuget/public/v3/publish", new ByteArrayContent([])));
    }

    [Fact]
    public async Task Downloads_are_counted()
    {
        var id = FiGetServerFixture.UniqueId("Downloads");
        using (var stream = TestPackages.Create(id, "1.0.0"))
        {
            HttpAssert.Status(HttpStatusCode.Created, await PushRawAsync("public", stream, FiGetServerFixture.AdminToken));
        }

        var lower = id.ToLowerInvariant();
        using var client = server.CreateClient();
        for (var i = 0; i < 3; i++)
        {
            await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/public/v3/flatcontainer/{lower}/1.0.0/{lower}.1.0.0.nupkg"));
        }

        var search = JsonNode.Parse(await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/public/v3/query?q=packageid:{id}")))!;
        Assert.Equal(3, (long)search["data"]![0]!["totalDownloads"]!);
    }

    private async Task<HttpResponseMessage> PushRawAsync(string feed, Stream package, string? apiKey, string resource = "publish")
    {
        using var client = server.CreateClient();
        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.Add("X-NuGet-ApiKey", apiKey);
        }

        using var content = new MultipartFormDataContent();
        var file = new StreamContent(package);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "package", "package.nupkg");
        return await client.PutAsync($"nuget/{feed}/v3/{resource}", content);
    }

    // The push goes over raw multipart: NuGet 7's PackageUpdateResource refuses plain-HTTP sources and says so only
    // through its logger, and it builds its own PackageSource, so a test cannot opt in. Push through the real client
    // is verified with `dotnet nuget push` against a running instance (docs/status.md).
    private async Task PushWithClientAsync(string feed, string id, string version, Action<PackageBuilder>? configure = null)
    {
        using var stream = TestPackages.Create(id, version, configure);
        await PushWithClientAsync(feed, stream);
    }

    private async Task PushWithClientAsync(string feed, Stream package) =>
        HttpAssert.Status(HttpStatusCode.Created, await PushRawAsync(feed, package, FiGetServerFixture.AdminToken));

    private async Task<string> CreateTokenAsync(FiGet.Domain.Entities.TokenScopes scopes, string? feed)
    {
        await using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateAsyncScope(server.Services);
        var services = scope.ServiceProvider;
        int? feedKey = null;
        if (feed is not null)
        {
            var feeds = (FiGet.Application.Ports.IFeedStore)services.GetService(typeof(FiGet.Application.Ports.IFeedStore))!;
            feedKey = (await feeds.FindAsync(feed, CancellationToken.None))!.Key;
        }

        var tokens = (FiGet.Application.Tokens.AccessTokenService)services.GetService(typeof(FiGet.Application.Tokens.AccessTokenService))!;
        return (await tokens.CreateServiceTokenAsync(FiGetServerFixture.SuperAdminActor, "test-" + scopes, scopes, feedKey, null, CancellationToken.None)).Created!.Secret;
    }

    private static string PortablePdbKey(byte[] pdb)
    {
        using var provider = System.Reflection.Metadata.MetadataReaderProvider.FromPortablePdbImage(System.Collections.Immutable.ImmutableArray.Create(pdb));
        var id = provider.GetMetadataReader().DebugMetadataHeader!.Id;
        return new Guid(id.AsSpan()[..16]).ToString("N") + "ffffffff";
    }
}
