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
}
