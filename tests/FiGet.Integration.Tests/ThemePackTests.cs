using FiGet.Integration.Tests.Infrastructure;

namespace FiGet.Integration.Tests;

/// <summary>
/// The packs actually shipped in <c>wwwroot/themes</c>, fetched through the endpoint that serves them.
///
/// <see cref="ThemeServiceTests"/> writes its own packs into a temporary directory, so it proves the
/// loader works and says nothing about the files this application ships. A pack is data, and a typo in it
/// fails the way theming defects tend to: no error, no log line, just a page wearing the built-in look
/// while someone wonders why the brand did not apply. The empty-path defect on 2026-09-12 was found in
/// production for exactly that reason.
/// </summary>
public sealed class ThemePackTests(SqliteServerFixture server) : IClassFixture<SqliteServerFixture>
{
    [Theory]
    [InlineData("graphite")]
    [InlineData("cobalt")]
    public async Task A_shipped_pack_compiles_to_guarded_css(string name)
    {
        using var client = server.CreateClient();

        var css = await HttpAssert.SuccessBodyAsync(await client.GetAsync($"/themes/{name}.css"));

        Assert.Contains(":root {", css, StringComparison.Ordinal);
        Assert.Contains("--accent:", css, StringComparison.Ordinal);

        // Both spellings of dark, and the media query guarded so an explicit light choice beats the
        // operating system. Without the :not() a reader who picks light on a dark machine stays dark.
        Assert.Contains("@media (prefers-color-scheme: dark) { :root:not([data-theme=\"light\"])", css, StringComparison.Ordinal);
        Assert.Contains("[data-theme=\"dark\"] {", css, StringComparison.Ordinal);
    }
    /// <summary>Each shipped pack's logo exists, is served as an image that runs nothing, and its page width is set.</summary>
    [Theory]
    [InlineData("graphite")]
    [InlineData("cobalt")]
    public async Task A_shipped_pack_serves_its_logo_safely(string name)
    {
        using var client = server.CreateClient();

        var logo = await client.GetAsync($"/themes/{name}/assets/{name}-logo.svg");
        HttpAssert.Status(System.Net.HttpStatusCode.OK, logo);
        Assert.Equal("image/svg+xml", logo.Content.Headers.ContentType?.MediaType);
        Assert.Contains("sandbox", logo.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        HttpAssert.Status(System.Net.HttpStatusCode.NotFound, await client.GetAsync($"/themes/{name}/assets/{name}.yaml"));

        var css = await HttpAssert.SuccessBodyAsync(await client.GetAsync($"/themes/{name}.css"));
        Assert.Contains("--page-width: 1440px", css, StringComparison.Ordinal);
    }
}
