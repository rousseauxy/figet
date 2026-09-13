using System.Net;
using System.Text.RegularExpressions;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

/// <summary>A server that knows its public address, as one behind a reverse proxy does.</summary>
public sealed class PublicBaseUrlServerFixture() : FiGetServerFixture(TestDatabase.Sqlite)
{
    public const string PublicBaseUrl = "https://figet.example.test";

    protected override void Configure(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSetting("FiGet:PublicBaseUrl", PublicBaseUrl);
    }
}

/// <summary>
/// Behind a proxy, the redirect URI a provider sees is built from the public base URL - the one the providers page tells
/// people to register - not from the request's own host. Both legs must send the same value: the authorize request, and
/// the code redemption, which the provider refuses when they differ.
/// </summary>
public sealed partial class ExternalSignInBehindProxyTests(PublicBaseUrlServerFixture server) : IClassFixture<PublicBaseUrlServerFixture>
{
    [Fact]
    public async Task The_redirect_uri_is_the_public_one_on_both_legs()
    {
        await using var idp = await FakeOidcProvider.StartAsync();
        var slug = "p" + Guid.NewGuid().ToString("N")[..10];
        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IOidcProviderStore>().AddAsync(
                new OidcProvider
                {
                    Slug = slug,
                    DisplayName = "Proxy test",
                    Authority = idp.Authority,
                    ClientId = FakeOidcProvider.ClientId,
                    ProtectedClientSecret = scope.ServiceProvider.GetRequiredService<ISecretProtector>().Protect(FakeOidcProvider.ClientSecret),
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow,
                },
                CancellationToken.None);
        }

        idp.Next = new FakeIdentity(Guid.NewGuid().ToString("N"), "proxy" + slug);
        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = server.BaseAddress };
        var page = await HttpAssert.SuccessBodyAsync(await browser.GetAsync("/account/login"));
        var token = WebUtility.HtmlDecode(Antiforgery().Match(page).Groups["value"].Value);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token });
        var challenge = await browser.PostAsync($"/account/external/{slug}", content);

        var authorize = challenge.Headers.Location!;
        var expected = $"{PublicBaseUrlServerFixture.PublicBaseUrl}/signin-oidc/{slug}";
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString(expected), authorize.Query, StringComparison.Ordinal);

        // The provider sends the browser to the public address; here the proxy's part is played by rewriting the host.
        var back = (await browser.GetAsync(authorize)).Headers.Location!;
        Assert.StartsWith(expected, back.ToString(), StringComparison.Ordinal);
        var callback = await browser.GetAsync(new Uri(server.BaseAddress, back.PathAndQuery));
        Assert.Equal("/account/external/complete", callback.Headers.Location!.OriginalString);
        Assert.Equal(1, idp.TokenRequests);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<value>[^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex Antiforgery();
}
