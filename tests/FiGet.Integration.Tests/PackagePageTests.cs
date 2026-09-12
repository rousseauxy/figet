using FiGet.Integration.Tests.Infrastructure;

namespace FiGet.Integration.Tests;

/// <summary>
/// The version list on a package page. A proxy feed is used because it is the cheap way to get a package
/// with more versions than fit on one page: the upstream stub can register versions without anybody
/// building a nupkg for each one.
/// </summary>
public sealed class PackagePageTests(ProxyServerFixture server) : IClassFixture<ProxyServerFixture>
{
    /// <summary>
    /// Fifty rows by default, and a way to ask for more - which is what a reader looking at a package with
    /// dozens of versions actually wants, rather than paging four times.
    /// </summary>
    [Fact]
    public async Task The_version_list_pages_at_fifty_and_offers_larger_pages()
    {
        var id = FiGetServerFixture.UniqueId("Page.Versions");
        server.Upstream.AddVersions(id, Enumerable.Range(1, 60).Select(n => $"1.0.{n}"));

        using var client = server.CreateClient();
        var page = await HttpAssert.SuccessBodyAsync(
            await client.GetAsync($"feeds/proxy/packages/{id}?tab=versions"));

        Assert.Contains("1 to 50 of 60 versions", page, StringComparison.Ordinal);
        Assert.Contains("per page", page, StringComparison.Ordinal);

        var larger = await HttpAssert.SuccessBodyAsync(
            await client.GetAsync($"feeds/proxy/packages/{id}?tab=versions&per=100"));

        Assert.Contains("1 to 60 of 60 versions", larger, StringComparison.Ordinal);
    }

    /// <summary>
    /// A size nobody offered falls back to the default. The rows are rendered server-side, so without this
    /// a hand-typed <c>per=100000</c> would render every version of a package that has 2098 of them.
    /// </summary>
    [Fact]
    public async Task A_page_size_that_is_not_offered_falls_back_to_the_default()
    {
        var id = FiGetServerFixture.UniqueId("Page.Capped");
        server.Upstream.AddVersions(id, Enumerable.Range(1, 60).Select(n => $"1.0.{n}"));

        using var client = server.CreateClient();
        var page = await HttpAssert.SuccessBodyAsync(
            await client.GetAsync($"feeds/proxy/packages/{id}?tab=versions&per=9999"));

        Assert.Contains("1 to 50 of 60 versions", page, StringComparison.Ordinal);
    }
}
