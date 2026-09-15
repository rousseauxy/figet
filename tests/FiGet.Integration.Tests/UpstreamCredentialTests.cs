using System.Collections.Concurrent;
using System.Net;
using System.Text;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FiGet.Integration.Tests;

/// <summary>
/// What the real upstream client sends as a credential, against a stub gallery that asks for one. Found by the 2026-09-14
/// review: the reference named any environment variable, so whoever could edit an upstream could have the process send
/// its connection string or bootstrap token to a URL of their choosing.
/// </summary>
public sealed class UpstreamCredentialTests(SqliteServerFixture server) : IClassFixture<SqliteServerFixture>
{
    /// <summary>
    /// A row naming a variable outside the prefix - written before the rule, or straight into the database - sends nothing;
    /// the same stub does receive a variable under the prefix, so the test cannot pass because no credential is ever sent.
    /// </summary>
    [Fact]
    public async Task Only_a_variable_under_the_upstream_prefix_is_ever_sent_to_an_upstream()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var outside = "REVIEW_PROBE_SECRET_" + suffix;
        var allowed = FeedUpstream.CredentialPrefix + "PROBE_" + suffix;
        var outsideValue = "outside-" + Guid.NewGuid().ToString("N");
        var allowedValue = "allowed-" + Guid.NewGuid().ToString("N");
        var withUser = FeedUpstream.CredentialPrefix + "PROBE_USER_" + suffix;
        var withUserValue = "alice:pass:" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(outside, outsideValue);
        Environment.SetEnvironmentVariable(allowed, allowedValue);
        Environment.SetEnvironmentVariable(withUser, withUserValue);

        var passwords = new ConcurrentQueue<string>();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        await using var stub = builder.Build();
        stub.Run(context =>
        {
            var header = context.Request.Headers.Authorization.ToString();
            if (header.StartsWith("Basic ", StringComparison.Ordinal))
            {
                passwords.Enqueue(Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..])));
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"stub\"";
            return Task.CompletedTask;
        });
        await stub.StartAsync();
        var address = stub.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');

        try
        {
            using var client = server.CreateClient();
            foreach (var (reference, path) in new[] { (outside, "outside"), (allowed, "allowed"), (withUser, "with-user") })
            {
                var feed = await CreateFeedAsync($"{address}/{path}/v3/index.json", reference);
                using var response = await client.GetAsync($"/nuget/{feed}/v3/flatcontainer/probe.pkg/index.json");
            }

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline && !(passwords.Any(p => p.EndsWith(allowedValue, StringComparison.Ordinal)) && passwords.Any(p => p.EndsWith(withUserValue[6..], StringComparison.Ordinal))))
            {
                await Task.Delay(200);
            }

            Assert.Contains(passwords, p => p == "figet:" + allowedValue);

            // A secret written as user:password sends that user, for an upstream that checks both; the password keeps its
            // own colons. Found cross-checking other package servers' issue trackers (2026-09-15).
            Assert.Contains(passwords, p => p == withUserValue);
            Assert.DoesNotContain(passwords, p => p.Contains(outsideValue, StringComparison.Ordinal));
        }
        finally
        {
            await stub.StopAsync();
            Environment.SetEnvironmentVariable(outside, null);
            Environment.SetEnvironmentVariable(allowed, null);
            Environment.SetEnvironmentVariable(withUser, null);
        }
    }

    private async Task<string> CreateFeedAsync(string url, string credentialRef)
    {
        var name = "cred" + Guid.NewGuid().ToString("N")[..8];
        await using var scope = server.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IFeedStore>().CreateAsync(
            new Feed
            {
                Name = name,
                NameLower = name,
                Kind = FeedKind.Proxy,
                AnonymousRead = true,
                CreatedUtc = DateTime.UtcNow,
                Upstreams = [new FeedUpstream { Name = "stub", Url = url, Kind = UpstreamKind.V3, CredentialRef = credentialRef }],
            },
            CancellationToken.None));
        return name;
    }
}
