using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Infrastructure.Persistence;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Testing;
using FiGet.Web.SignIn;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace FiGet.Integration.Tests;

/// <summary>
/// Access tokens from a trusted issuer on the API, next to FiGet's own keys: an application's client-credentials token or a
/// CI job's token, checked against <see cref="FakeOidcProvider"/>'s published keys, and allowed what the FiGet groups linked
/// to its groups claim may do - at most publish.
/// </summary>
public abstract partial class ExternalSignInTests
{
    private const string ApiAudience = "api://figet-tests";

    /// <summary>
    /// A token whose role is linked to a group with Publish pushes and reads, sent every way a client sends a key: as the
    /// API key header, as a Basic password and as a Bearer token.
    /// </summary>
    [Fact]
    public async Task An_access_token_publishes_and_reads_through_its_linked_group()
    {
        var provider = await AddTokenIssuerAsync();
        var feed = await CreateFeedAsync();
        await LinkAsync(provider, "FiGet.Publishers", feed, FeedAccessLevel.Publish);
        var token = idp.CreateAccessToken(Audience(provider), Claims("ci-app", roles: ["FiGet.Publishers"]));

        HttpAssert.Status(HttpStatusCode.Created, await PushWithApiKeyAsync(feed, token));
        HttpAssert.Status(HttpStatusCode.OK, await ReadIndexAsync(feed, new AuthenticationHeaderValue("Bearer", token)));
        HttpAssert.Status(HttpStatusCode.OK, await ReadIndexAsync(feed, new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("any:" + token)))));

        var used = await WaitForAuditAsync("token.external.used", e => e.Subject == $"{provider.Slug}:ci-app");
        Assert.Contains($"feed={feed}", used.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The groups decide, request by request: Read reads and does not push, Manage still only publishes, a role nobody linked
    /// grants nothing, and removing the link takes effect at the next request with the same token.
    /// </summary>
    [Fact]
    public async Task An_access_token_does_what_its_groups_may_do_now_and_never_more_than_publish()
    {
        var provider = await AddTokenIssuerAsync();
        var readFeed = await CreateFeedAsync();
        var managedFeed = await CreateFeedAsync();
        var readers = await LinkAsync(provider, "FiGet.Readers", readFeed, FeedAccessLevel.Read);
        await LinkAsync(provider, "FiGet.Managers", managedFeed, FeedAccessLevel.Manage);

        var reader = idp.CreateAccessToken(Audience(provider), Claims("reader-app", roles: ["FiGet.Readers"]));
        HttpAssert.Status(HttpStatusCode.OK, await ReadIndexAsync(readFeed, new AuthenticationHeaderValue("Bearer", reader)));
        HttpAssert.Status(HttpStatusCode.Forbidden, await PushWithApiKeyAsync(readFeed, reader));
        HttpAssert.Status(HttpStatusCode.Forbidden, await ReadIndexAsync(managedFeed, new AuthenticationHeaderValue("Bearer", reader)));

        var manager = idp.CreateAccessToken(Audience(provider), Claims("manager-app", roles: ["FiGet.Managers"]));
        HttpAssert.Status(HttpStatusCode.Created, await PushWithApiKeyAsync(managedFeed, manager));

        var stranger = idp.CreateAccessToken(Audience(provider), Claims("other-app", roles: ["Someone.Else"]));
        HttpAssert.Status(HttpStatusCode.Forbidden, await ReadIndexAsync(readFeed, new AuthenticationHeaderValue("Bearer", stranger)));

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var groups = scope.ServiceProvider.GetRequiredService<IGroupStore>();
            var link = (await groups.ProviderLinksAsync(readers, CancellationToken.None)).Single();
            Assert.True(await groups.RemoveProviderLinkAsync(readers, link.Key, CancellationToken.None));
        }

        HttpAssert.Status(HttpStatusCode.Forbidden, await ReadIndexAsync(readFeed, new AuthenticationHeaderValue("Bearer", reader)));
    }

    /// <summary>
    /// Every check has to pass: an issuer not trusted for tokens, the wrong audience, an expired token, one valid for longer
    /// than a day, a forged signature, a key the issuer never published, a shared-secret algorithm, and a sign-in's ID token
    /// are each refused as no credential at all. A push sending one gets the refusal a bad API key gets.
    /// </summary>
    [Fact]
    public async Task An_access_token_is_refused_unless_every_check_passes()
    {
        var provider = await AddTokenIssuerAsync();
        var feed = await CreateFeedAsync();
        await LinkAsync(provider, "FiGet.Publishers", feed, FeedAccessLevel.Publish);
        var claims = Claims("ci-app", roles: ["FiGet.Publishers"]);
        HttpAssert.Status(HttpStatusCode.OK, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(provider), claims))));

        var nonce = new Dictionary<string, object>(claims) { ["nonce"] = "a-sign-in" };
        var refused = new Dictionary<string, string>
        {
            ["wrong audience"] = idp.CreateAccessToken("api://someone-else", claims),
            ["expired"] = idp.CreateAccessToken(Audience(provider), claims, expires: DateTime.UtcNow.AddMinutes(-5)),
            ["longer than a day"] = idp.CreateAccessToken(Audience(provider), claims, lifetime: TimeSpan.FromDays(2)),
            ["forged signature"] = idp.CreateAccessToken(Audience(provider), claims, key: FakeOidcProvider.ForgedKey()),
            ["unpublished key"] = idp.CreateAccessToken(Audience(provider), claims, key: FakeOidcProvider.ForgedKey("never-published")),
            ["shared secret"] = idp.CreateAccessToken(Audience(provider), claims, key: new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64)), algorithm: SecurityAlgorithms.HmacSha256),
            ["ID token"] = idp.CreateAccessToken(Audience(provider), nonce),
        };

        foreach (var (why, token) in refused)
        {
            var response = await ReadIndexAsync(feed, Bearer(token));
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"A token with {why} was answered {(int)response.StatusCode}.");
        }

        var push = await PushWithApiKeyAsync(feed, refused["wrong audience"]);
        HttpAssert.Status(HttpStatusCode.Forbidden, push);
        Assert.Contains("invalid, expired or revoked", await push.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);

        await ChangeProviderAsync(provider.Slug, p => p.AcceptApiTokens = false);
        HttpAssert.Status(HttpStatusCode.Unauthorized, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(provider), claims))));
    }

    /// <summary>
    /// An issuer many projects share, the way a CI system's is: nobody signs in with it, the project path is the groups claim,
    /// and required claims keep an unprotected branch of a linked project from publishing.
    /// </summary>
    [Fact]
    public async Task A_ci_issuer_needs_its_required_claims_and_a_linked_project()
    {
        var provider = await AddTokenIssuerAsync(groupsClaim: "project_path", requiredClaims: "ref_protected=true\nnamespace_path=tools");
        var feed = await CreateFeedAsync();
        await LinkAsync(provider, "tools/module-builder", feed, FeedAccessLevel.Publish);

        Dictionary<string, object> Job(string project, string protectedRef, string space = "tools") => new()
        {
            ["sub"] = $"project_path:{project}:ref_type:branch:ref:main",
            ["project_path"] = project,
            ["ref_protected"] = protectedRef,
            ["namespace_path"] = space,
        };

        HttpAssert.Status(HttpStatusCode.Created, await PushWithApiKeyAsync(feed, idp.CreateAccessToken(Audience(provider), Job("tools/module-builder", "true"))));
        HttpAssert.Status(HttpStatusCode.Forbidden, await PushWithApiKeyAsync(feed, idp.CreateAccessToken(Audience(provider), Job("tools/module-builder", "false"))));
        HttpAssert.Status(HttpStatusCode.Forbidden, await PushWithApiKeyAsync(feed, idp.CreateAccessToken(Audience(provider), Job("tools/module-builder", "true", space: "other"))));
        HttpAssert.Status(HttpStatusCode.Forbidden, await PushWithApiKeyAsync(feed, idp.CreateAccessToken(Audience(provider), Job("someone/else", "true"))));

        var missing = await WaitForAuditAsync("token.refused", e => e.Subject == $"access token from {provider.Slug}" && e.Detail.Contains("required claim", StringComparison.Ordinal));
        Assert.Contains("ref_protected", missing.Detail, StringComparison.Ordinal);
    }

    /// <summary>After the issuer rolls its signing key over, tokens with the new key are accepted: the keys are fetched again.</summary>
    [Fact]
    public async Task A_signing_key_rollover_at_the_issuer_is_picked_up()
    {
        var provider = await AddTokenIssuerAsync();
        var feed = await CreateFeedAsync();
        await LinkAsync(provider, "FiGet.Readers", feed, FeedAccessLevel.Read);
        var claims = Claims("ci-app", roles: ["FiGet.Readers"]);
        HttpAssert.Status(HttpStatusCode.OK, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(provider), claims))));

        var validator = server.Services.GetRequiredService<ApiTokenValidator>();
        var interval = validator.KeyRefreshInterval;
        validator.KeyRefreshInterval = TimeSpan.Zero;
        try
        {
            idp.RotateKey();
            HttpAssert.Status(HttpStatusCode.OK, await ReadIndexAsync(feed, Bearer(idp.CreateAccessToken(Audience(provider), claims))));
        }
        finally
        {
            validator.KeyRefreshInterval = interval;
        }
    }

    /// <summary>
    /// A refused token is audited with the provider and the reason, and nothing of the token itself; a token from an issuer
    /// nobody trusts is recorded without its unverified issuer.
    /// </summary>
    [Fact]
    public async Task A_refused_access_token_is_audited_without_its_text()
    {
        var provider = await AddTokenIssuerAsync();
        var feed = await CreateFeedAsync();
        var token = idp.CreateAccessToken("api://someone-else", Claims("audit-app", roles: []));
        await PushWithApiKeyAsync(feed, token);

        var entry = await WaitForAuditAsync("token.refused", e => e.Subject == $"access token from {provider.Slug}" && e.Detail.Contains($"feed={feed}", StringComparison.Ordinal));
        Assert.Contains("reason=wrong audience", entry.Detail, StringComparison.Ordinal);

        await using var scope = server.Services.CreateAsyncScope();
        var all = await scope.ServiceProvider.GetRequiredService<IAuditStore>().QueryAsync(new AuditQuery(Take: 500), CancellationToken.None);
        Assert.DoesNotContain(all, e => (e.Subject + e.Detail).Contains(token[..40], StringComparison.Ordinal) || (e.Subject + e.Detail).Contains(token[^40..], StringComparison.Ordinal));
    }

    /// <summary>
    /// An issuer trusted only for API tokens is added on the providers page without a client id or secret; it is not on the
    /// sign-in page, and the page refuses API tokens without an audience or with a required claim that is not name=value.
    /// </summary>
    [Fact]
    public async Task An_api_token_issuer_is_added_on_the_page_without_a_client()
    {
        using var admin = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(admin));
        var slug = "t" + Guid.NewGuid().ToString("N")[..10];
        var page = await HttpAssert.SuccessBodyAsync(await admin.GetAsync("/admin/providers"));
        Assert.Contains("Accept access tokens from this issuer on the API", page, StringComparison.Ordinal);

        var form = FormWith(page, "create-provider");
        var fields = Hidden(form);
        fields[BrowserSignIn.InputName(form, "provider-display")] = "CI jobs";
        fields[BrowserSignIn.InputName(form, "provider-slug")] = slug;
        fields[BrowserSignIn.InputName(form, "provider-authority")] = idp.Authority;
        fields[BrowserSignIn.InputName(form, "provider-username-claim")] = "preferred_username";
        fields[BrowserSignIn.InputName(form, "provider-groups-claim")] = "project_path";
        fields[InputNameOfCheckbox(form, "AcceptApiTokens")] = "true";
        var audiences = TextAreaName(form, "provider-audiences");
        var required = TextAreaName(form, "provider-required-claims");

        fields[required] = "ref_protected";
        Assert.Contains("Enter the audience API tokens must be for.", await PostProviderFormAsync(admin, fields), StringComparison.Ordinal);
        fields[audiences] = ApiAudience;
        Assert.Contains("Required claims are one name=value per line", await PostProviderFormAsync(admin, fields), StringComparison.Ordinal);
        fields[required] = "ref_protected=true";
        using (var content = new FormUrlEncodedContent(fields))
        {
            HttpAssert.Status(HttpStatusCode.Redirect, await admin.PostAsync("/admin/providers", content));
        }

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<FiGetDbContext>().OidcProviders.SingleAsync(p => p.Slug == slug, TestContext.Current.CancellationToken);
            Assert.False(stored.Enabled);
            Assert.True(stored.AcceptApiTokens);
            Assert.Equal("", stored.ClientId);
            Assert.Equal(ApiAudience, stored.ApiAudiences);
            Assert.Equal("ref_protected=true", stored.ApiRequiredClaims);
        }

        using var stranger = CreateBrowser();
        Assert.DoesNotContain($"/account/external/{slug}", await HttpAssert.SuccessBodyAsync(await stranger.GetAsync("/account/login")), StringComparison.Ordinal);
    }

    private async Task<OidcProvider> AddTokenIssuerAsync(string groupsClaim = "roles", string requiredClaims = "")
    {
        var slug = "t" + Guid.NewGuid().ToString("N")[..10];
        await using var scope = server.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOidcProviderStore>();
        Assert.True(await store.AddAsync(
            new OidcProvider
            {
                Slug = slug,
                DisplayName = "Issuer " + slug,
                Authority = idp.Authority,
                ClientId = "",
                Enabled = false,
                GroupsClaim = groupsClaim,
                AcceptApiTokens = true,
                ApiAudiences = Audience(slug),
                ApiRequiredClaims = requiredClaims,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            },
            CancellationToken.None));
        return (await store.FindBySlugAsync(slug, CancellationToken.None))!;
    }

    private async Task<string> CreateFeedAsync()
    {
        var name = "feed" + Guid.NewGuid().ToString("N")[..8];
        await using var scope = server.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IFeedStore>().CreateAsync(
            new Feed { Name = name, NameLower = name, Kind = FeedKind.Curated, CreatedUtc = DateTime.UtcNow },
            CancellationToken.None));
        return name;
    }

    /// <summary>A new FiGet group with <paramref name="level"/> on the feed, linked to the provider group; returns the group's key.</summary>
    private async Task<int> LinkAsync(OidcProvider provider, string providerGroup, string feed, FeedAccessLevel level)
    {
        var name = "g" + Guid.NewGuid().ToString("N")[..10];
        await using var scope = server.Services.CreateAsyncScope();
        var groups = scope.ServiceProvider.GetRequiredService<IGroupStore>();
        Assert.True(await groups.AddAsync(new FiGet.Domain.Entities.Group { Name = name, NameLower = name, CreatedUtc = DateTime.UtcNow }, CancellationToken.None));
        var key = (await groups.ListAsync(CancellationToken.None)).Single(g => g.Name == name).Key;
        Assert.True(await groups.AddProviderLinkAsync(new GroupProviderLink { GroupKey = key, ProviderKey = provider.Key, ProviderGroup = providerGroup }, CancellationToken.None));
        var feedKey = (await scope.ServiceProvider.GetRequiredService<IFeedStore>().FindAsync(feed, CancellationToken.None))!.Key;
        await scope.ServiceProvider.GetRequiredService<IFeedPermissionStore>().SetAsync(feedKey, null, key, level, CancellationToken.None);
        return key;
    }

    private static Dictionary<string, object> Claims(string clientId, string[] roles) => new()
    {
        ["sub"] = Guid.NewGuid().ToString("N"),
        ["azp"] = clientId,
        ["roles"] = roles,
    };

    /// <summary>
    /// An audience of this provider's own. The issuer's port is the operating system's choice and may come round again for a
    /// later test, whose tokens must not match an earlier test's provider row with the same issuer.
    /// </summary>
    private static string Audience(OidcProvider provider) => Audience(provider.Slug);

    private static string Audience(string slug) => $"api://figet-tests/{slug}";

    private static AuthenticationHeaderValue Bearer(string token) => new("Bearer", token);

    private async Task<HttpResponseMessage> ReadIndexAsync(string feed, AuthenticationHeaderValue authorization)
    {
        using var client = new HttpClient { BaseAddress = server.BaseAddress };
        client.DefaultRequestHeaders.Authorization = authorization;
        return await client.GetAsync($"nuget/{feed}/v3/query");
    }

    private async Task<HttpResponseMessage> PushWithApiKeyAsync(string feed, string token)
    {
        using var client = new HttpClient { BaseAddress = server.BaseAddress };
        client.DefaultRequestHeaders.Add("X-NuGet-ApiKey", token);
        using var package = TestPackages.Create(FiGetServerFixture.UniqueId("Api.Token"), "1.0.0");
        using var content = new MultipartFormDataContent();
        var file = new StreamContent(package);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "package", "package.nupkg");
        return await client.PutAsync($"nuget/{feed}/v3/publish", content);
    }

    private static async Task<string> PostProviderFormAsync(HttpClient admin, Dictionary<string, string> fields)
    {
        using var content = new FormUrlEncodedContent(fields);
        var response = await admin.PostAsync("/admin/providers", content);
        HttpAssert.Status(HttpStatusCode.OK, response);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private static string TextAreaName(string form, string id) =>
        WebUtility.HtmlDecode(Regex.Match(form, $"<textarea[^>]*id=\"{id}\"[^>]*name=\"(?<n>[^\"]+)\"").Groups["n"].Value);

    /// <summary>Waits for the background writer: a request never waits on the audit table, so a test has to.</summary>
    private async Task<AuditEntry> WaitForAuditAsync(string action, Func<AuditEntry, bool> match)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using (var scope = server.Services.CreateAsyncScope())
            {
                var found = (await scope.ServiceProvider.GetRequiredService<IAuditStore>().QueryAsync(new AuditQuery(Action: action, Take: 500), CancellationToken.None)).FirstOrDefault(match);
                if (found is not null)
                {
                    return found;
                }
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new Xunit.Sdk.XunitException($"No {action} audit entry arrived within five seconds.");
    }
}
