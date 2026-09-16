using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.AspNetCore.Components;

namespace FiGet.Web.Components.Shared;

/// <summary>
/// Formatting shared by the package and version pages. Small, but it was written three times before it
/// was written once, and two of the copies had already started to differ.
/// </summary>
public static class Display
{
    /// <summary>Escapes what markup would read and nothing else: the rest is text and should stay legible in the source.</summary>
    private static readonly HtmlEncoder Text = HtmlEncoder.Create(UnicodeRanges.All);

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
    /// How often something happens, in the largest whole unit that fits: "6 hours", "90 minutes", "2 days". A schedule
    /// nobody can read at a glance is a schedule nobody checks.
    /// </summary>
    public static string Every(TimeSpan interval) => interval switch
    {
        { TotalDays: >= 1 } when interval.TotalDays == Math.Floor(interval.TotalDays) => Plural(interval.TotalDays, "day"),
        { TotalHours: >= 1 } when interval.TotalHours == Math.Floor(interval.TotalHours) => Plural(interval.TotalHours, "hour"),
        { TotalMinutes: >= 1 } when interval.TotalMinutes == Math.Floor(interval.TotalMinutes) => Plural(interval.TotalMinutes, "minute"),
        _ => interval.ToString("g", CultureInfo.InvariantCulture),
    };

    private static string Plural(double value, string unit) =>
        value.ToString("0", CultureInfo.InvariantCulture) + " " + unit + (value == 1 ? "" : "s");

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

    /// <summary>
    /// A timestamp, written as UTC and marked up so the browser can show it in the reader's own zone.
    ///
    /// The element carries the instant in <c>datetime</c>, which is what the script rewrites from and what a copy or a
    /// screen reader gets; the text between the tags is the same instant in UTC, which is what anyone without that
    /// script sees. So the page never depends on the script being there, and never on the server's time zone either -
    /// two instances in different regions render the same page, and the reader's browser decides what it says.
    /// </summary>
    public static MarkupString Date(DateTime? value, string empty = None)
    {
        if (value is null || value == default(DateTime))
        {
            // Encoded, because the marker is a caller's string; through an encoder that leaves text alone and escapes
            // only what markup would read, so an em dash stays an em dash instead of arriving as a numeric entity.
            return new MarkupString(Text.Encode(empty));
        }

        var utc = DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
        var instant = utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var shown = utc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
        return new MarkupString($"<time class=\"fg-time\" datetime=\"{instant}\">{shown}</time>");
    }

    /// <summary>
    /// The URL only when it is one we are willing to render as a link. A package's own metadata is not
    /// to be trusted with the scheme: it arrives from whoever published it.
    /// </summary>
    public static string? Link(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "https" or "http"
            ? parsed.ToString()
            : null;
}
