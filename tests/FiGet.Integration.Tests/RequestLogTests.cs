using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FiGet.Integration.Tests;

/// <summary>Collects what was logged, so a test can assert on it rather than on a console somebody reads.</summary>
public sealed class CollectingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Collector(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Collector(CollectingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            owner.Lines.Enqueue(category + " | " + formatter(state, exception));
        }
    }
}

/// <summary>A server with request logging turned on, which is not the default.</summary>
public sealed class RequestLogFixture() : FiGetServerFixture(TestDatabase.Sqlite)
{
    public CollectingLoggerProvider Logs { get; } = new();

    protected override void Configure(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSetting("FiGet:Logging:Requests", "true");

        // The shared fixture silences Information for every test, and the request log is written at
        // Information - so without this the collector sees only warnings and the assertions below fail
        // for a reason that has nothing to do with the middleware. Raised for this category alone, so
        // every other suite stays as quiet as it was.
        builder.UseSetting("Logging:LogLevel:FiGet.Web.Logging", "Information");
        builder.UseSetting("Logging:LogLevel:FiGet.Audit", "Information");
        builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(Logs));
    }
}

/// <summary>
/// The request log exists to answer one question: did a client reach this server, and what did it ask for.
/// It was written after a colleague reported a package "no longer being cached" and nothing on this side
/// could confirm whether his client had ever arrived - the answer had to be argued from download counters
/// and how long his install took.
/// </summary>
public sealed class RequestLogTests(RequestLogFixture server) : IClassFixture<RequestLogFixture>
{
    [Fact]
    public async Task A_package_request_is_logged_with_its_path_and_status()
    {
        using var client = server.CreateClient();
        var id = FiGetServerFixture.UniqueId("Log.Request");

        // A 404 on purpose: a request that failed is the one somebody is most likely hunting for.
        var response = await client.GetAsync($"nuget/public/FindPackagesById()?id='{id}'");
        var status = (int)response.StatusCode;

        var line = await WaitForLineAsync(id);
        Assert.True(
            line is not null,
            $"request returned {status}; queue held {server.Logs.Lines.Count} line(s): "
                + string.Join(" || ", server.Logs.Lines.Take(10)));
        Assert.Contains("GET", line, StringComparison.Ordinal);
        Assert.Contains("FindPackagesById", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// Who asked, not just from where. An address is not an answer when several people share a proxy, and
    /// every protocol request already resolves a token, so the log can name it.
    /// </summary>
    [Fact]
    public async Task A_request_that_presented_a_token_is_logged_with_the_token_name()
    {
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-NuGet-ApiKey", FiGetServerFixture.AdminToken);

        // A feed that requires a token, so the token is the reason the request is allowed at all.
        await client.GetAsync("nuget/private/FindPackagesById()?id='Log.Token.Probe'");

        var line = await WaitForLineAsync("Log.Token.Probe");
        Assert.NotNull(line);
        Assert.Contains("who=token:", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// A push is recorded, and recorded against whoever pushed it.
    ///
    /// Driven over the API with a token rather than through a page, because that is both the cheap path -
    /// no sign-in, no antiforgery - and the one that matters: a package arriving in a feed is the change
    /// somebody will want attributed later. It also proves the wiring end to end, which a compiler cannot:
    /// the audit log reaches these handlers through minimal-API dependency injection, so a missing
    /// registration would surface as a 500 when the endpoint is hit and not before.
    /// </summary>
    [Fact]
    public async Task A_push_is_recorded_in_the_audit_log()
    {
        var id = FiGetServerFixture.UniqueId("Audit.Push");

        using var client = server.CreateClient();
        using var package = TestPackages.Create(id, "1.0.0");
        using var content = new MultipartFormDataContent();
        using var file = new StreamContent(package);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "package", "package.nupkg");

        using var request = new HttpRequestMessage(HttpMethod.Put, "nuget/public/") { Content = content };
        request.Headers.Add("X-NuGet-ApiKey", FiGetServerFixture.AdminToken);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var line = await WaitForLineAsync("package.push");
        Assert.NotNull(line);
        Assert.Contains(id, line, StringComparison.Ordinal);

        // The token, not just an address: several people share one proxy, and an address is not an answer.
        // "by", not "who=": the audit line has its own shape, and the first version of this assertion
        // borrowed the request log's.
        Assert.True(line.Contains("by token:", StringComparison.Ordinal), "audit line was: " + line);
    }

    /// <summary>Health probes would otherwise be most of the log, and nobody is ever looking for them.</summary>
    [Fact]
    public async Task Health_probes_are_not_logged()
    {
        using var client = server.CreateClient();
        await client.GetAsync("health/live");

        // A request that IS logged, to prove the log was working at all - otherwise this passes on an
        // empty queue and proves nothing, which is exactly how it hid a real failure earlier.
        var marker = FiGetServerFixture.UniqueId("Log.Marker");
        await client.GetAsync($"nuget/public/FindPackagesById()?id='{marker}'");
        Assert.NotNull(await WaitForLineAsync(marker));

        Assert.DoesNotContain(server.Logs.Lines, l => l.Contains("/health/live", StringComparison.Ordinal));
    }

    /// <summary>
    /// Waits for a line to show up, bounded. The log is written after the response is handed back, so a
    /// client can be reading the body while the line is still being written - and under a full assembly
    /// run that gap widens enough to matter. Bounded, because a test that hangs on a broken log tells
    /// nobody anything; the same shape the stale-catalogue tests use for work that happens out of band.
    /// </summary>
    private async Task<string?> WaitForLineAsync(string contains)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var found = server.Logs.Lines.FirstOrDefault(l => l.Contains(contains, StringComparison.Ordinal));
            if (found is not null)
            {
                return found;
            }

            await Task.Delay(50);
        }

        return null;
    }
}
