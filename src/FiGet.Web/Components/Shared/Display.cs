using System.Globalization;

namespace FiGet.Web.Components.Shared;

/// <summary>
/// Formatting shared by the package and version pages. Small, but it was written three times before it
/// was written once, and two of the copies had already started to differ.
/// </summary>
public static class Display
{
    /// <summary>An em dash, used everywhere a value is genuinely unknown rather than zero.</summary>
    public const string None = "—";

    public static string Size(long bytes) => bytes switch
    {
        >= 1024 * 1024 => (bytes / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        >= 1024 => (bytes / 1024d).ToString("0", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " B",
    };

    public static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>A UTC timestamp, or the empty marker when there is nothing to show.</summary>
    public static string Date(DateTime? value, string empty = None) =>
        value is null || value == default(DateTime)
            ? empty
            : value.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

    /// <summary>
    /// The URL only when it is one we are willing to render as a link. A package's own metadata is not
    /// to be trusted with the scheme: it arrives from whoever published it.
    /// </summary>
    public static string? Link(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "https" or "http"
            ? parsed.ToString()
            : null;
}
