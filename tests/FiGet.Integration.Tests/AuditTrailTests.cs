using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using FiGet.Application.Ports;
using FiGet.Application.Tokens;
using FiGet.Domain.Entities;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Integration.Tests;

public sealed class SqliteAuditTrailTests(SqliteServerFixture fixture) : AuditTrailTests(fixture), IClassFixture<SqliteServerFixture>;

public sealed class SqlServerAuditTrailTests(SqlServerServerFixture fixture) : AuditTrailTests(fixture), IClassFixture<SqlServerServerFixture>;

/// <summary>
/// The audit log in the database (docs/auth-plan.md, phase 5): the same calls that write the console line store a row,
/// the admin page filters them, a key that no longer works is recorded without flooding, and old rows are pruned.
/// </summary>
public abstract partial class AuditTrailTests
{
    private readonly FiGetServerFixture server;

    protected AuditTrailTests(FiGetServerFixture server)
    {
        this.server = server;
        server.SkipIfUnavailable();
    }

    /// <summary>A change made on an admin page is stored with who made it, and the page finds it by who and by event.</summary>
    [Fact]
    public async Task An_admin_change_is_stored_and_the_page_filters_it()
    {
        var group = "audit" + Guid.NewGuid().ToString("N")[..8];
        int groupKey;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var groups = scope.ServiceProvider.GetRequiredService<IGroupStore>();
            await groups.AddAsync(new FiGet.Domain.Entities.Group { Name = group, NameLower = group, CreatedUtc = DateTime.UtcNow }, CancellationToken.None);
            groupKey = (await groups.ListAsync(CancellationToken.None)).Single(g => g.Name == group).Key;
        }

        using var admin = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(admin));
        var page = await HttpAssert.SuccessBodyAsync(await admin.GetAsync("/account/profile"));
        using (var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = Antiforgery(page) }))
        {
            HttpAssert.Status(HttpStatusCode.Redirect, await admin.PostAsync($"/admin/groups/{groupKey}/delete", content));
        }

        var entry = await WaitForAsync(new AuditQuery(Action: "group.delete", Actor: FiGetServerFixture.AdminUserName), e => e.Subject == group);
        Assert.Equal("user:" + FiGetServerFixture.AdminUserName, entry.Actor);
        Assert.False(string.IsNullOrEmpty(entry.Caller));

        var filtered = await HttpAssert.SuccessBodyAsync(await admin.GetAsync($"/admin/audit?action=group.&actor={FiGetServerFixture.AdminUserName}"));
        Assert.Contains(group, filtered, StringComparison.Ordinal);
        var other = await HttpAssert.SuccessBodyAsync(await admin.GetAsync("/admin/audit?action=package.push&actor=nobody-at-all"));
        Assert.DoesNotContain(group, other, StringComparison.Ordinal);
    }

    /// <summary>A push over the protocol is stored under its feed and its token, so "what went into this feed" has an answer.</summary>
    [Fact]
    public async Task A_push_is_stored_under_its_feed_and_token()
    {
        var id = FiGetServerFixture.UniqueId("Audit.Push");
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-NuGet-ApiKey", FiGetServerFixture.AdminToken);
        using var package = TestPackages.Create(id, "1.0.0");
        using var content = new MultipartFormDataContent();
        var file = new StreamContent(package);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "package", "package.nupkg");
        HttpAssert.Status(HttpStatusCode.Created, await client.PutAsync("nuget/private/v3/publish", content));

        var entry = await WaitForAsync(new AuditQuery(Action: "package.push", Feed: "PRIVATE"), e => e.Subject.Contains(id, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("private", entry.Feed);
        Assert.Equal("token:bootstrap", entry.Actor);
    }

    /// <summary>
    /// A revoked key still being sent is recorded, by name and reason, once per window however often it is tried. An API
    /// key header that is no key at all is recorded without anything of the secret; a Basic password that is no key is not
    /// recorded, because it may be a person's real password.
    /// </summary>
    [Fact]
    public async Task Refused_keys_are_recorded_once_per_window_and_never_with_the_secret()
    {
        var name = "dead-" + Guid.NewGuid().ToString("N")[..8];
        string secret;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var tokens = scope.ServiceProvider.GetRequiredService<AccessTokenService>();
            var created = (await tokens.CreateServiceTokenAsync(FiGetServerFixture.SuperAdminActor, name, TokenScopes.Read, null, null, CancellationToken.None)).Created!;
            await tokens.RevokeAsync(created.Token.Key, CancellationToken.None);
            secret = created.Secret;
        }

        for (var i = 0; i < 3; i++)
        {
            using var client = server.CreateClient();
            client.DefaultRequestHeaders.Add("X-NuGet-ApiKey", secret);
            await client.GetAsync("nuget/private/v3/index.json");
        }

        // The Basic attempt goes first: entries are stored in the order they were queued, so waiting for the unknown key's
        // entry below also waits past anything the Basic attempt might have queued.
        var password = "not-a-key-" + Guid.NewGuid().ToString("N");
        using (var client = server.CreateClient())
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("someone:" + password)));
            await client.GetAsync("nuget/overwrite/v3/index.json");
        }

        var unknown = "figet_" + Guid.NewGuid().ToString("N");
        using (var client = server.CreateClient())
        {
            client.DefaultRequestHeaders.Add("X-NuGet-ApiKey", unknown);
            await client.GetAsync("nuget/private/v3/index.json");
        }

        var revoked = await WaitForAsync(new AuditQuery(Action: "token.refused"), e => e.Subject == name);
        Assert.Contains("reason=revoked", revoked.Detail, StringComparison.Ordinal);
        await WaitForAsync(new AuditQuery(Action: "token.refused"), e => e.Subject == "unknown key");

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var all = await scope.ServiceProvider.GetRequiredService<IAuditStore>().QueryAsync(new AuditQuery(Take: 500), CancellationToken.None);
            Assert.Single(all, e => e.Subject == name);
            Assert.DoesNotContain(all, e => e.Action == "token.refused" && e.Feed == "overwrite");
            Assert.DoesNotContain(all, e => (e.Subject + e.Detail).Contains(secret, StringComparison.Ordinal) || (e.Subject + e.Detail).Contains(unknown, StringComparison.Ordinal) || (e.Subject + e.Detail).Contains(password, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task The_audit_page_is_for_admins()
    {
        using var stranger = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await stranger.GetAsync("/admin/audit"));
    }

    /// <summary>Waits for the background writer: a request never waits on the audit table, so a test has to.</summary>
    private async Task<AuditEntry> WaitForAsync(AuditQuery query, Func<AuditEntry, bool> match)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using (var scope = server.Services.CreateAsyncScope())
            {
                var found = (await scope.ServiceProvider.GetRequiredService<IAuditStore>().QueryAsync(query, CancellationToken.None)).FirstOrDefault(match);
                if (found is not null)
                {
                    return found;
                }
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new Xunit.Sdk.XunitException($"No audit entry for {query} arrived within five seconds.");
    }

    private HttpClient CreateBrowser() =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = server.BaseAddress };

    private static string Antiforgery(string page) =>
        WebUtility.HtmlDecode(AntiforgeryPattern().Match(page).Groups["value"].Value);

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<value>[^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex AntiforgeryPattern();
}

/// <summary>A server that keeps audit entries for 30 days, with nothing written before the test asks.</summary>
public sealed class AuditRetentionServerFixture() : FiGetServerFixture(TestDatabase.Sqlite)
{
    protected override void Configure(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSetting("FiGet:Audit:RetentionDays", "30");
    }
}

/// <summary>Entries past the retention are deleted by the writer; newer ones stay.</summary>
public sealed class AuditRetentionTests(AuditRetentionServerFixture server) : IClassFixture<AuditRetentionServerFixture>
{
    [Fact]
    public async Task Entries_past_the_retention_are_pruned()
    {
        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAuditStore>().AddRangeAsync(
                [
                    new AuditEntry { WhenUtc = DateTime.UtcNow.AddDays(-45), Action = "test.old", Subject = "old", Actor = "system", ActorLower = "system" },
                    new AuditEntry { WhenUtc = DateTime.UtcNow.AddDays(-5), Action = "test.recent", Subject = "recent", Actor = "system", ActorLower = "system" },
                ],
                CancellationToken.None);
        }

        // Anything recorded wakes the writer, which prunes on its first round.
        server.Services.GetRequiredService<FiGet.Http.AuditLog>().Record(null, "test.wake", "wake");

        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var scope = server.Services.CreateAsyncScope();
            var entries = await scope.ServiceProvider.GetRequiredService<IAuditStore>().QueryAsync(new AuditQuery(Action: "test."), CancellationToken.None);
            if (entries.Any(e => e.Subject == "wake") && entries.All(e => e.Subject != "old"))
            {
                Assert.Contains(entries, e => e.Subject == "recent");
                return;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new Xunit.Sdk.XunitException("The entry past the retention was not pruned within five seconds.");
    }
}
