namespace FiGet.Web.Theming;

/// <summary>
/// A declarative theme, loaded from a YAML file. Token keys map one to one onto the CSS custom
/// properties in <c>app.css</c>, so a pack only states what it changes and anything the tokens cannot
/// express goes in <see cref="CustomCss"/>. Drop a file in the themes directory and pick it in
/// configuration: no rebuild, no redeploy.
/// </summary>
public sealed class ThemePack
{
    /// <summary>File-name-safe identifier, also the URL segment: <c>/themes/{name}.css</c>.</summary>
    public string Name { get; set; } = "";

    /// <summary>What to show a person choosing a theme. Falls back to the name.</summary>
    public string? Label { get; set; }

    public string? Description { get; set; }

    public ThemeFonts? Fonts { get; set; }

    public ThemeLayout? Layout { get; set; }

    public ThemeTokens? Tokens { get; set; }

    /// <summary>Appended verbatim after the token blocks, for the rare rule tokens cannot express.</summary>
    public string? CustomCss { get; set; }
}

public sealed class ThemeFonts
{
    /// <summary>Body and interface font stack, emitted as <c>--font-ui</c>.</summary>
    public string? Ui { get; set; }

    /// <summary>Heading font stack, emitted as <c>--font-display</c>.</summary>
    public string? Display { get; set; }

    /// <summary>Monospace stack for versions, ids and code, emitted as <c>--font-mono</c>.</summary>
    public string? Mono { get; set; }

    /// <summary>
    /// Optional stylesheet to import for the fonts. Self-hosting is preferred: an import reaches out to
    /// another origin on every page load, which an air-gapped install cannot do.
    /// </summary>
    public string? FontUrl { get; set; }
}

public sealed class ThemeLayout
{
    /// <summary>Base corner radius, emitted as <c>--r-1</c>.</summary>
    public string? Radius { get; set; }

    /// <summary>Card and panel radius, emitted as <c>--r-2</c>.</summary>
    public string? RadiusLarge { get; set; }
}

public sealed class ThemeTokens
{
    /// <summary>Applied to <c>:root</c>. A key may be written with or without the leading dashes.</summary>
    public Dictionary<string, string>? Light { get; set; }

    /// <summary>Applied to <c>[data-theme="dark"]</c>.</summary>
    public Dictionary<string, string>? Dark { get; set; }
}
