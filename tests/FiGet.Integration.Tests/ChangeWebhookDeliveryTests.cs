using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FiGet.Application.Ports;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Testing;
using FiGet.Web.Connectors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FiGet.Integration.Tests;

/// <summary>
/// A server whose reports go to a receiver this test owns, on loopback - which is why private addresses are allowed
/// here and off everywhere else.
/// </summary>
public sealed class WebhookServerFixture() : FiGetServerFixture(TestDatabase.Sqlite)
{
    /// <summary>Set by the test class before the server starts, because the address is only known then.</summary>
    public static string ReceiverUrl { get; set; } = "";

    protected override void Configure(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSetting("FiGet:Changes:Webhook:Url", ReceiverUrl);
        builder.UseSetting("FiGet:Changes:Webhook:AllowPrivateNetworks", "true");
        builder.UseSetting("FiGet:Changes:Webhook:HeaderName", "Authorization");
        builder.UseSetting("FiGet:Changes:Webhook:HeaderValue", "Bearer test-relay-token");
    }
}

/// <summary>What is actually posted, and what a failure leaves behind.</summary>
public sealed class ChangeWebhookDeliveryTests : IAsyncLifetime
{
    private WebApplication receiver = null!;
    private WebhookServerFixture server = null!;
    private readonly ConcurrentQueue<(string Body, string? Authorization)> posted = new();
    private int status = StatusCodes.Status200OK;
    private string? redirectTo;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(k => k.Listen(IPAddress.Loopback, 0));
        receiver = builder.Build();
        receiver.Run(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            posted.Enqueue((await reader.ReadToEndAsync(), context.Request.Headers.Authorization.ToString()));
            if (redirectTo is not null)
            {
                context.Response.Redirect(redirectTo);
                return;
            }

            context.Response.StatusCode = status;
        });

        await receiver.StartAsync();
        var address = receiver.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

        WebhookServerFixture.ReceiverUrl = address.TrimEnd('/') + "/hooks/abcdef123456?sig=TOPSECRET";
        server = new WebhookServerFixture();
        await server.InitializeAsync();
        server.SkipIfUnavailable();
    }

    public async ValueTask DisposeAsync()
    {
        await server.DisposeAsync();
        await receiver.StopAsync();
        await receiver.DisposeAsync();
    }

    /// <summary>The report arrives as data, with the header a relay authenticates by.</summary>
    [Fact]
    public async Task A_report_is_posted_with_the_configured_header()
    {
        var id = FiGetServerFixture.UniqueId("Hook.Sent");
        await PushAsync("public", id, "1.0.0");

        Assert.Equal(ChangeReportOutcome.Sent, await SendAsync("public"));

        Assert.True(posted.TryDequeue(out var sent), "Nothing was posted.");
        Assert.Equal("Bearer test-relay-token", sent.Authorization);
        var body = JsonNode.Parse(sent.Body)!;
        Assert.Equal("public", (string?)body["report"]!["feed"]);
        Assert.Contains(body["report"]!["changes"]!.AsArray(), c => (string?)c!["name"] == id);
    }

    /// <summary>
    /// A failure leaves the mark where it was, so the next run still covers what this one could not deliver. Runs the
    /// success case first on purpose: "unchanged" proves nothing unless the same value is known to move.
    /// </summary>
    [Fact]
    public async Task A_failed_post_does_not_advance_what_was_reported()
    {
        var id = FiGetServerFixture.UniqueId("Hook.Failed");
        await PushAsync("overwrite", id, "1.0.0");

        Assert.Equal(ChangeReportOutcome.Sent, await SendAsync("overwrite"));
        var afterSuccess = await ReportedThroughAsync("overwrite");
        Assert.NotNull(afterSuccess);

        await PushAsync("overwrite", id, "2.0.0");
        status = StatusCodes.Status500InternalServerError;
        try
        {
            Assert.Equal(ChangeReportOutcome.Failed, await SendAsync("overwrite"));
        }
        finally
        {
            status = StatusCodes.Status200OK;
        }

        Assert.Equal(afterSuccess, await ReportedThroughAsync("overwrite"));

        // And the next run really does carry what the failed one held: the same version is in the body.
        posted.Clear();
        Assert.Equal(ChangeReportOutcome.Sent, await SendAsync("overwrite"));
        Assert.True(posted.TryDequeue(out var sent));
        Assert.Contains("2.0.0", sent.Body, StringComparison.Ordinal);
    }

    /// <summary>Nothing changed, nothing posted: silence is what makes a daily report bearable.</summary>
    [Fact]
    public async Task A_second_run_over_the_same_data_posts_nothing()
    {
        var id = FiGetServerFixture.UniqueId("Hook.Quiet");
        await PushAsync("private", id, "1.0.0");

        Assert.Equal(ChangeReportOutcome.Sent, await SendAsync("private"));
        posted.Clear();

        Assert.Equal(ChangeReportOutcome.NothingToSay, await SendAsync("private"));
        Assert.Empty(posted);
    }

    /// <summary>A redirect would re-send the whole body to an address nobody vetted.</summary>
    [Fact]
    public async Task A_redirect_is_not_followed()
    {
        var id = FiGetServerFixture.UniqueId("Hook.Redirect");
        await PushAsync("public", id, "3.0.0");
        redirectTo = "/elsewhere";
        try
        {
            Assert.Equal(ChangeReportOutcome.Failed, await SendAsync("public"));
        }
        finally
        {
            redirectTo = null;
        }

        // One request: the body was posted once and the redirect was not chased.
        Assert.Single(posted);
    }

    /// <summary>
    /// The address an administrator saves is stored encrypted and shown back as a host, never as itself. The stored
    /// value is read straight out of the settings table here, because "it is encrypted" is exactly the kind of claim
    /// that quietly stops being true.
    /// </summary>
    [Fact]
    public async Task A_saved_address_is_stored_encrypted_and_shown_as_a_host()
    {
        const string Secret = "https://receiver.example.test/hooks/SECRET-PATH-VALUE?sig=SECRET-QUERY-VALUE";

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<ChangeWebhookFactory>();
            await factory.SetUrlAsync(ChangeWebhookFactory.ServerKey, Secret, "tester", TestContext.Current.CancellationToken);
        }

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<ISettingStore>();
            var stored = await settings.GetAsync(ChangeWebhookFactory.ServerKey, TestContext.Current.CancellationToken);

            Assert.NotNull(stored);
            Assert.DoesNotContain("SECRET-PATH-VALUE", stored, StringComparison.Ordinal);
            Assert.DoesNotContain("SECRET-QUERY-VALUE", stored, StringComparison.Ordinal);
            Assert.DoesNotContain("receiver.example.test", stored, StringComparison.Ordinal);

            // And it is still usable: encrypted, not mangled.
            var factory = scope.ServiceProvider.GetRequiredService<ChangeWebhookFactory>();
            var webhook = await factory.ServerAsync(TestContext.Current.CancellationToken);
            Assert.Equal("https://receiver.example.test", webhook!.Host);
            (webhook as IDisposable)?.Dispose();
        }

        // Put the test's own receiver back, so the ordering of these tests cannot matter.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<ChangeWebhookFactory>();
            await factory.SetUrlAsync(ChangeWebhookFactory.ServerKey, "", "tester", TestContext.Current.CancellationToken);
        }
    }

    /// <summary>A feed's own address wins over the server's: one webhook per channel is exactly this.</summary>
    [Fact]
    public async Task A_feeds_own_address_is_used_instead_of_the_servers()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var feeds = scope.ServiceProvider.GetRequiredService<IFeedStore>();
        var factory = scope.ServiceProvider.GetRequiredService<ChangeWebhookFactory>();
        var feed = await feeds.FindAsync("public", TestContext.Current.CancellationToken);

        await factory.SetUrlAsync(ChangeWebhookFactory.FeedKey(feed!.Key), "https://feed.example.test/hook", "tester", TestContext.Current.CancellationToken);
        try
        {
            var webhook = await factory.ForAsync(feed, TestContext.Current.CancellationToken);
            Assert.Equal("https://feed.example.test", webhook!.Host);
            (webhook as IDisposable)?.Dispose();
        }
        finally
        {
            await factory.SetUrlAsync(ChangeWebhookFactory.FeedKey(feed.Key), "", "tester", TestContext.Current.CancellationToken);
        }
    }

    private async Task<ChangeReportOutcome> SendAsync(string feed)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var feeds = scope.ServiceProvider.GetRequiredService<IFeedStore>();
        var job = server.Services.GetRequiredService<ChangeReportJobService>();
        var found = await feeds.FindAsync(feed, TestContext.Current.CancellationToken);
        return await job.SendAsync(found!, TestContext.Current.CancellationToken);
    }

    private async Task<string?> ReportedThroughAsync(string feed)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var feeds = scope.ServiceProvider.GetRequiredService<IFeedStore>();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingStore>();
        var found = await feeds.FindAsync(feed, TestContext.Current.CancellationToken);
        return await settings.GetAsync(ChangeWebhookFactory.ReportedThroughKey(found!.Key), TestContext.Current.CancellationToken);
    }

    private async Task PushAsync(string feed, string id, string version)
    {
        using var client = server.CreateClient(FiGetServerFixture.AdminToken);
        using var package = TestPackages.Create(id, version);
        using var content = new MultipartFormDataContent();
        var file = new StreamContent(package);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "package", "package.nupkg");
        HttpAssert.Status(HttpStatusCode.Created, await client.PutAsync($"nuget/{feed}/v3/publish", content));
    }
}
