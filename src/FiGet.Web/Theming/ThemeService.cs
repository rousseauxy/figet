using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FiGet.Web.Theming;

/// <summary>One theme, as a chooser needs to know it.</summary>
public sealed record ThemeSummary(string Name, string Label, string? Description);

public interface IThemeService
{
    /// <summary>Every theme found, by label.</summary>
    IReadOnlyList<ThemeSummary> Packs { get; }

    /// <summary>The compiled stylesheet and its entity tag, or null when no such theme exists.</summary>
    (string Css, string ETag)? GetCss(string name);

    ThemePack? GetPack(string name);

    /// <summary>Re-reads the themes directory, so a pack can be added or edited without a restart.</summary>
    void Reload();
}

/// <summary>
/// Loads theme packs (<c>*.json</c>) and compiles each into a small stylesheet of custom-property
/// overrides, served after <c>app.css</c>. The base stylesheet defines every token with a default, so a
/// pack that sets three colours is a valid theme and everything it does not mention still looks right.
/// </summary>
public sealed class ThemeService : IThemeService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string directory;
    private readonly ILogger<ThemeService> logger;
    private readonly ConcurrentDictionary<string, (ThemePack Pack, string Css, string ETag)> packs =
        new(StringComparer.OrdinalIgnoreCase);

    public ThemeService(IWebHostEnvironment environment, IConfiguration configuration, ILogger<ThemeService> logger)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(configuration);
        this.logger = logger;
        directory = configuration["FiGet:Theming:Path"]
            ?? Path.Combine(environment.WebRootPath ?? "wwwroot", "themes");
        Reload();
    }

    public IReadOnlyList<ThemeSummary> Packs =>
        packs.Values
            .Select(entry => new ThemeSummary(entry.Pack.Name, entry.Pack.Label ?? entry.Pack.Name, entry.Pack.Description))
            .OrderBy(summary => summary.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public (string Css, string ETag)? GetCss(string name) =>
        packs.TryGetValue(name, out var entry) ? (entry.Css, entry.ETag) : null;

    public ThemePack? GetPack(string name) => packs.TryGetValue(name, out var entry) ? entry.Pack : null;

    public void Reload()
    {
        packs.Clear();
        if (!Directory.Exists(directory))
        {
            logger.LogInformation("No themes directory at {Directory}; the built-in theme is the only one.", directory);
            return;
        }

        foreach (var file in Directory.GetFiles(directory, "*.json"))
        {
            try
            {
                var pack = JsonSerializer.Deserialize<ThemePack>(File.ReadAllText(file), Json);
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
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
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
        css.Append("/* theme: ").Append(pack.Name).AppendLine(" — generated, do not edit */");

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

        Block(css, ":root", light);
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

    private static void Block(StringBuilder css, string selector, IReadOnlyDictionary<string, string>? tokens)
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

        css.AppendLine("}");
    }
}
