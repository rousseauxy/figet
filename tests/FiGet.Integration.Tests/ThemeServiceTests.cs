using FiGet.Web.Theming;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace FiGet.Integration.Tests;

/// <summary>
/// The theme loader had no tests, and the first thing that went wrong with it went wrong in production:
/// an empty <c>FiGet:Theming:Path</c> resolved to the empty directory rather than to the default, so no
/// pack loaded and <c>/themes/{name}.css</c> answered 404 while the page still linked it.
/// </summary>
public sealed class ThemeServiceTests : IDisposable
{
    private const string Pack = """
        name: test
        label: Test pack
        description: A pack used by the tests.
        layout:
          radius: 3px
        tokens:
          light:
            accent: "#123456"
          dark:
            accent: "#654321"
        """;

    private readonly string root = Directory.CreateTempSubdirectory("figet-themes").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void A_pack_is_read_from_the_configured_directory()
    {
        File.WriteAllText(Path.Combine(root, "test.yaml"), Pack);

        var service = Service(path: root, webRoot: Path.Combine(root, "unused"));

        var pack = Assert.Single(service.Packs);
        Assert.Equal("test", pack.Name);
        Assert.Equal("Test pack", pack.Label);
        Assert.Contains("--accent: #123456", service.GetCss("test")!.Value.Css, StringComparison.Ordinal);
    }

    /// <summary>
    /// The regression. An empty value is how appsettings.json states a key without choosing one, so it
    /// has to mean the same as leaving the key out: themes under the web root.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void An_empty_configured_path_falls_back_to_the_web_root(string? configured)
    {
        var themes = Directory.CreateDirectory(Path.Combine(root, "themes")).FullName;
        File.WriteAllText(Path.Combine(themes, "test.yaml"), Pack);

        var service = Service(path: configured, webRoot: root);

        Assert.Single(service.Packs);
        Assert.NotNull(service.GetCss("test"));
    }

    /// <summary>
    /// Dark has to be emitted twice: the media query answers the reader's system preference, and the
    /// attribute answers an explicit choice and must be able to beat it.
    /// </summary>
    [Fact]
    public void A_pack_states_dark_for_both_the_media_query_and_the_attribute()
    {
        File.WriteAllText(Path.Combine(root, "test.yaml"), Pack);

        var css = Service(path: root, webRoot: root).GetCss("test")!.Value.Css;

        Assert.Contains("@media (prefers-color-scheme: dark)", css, StringComparison.Ordinal);
        Assert.Contains("[data-theme=\"dark\"]", css, StringComparison.Ordinal);
        Assert.Contains("--accent: #654321", css, StringComparison.Ordinal);
        // The layout section rides along with the light tokens.
        Assert.Contains("--r-1: 3px", css, StringComparison.Ordinal);
        // Balanced braces: the nested media block is closed by hand, so a stray one is plausible.
        Assert.Equal(css.Count(c => c == '{'), css.Count(c => c == '}'));
    }

    [Fact]
    public void A_directory_that_does_not_exist_is_not_an_error()
    {
        var service = Service(path: Path.Combine(root, "nothing-here"), webRoot: root);

        Assert.Empty(service.Packs);
        Assert.Null(service.GetCss("test"));
    }

    /// <summary>One unreadable pack must not take the others, or the server, down with it.</summary>
    [Fact]
    public void A_broken_pack_is_skipped_and_the_others_still_load()
    {
        File.WriteAllText(Path.Combine(root, "test.yaml"), Pack);
        File.WriteAllText(Path.Combine(root, "broken.yaml"), "name: broken\ntokens:\n  light:\n   - this is not a mapping\n");

        var service = Service(path: root, webRoot: root);

        var pack = Assert.Single(service.Packs);
        Assert.Equal("test", pack.Name);
    }

    /// <summary>
    /// A pack's logo is served only when it is the file the pack names, next to the pack: never another file in the
    /// directory, never a path out of it, and never a link to another site.
    /// </summary>
    [Fact]
    public void Only_the_logo_a_pack_names_is_served()
    {
        File.WriteAllText(Path.Combine(root, "brand.yaml"), """
            name: brand
            branding:
              titlePlain: Acme packages
              logo: acme.svg
              logoAlt: Acme
            """);
        File.WriteAllText(Path.Combine(root, "acme.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        File.WriteAllText(Path.Combine(root, "other.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");

        var service = Service(path: root, webRoot: Path.Combine(root, "unused"));

        var brand = service.GetBrand("brand");
        Assert.Equal("Acme packages", brand.Title);
        Assert.Equal("/themes/brand/assets/acme.svg", brand.LogoUrl);
        Assert.Equal("Acme", brand.LogoAlt);
        Assert.Equal("image/svg+xml", service.GetAsset("brand", "acme.svg")!.Value.ContentType);
        Assert.Null(service.GetAsset("brand", "other.svg"));
        Assert.Null(service.GetAsset("brand", "../brand.yaml"));
        Assert.Null(service.GetAsset("nobody", "acme.svg"));
        Assert.Equal(ThemeBrand.Default, service.GetBrand("nobody"));
    }

    [Theory]
    [InlineData("data:image/svg+xml;base64,PHN2Zy8+", true)]
    [InlineData("https://example.com/logo.svg", false)]
    [InlineData("../outside.svg", false)]
    [InlineData("logo.exe", false)]
    public void A_logo_is_a_file_next_to_the_pack_or_a_data_url(string logo, bool shown)
    {
        File.WriteAllText(Path.Combine(root, "brand.yaml"), $"""
            name: brand
            branding:
              logo: "{logo}"
              hideTitle: true
            """);

        var brand = Service(path: root, webRoot: Path.Combine(root, "unused")).GetBrand("brand");

        Assert.Equal(shown, brand.LogoUrl is not null);
        Assert.Equal(shown, brand.HideTitle);
    }

    private static ThemeService Service(string? path, string webRoot) =>
        new(new StubEnvironment(webRoot),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["FiGet:Theming:Path"] = path })
                .Build(),
            NullLogger<ThemeService>.Instance);

    private sealed class StubEnvironment(string webRoot) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = webRoot;

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

        public string ApplicationName { get; set; } = "FiGet.Integration.Tests";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();

        public string ContentRootPath { get; set; } = webRoot;

        public string EnvironmentName { get; set; } = "Test";
    }
}
