using System.Net;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

/// <summary>
/// The address an audit entry records. Found by the 2026-09-14 review: it was the raw <c>X-Forwarded-For</c> header, so
/// every "from" in the audit log was whatever the client wrote there, even behind the documented proxy set-up. Each case
/// has a server of its own, because refused keys are recorded once per address a window and a shared server would
/// swallow the second entry.
/// </summary>
public sealed class AuditCallerTests(AuditCallerServerFixture server) : IClassFixture<AuditCallerServerFixture>
{
    [Fact]
    public async Task Without_a_trusted_proxy_the_caller_is_the_connection_address_whatever_the_header_says()
    {
        using var client = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/nuget/public/v3/index.json");
        request.Headers.Add("X-NuGet-ApiKey", "figet_not_a_key_" + Guid.NewGuid().ToString("N")[..8]);
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");
        HttpAssert.Status(HttpStatusCode.OK, await client.SendAsync(request));

        var entry = await AuditWait.ForAsync(server, "token.refused");
        Assert.True(IPAddress.IsLoopback(IPAddress.Parse(entry.Caller)), $"The caller was '{entry.Caller}'.");
    }
}

public sealed class AuditCallerServerFixture() : FiGetServerFixture(TestDatabase.Sqlite);

/// <summary>A server with the forwarded-headers switch on, as docs/configuration.md tells operators to run it behind a proxy.</summary>
public sealed class ForwardedHeadersServerFixture() : FiGetServerFixture(TestDatabase.Sqlite)
{
    protected override void Configure(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSetting("ForwardedHeaders_Enabled", "true");
    }
}

public sealed class AuditCallerBehindProxyTests(ForwardedHeadersServerFixture server) : IClassFixture<ForwardedHeadersServerFixture>
{
    /// <summary>A proxy that appends leaves the client's own text first and the address it saw last; the last one is recorded.</summary>
    [Fact]
    public async Task Behind_a_proxy_that_appends_the_caller_is_the_address_the_proxy_saw()
    {
        using var client = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/nuget/public/v3/index.json");
        request.Headers.Add("X-NuGet-ApiKey", "figet_not_a_key_" + Guid.NewGuid().ToString("N")[..8]);
        request.Headers.Add("X-Forwarded-For", "198.51.100.9, 10.0.0.5");
        HttpAssert.Status(HttpStatusCode.OK, await client.SendAsync(request));

        var entry = await AuditWait.ForAsync(server, "token.refused");
        Assert.Equal("10.0.0.5", entry.Caller);
    }
}

internal static class AuditWait
{
    public static async Task<AuditEntry> ForAsync(FiGetServerFixture server, string action)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            await using (var scope = server.Services.CreateAsyncScope())
            {
                var entries = await scope.ServiceProvider.GetRequiredService<IAuditStore>()
                    .QueryAsync(new AuditQuery(action, null, null, null, null, null, 50), CancellationToken.None);
                if (entries.Count > 0)
                {
                    return entries[0];
                }
            }

            Assert.True(DateTime.UtcNow < deadline, $"No audit entry '{action}' within ten seconds.");
            await Task.Delay(200);
        }
    }
}
