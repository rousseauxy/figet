using System.Net;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Web.SignIn;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FiGet.Integration.Tests;

/// <summary>Headers on pages, found missing by the 2026-09-14 review: any site could frame the admin pages.</summary>
public sealed class ResponseHeaderTests(SqliteServerFixture server) : IClassFixture<SqliteServerFixture>
{
    [Fact]
    public async Task Pages_cannot_be_framed_and_protocol_answers_are_left_alone()
    {
        using var client = server.CreateClient();
        foreach (var path in new[] { "/", "/account/login/local", "/feeds/public" })
        {
            using var page = await client.GetAsync(path);
            HttpAssert.Status(HttpStatusCode.OK, page);
            Assert.Equal("DENY", page.Headers.GetValues("X-Frame-Options").Single());
            Assert.Equal("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal("nosniff", page.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("strict-origin-when-cross-origin", page.Headers.GetValues("Referrer-Policy").Single());
        }

        using var index = await client.GetAsync("/nuget/public/v3/index.json");
        HttpAssert.Status(HttpStatusCode.OK, index);
        Assert.False(index.Headers.Contains("X-Frame-Options"));
        Assert.False(index.Headers.Contains("Content-Security-Policy"));

        // Not over plain HTTP without an HTTPS public address: the cookie would never come back.
        Assert.Equal(CookieSecurePolicy.SameAsRequest, server.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme).Cookie.SecurePolicy);
    }
}

public sealed class HttpsPublicAddressServerFixture() : FiGetServerFixture(TestDatabase.Sqlite)
{
    protected override void Configure(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSetting("FiGet:PublicBaseUrl", "https://packages.example.test");
    }
}

public sealed class SecureCookieTests(HttpsPublicAddressServerFixture server) : IClassFixture<HttpsPublicAddressServerFixture>
{
    /// <summary>
    /// With an HTTPS public address the sign-in cookies are Secure, even when the proxy in front talks plain HTTP to the
    /// container; and the sign-in page still renders there, which it would not with the antiforgery cookie forced as well.
    /// </summary>
    [Fact]
    public async Task Sign_in_cookies_are_secure_when_the_public_address_is_https()
    {
        using var client = server.CreateClient();
        HttpAssert.Status(HttpStatusCode.OK, await client.GetAsync("/account/login/local"));

        var cookies = server.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();
        Assert.Equal(CookieSecurePolicy.Always, cookies.Get(CookieAuthenticationDefaults.AuthenticationScheme).Cookie.SecurePolicy);
        Assert.Equal(CookieSecurePolicy.Always, cookies.Get(OidcSchemes.ExternalCookie).Cookie.SecurePolicy);
    }
}
