using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FiGet.Application.Reports;

/// <summary>Which document a webhook receives, because receivers disagree about what a body may contain.</summary>
public enum ChangeWebhookFormat
{
    /// <summary>The report as data, for an automation runner, a script or a relay.</summary>
    Json,

    /// <summary>A short summary under both <c>content</c> and <c>text</c>, which Discord and Slack render as they are.</summary>
    Chat,

    /// <summary>An adaptive card, for a receiver that forwards the body to Teams as one.</summary>
    Teams,
}

/// <summary>
/// Writes the body a webhook is posted. Three shapes, not one with extra keys: a generic JSON document renders in none
/// of the chat platforms (Discord wants <c>content</c>, Slack wants <c>text</c>), and a receiver of the Teams kind
/// forwards the whole body as a card, so anything riding along would be handed to Teams as part of it.
/// </summary>
public static class ChangeReportBody
{
    /// <summary>Lines in a chat summary before it says how many more there are. A wall of text is not a notification.</summary>
    public const int ChatLines = 12;

    /// <summary>Rows in a card, for the same reason.</summary>
    public const int CardRows = 20;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Write(ChangeReport report, ChangeWebhookFormat format, string? target, DateTime generatedUtc)
    {
        ArgumentNullException.ThrowIfNull(report);
        var label = string.IsNullOrWhiteSpace(target) ? null : target!.Trim();
        return format switch
        {
            ChangeWebhookFormat.Chat => JsonSerializer.Serialize(
                new ChatBody(Summary(report), Summary(report), label),
                Json),
            ChangeWebhookFormat.Teams => JsonSerializer.Serialize(
                new TeamsBody("message", label, [new Attachment("application/vnd.microsoft.card.adaptive", Card(report))]),
                Json),
            _ => JsonSerializer.Serialize(
                new JsonBody(generatedUtc, label, Report.Of(report)),
                Json),
        };
    }

    /// <summary>
    /// One line per change, newest first, with the breaking ones marked. Plain text on purpose: it has to read the
    /// same in a chat client, a mail relay and a log line, and none of those agree on markup.
    /// </summary>
    public static string Summary(ChangeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var breaking = report.Changes.Count(c => c.Breaking);
        var head = report.Changes.Count == 0
            ? $"{report.Feed}: nothing changed since {report.FromUtc:yyyy-MM-dd}."
            : $"{report.Feed}: {report.Changes.Count} change{(report.Changes.Count == 1 ? "" : "s")} since {report.FromUtc:yyyy-MM-dd}"
              + (breaking == 0 ? "." : $", {breaking} of them breaking.");

        var text = new StringBuilder(head);
        foreach (var change in report.Changes.Take(ChatLines))
        {
            text.Append(CultureInfo.InvariantCulture, $"\n- {change.Id} {Move(change)}{(change.Breaking ? " (breaking)" : "")} [{Kind(change.Kind)}]");
        }

        if (report.Changes.Count > ChatLines)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n- and {report.Changes.Count - ChatLines} more.");
        }

        return text.ToString();
    }

    /// <summary>What a row says about the move, in the shape a person reads: from what, to what.</summary>
    private static string Move(PackageChange change) =>
        change.PreviousVersion is null ? change.Version : $"{change.PreviousVersion} -> {change.Version}";

    /// <summary>Written by hand rather than from the enum, so renaming a member cannot move what a receiver reads.</summary>
    private static string Kind(PackageChangeKind kind) => kind switch
    {
        PackageChangeKind.Pushed => "pushed",
        PackageChangeKind.Cached => "cached",
        _ => "upstream",
    };

    private static AdaptiveCard Card(ChangeReport report)
    {
        var blocks = new List<object>
        {
            new TextBlock("TextBlock", $"What's new in {report.Feed}", "Bolder", "Large", true, null),
            new TextBlock("TextBlock", $"Since {report.FromUtc:yyyy-MM-dd}", null, "Small", true, "Accent"),
        };

        foreach (var change in report.Changes.Take(CardRows))
        {
            blocks.Add(new TextBlock(
                "TextBlock",
                $"{change.Id} {Move(change)}{(change.Breaking ? " (breaking)" : "")} - {Kind(change.Kind)}",
                null,
                null,
                true,
                change.Breaking ? "Attention" : null));
        }

        if (report.Changes.Count == 0)
        {
            blocks.Add(new TextBlock("TextBlock", "Nothing changed.", null, null, true, null));
        }
        else if (report.Changes.Count > CardRows)
        {
            blocks.Add(new TextBlock("TextBlock", $"and {report.Changes.Count - CardRows} more", null, "Small", true, null));
        }

        return new AdaptiveCard("http://adaptivecards.io/schemas/adaptive-card.json", "AdaptiveCard", "1.5", blocks);
    }

    private sealed record JsonBody(DateTime GeneratedUtc, string? Target, Report Report);

    private sealed record Report(string Feed, DateTime From, DateTime To, bool Truncated, IReadOnlyList<Change> Changes)
    {
        public static Report Of(ChangeReport report) => new(
            report.Feed,
            report.FromUtc,
            report.ToUtc,
            report.StoppedAtLimit,
            [.. report.Changes.Select(Change.Of)]);
    }

    private sealed record Change(
        string Name,
        string Version,
        string? PreviousVersion,
        bool Breaking,
        DateTime Published,
        string Kind,
        string? Upstream,
        string? Authors,
        string? ReleaseNotes)
    {
        public static Change Of(PackageChange change) => new(
            change.Id,
            change.Version,
            change.PreviousVersion,
            change.Breaking,
            change.PublishedUtc,
            ChangeReportBody.Kind(change.Kind),
            change.Upstream.Length == 0 ? null : change.Upstream,
            change.Authors.Length == 0 ? null : change.Authors,
            change.ReleaseNotes.Length == 0 ? null : change.ReleaseNotes);
    }

    /// <summary>Discord reads <c>content</c>, Slack reads <c>text</c>; writing both costs nothing and serves both.</summary>
    private sealed record ChatBody(string Content, string Text, string? Target);

    private sealed record TeamsBody(string Type, string? Target, IReadOnlyList<Attachment> Attachments);

    private sealed record Attachment(string ContentType, AdaptiveCard Content);

    private sealed record AdaptiveCard(
        [property: JsonPropertyName("$schema")] string Schema,
        string Type,
        string Version,
        IReadOnlyList<object> Body);

    private sealed record TextBlock(string Type, string Text, string? Weight, string? Size, bool Wrap, string? Color);
}
