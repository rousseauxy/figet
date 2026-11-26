using System.Net;
using System.Net.Http.Headers;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FiGet.Integration.Tests;

/// <summary>A server with the full request log off, as instances run by default, and the slow threshold off.</summary>
public sealed class ProblemLogFixture() : FiGetServerFixture(TestDatabase.Sqlite)
{
    public CollectingLoggerProvider Logs { get; } = new();

    protected override void Configure(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSetting("FiGet:Logging:SlowRequestSeconds", "0");
        builder.UseSetting("Logging:LogLevel:FiGet.Web.Logging", "Information");
        builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(Logs));
    }
}

/// <summary>The same, with every request counting as slow.</summary>
public sealed class SlowLogFixture() : FiGetServerFixture(TestDatabase.Sqlite)
{
    public CollectingLoggerProvider Logs { get; } = new();

    protected override void Configure(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSetting("FiGet:Logging:SlowRequestSeconds", "0.000001");
        builder.UseSetting("Logging:LogLevel:FiGet.Web.Logging", "Information");
        builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(Logs));
    }
}

/// <summary>
/// With the full request log off, the requests that went wrong are still logged (FG-61). A QA load test on 2026-11-26 saw
/// timeouts, cut-off answers and 404s on the client side and nothing at all in the server's log.
/// </summary>
public sealed class ProblemRequestLogTests(ProblemLogFixture server) : IClassFixture<ProblemLogFixture>
{
    [Fact]
    public async Task An_ordinary_request_writes_no_line()
    {
        var id = FiGetServerFixture.UniqueId("Quiet.Request");
        using var client = server.CreateClient();
        await client.GetAsync($"nuget/public/FindPackagesById()?id='{id}'");

        await Task.Delay(500);
        Assert.DoesNotContain(server.Logs.Lines, l => l.Contains(id, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_client_that_leaves_during_a_download_is_logged_as_such()
    {
        var id = FiGetServerFixture.UniqueId("Left.Download");
        var noise = new byte[8 * 1024 * 1024];
        Random.Shared.NextBytes(noise);
        using (var package = TestPackages.Create(id, "1.0.0", b => b.AddContent("tools/noise.bin", noise)))
        using (var pusher = server.CreateClient())
        using (var content = new MultipartFormDataContent())
        {
            pusher.DefaultRequestHeaders.Add("X-NuGet-ApiKey", FiGetServerFixture.AdminToken);
            content.Add(new StreamContent(package) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } }, "package", "package.nupkg");
            Assert.Equal(HttpStatusCode.Created, (await pusher.PutAsync("nuget/public/v3/publish", content)).StatusCode);
        }

        using (var client = server.CreateClient())
        {
            var response = await client.GetAsync($"nuget/public/package/{id}/1.0.0", HttpCompletionOption.ResponseHeadersRead);
            var stream = await response.Content.ReadAsStreamAsync();
            await stream.ReadExactlyAsync(new byte[64 * 1024]);
            response.Dispose();
        }

        var line = await RequestLog.WaitForLineAsync(server.Logs, $"/nuget/public/package/{id}/1.0.0");
        Assert.NotNull(line);
        Assert.Contains("client left during the answer", line, StringComparison.Ordinal);
        Assert.DoesNotContain("exception", line, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class SlowRequestLogTests(SlowLogFixture server) : IClassFixture<SlowLogFixture>
{
    /// <summary>Past the threshold to the first byte, a request is logged with how long it waited, whatever its status.</summary>
    [Fact]
    public async Task A_request_slower_than_the_threshold_is_logged_with_its_first_byte_time()
    {
        var id = FiGetServerFixture.UniqueId("Slow.Request");
        using var client = server.CreateClient();
        await client.GetAsync($"nuget/public/FindPackagesById()?id='{id}'");

        var line = await RequestLog.WaitForLineAsync(server.Logs, id);
        Assert.NotNull(line);
        Assert.Contains("-> 200 done", line, StringComparison.Ordinal);
        Assert.Matches(@"first byte \d+ms", line);
    }
}

internal static class RequestLog
{
    public static async Task<string?> WaitForLineAsync(CollectingLoggerProvider logs, string contains)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (logs.Lines.FirstOrDefault(l => l.Contains(contains, StringComparison.Ordinal)) is { } found)
            {
                return found;
            }

            await Task.Delay(50);
        }

        return null;
    }
}
