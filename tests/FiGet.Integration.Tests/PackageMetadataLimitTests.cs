using System.Net;
using System.Net.Http.Headers;
using FiGet.Integration.Tests.Infrastructure;
using FiGet.Testing;

namespace FiGet.Integration.Tests;

public sealed class SqlitePackageMetadataLimitTests(SqliteServerFixture fixture) : PackageMetadataLimitTests(fixture), IClassFixture<SqliteServerFixture>;

public sealed class SqlServerPackageMetadataLimitTests(SqlServerServerFixture fixture) : PackageMetadataLimitTests(fixture), IClassFixture<SqlServerServerFixture>;

/// <summary>
/// How long package metadata may be, on both database providers alike. SQLite enforces no column length and SQL Server
/// does, so a limit that only SQL Server applies passes every demonstration on SQLite and fails in production.
/// </summary>
public abstract class PackageMetadataLimitTests
{
    private readonly FiGetServerFixture server;

    protected PackageMetadataLimitTests(FiGetServerFixture server)
    {
        this.server = server;
        server.SkipIfUnavailable();
    }

    /// <summary>
    /// The PowerShell Gallery encodes every exported command of a module as a tag, so real modules carry tag strings of
    /// 15,000 to 20,000 characters (Microsoft.Graph.Users 2.39.0: 18,149; Az.Compute 11.9.0: 20,048, measured on
    /// 2026-09-15). Such a module is stored with its tags intact and listed by v2 and v3. Found cross-checking other package
    /// servers' issue trackers: on SQL Server the tags column used to hold 4,000 characters, the insert failed, and the
    /// failure was answered as "already exists".
    /// </summary>
    [Fact]
    public async Task A_module_with_twenty_thousand_characters_of_tags_is_stored_and_listed()
    {
        var id = FiGetServerFixture.UniqueId("Limits.LongTags");
        var tags = Enumerable.Range(0, 700).Select(i => $"PSCommand_Get-SomethingRatherLongName{i:D4}").ToList();
        Assert.True(string.Join(' ', tags).Length > 20_000);

        var push = await PushAsync(id, b =>
        {
            foreach (var tag in tags)
            {
                b.Tags.Add(tag);
            }

            b.Authors.Clear();
            foreach (var author in Enumerable.Range(0, 400).Select(i => $"Contributor Number {i:D4}"))
            {
                b.Authors.Add(author);
            }
        });
        Assert.True(push.StatusCode == HttpStatusCode.Created, $"Push answered {(int)push.StatusCode}: {await push.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");

        using var client = server.CreateClient();
        Assert.Contains(tags[^1], await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/public/FindPackagesById()?id='{id}'")), StringComparison.Ordinal);
        Assert.Contains(tags[^1], await HttpAssert.SuccessBodyAsync(await client.GetAsync($"nuget/public/v3/registration/{id.ToLowerInvariant()}/index.json")), StringComparison.Ordinal);
    }

    /// <summary>A value longer than a column that stays bounded is a 400 naming the nuspec element, on both providers, not a failed insert.</summary>
    [Fact]
    public async Task A_title_longer_than_its_column_is_refused_with_its_name()
    {
        var id = FiGetServerFixture.UniqueId("Limits.LongTitle");
        var push = await PushAsync(id, b => b.Title = new string('T', 600));

        HttpAssert.Status(HttpStatusCode.BadRequest, push);
        Assert.Contains("title is 600 characters", await push.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);

        using var client = server.CreateClient();
        HttpAssert.Status(HttpStatusCode.NotFound, await client.GetAsync($"nuget/public/v3/registration/{id.ToLowerInvariant()}/index.json"));
    }

    private async Task<HttpResponseMessage> PushAsync(string id, Action<NuGet.Packaging.PackageBuilder> configure)
    {
        using var package = TestPackages.Create(id, "1.0.0", configure);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-NuGet-ApiKey", FiGetServerFixture.AdminToken);
        using var content = new MultipartFormDataContent();
        var file = new StreamContent(package);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "package", "package.nupkg");
        return await client.PutAsync("nuget/public/v3/publish", content);
    }
}
