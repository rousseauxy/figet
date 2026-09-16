using System.Text.RegularExpressions;
using FiGet.Web.Components.Shared;

namespace FiGet.Integration.Tests;

/// <summary>
/// How an instant reaches a page. It needs no server - it sits here only because the unit tests deliberately cannot see
/// FiGet.Web - and it guards the two halves the browser depends on: a machine-readable instant, and readable text that
/// stands on its own when no script runs.
/// </summary>
public sealed partial class DisplayTimeTests
{
    [Fact]
    public void A_time_carries_the_instant_a_browser_can_read_and_the_utc_a_reader_can()
    {
        var html = Display.Date(new DateTime(2026, 9, 16, 12, 39, 0, DateTimeKind.Utc)).Value;

        Assert.Equal("<time class=\"fg-time\" datetime=\"2026-09-16T12:39:00Z\">2026-09-16 12:39 UTC</time>", html);
    }

    /// <summary>
    /// A value read back from either database arrives Unspecified, not Utc. Taken at face value the attribute would lose
    /// its Z, and every browser would then read it as the reader's own local time - shifting each timestamp by the
    /// reader's offset, silently and in the plausible direction.
    /// </summary>
    [Fact]
    public void An_instant_without_a_kind_is_still_written_as_utc()
    {
        var html = Display.Date(new DateTime(2026, 9, 16, 12, 39, 0, DateTimeKind.Unspecified)).Value;

        Assert.Contains("datetime=\"2026-09-16T12:39:00Z\"", html, StringComparison.Ordinal);
    }

    /// <summary>Nothing to show is text, not an empty element a script would try to rewrite.</summary>
    [Theory]
    [InlineData(null)]
    public void Nothing_to_show_is_the_empty_marker(DateTime? value)
    {
        Assert.Equal(Display.None, Display.Date(value).Value);
        Assert.Equal("not since it was added", Display.Date(value, "not since it was added").Value);
    }

    /// <summary>The marker is written by callers, so it is encoded: it reaches the page as text and never as markup.</summary>
    [Fact]
    public void An_empty_marker_is_encoded()
    {
        Assert.DoesNotContain("<b>", Display.Date(null, "<b>never</b>").Value, StringComparison.Ordinal);
    }

    /// <summary>Every rendered time must be one the script can find and parse; this is the contract between the two.</summary>
    [Fact]
    public void The_shape_matches_what_the_script_looks_for()
    {
        var html = Display.Date(DateTime.UtcNow).Value;

        Assert.Matches(TimeElement(), html);
    }

    [GeneratedRegex("""^<time class="fg-time" datetime="\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z">""")]
    private static partial Regex TimeElement();
}
