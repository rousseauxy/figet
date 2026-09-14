using System.Net;
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

/// <summary>The real upstream client downloading a package, against a minimal v3 feed on loopback.</summary>
public sealed class UpstreamDownloadTests : IDisposable
{
    private readonly string tempPath = Directory.CreateTempSubdirectory("figet-upstream-temp").FullName;

    public void Dispose() => Directory.Delete(tempPath, recursive: true);

    /// <summary>
    /// A download is buffered under <c>FiGet:Storage:TempPath</c> and gone when it is closed. Found by the 2026-09-14 review:
    /// it went to the system temp directory, which on a pod is the container's small writable layer.
    /// </summary>
    [Fact]
    public async Task A_download_is_buffered_under_the_configured_temp_path_and_removed_when_closed()
    {
        using var package = TestPackages.Create("Temp.Probe", "1.0.0");
        var bytes = package.ToArray();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        await using var feed = builder.Build();
        feed.MapGet("/v3/index.json", (HttpContext http) => Results.Json(new
        {
            version = "3.0.0",
            resources = new[] { new Dictionary<string, string> { ["@id"] = $"{http.Request.Scheme}://{http.Request.Host}/flat/", ["@type"] = "PackageBaseAddress/3.0.0" } },
        }));
        feed.MapGet("/flat/temp.probe/index.json", () => Results.Json(new { versions = new[] { "1.0.0" } }));
        feed.MapGet("/flat/temp.probe/1.0.0/temp.probe.1.0.0.nupkg", () => Results.Bytes(bytes, "application/octet-stream"));
        await feed.StartAsync();
        var address = feed.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');

        try
        {
            using var client = new NuGetUpstreamClient(new ConnectorSettings { TempPath = tempPath });
            var upstream = new FeedUpstream { Key = 1, Name = "loopback", Url = address + "/v3/index.json", Kind = UpstreamKind.V3 };

            await using (var stream = await client.OpenPackageAsync(upstream, "temp.probe", NuGetVersion.Parse("1.0.0"), CancellationToken.None))
            {
                Assert.NotNull(stream);
                Assert.Equal(bytes.Length, stream.Length);
                Assert.Single(Directory.GetFiles(tempPath, "figet-upstream-*.tmp"));
            }

            Assert.Empty(Directory.GetFiles(tempPath));
        }
        finally
        {
            await feed.StopAsync();
        }
    }
}
