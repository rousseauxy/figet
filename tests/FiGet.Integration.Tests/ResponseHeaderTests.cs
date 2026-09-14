using System.Net;
using FiGet.Integration.Tests.Infrastructure;

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
    }
}
