using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Web.SignIn;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace FiGet.Integration.Tests;

/// <summary>
/// The edges of the API access token checks, one probe per rule in docs/auth-plan.md "API access tokens": algorithms and key
/// ids, the issuer comparison, several audiences, clock skew and the lifetime cap, the nonce rule, how required claims and
/// the groups claim are matched, the ceiling at Publish, the key-fetch limit, an issuer outage, concurrent first requests,
/// the two switches on a provider, and deleting a provider that is in use.
/// </summary>
public abstract partial class ExternalSignInTests
{
    /// <summary>
    /// RS256 and PS256 with the published RSA key pass, with or without a key id in the header; a token whose header says
    /// <c>none</c> but still has a signature part, and an ES256 token under a key the issuer never published, do not.
    /// </summary>
    [Fact]
    public async Task Only_asymmetric_algorithms_pass_with_or_without_a_key_id()
    {
        var provider = await AddTokenIssuerAsync();
        var feed = await CreateFeedAsync();
        await LinkAsync(provider, "FiGet.Readers", feed, FeedAccessLevel.Read);
        var claims = Claims("alg-app", roles: ["FiGet.Readers"]);

        HttpAssert.Status(HttpStatusCode.OK, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(provider), claims, algorithm: SecurityAlgorithms.RsaSsaPssSha256))));
        HttpAssert.Status(HttpStatusCode.OK, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(provider), claims, omitKeyId: true))));

        var signed = idp.CreateAccessToken(Audience(provider), claims);
        var none = Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes("{\"alg\":\"none\",\"typ\":\"JWT\"}")) + "." + signed.Split('.')[1] + ".c2ln";
        HttpAssert.Status(HttpStatusCode.Unauthorized, await ReadIndexAsync(feed, Bearer(none)));

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ec = idp.CreateAccessToken(Audience(provider), claims, key: new ECDsaSecurityKey(ecdsa) { KeyId = "ec-key" }, algorithm: SecurityAlgorithms.EcdsaSha256);
        HttpAssert.Status(HttpStatusCode.Unauthorized, await ReadIndexAsync(feed, Bearer(ec)));
    }

    /// <summary>
    /// The issuer is compared exactly. A provider saved with a trailing slash on its authority still works, because the
    /// slash is trimmed before providers are matched; a token whose <c>iss</c> carries the slash, differs in case, or is a
    /// look-alike is refused; and a provider whose authority is a different name for the same issuer is refused too, since
    /// the discovery document's <c>issuer</c> must equal the token's.
    /// </summary>
    [Fact]
    public async Task The_issuer_must_match_exactly_and_agree_with_the_discovery_document()
    {
        var feed = await CreateFeedAsync();
        var claims = Claims("iss-app", roles: ["FiGet.Publishers"]);

        var slashed = await AddTokenIssuerAtAsync(idp.Authority + "/");
        await LinkAsync(slashed, "FiGet.Publishers", feed, FeedAccessLevel.Publish);
        HttpAssert.Status(HttpStatusCode.OK, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(slashed), claims))));

        HttpAssert.Status(HttpStatusCode.Forbidden, await PushWithApiKeyAsync(feed, idp.CreateAccessToken(Audience(slashed), claims, issuer: idp.Authority + "/")));
        var wrongIssuer = await WaitForAuditAsync("token.refused", e => e.Subject == $"access token from {slashed.Slug}" && e.Detail.Contains("reason=wrong issuer", StringComparison.Ordinal));
        Assert.Contains($"feed={feed}", wrongIssuer.Detail, StringComparison.Ordinal);

        HttpAssert.Status(HttpStatusCode.Unauthorized, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(slashed), claims, issuer: idp.Authority.ToUpperInvariant()))));
        HttpAssert.Status(HttpStatusCode.Unauthorized, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(slashed), claims, issuer: idp.Authority + ".evil"))));
        HttpAssert.Status(HttpStatusCode.Unauthorized, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(slashed), claims, issuer: idp.Authority.Replace("http://", "https://", StringComparison.Ordinal)))));

        var alias = idp.Authority.Replace("127.0.0.1", "localhost", StringComparison.Ordinal);
        var aliased = await AddTokenIssuerAtAsync(alias);
        await LinkAsync(aliased, "FiGet.Publishers", feed, FeedAccessLevel.Publish);
        HttpAssert.Status(HttpStatusCode.Unauthorized, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(aliased), claims, issuer: alias))));
        HttpAssert.Status(HttpStatusCode.Unauthorized, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(aliased), claims))));
    }

    /// <summary>A token for several audiences passes when one of them is the provider's; a provider may list several too.</summary>
    [Fact]
    public async Task One_of_several_audiences_is_enough_on_either_side()
    {
        var provider = await AddTokenIssuerAsync();
        var feed = await CreateFeedAsync();
        await LinkAsync(provider, "FiGet.Readers", feed, FeedAccessLevel.Read);
        var claims = Claims("aud-app", roles: ["FiGet.Readers"]);

        HttpAssert.Status(HttpStatusCode.OK, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken("", claims, audiences: ["api://someone-else", Audience(provider), "api://a-third"]))));
        HttpAssert.Status(HttpStatusCode.Unauthorized, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken("", claims, audiences: ["api://someone-else", "api://a-third"]))));

        await ChangeProviderAsync(provider.Slug, p => p.ApiAudiences = Audience(provider) + "\napi://second-" + provider.Slug);
        HttpAssert.Status(HttpStatusCode.OK, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken("api://second-" + provider.Slug, claims))));
        HttpAssert.Status(HttpStatusCode.OK, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(provider), claims))));
    }

    /// <summary>
    /// One minute of clock skew either way, an expiry required, and the day-long cap. The cap is measured from now to the
    /// expiry: a token issued seven hours ago for thirty hours has twenty-three left and passes, which is what the plan's
    /// "valid for more than 24 hours is refused" comes to in practice.
    /// </summary>
    [Fact]
    public async Task Lifetime_allows_a_minute_of_skew_and_caps_the_remaining_validity_at_a_day()
    {
        var provider = await AddTokenIssuerAsync();
        var feed = await CreateFeedAsync();
        await LinkAsync(provider, "FiGet.Readers", feed, FeedAccessLevel.Read);
        var claims = Claims("time-app", roles: ["FiGet.Readers"]);
        var now = DateTime.UtcNow;

        HttpAssert.Status(HttpStatusCode.OK, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(provider), claims, expires: now.AddSeconds(-30)))));
        HttpAssert.Status(HttpStatusCode.Unauthorized, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(provider), claims, expires: now.AddMinutes(-2)))));
        HttpAssert.Status(HttpStatusCode.OK, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(provider), claims, notBefore: now.AddSeconds(30)))));
        HttpAssert.Status(HttpStatusCode.Unauthorized, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(provider), claims, notBefore: now.AddMinutes(5)))));
        HttpAssert.Status(HttpStatusCode.Unauthorized, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(provider), claims, noExpiry: true))));
        HttpAssert.Status(HttpStatusCode.OK, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(provider), claims, lifetime: TimeSpan.FromHours(23)))));
        HttpAssert.Status(HttpStatusCode.Unauthorized, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(provider), claims, lifetime: TimeSpan.FromHours(25)))));

        var longLived = idp.CreateAccessToken(Audience(provider), claims, issuedAt: now.AddHours(-7), notBefore: now.AddHours(-7), expires: now.AddHours(23));
        HttpAssert.Status(HttpStatusCode.OK, await ReadIndexAsync(feed, Bearer(longLived)));
    }

    /// <summary>An empty nonce is still a nonce: the token is a sign-in's ID token and is refused.</summary>
    [Fact]
    public async Task A_token_with_an_empty_nonce_is_an_id_token()
    {
        var provider = await AddTokenIssuerAsync();
        var feed = await CreateFeedAsync();
        await LinkAsync(provider, "FiGet.Readers", feed, FeedAccessLevel.Read);
        var claims = new Dictionary<string, object>(Claims("nonce-app", roles: ["FiGet.Readers"])) { ["nonce"] = "" };

        HttpAssert.Status(HttpStatusCode.Unauthorized, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(provider), claims))));
    }

    /// <summary>
    /// Required claims: the name is matched exactly, the value ignoring case; an array-valued claim satisfies a rule when
    /// one of its values does; a JSON boolean or number is compared as its text.
    /// </summary>
    [Fact]
    public async Task Required_claims_match_names_exactly_and_values_loosely()
    {
        var provider = await AddTokenIssuerAsync(requiredClaims: "ref_protected=true\nnamespace_path=tools\ntier=1");
        var feed = await CreateFeedAsync();
        await LinkAsync(provider, "FiGet.Publishers", feed, FeedAccessLevel.Publish);

        Dictionary<string, object> Token(object protectedRef, object space, object tier)
        {
            var claims = Claims("claims-app", roles: ["FiGet.Publishers"]);
            claims["ref_protected"] = protectedRef;
            claims["namespace_path"] = space;
            claims["tier"] = tier;
            return claims;
        }

        HttpAssert.Status(HttpStatusCode.OK, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(provider), Token("true", "tools", "1")))));
        HttpAssert.Status(HttpStatusCode.OK, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(provider), Token("TRUE", "Tools", "1")))));
        HttpAssert.Status(HttpStatusCode.OK, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(provider), Token(true, new[] { "other", "tools" }, 1)))));
        HttpAssert.Status(HttpStatusCode.Unauthorized, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(provider), Token(false, "tools", 1)))));
        HttpAssert.Status(HttpStatusCode.Unauthorized, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(provider), Token("true", new[] { "other", "platform" }, 1)))));
        HttpAssert.Status(HttpStatusCode.Unauthorized, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(provider), Token("true", "tools", 2)))));

        var upper = await AddTokenIssuerAsync(requiredClaims: "Ref_Protected=true");
        await LinkAsync(upper, "FiGet.Publishers", feed, FeedAccessLevel.Publish);
        var lowerCaseName = new Dictionary<string, object>(Claims("claims-app", roles: ["FiGet.Publishers"])) { ["ref_protected"] = "true" };
        HttpAssert.Status(HttpStatusCode.Unauthorized, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(upper), lowerCaseName))));
    }

    /// <summary>
    /// The groups claim is matched to the links ignoring case, as a single string as well as an array; a provider with no
    /// groups claim gives a valid token nothing (403), not a signature problem (401).
    /// </summary>
    [Fact]
    public async Task Groups_are_matched_ignoring_case_and_a_provider_without_a_groups_claim_grants_nothing()
    {
        var provider = await AddTokenIssuerAsync();
        var feed = await CreateFeedAsync();
        await LinkAsync(provider, "FiGet.Publishers", feed, FeedAccessLevel.Publish);

        HttpAssert.Status(HttpStatusCode.OK, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(provider), Claims("groups-app", roles: ["figet.PUBLISHERS"])))));
        var single = new Dictionary<string, object>(Claims("groups-app", roles: [])) { ["roles"] = "FiGet.Publishers" };
        HttpAssert.Status(HttpStatusCode.OK, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(provider), single))));

        var noGroups = await AddTokenIssuerAsync(groupsClaim: "");
        await LinkAsync(noGroups, "FiGet.Publishers", feed, FeedAccessLevel.Publish);
        HttpAssert.Status(HttpStatusCode.Forbidden, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(noGroups), Claims("groups-app", roles: ["FiGet.Publishers"])))));
    }

    /// <summary>
    /// A token never reaches a page, whatever its groups hold: the admin and profile pages send it to the sign-in page. On
    /// the API it does at most what Publish allows, and a refusal's body carries nothing of the token.
    /// </summary>
    [Fact]
    public async Task A_token_reaches_no_page_and_no_more_than_publish_on_the_api()
    {
        var provider = await AddTokenIssuerAsync();
        var feed = await CreateFeedAsync();
        await LinkAsync(provider, "FiGet.Managers", feed, FeedAccessLevel.Manage);
        await LinkAsync(provider, "FiGet.Readers", feed, FeedAccessLevel.Read);
        var manager = idp.CreateAccessToken(Audience(provider), Claims("manager-app", roles: ["FiGet.Managers"]));

        using var browser = CreateBrowser();
        browser.DefaultRequestHeaders.Authorization = Bearer(manager);
        foreach (var page in new[] { "/admin/providers", $"/admin/feeds/{feed}", $"/admin/feeds/{feed}/access", "/admin/tokens", "/account/profile" })
        {
            var response = await browser.GetAsync(page);
            Assert.True(response.StatusCode == HttpStatusCode.Redirect, $"{page} answered {(int)response.StatusCode} to a token.");
            var target = response.Headers.Location!.IsAbsoluteUri ? response.Headers.Location.AbsolutePath : response.Headers.Location.OriginalString;
            Assert.StartsWith("/account/login", target, StringComparison.Ordinal);
        }

        var reader = idp.CreateAccessToken(Audience(provider), Claims("reader-app", roles: ["FiGet.Readers"]));
        using var client = new HttpClient { BaseAddress = server.BaseAddress };
        client.DefaultRequestHeaders.Add("X-NuGet-ApiKey", reader);
        var delete = await client.PostAsync($"api/packages/{feed}/delete?name=Api.Token&version=1.0.0", content: null);
        HttpAssert.Status(HttpStatusCode.Forbidden, delete);
        var body = await delete.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(reader[..40], body, StringComparison.Ordinal);

        var wrong = idp.CreateAccessToken("api://someone-else", Claims("reader-app", roles: ["FiGet.Readers"]));
        var refused = await ReadIndexAsync(feed, Bearer(wrong));
        HttpAssert.Status(HttpStatusCode.Unauthorized, refused);
        Assert.DoesNotContain(wrong[..40], await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    /// <summary>
    /// A stream of tokens naming key ids the issuer never published does not make FiGet fetch the keys on every one: within
    /// the refresh interval the keys fetched a moment ago are the answer.
    /// </summary>
    [Fact]
    public async Task Unknown_key_ids_fetch_the_keys_at_most_once_per_interval()
    {
        var provider = await AddTokenIssuerAsync();
        var feed = await CreateFeedAsync();
        await LinkAsync(provider, "FiGet.Readers", feed, FeedAccessLevel.Read);
        var claims = Claims("kid-app", roles: ["FiGet.Readers"]);
        HttpAssert.Status(HttpStatusCode.OK, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(provider), claims))));

        var before = idp.JwksRequests;
        for (var i = 0; i < 5; i++)
        {
            var forged = idp.CreateAccessToken(Audience(provider), claims, key: FakeOidcProvider.ForgedKey("made-up-" + i));
            HttpAssert.Status(HttpStatusCode.Unauthorized, await ReadIndexAsync(feed, Bearer(forged)));
        }

        Assert.True(idp.JwksRequests - before <= 1, $"Five unknown key ids fetched the keys {idp.JwksRequests - before} times.");
    }

    /// <summary>
    /// When the issuer stops answering, the keys fetched before keep checking tokens: a good token still passes while the
    /// issuer is down, and one with an unknown key id is refused without waiting on the outage. An issuer that never
    /// answered at all refuses every token, promptly.
    /// </summary>
    [Fact]
    public async Task An_issuer_outage_keeps_the_keys_fetched_before()
    {
        var provider = await AddTokenIssuerAsync();
        var feed = await CreateFeedAsync();
        await LinkAsync(provider, "FiGet.Readers", feed, FeedAccessLevel.Read);
        var claims = Claims("outage-app", roles: ["FiGet.Readers"]);
        HttpAssert.Status(HttpStatusCode.OK, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(provider), claims))));

        var validator = server.Services.GetRequiredService<ApiTokenValidator>();
        var interval = validator.KeyRefreshInterval;
        validator.KeyRefreshInterval = TimeSpan.Zero;
        idp.Broken = true;
        try
        {
            var watch = Stopwatch.StartNew();
            HttpAssert.Status(HttpStatusCode.Unauthorized, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(provider), claims, key: FakeOidcProvider.ForgedKey("rolled")))));
            HttpAssert.Status(HttpStatusCode.OK, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(provider), claims))));
            HttpAssert.Status(HttpStatusCode.Unauthorized, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(provider), claims, key: FakeOidcProvider.ForgedKey("rolled-again")))));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Three requests during the outage took {watch.Elapsed}.");
        }
        finally
        {
            idp.Broken = false;
            validator.KeyRefreshInterval = interval;
        }

        var closed = "http://127.0.0.1:1";
        var unreachable = await AddTokenIssuerAtAsync(closed);
        await LinkAsync(unreachable, "FiGet.Readers", feed, FeedAccessLevel.Read);
        var timing = Stopwatch.StartNew();
        HttpAssert.Status(HttpStatusCode.Unauthorized, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(unreachable), claims, issuer: closed))));
        Assert.True(timing.Elapsed < TimeSpan.FromSeconds(10), $"A token from an unreachable issuer took {timing.Elapsed} to refuse.");
    }

    /// <summary>Twenty first requests at once for a new provider fetch its discovery document and keys once, not twenty times.</summary>
    [Fact]
    public async Task Concurrent_first_requests_share_one_fetch_of_the_issuer_metadata()
    {
        var provider = await AddTokenIssuerAsync();
        var feed = await CreateFeedAsync();
        await LinkAsync(provider, "FiGet.Readers", feed, FeedAccessLevel.Read);
        var token = idp.CreateAccessToken(Audience(provider), Claims("burst-app", roles: ["FiGet.Readers"]));

        var discovery = idp.DiscoveryRequests;
        var keys = idp.JwksRequests;
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => ReadIndexAsync(feed, Bearer(token))));
        Assert.All(responses, r => HttpAssert.Status(HttpStatusCode.OK, r));
        Assert.Equal(1, idp.DiscoveryRequests - discovery);
        Assert.Equal(1, idp.JwksRequests - keys);
    }

    /// <summary>
    /// The two switches are independent: with sign-ins off and tokens on, nobody signs in with the provider and tokens work;
    /// with sign-ins on and tokens off, the sign-in button is there and a token is refused.
    /// </summary>
    [Fact]
    public async Task Sign_in_and_api_tokens_are_separate_switches()
    {
        var feed = await CreateFeedAsync();
        var claims = Claims("switch-app", roles: ["FiGet.Readers"]);

        var tokensOnly = await AddTokenIssuerAsync();
        await LinkAsync(tokensOnly, "FiGet.Readers", feed, FeedAccessLevel.Read);
        HttpAssert.Status(HttpStatusCode.OK, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(tokensOnly), claims))));
        using (var browser = CreateBrowser())
        using (var content = new FormUrlEncodedContent(new Dictionary<string, string>()))
        {
            var attempt = await browser.PostAsync($"/account/external/{tokensOnly.Slug}", content);
            HttpAssert.Status(HttpStatusCode.Redirect, attempt);
            Assert.StartsWith("/account/login?external=", attempt.Headers.Location!.OriginalString, StringComparison.Ordinal);
        }

        var signInOnly = await AddProviderAsync(groupsClaim: "roles");
        var provider = (await FindProviderAsync(signInOnly))!;
        await LinkAsync(provider, "FiGet.Readers", feed, FeedAccessLevel.Read);
        HttpAssert.Status(HttpStatusCode.Unauthorized, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(provider), claims))));
        using (var stranger = CreateBrowser())
        {
            Assert.Contains($"/account/external/{signInOnly}", await HttpAssert.SuccessBodyAsync(await stranger.GetAsync("/account/login")), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Deleting a provider while a pipeline uses it: the next request with the same token is refused, the group links are
    /// gone, and a provider added again under the same slug starts without them.
    /// </summary>
    [Fact]
    public async Task Deleting_a_provider_refuses_its_tokens_at_the_next_request_and_drops_its_links()
    {
        var provider = await AddTokenIssuerAsync();
        var feed = await CreateFeedAsync();
        var group = await LinkAsync(provider, "FiGet.Readers", feed, FeedAccessLevel.Read);
        var token = idp.CreateAccessToken(Audience(provider), Claims("gone-app", roles: ["FiGet.Readers"]));
        HttpAssert.Status(HttpStatusCode.OK, await ReadIndexAsync(feed, Bearer(token)));

        await using (var scope = server.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<IOidcProviderStore>().DeleteAsync(provider.Key, CancellationToken.None));
        }

        HttpAssert.Status(HttpStatusCode.Unauthorized, await ReadIndexAsync(feed, Bearer(token)));
        await using (var scope = server.Services.CreateAsyncScope())
        {
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<IGroupStore>().ProviderLinksAsync(group, CancellationToken.None));
            Assert.True(await scope.ServiceProvider.GetRequiredService<IOidcProviderStore>().AddAsync(
                new OidcProvider
                {
                    Slug = provider.Slug,
                    DisplayName = provider.DisplayName,
                    Authority = idp.Authority,
                    ClientId = "",
                    Enabled = false,
                    GroupsClaim = "roles",
                    AcceptApiTokens = true,
                    ApiAudiences = Audience(provider),
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow,
                },
                CancellationToken.None));
        }

        HttpAssert.Status(HttpStatusCode.Forbidden, await ReadIndexAsync(feed, Bearer(token)));
    }

    /// <summary>A token issuer whose authority is written differently from the fake's own: a trailing slash, a host alias, a closed port.</summary>
    private async Task<OidcProvider> AddTokenIssuerAtAsync(string authority)
    {
        var slug = "t" + Guid.NewGuid().ToString("N")[..10];
        await using var scope = server.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOidcProviderStore>();
        Assert.True(await store.AddAsync(
            new OidcProvider
            {
                Slug = slug,
                DisplayName = "Issuer " + slug,
                Authority = authority,
                ClientId = "",
                Enabled = false,
                GroupsClaim = "roles",
                AcceptApiTokens = true,
                ApiAudiences = Audience(slug),
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            },
            CancellationToken.None));
        return (await store.FindBySlugAsync(slug, CancellationToken.None))!;
    }

    private async Task<OidcProvider?> FindProviderAsync(string slug)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IOidcProviderStore>().FindBySlugAsync(slug, CancellationToken.None);
    }
}
