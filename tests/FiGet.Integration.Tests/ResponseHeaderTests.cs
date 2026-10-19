using System.Net;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Web;
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
    /// <summary>Written out rather than read from the code, so a change to the policy has to be made here too, on purpose.</summary>
    internal const string StrictPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src * data:; connect-src 'self'; "
        + "object-src 'none'; base-uri 'self'; frame-ancestors 'none'";

    [Fact]
    public async Task Pages_cannot_be_framed_and_protocol_answers_are_left_alone()
    {
        using var client = server.CreateClient();
        foreach (var path in new[] { "/", "/account/login/local", "/feeds/public" })
        {
            using var page = await client.GetAsync(path);
            HttpAssert.Status(HttpStatusCode.OK, page);
            Assert.Equal("DENY", page.Headers.GetValues("X-Frame-Options").Single());
            Assert.Equal(StrictPolicy, page.Headers.GetValues("Content-Security-Policy").Single());
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

/// <summary>A server whose theme directory holds the shipped packs plus one that imports its fonts from a font service.</summary>
public sealed class WebFontThemeServerFixture() : FiGetServerFixture(TestDatabase.Sqlite)
{
    protected override void Configure(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var themes = Directory.CreateTempSubdirectory("figet-font-themes").FullName;
        File.WriteAllText(Path.Combine(themes, "webfont.yaml"), """
            name: webfont
            fonts:
              ui: "Inter, sans-serif"
              fontUrl: "https://fonts.example.test/css2?family=Inter"
            """);
        builder.UseSetting("FiGet:Theming:Path", themes);
    }
}

public sealed class WebFontPolicyTests(WebFontThemeServerFixture server) : IClassFixture<WebFontThemeServerFixture>
{
    /// <summary>
    /// Found after 1.5.1 shipped: the policy blocked a pack's font import, and the pack still compiled, the page still
    /// rendered and every test passed. Only the shipped packs were ever exercised, and none of them imports a font.
    /// </summary>
    [Fact]
    public async Task A_pack_that_imports_fonts_lets_that_site_and_font_files_in()
    {
        using var client = server.CreateClient();
        using var page = await client.GetAsync("/");
        HttpAssert.Status(HttpStatusCode.OK, page);

        var policy = page.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("style-src 'self' https://fonts.example.test;", policy, StringComparison.Ordinal);
        Assert.Contains("font-src 'self' https:;", policy, StringComparison.Ordinal);
        Assert.Contains("script-src 'self';", policy, StringComparison.Ordinal);

        // The import really is in the pack's stylesheet: that is what the policy has to let through.
        Assert.Contains("@import url('https://fonts.example.test/css2?family=Inter');", await HttpAssert.SuccessBodyAsync(await client.GetAsync("/themes/webfont.css")), StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_font_import_the_policy_is_the_strict_one() =>
        Assert.Equal(ResponseHeaderTests.StrictPolicy, FiGetApp.PageContentPolicy([]));
}
