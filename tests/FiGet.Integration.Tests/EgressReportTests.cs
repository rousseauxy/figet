using System.Net;
using FiGet.Application.Ports;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Web.Connectors;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

/// <summary>A server with a gallery whose downloads come from somewhere else, and a webhook whose path is a secret.</summary>
public sealed class EgressServerFixture() : FiGetServerFixture(TestDatabase.Sqlite)
{
    protected override void Configure(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSetting("FiGet:Feeds:7:Name", "gallery");
        builder.UseSetting("FiGet:Feeds:7:Upstreams:0:Name", "PowerShell Gallery");
        builder.UseSetting("FiGet:Feeds:7:Upstreams:0:Url", "https://www.powershellgallery.com/api/v2");
        builder.UseSetting("FiGet:Feeds:7:Upstreams:0:Kind", "V2");
        builder.UseSetting("FiGet:Changes:Webhook:Url", "https://hooks.example.test/services/SECRET-PATH?token=SECRET-QUERY");
    }
}

/// <summary>
/// The list an operator hands to whoever opens firewalls. It is computed from this instance's own configuration, so the
/// test asserts on what was configured - and on the two things that make it worth having: the download host a gallery
/// redirects to, which a reader would not know to ask for, and that a webhook's path never appears.
/// </summary>
public sealed class EgressReportTests(EgressServerFixture server) : IClassFixture<EgressServerFixture>
{
    [Fact]
    public async Task A_gallerys_api_and_its_download_host_are_both_listed()
    {
        var hosts = await ReadAsync();

        var api = Assert.Single(hosts, h => h.Host == "https://www.powershellgallery.com");
        Assert.Equal("443", api.Port);
        Assert.Contains("gallery", api.Source, StringComparison.Ordinal);

        // The one a reader would not think to ask for: listings and downloads are not the same host.
        var downloads = Assert.Single(hosts, h => h.Host == "https://cdn.powershellgallery.com");
        Assert.Contains("redirects", downloads.Why, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A webhook address carries its token in its path, and this page exists to be copied into a ticket. The host is
    /// what a firewall needs; everything after it is a password.
    /// </summary>
    [Fact]
    public async Task A_webhook_is_listed_by_host_and_never_by_path()
    {
        var hosts = await ReadAsync();

        var webhook = Assert.Single(hosts, h => h.Host == "https://hooks.example.test");
        Assert.DoesNotContain("SECRET", webhook.Host, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET", webhook.Why, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET", webhook.Source, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One row per destination, carrying every reason: a rule is removed when the last cause for it is gone.</summary>
    [Fact]
    public async Task One_row_per_host_and_port()
    {
        var hosts = await ReadAsync();

        Assert.Equal(hosts.Count, hosts.Select(h => (h.Host, h.Port)).Distinct().Count());
        Assert.All(hosts, h => Assert.False(string.IsNullOrWhiteSpace(h.Source), $"{h.Host} says nothing about what asked for it."));
    }

    /// <summary>And it reaches the page an administrator opens, with nothing secret on it.</summary>
    [Fact]
    public async Task The_page_shows_it_to_an_administrator()
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() })
        {
            BaseAddress = server.BaseAddress,
        };
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(client));

        var page = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/admin/system"));

        Assert.Contains("What this server reaches", page, StringComparison.Ordinal);
        Assert.Contains("cdn.powershellgallery.com", page, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-PATH", page, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-QUERY", page, StringComparison.Ordinal);
    }

    private async Task<IReadOnlyList<EgressHost>> ReadAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<EgressReportService>().ReadAsync(TestContext.Current.CancellationToken);
    }
}
