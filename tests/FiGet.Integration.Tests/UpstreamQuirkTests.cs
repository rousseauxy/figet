using System.Globalization;
using System.Net;
using System.Security;
using System.Text;
using FiGet.Application.Connectors;
using FiGet.Domain.Entities;
using FiGet.Infrastructure.Upstream;
using FiGet.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NuGet.Versioning;

namespace FiGet.Integration.Tests;

/// <summary>
/// What real upstreams do that a stub does not, against the real upstream client and small feeds on loopback: a download
/// redirected to another host, a body without a length, a v2 gallery with a version it cannot parse. Open questions from
/// cross-checking other package servers' issue trackers (2026-09-15).
/// </summary>
public sealed class UpstreamQuirkTests : IDisposable
{
    private readonly string tempPath = Directory.CreateTempSubdirectory("figet-upstream-quirks").FullName;

    public void Dispose() => Directory.Delete(tempPath, recursive: true);

    /// <summary>
    /// A v3 feed whose package download answers with a redirect to another host - a CDN, blob storage - and that host sends
    /// the package without a Content-Length, chunked. The package arrives whole.
    /// </summary>
    [Fact]
    public async Task A_download_redirected_to_another_host_and_sent_chunked_arrives_whole()
    {
        using var package = TestPackages.Create("Quirk.Redirect", "1.0.0");
        var bytes = package.ToArray();

        await using var blobs = await StartAsync(app => app.MapGet("/blob/quirk.redirect.1.0.0.nupkg", async (HttpContext http) =>
        {
            http.Response.ContentType = "application/octet-stream";
            foreach (var chunk in bytes.Chunk(1024))
            {
                await http.Response.Body.WriteAsync(chunk);
                await http.Response.Body.FlushAsync();
            }
        }));

        await using var feed = await StartAsync(app =>
        {
            app.MapGet("/v3/index.json", (HttpContext http) => Results.Json(new
            {
                version = "3.0.0",
                resources = new[] { new Dictionary<string, string> { ["@id"] = $"{http.Request.Scheme}://{http.Request.Host}/flat/", ["@type"] = "PackageBaseAddress/3.0.0" } },
            }));
            app.MapGet("/flat/quirk.redirect/index.json", () => Results.Json(new { versions = new[] { "1.0.0" } }));
            app.MapGet("/flat/quirk.redirect/1.0.0/quirk.redirect.1.0.0.nupkg", () => Results.Redirect(blobs.Address + "/blob/quirk.redirect.1.0.0.nupkg"));
        });

        using var client = new NuGetUpstreamClient(new ConnectorSettings { TempPath = tempPath });
        var upstream = new FeedUpstream { Key = 1, Name = "redirecting", Url = feed.Address + "/v3/index.json", Kind = UpstreamKind.V3 };
        await using var stream = await client.OpenPackageAsync(upstream, "quirk.redirect", NuGetVersion.Parse("1.0.0"), CancellationToken.None);

        Assert.NotNull(stream);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        Assert.Equal(bytes, copy.ToArray());
    }

    /// <summary>
    /// A v2 gallery listing one version the client library cannot parse among valid ones. The valid versions are still
    /// listed: the id is not lost because of one bad entry.
    /// </summary>
    [Fact]
    public async Task A_v2_gallery_with_an_unparsable_version_still_lists_the_valid_ones()
    {
        await using var feed = await StartAsync(app =>
        {
            app.MapGet("/", (HttpContext http) => Results.Text(
                $"""<?xml version="1.0" encoding="utf-8"?><service xml:base="{Base(http)}" xmlns:atom="http://www.w3.org/2005/Atom" xmlns="http://www.w3.org/2007/app"><workspace><atom:title type="text">Default</atom:title><collection href="Packages"><atom:title type="text">Packages</atom:title></collection></workspace></service>""",
                "application/xml"));
            app.MapGet("/FindPackagesById()", (HttpContext http) => Results.Text(
                Feed(Base(http), ["1.0.0", "not-a-version", "2.0.0"]),
                "application/atom+xml;type=feed;charset=utf-8"));
        });

        using var client = new NuGetUpstreamClient(new ConnectorSettings { TempPath = tempPath });
        var upstream = new FeedUpstream { Key = 2, Name = "odd-gallery", Url = feed.Address + "/", Kind = UpstreamKind.V2 };

        var catalog = await client.GetCatalogAsync(upstream, "quirk.v2", CancellationToken.None);
        Assert.Equal(["1.0.0", "2.0.0"], catalog.Versions.Select(v => v.Version.ToNormalizedString()).ToArray());
    }

    /// <summary>
    /// A v2 gallery publishes each package's hash. A download that matches it is served; one that does not - truncated,
    /// altered, a different file behind the name - is refused, so it is never cached as that version. Backlog item from the
    /// 2026-09-14 review.
    /// </summary>
    [Fact]
    public async Task A_v2_download_is_checked_against_the_published_hash()
    {
        using var good = TestPackages.Create("Quirk.Hash", "1.0.0");
        using var other = TestPackages.Create("Quirk.Hash", "2.0.0");
        var goodBytes = good.ToArray();
        var published = new Dictionary<string, string>
        {
            ["1.0.0"] = Convert.ToBase64String(System.Security.Cryptography.SHA512.HashData(goodBytes)),
            ["2.0.0"] = Convert.ToBase64String(System.Security.Cryptography.SHA512.HashData(goodBytes)),
        };
        var served = new Dictionary<string, byte[]> { ["1.0.0"] = goodBytes, ["2.0.0"] = other.ToArray() };

        await using var feed = await StartAsync(app =>
        {
            app.MapGet("/", (HttpContext http) => Results.Text(
                $"""<?xml version="1.0" encoding="utf-8"?><service xml:base="{Base(http)}" xmlns:atom="http://www.w3.org/2005/Atom" xmlns="http://www.w3.org/2007/app"><workspace><atom:title type="text">Default</atom:title><collection href="Packages"><atom:title type="text">Packages</atom:title></collection></workspace></service>""",
                "application/xml"));
            app.MapGet("/FindPackagesById()", (HttpContext http) => Results.Text(Feed(Base(http), ["1.0.0", "2.0.0"], "Quirk.Hash", published), "application/atom+xml;type=feed;charset=utf-8"));
            app.MapGet("/Packages(Id='{id}',Version='{version}')", (HttpContext http, string version) => Results.Text(Feed(Base(http), [version], "Quirk.Hash", published), "application/atom+xml;type=feed;charset=utf-8"));
            app.MapGet("/package/Quirk.Hash/{version}", (string version) => Results.Bytes(served[version], "application/zip"));
        });

        using var client = new NuGetUpstreamClient(new ConnectorSettings { TempPath = tempPath });
        var upstream = new FeedUpstream { Key = 3, Name = "hashing-gallery", Url = feed.Address + "/", Kind = UpstreamKind.V2 };

        await using (var stream = await client.OpenPackageAsync(upstream, "quirk.hash", NuGetVersion.Parse("1.0.0"), CancellationToken.None))
        {
            Assert.NotNull(stream);
            Assert.Equal(goodBytes.Length, stream.Length);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => client.OpenPackageAsync(upstream, "quirk.hash", NuGetVersion.Parse("2.0.0"), CancellationToken.None));
        Assert.Empty(Directory.GetFiles(tempPath));
    }

    private static string Base(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}/";

    private static string Feed(string root, IEnumerable<string> versions) => Feed(root, versions, "Quirk.V2", new Dictionary<string, string>());

    private static string Feed(string root, IEnumerable<string> versions, string id, IReadOnlyDictionary<string, string> hashes)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"""<?xml version="1.0" encoding="utf-8"?><feed xml:base="{root}" xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata" xmlns="http://www.w3.org/2005/Atom"><id>{root}FindPackagesById()</id><title type="text">Packages</title><updated>2026-09-15T00:00:00Z</updated>""");
        foreach (var version in versions)
        {
            var v = SecurityElement.Escape(version);
            builder.Append(CultureInfo.InvariantCulture, $"""<entry><id>{root}Packages(Id='{id}',Version='{v}')</id><category term="NuGetGallery.OData.V2FeedPackage" scheme="http://schemas.microsoft.com/ado/2007/08/dataservices/scheme" /><title type="text">{id}</title><updated>2026-01-01T00:00:00Z</updated><author><name>Quirks</name></author><content type="application/zip" src="{root}package/{id}/{v}" /><m:properties><d:Id>{id}</d:Id><d:Version>{v}</d:Version><d:NormalizedVersion>{v}</d:NormalizedVersion><d:Description>A quirky package</d:Description><d:IsLatestVersion m:type="Edm.Boolean">false</d:IsLatestVersion><d:IsAbsoluteLatestVersion m:type="Edm.Boolean">false</d:IsAbsoluteLatestVersion><d:IsPrerelease m:type="Edm.Boolean">false</d:IsPrerelease><d:Published m:type="Edm.DateTime">2026-01-01T00:00:00Z</d:Published><d:Dependencies /><d:Tags /><d:PackageHash>{(hashes.TryGetValue(version, out var hash) ? hash : "")}</d:PackageHash><d:PackageHashAlgorithm>SHA512</d:PackageHashAlgorithm></m:properties></entry>""");
        }

        builder.Append("</feed>");
        return builder.ToString();
    }

    private static async Task<LoopbackApp> StartAsync(Action<WebApplication> map)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        map(app);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
        return new LoopbackApp(app, address);
    }

    private sealed class LoopbackApp(WebApplication app, string address) : IAsyncDisposable
    {
        public string Address { get; } = address;

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
