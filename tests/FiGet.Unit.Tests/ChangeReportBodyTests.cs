using System.Text.Json.Nodes;
using FiGet.Application.Reports;

namespace FiGet.Unit.Tests;

/// <summary>The three documents a webhook may be sent, and what each receiver needs to find in them.</summary>
public sealed class ChangeReportBodyTests
{
    private static readonly DateTime Generated = new(2026, 9, 16, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_json_body_carries_the_report_as_data()
    {
        var body = JsonNode.Parse(Write(ChangeWebhookFormat.Json, "intune"))!;

        Assert.Equal("intune", (string?)body["target"]);
        Assert.Equal("modules", (string?)body["report"]!["feed"]);
        var change = body["report"]!["changes"]!.AsArray()[0]!;
        Assert.Equal("PnP.PowerShell", (string?)change["name"]);
        Assert.Equal("3.4.1", (string?)change["version"]);
        Assert.Equal("3.3.0", (string?)change["previousVersion"]);
        Assert.True((bool?)change["breaking"]);
        Assert.Equal("upstream", (string?)change["kind"]);

        // Absent rather than empty, so a reader need not tell "" from "nothing to say".
        Assert.Null(change["releaseNotes"]);
    }

    /// <summary>
    /// Discord reads <c>content</c> and Slack reads <c>text</c>. Both are written, which is why one format serves
    /// both without a relay; a test says so because it looks like duplication otherwise.
    /// </summary>
    [Fact]
    public void The_chat_body_says_the_same_thing_in_both_keys()
    {
        var body = JsonNode.Parse(Write(ChangeWebhookFormat.Chat, "alerts"))!;

        var content = (string?)body["content"];
        Assert.Equal(content, (string?)body["text"]);
        Assert.Contains("PnP.PowerShell 3.3.0 -> 3.4.1 (breaking)", content, StringComparison.Ordinal);
        Assert.Contains("modules: 2 changes", content, StringComparison.Ordinal);
        Assert.Equal("alerts", (string?)body["target"]);
    }

    /// <summary>
    /// A receiver of this kind forwards the whole body to Teams as the card, after removing the target. Anything else
    /// in the document would be handed to Teams as part of the card, so the body carries nothing else - which is a
    /// property worth a test, because adding a field here looks harmless.
    /// </summary>
    [Fact]
    public void The_teams_body_carries_the_card_and_nothing_else()
    {
        var body = JsonNode.Parse(Write(ChangeWebhookFormat.Teams, "sys-automation-scripts"))!.AsObject();

        Assert.Equal(["type", "target", "attachments"], body.Select(p => p.Key).ToArray());
        Assert.Equal("message", (string?)body["type"]);
        var card = body["attachments"]!.AsArray()[0]!;
        Assert.Equal("application/vnd.microsoft.card.adaptive", (string?)card["contentType"]);
        Assert.Equal("AdaptiveCard", (string?)card["content"]!["type"]);
        Assert.Contains(
            card["content"]!["body"]!.AsArray(),
            block => ((string?)block!["text"])?.Contains("PnP.PowerShell", StringComparison.Ordinal) == true
                && (string?)block["color"] == "Attention");
    }

    /// <summary>No target set means no target key: the right body for a receiver with one channel and no dispatcher.</summary>
    [Fact]
    public void A_body_without_a_target_does_not_carry_the_key()
    {
        foreach (var format in Enum.GetValues<ChangeWebhookFormat>())
        {
            var body = JsonNode.Parse(Write(format, null))!.AsObject();

            Assert.False(body.ContainsKey("target"), $"The {format} body carried a target nobody set.");
        }
    }

    [Fact]
    public void A_quiet_period_is_one_line_rather_than_an_empty_table()
    {
        var empty = new ChangeReport("modules", Generated.AddDays(-7), Generated, [], false);

        Assert.Equal("modules: nothing changed since 2026-09-09.", ChangeReportBody.Summary(empty));
    }

    /// <summary>A busy week is a notification, not a wall: the rest are counted rather than listed.</summary>
    [Fact]
    public void A_long_report_is_cut_short_and_says_how_much_is_left()
    {
        var many = Enumerable.Range(0, ChangeReportBody.ChatLines + 5)
            .Select(i => new PackageChange($"Pkg{i}", "2.0.0", "1.0.0", false, Generated, "", "", PackageChangeKind.Pushed, ""))
            .ToList();

        var summary = ChangeReportBody.Summary(new ChangeReport("modules", Generated.AddDays(-1), Generated, many, false));

        Assert.Equal(ChangeReportBody.ChatLines + 2, summary.Split('\n').Length);
        Assert.Contains("and 5 more.", summary, StringComparison.Ordinal);
    }

    private static string Write(ChangeWebhookFormat format, string? target) =>
        ChangeReportBody.Write(Report(), format, target, Generated);

    private static ChangeReport Report() => new(
        "modules",
        Generated.AddDays(-7),
        Generated,
        [
            new PackageChange("PnP.PowerShell", "3.4.1", "3.3.0", true, Generated.AddHours(-2), "", "PnP", PackageChangeKind.Upstream, "gallery"),
            new PackageChange("Contoso.Deploy", "1.2.0", "1.1.0", false, Generated.AddHours(-5), "Fixes the installer.", "Platform", PackageChangeKind.Pushed, ""),
        ],
        false);
}
