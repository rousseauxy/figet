using System.Globalization;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Components;

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
        >= 1024L * 1024 * 1024 => (bytes / 1024d / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
        >= 1024 * 1024 => (bytes / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        >= 1024 => (bytes / 1024d).ToString("0", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " B",
    };

    /// <summary>What a feed is, in words rather than the enum name: "Assets" alone reads like a count.</summary>
    public static string Kind(FiGet.Domain.Entities.FeedKind kind) => kind switch
    {
        FiGet.Domain.Entities.FeedKind.Assets => "Asset directory",
        _ => kind.ToString(),
    };

    /// <summary>What a package feed is used for, as the create form and the settings page offer it.</summary>
    public static string Purpose(FiGet.Domain.Entities.FeedPurpose purpose) => purpose switch
    {
        FiGet.Domain.Entities.FeedPurpose.PowerShell => "PowerShell modules",
        FiGet.Domain.Entities.FeedPurpose.NuGet => "NuGet packages",
        FiGet.Domain.Entities.FeedPurpose.Chocolatey => "Chocolatey packages",
        _ => "Any client",
    };

    public static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// A package id that may break after its dots. Ids are long and unspaced -
    /// <c>Microsoft.Entra.CertificateBasedAuthentication</c> - so in a narrow column they broke at any
    /// character, mid-word: "Microsoft.Entra.A / pplications". A break opportunity after each dot lets the
    /// browser wrap at the natural seams, and fall back to breaking anywhere only when one segment is itself
    /// too long for the column.
    ///
    /// Returned as markup, which bypasses Razor's own encoding, so the id is encoded here first: it comes
    /// from whoever pushed the package and is never to be trusted as markup. Encoding never produces a dot,
    /// so a break inserted after encoding can never land inside an entity.
    /// </summary>
    public static MarkupString BreakableId(string? id) =>
        string.IsNullOrEmpty(id)
            ? new MarkupString(string.Empty)
            : new MarkupString(HtmlEncoder.Default.Encode(id).Replace(".", ".<wbr>", StringComparison.Ordinal));

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
