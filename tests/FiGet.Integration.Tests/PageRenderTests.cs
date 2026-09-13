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

    [Fact]
    public async Task Pages_render_without_error_under_concurrent_requests()
    {
        using var client = server.CreateClient();
        var responses = await Task.WhenAll(Enumerable.Range(0, 60).Select(i => client.GetAsync(i % 2 == 0 ? "/feeds/public" : "/")));

        var failed = responses.Where(r => r.StatusCode != HttpStatusCode.OK).Select(r => (int)r.StatusCode).ToList();
        Assert.True(failed.Count == 0, $"{failed.Count} of {responses.Length} page loads failed: {string.Join(", ", failed)}");
    }
}
