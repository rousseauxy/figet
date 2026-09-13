using System.Net;
using FiGet.Integration.Tests.Infrastructure;

namespace FiGet.Integration.Tests;

public sealed class SqlitePageRenderTests(SqliteServerFixture fixture) : PageRenderTests(fixture), IClassFixture<SqliteServerFixture>;

public sealed class SqlServerPageRenderTests(SqlServerServerFixture fixture) : PageRenderTests(fixture), IClassFixture<SqlServerServerFixture>;

/// <summary>
/// Pages render their components concurrently: while one component waits on its data, the renderer carries on
/// into the next. Two of them reading through the request's one database context at the same moment is an
/// error, and it surfaced as an occasional 500 on the feed page - on SQL Server only, because SQLite runs its
/// queries synchronously and never lets the two overlap. The root component read the chosen theme while the
/// page read its feed.
///
/// A single request hit it now and then; many at once make it all but certain, which is what this relies on.
/// </summary>
public abstract class PageRenderTests
{
    private readonly FiGetServerFixture server;

    protected PageRenderTests(FiGetServerFixture server)
    {
        this.server = server;
        server.SkipIfUnavailable();
    }

    /// <summary>Every page names a tab icon, and without a theme it is FiGet's own mark, which is served.</summary>
    [Fact]
    public async Task A_page_links_a_favicon_that_is_served()
    {
        using var client = server.CreateClient();
        var page = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/"));

        var match = System.Text.RegularExpressions.Regex.Match(page, "<link rel=\"icon\" href=\"(?<href>[^\"]+)\"");
        Assert.True(match.Success, "No favicon link on the page.");
        Assert.Contains("favicon", match.Groups["href"].Value, StringComparison.Ordinal);
        HttpAssert.Status(HttpStatusCode.OK, await client.GetAsync(match.Groups["href"].Value.TrimStart('/')));
    }

    [Fact]
    public async Task Pages_render_without_error_under_concurrent_requests()
    {
        using var client = server.CreateClient();
        var responses = await Task.WhenAll(Enumerable.Range(0, 60).Select(i => client.GetAsync(i % 2 == 0 ? "/feeds/public" : "/")));

        var failed = responses.Where(r => r.StatusCode != HttpStatusCode.OK).Select(r => (int)r.StatusCode).ToList();
        Assert.True(failed.Count == 0, $"{failed.Count} of {responses.Length} page loads failed: {string.Join(", ", failed)}");
    }
}
