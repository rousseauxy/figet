using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace FiGet.Web.Theming;

/// <summary>One theme, as a chooser needs to know it.</summary>
public sealed record ThemeSummary(string Name, string Label, string? Description);

/// <summary>What the top bar shows for a theme: the name, and the logo's address when the pack has one.</summary>
public sealed record ThemeBrand(string Title, string? LogoUrl, string LogoAlt, bool HideTitle)
{
    public static readonly ThemeBrand Default = new("FiGet", null, "FiGet", HideTitle: false);
}

public interface IThemeService
{
    /// <summary>Every theme found, by label.</summary>
    IReadOnlyList<ThemeSummary> Packs { get; }

    /// <summary>The compiled stylesheet and its entity tag, or null when no such theme exists.</summary>
    (string Css, string ETag)? GetCss(string name);

    ThemePack? GetPack(string name);

    /// <summary>The brand of a theme, or the default one when there is no such theme or it names no brand.</summary>
    ThemeBrand GetBrand(string? name);

    /// <summary>
    /// A file a pack's branding names, to serve: its path and content type. Null for anything else - a file the pack does
    /// not name is never served, whatever sits in the directory.
    /// </summary>
    (string Path, string ContentType)? GetAsset(string name, string file);

    /// <summary>Re-reads the themes directory, so a pack can be added or edited without a restart.</summary>
    void Reload();
}

/// <summary>
/// Loads theme packs (<c>*.yaml</c>) and compiles each into a small stylesheet of custom-property
/// overrides, served after <c>app.css</c>. The base stylesheet defines every token with a default, so a
/// pack that sets three colours is a valid theme and everything it does not mention still looks right.
///
/// YAML rather than JSON, and the same token names as the sibling application, so one pack can be
/// dropped into either without editing: a brand is defined once, not once per product.
/// </summary>
public sealed class ThemeService : IThemeService
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        // A pack written for the sibling application carries keys this one has no use for, such as its
        // branding block. Those are ignored rather than refused, which is what makes packs portable.
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly string directory;
    private readonly ILogger<ThemeService> logger;
    private readonly ConcurrentDictionary<string, (ThemePack Pack, string Css, string ETag)> packs =
        new(StringComparer.OrdinalIgnoreCase);

    public ThemeService(IWebHostEnvironment environment, IConfiguration configuration, ILogger<ThemeService> logger)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(configuration);
        this.logger = logger;
        // Empty means "not set". Every key in appsettings.json is listed with an empty default so the
        // shape is discoverable, and `??` only falls back on null — which resolved this to "" in the
        // container and quietly loaded no packs at all, while every local run, with the key absent
        // entirely, worked. An empty string is the normal way to say "leave it alone" here.
        var configured = configuration["FiGet:Theming:Path"];
        directory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(environment.WebRootPath ?? "wwwroot", "themes")
            : configured;
        Reload();
    }

    public IReadOnlyList<ThemeSummary> Packs =>
        [.. packs.Values
            .Select(entry => new ThemeSummary(entry.Pack.Name, entry.Pack.Label ?? entry.Pack.Name, entry.Pack.Description))
            .OrderBy(summary => summary.Label, StringComparer.OrdinalIgnoreCase)];

    public (string Css, string ETag)? GetCss(string name) =>
        packs.TryGetValue(name, out var entry) ? (entry.Css, entry.ETag) : null;

    public ThemePack? GetPack(string name) => packs.TryGetValue(name, out var entry) ? entry.Pack : null;

    /// <summary>The largest logo served. A logo is kilobytes; anything near this is a mistake worth refusing.</summary>
    public const long MaxAssetBytes = 1024 * 1024;

    private static readonly Dictionary<string, string> AssetTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".webp"] = "image/webp",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
    };

    public ThemeBrand GetBrand(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !packs.TryGetValue(name, out var entry) || entry.Pack.Branding is not { } branding)
        {
            return ThemeBrand.Default;
        }

        var title = string.IsNullOrWhiteSpace(branding.TitlePlain) ? ThemeBrand.Default.Title : branding.TitlePlain.Trim();
        string? logo = null;
        var value = branding.Logo?.Trim();
        if (!string.IsNullOrEmpty(value))
        {
            if (value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                logo = value;
            }
            else if (IsPlainFileName(value) && AssetTypes.ContainsKey(Path.GetExtension(value)))
            {
                logo = $"/themes/{Uri.EscapeDataString(entry.Pack.Name)}/assets/{Uri.EscapeDataString(value)}";
            }
            else
            {
                logger.LogWarning("Theme {Theme} names a logo that is neither a file next to the pack nor a data: URL; it is not shown.", entry.Pack.Name);
            }
        }

        var alt = string.IsNullOrWhiteSpace(branding.LogoAlt) ? title : branding.LogoAlt.Trim();
        return new ThemeBrand(title, logo, alt, branding.HideTitle && logo is not null);
    }

    public (string Path, string ContentType)? GetAsset(string name, string file)
    {
        if (!packs.TryGetValue(name, out var entry)
            || entry.Pack.Branding?.Logo?.Trim() is not { } logo
            || !string.Equals(logo, file, StringComparison.OrdinalIgnoreCase)
            || !IsPlainFileName(logo)
            || !AssetTypes.TryGetValue(Path.GetExtension(logo), out var type))
        {
            return null;
        }

        var path = Path.Combine(directory, logo);
        var info = new FileInfo(path);
        return info.Exists && info.Length <= MaxAssetBytes ? (path, type) : null;
    }

    /// <summary>A name in the pack's own directory: no separators, no parent references, nothing a path could escape through.</summary>
    private static bool IsPlainFileName(string value) =>
        value.Length is > 0 and <= 128
        && value.IndexOfAny(['/', '\\', ':']) < 0
        && value != "." && value != ".."
        && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    public void Reload()
    {
        packs.Clear();
        if (!Directory.Exists(directory))
        {
            logger.LogInformation("No themes directory at {Directory}; the built-in theme is the only one.", directory);
            return;
        }

        foreach (var file in Directory.GetFiles(directory, "*.yaml").Concat(Directory.GetFiles(directory, "*.yml")))
        {
            try
            {
                var pack = Yaml.Deserialize<ThemePack>(File.ReadAllText(file));
                if (string.IsNullOrWhiteSpace(pack?.Name))
                {
                    logger.LogWarning("Theme {File} has no name and was skipped.", Path.GetFileName(file));
                    continue;
                }

                var css = Compile(pack);
                var tag = "\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(css)))[..16] + "\"";
                packs[pack.Name] = (pack, css, tag);
                logger.LogInformation("Theme loaded: {Theme} from {File}.", pack.Name, Path.GetFileName(file));
            }
            catch (Exception ex) when (ex is YamlException or IOException or UnauthorizedAccessException)
            {
                // One broken pack must not stop the others, and never the server.
                logger.LogError(ex, "Theme {File} could not be read.", Path.GetFileName(file));
            }
        }
    }

    /// <summary>Turns a pack into the stylesheet: fonts and radii ride along with the light tokens.</summary>
    private static string Compile(ThemePack pack)
    {
        var css = new StringBuilder();
        css.Append("/* theme: ").Append(pack.Name).AppendLine(" — generated from YAML, do not edit */");

        if (!string.IsNullOrWhiteSpace(pack.Fonts?.FontUrl))
        {
            css.Append("@import url('").Append(pack.Fonts.FontUrl).AppendLine("');");
        }

        var light = new Dictionary<string, string>(pack.Tokens?.Light ?? [], StringComparer.OrdinalIgnoreCase);
        Add(light, "font-ui", pack.Fonts?.Ui);
        Add(light, "font-display", pack.Fonts?.Display);
        Add(light, "font-mono", pack.Fonts?.Mono);
        Add(light, "r-1", pack.Layout?.Radius);
        Add(light, "r-2", pack.Layout?.RadiusLarge);
        Add(light, "page-width", pack.Layout?.PageWidth);

        Block(css, ":root", light);

        // Both selectors, in the order the base stylesheet uses them: the media query answers the
        // reader's system preference, the attribute answers an explicit choice and must win.
        Block(css, "@media (prefers-color-scheme: dark) { :root:not([data-theme=\"light\"])", pack.Tokens?.Dark, closeMedia: true);
        Block(css, "[data-theme=\"dark\"]", pack.Tokens?.Dark);

        if (!string.IsNullOrWhiteSpace(pack.CustomCss))
        {
            css.AppendLine().AppendLine(pack.CustomCss);
        }

        return css.ToString();
    }

    private static void Add(Dictionary<string, string> tokens, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            tokens[name] = value;
        }
    }

    private static void Block(StringBuilder css, string selector, IReadOnlyDictionary<string, string>? tokens, bool closeMedia = false)
    {
        if (tokens is not { Count: > 0 })
        {
            return;
        }

        css.Append(selector).AppendLine(" {");
        foreach (var (key, value) in tokens)
        {
            var name = key.StartsWith("--", StringComparison.Ordinal) ? key : "--" + key;
            css.Append("  ").Append(name).Append(": ").Append(value).AppendLine(";");
        }

        css.AppendLine(closeMedia ? "} }" : "}");
    }
}
