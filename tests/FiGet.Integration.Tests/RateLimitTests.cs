using System.Net;
using FiGet.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace FiGet.Integration.Tests;

/// <summary>A server with small limits, so a test can reach them in a handful of requests.</summary>
public sealed class RateLimitServerFixture() : FiGetServerFixture(TestDatabase.Sqlite)
{
    protected override void Configure(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSetting("FiGet:RateLimits:AnonymousRequestsPerMinute", "60");
        builder.UseSetting("FiGet:RateLimits:AnonymousBurst", "5");
        builder.UseSetting("FiGet:RateLimits:SignInAttemptsPerMinute", "3");
    }
}

/// <summary>
/// Per-address limits without credentials: anonymous protocol reads and pages are limited, a valid key is not, a garbage
/// key does not get around the limit, and sign-in attempts have their own, smaller bucket. The tests share one address,
/// so they run in one method, in order.
/// </summary>
public sealed class RateLimitTests(RateLimitServerFixture server) : IClassFixture<RateLimitServerFixture>
{
    [Fact]
    public async Task Anonymous_requests_and_sign_ins_are_limited_per_address_and_keys_are_not()
    {
        using var client = server.CreateClient();

        // Five at once pass, the sixth is refused with a time to come back.
        for (var i = 0; i < 5; i++)
        {
            HttpAssert.Status(HttpStatusCode.OK, await client.GetAsync("nuget/public/v3/index.json"));
        }

        var refused = await client.GetAsync("nuget/public/v3/index.json");
        HttpAssert.Status(HttpStatusCode.TooManyRequests, refused);
        Assert.True(refused.Headers.RetryAfter is not null, "No Retry-After on the refusal.");

        // A garbage key is still anonymous; a real one is not limited.
        using (var garbage = new HttpRequestMessage(HttpMethod.Get, "nuget/public/v3/index.json"))
        {
            garbage.Headers.Add("X-NuGet-ApiKey", "figet_not_a_real_key");
            HttpAssert.Status(HttpStatusCode.TooManyRequests, await client.SendAsync(garbage));
        }

        for (var i = 0; i < 20; i++)
        {
            using var keyed = new HttpRequestMessage(HttpMethod.Get, "nuget/public/v3/index.json");
            keyed.Headers.Add("X-NuGet-ApiKey", FiGetServerFixture.AdminToken);
            HttpAssert.Status(HttpStatusCode.OK, await client.SendAsync(keyed));
        }

        // Pages count too, and a refused page still gets the error page rather than an empty answer.
        var page = await client.GetAsync("/feeds/public");
        HttpAssert.Status(HttpStatusCode.TooManyRequests, page);
        Assert.Contains("429", await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);

        // Sign-in attempts have their own bucket of three.
        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = server.BaseAddress };
        for (var i = 0; i < 3; i++)
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["x"] = "y" });
            var attempt = await browser.PostAsync("/account/login/local", content);
            Assert.NotEqual("/account/login/local?external=limited", attempt.Headers.Location?.OriginalString);
        }

        using (var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["x"] = "y" }))
        {
            var limited = await browser.PostAsync("/account/login/local", content);
            HttpAssert.Status(HttpStatusCode.Redirect, limited);
            Assert.Equal("/account/login/local?external=limited", limited.Headers.Location!.OriginalString);
        }
    }
}
