using System.Net;
using System.Text;
using FiGet.Application.Ports;
using FiGet.Infrastructure.Assets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FiGet.Integration.Tests;

/// <summary>
/// Fetching by URL through an HTTP proxy. The proxy here is a small local server that answers requests in proxy form
/// (<c>GET http://files.test/a.txt</c>) as if it had fetched them, so the test sees exactly what the fetcher sent through it.
/// </summary>
public sealed class RemoteFetchProxyTests : IAsyncLifetime
{
    private WebApplication proxy = null!;
    private Uri proxyAddress = null!;
    private readonly List<string> seen = [];

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(k => k.Listen(IPAddress.Loopback, 0));
        proxy = builder.Build();
        proxy.Run(async context =>
        {
            var target = $"{context.Request.Host}{context.Request.Path}";
            lock (seen)
            {
                seen.Add(target);
            }

            switch (target)
            {
                case "files.test/a.txt":
                    context.Response.ContentType = "text/plain";
                    await context.Response.WriteAsync("through the proxy");
                    break;
                case "files.test/moved":
                    context.Response.Redirect("http://mirror.files.test/a.txt");
                    break;
                case "mirror.files.test/a.txt":
                    await context.Response.WriteAsync("from the mirror");
                    break;
                case "files.test/elsewhere":
                    context.Response.Redirect("http://internal.test/secret");
                    break;
                default:
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    break;
            }
        });
        await proxy.StartAsync();
        var address = proxy.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        proxyAddress = new Uri(address.Replace("[::]", "127.0.0.1", StringComparison.Ordinal));
    }

    public async ValueTask DisposeAsync()
    {
        await proxy.DisposeAsync();
    }

    [Fact]
    public async Task A_fetch_goes_through_the_proxy_to_allowed_hosts_only()
    {
        using var source = new HttpRemoteFileSource(new RemoteFetchSettings { Proxy = proxyAddress, AllowedHosts = ["files.test", "*.files.test"] });

        Assert.Equal("through the proxy", await ReadAsync(source, "http://files.test/a.txt"));
        Assert.Equal("from the mirror", await ReadAsync(source, "http://files.test/moved"));

        var refused = await Assert.ThrowsAsync<RemoteFetchException>(() => source.OpenAsync(new Uri("http://internal.test/secret"), CancellationToken.None));
        Assert.True(refused.Refused, refused.Message);

        var redirected = await Assert.ThrowsAsync<RemoteFetchException>(() => source.OpenAsync(new Uri("http://files.test/elsewhere"), CancellationToken.None));
        Assert.True(redirected.Refused, redirected.Message);

        lock (seen)
        {
            Assert.Equal(["files.test/a.txt", "files.test/moved", "mirror.files.test/a.txt", "files.test/elsewhere"], seen);
        }
    }

    [Fact]
    public async Task A_proxy_without_allowed_hosts_fetches_nothing()
    {
        using var source = new HttpRemoteFileSource(new RemoteFetchSettings { Proxy = proxyAddress });

        var refused = await Assert.ThrowsAsync<RemoteFetchException>(() => source.OpenAsync(new Uri("http://files.test/a.txt"), CancellationToken.None));

        Assert.True(refused.Refused, refused.Message);
        Assert.Contains("AllowedHosts", refused.Message, StringComparison.Ordinal);
        lock (seen)
        {
            Assert.Empty(seen);
        }
    }

    private static async Task<string> ReadAsync(HttpRemoteFileSource source, string url)
    {
        await using var file = await source.OpenAsync(new Uri(url), CancellationToken.None);
        using var reader = new StreamReader(file.Content, Encoding.UTF8);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }
}
