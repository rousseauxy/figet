using System.Net;
using System.Net.Http.Headers;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

/// <summary>A server with a small anonymous bucket, so the limit is reached in a handful of requests.</summary>
public sealed class ApiTokenRateLimitServerFixture() : FiGetServerFixture(TestDatabase.Sqlite)
{
    protected override void Configure(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSetting("FiGet:RateLimits:AnonymousRequestsPerMinute", "1");
        builder.UseSetting("FiGet:RateLimits:AnonymousBurst", "5");
    }
}

/// <summary>
/// An access token from a trusted issuer counts as a credential for the rate limiter the way a key does: a valid, linked
/// token is not limited, a token that fails a check is anonymous and does not get around the limit, and a valid token
/// linked to nothing is anonymous too. One address for all of it, so one method, in order.
/// </summary>
public sealed class ApiTokenRateLimitTests(ApiTokenRateLimitServerFixture server) : IClassFixture<ApiTokenRateLimitServerFixture>, IAsyncLifetime
{
    private FakeOidcProvider idp = null!;

    public async ValueTask InitializeAsync() => idp = await FakeOidcProvider.StartAsync();

    public async ValueTask DisposeAsync()
    {
        await idp.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_valid_linked_token_is_not_limited_and_a_refused_one_is_anonymous()
    {
        var slug = "t" + Guid.NewGuid().ToString("N")[..10];
        var audience = "api://figet-tests/" + slug;
        var feed = "feed" + Guid.NewGuid().ToString("N")[..8];
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var providers = scope.ServiceProvider.GetRequiredService<IOidcProviderStore>();
            Assert.True(await providers.AddAsync(
                new OidcProvider
                {
                    Slug = slug,
                    DisplayName = "Issuer " + slug,
                    Authority = idp.Authority,
                    ClientId = "",
                    Enabled = false,
                    GroupsClaim = "roles",
                    AcceptApiTokens = true,
                    ApiAudiences = audience,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow,
                },
                CancellationToken.None));
            var provider = (await providers.FindBySlugAsync(slug, CancellationToken.None))!;

            var feeds = scope.ServiceProvider.GetRequiredService<IFeedStore>();
            Assert.True(await feeds.CreateAsync(new Feed { Name = feed, NameLower = feed, Kind = FeedKind.Curated, CreatedUtc = DateTime.UtcNow }, CancellationToken.None));
            var feedKey = (await feeds.FindAsync(feed, CancellationToken.None))!.Key;

            var groups = scope.ServiceProvider.GetRequiredService<IGroupStore>();
            var name = "g" + Guid.NewGuid().ToString("N")[..10];
            Assert.True(await groups.AddAsync(new FiGet.Domain.Entities.Group { Name = name, NameLower = name, CreatedUtc = DateTime.UtcNow }, CancellationToken.None));
            var groupKey = (await groups.ListAsync(CancellationToken.None)).Single(g => g.Name == name).Key;
            Assert.True(await groups.AddProviderLinkAsync(new GroupProviderLink { GroupKey = groupKey, ProviderKey = provider.Key, ProviderGroup = "FiGet.Readers" }, CancellationToken.None));
            await scope.ServiceProvider.GetRequiredService<IFeedPermissionStore>().SetAsync(feedKey, null, groupKey, FeedAccessLevel.Read, CancellationToken.None);
        }

        var claims = new Dictionary<string, object> { ["sub"] = "rate-app", ["azp"] = "rate-app", ["roles"] = new[] { "FiGet.Readers" } };
        var good = idp.CreateAccessToken(audience, claims);
        for (var i = 0; i < 20; i++)
        {
            HttpAssert.Status(HttpStatusCode.OK, await ReadAsync(feed, good));
        }

        // The bucket holds five. A token for someone else's audience is no credential, and spends them.
        var wrong = idp.CreateAccessToken("api://someone-else", claims);
        var answers = new List<HttpStatusCode>();
        for (var i = 0; i < 7; i++)
        {
            answers.Add((await ReadAsync(feed, wrong)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, answers[0]);
        Assert.Contains(HttpStatusCode.TooManyRequests, answers);
        Assert.Equal(HttpStatusCode.TooManyRequests, answers[^1]);

        // A valid token linked to nothing is anonymous as well, and the bucket is already empty.
        var unlinked = idp.CreateAccessToken(audience, new Dictionary<string, object> { ["sub"] = "nobody", ["roles"] = new[] { "Someone.Else" } });
        HttpAssert.Status(HttpStatusCode.TooManyRequests, await ReadAsync(feed, unlinked));

        // The good token still gets through: it never touched the bucket.
        HttpAssert.Status(HttpStatusCode.OK, await ReadAsync(feed, good));
    }

    private async Task<HttpResponseMessage> ReadAsync(string feed, string token)
    {
        using var client = new HttpClient { BaseAddress = server.BaseAddress };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.GetAsync($"nuget/{feed}/v3/query");
    }
}
