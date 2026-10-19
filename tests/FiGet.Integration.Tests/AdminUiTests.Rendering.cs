using System.Net;
using FiGet.Integration.Tests.Infrastructure;

namespace FiGet.Integration.Tests;

/// <summary>
/// Guards static rendering: no page ships the framework script or an interactive component, signed in or not.
///
/// This is asserted rather than assumed because nothing else would notice it changing. A circuit is server state
/// held for as long as somebody keeps the page open and pinned to one replica, so a single interactive component
/// would quietly bring back the need for session affinity behind a load balancer.
/// </summary>
public sealed partial class AdminUiTests
{
    /// <summary>The marker the framework emits for a prerendered interactive component.</summary>
    private const string InteractiveMarker = "<!--Blazor:";

    /// <summary>
    /// Matched without the extension on purpose. MapStaticAssets fingerprints served names, so the
    /// script is requested as <c>blazor.web.&lt;hash&gt;.js</c> and the literal "blazor.web.js" appears
    /// nowhere in the page. Asserting on the full filename does not fail loudly — it makes the negative
    /// assertion below pass no matter what the page contains, which is worse than having no test.
    /// </summary>
    private const string Framework = "_framework/blazor.web";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task No_page_ships_the_framework_signed_in_or_not(bool signedIn)
    {
        using var client = CreateBrowser();
        if (signedIn)
        {
            HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(client));
        }

        foreach (var path in signedIn ? new[] { "/", "/feeds/public", "/admin/feeds" } : ["/", "/feeds/public"])
        {
            var page = await HttpAssert.SuccessBodyAsync(await client.GetAsync(path));
            Assert.DoesNotContain(Framework, page, StringComparison.Ordinal);
            Assert.DoesNotContain(InteractiveMarker, page, StringComparison.Ordinal);
            Assert.DoesNotContain("components-reconnect-modal", page, StringComparison.Ordinal);
        }

        // Still a usable page: the plain GET search form, for an admin as much as for anyone.
        Assert.Contains("name=\"q\"", await HttpAssert.SuccessBodyAsync(await client.GetAsync("/feeds/public")), StringComparison.Ordinal);
    }

    /// <summary>
    /// The signed-in menu: the one door to everything that changes something, and markup that renders on
    /// every page. Asserted because nothing else would notice it going missing - the theme toggle sat
    /// broken in this same bar until somebody used it.
    ///
    /// The <c>&lt;summary&gt;</c> is part of the contract, not decoration. This bar is statically rendered
    /// even for a signed-in reader, so a click handler here would never run; the menu opens because it is
    /// a disclosure element. Replacing it with a scripted popover fails this test, which is the point.
    /// </summary>
    [Fact]
    public async Task The_signed_in_menu_carries_the_admin_links_and_is_hidden_from_a_stranger()
    {
        using var client = CreateBrowser();

        var anonymous = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/feeds/public"));
        Assert.DoesNotContain("fg-nav-dropdown", anonymous, StringComparison.Ordinal);
        Assert.DoesNotContain("Sign out", anonymous, StringComparison.Ordinal);

        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(client));
        var page = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/feeds/public"));

        Assert.Contains("fg-nav-dropdown", page, StringComparison.Ordinal);
        Assert.Contains("data-nav-menu", page, StringComparison.Ordinal);
        Assert.Contains("<summary>", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/admin/feeds\"", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/account/profile\"", page, StringComparison.Ordinal);
        Assert.Contains("Sign out", page, StringComparison.Ordinal);
        Assert.Contains("fg-nav-dropdown-version", page, StringComparison.Ordinal);

        // Nothing stamps a version here, and the SDK's default 1.0.0 counts as nothing said, so the menu
        // must admit that rather than announce a release nobody cut. Read out of the chip itself: the page
        // also lists package versions, and one of those may legitimately be 1.0.0.
        var chip = System.Text.RegularExpressions.Regex.Match(page, "<div class=\"fg-nav-dropdown-version\">(?<version>[^<]*)</div>");
        Assert.True(chip.Success, "The menu has no version chip.");
        Assert.Equal("version not set", chip.Groups["version"].Value.Trim());
    }

    /// <summary>
    /// The content policy allows no inline script, style attribute or event handler (FiGetApp.PageContentPolicy). A page
    /// that adds one still renders and passes every other test; only a browser notices, by quietly ignoring it. So the
    /// markup is read for them here, on the public pages and on every admin page that takes no parameter.
    /// </summary>
    [Fact]
    public async Task No_page_carries_inline_script_or_style()
    {
        var inline = new System.Text.RegularExpressions.Regex(
            "<script(?![^>]*\\bsrc=)[^>]*>|<style[\\s>]|\\sstyle=\"|\\son[a-z]+=\"",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        using var client = CreateBrowser();
        string[] anonymous = ["/", "/assets", "/feeds/public", "/account/login/local"];
        foreach (var path in anonymous)
        {
            var match = inline.Match(await HttpAssert.SuccessBodyAsync(await client.GetAsync(path)));
            Assert.False(match.Success, $"{path} carries '{match.Value}', which the content policy blocks.");
        }

        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(client));
        string[] signedIn =
        [
            .. anonymous[..3], "/account/profile", "/admin/feeds", "/admin/assets", "/admin/feeds/public", "/admin/users",
            "/admin/groups", "/admin/providers", "/admin/tokens", "/admin/changes", "/admin/audit", "/admin/storage",
            "/admin/system", "/admin/appearance",
        ];
        foreach (var path in signedIn)
        {
            var match = inline.Match(await HttpAssert.SuccessBodyAsync(await client.GetAsync(path)));
            Assert.False(match.Success, $"{path} (signed in) carries '{match.Value}', which the content policy blocks.");
        }
    }

    /// <summary>
    /// The admin area keeps the site chrome. AdminLayout nests inside MainLayout rather than replacing it,
    /// which is easy to lose: a layout that forgets its own @@layout silently drops the header, the theme
    /// toggle and the way back out, and every admin page loses them at once.
    /// </summary>
    [Fact]
    public async Task The_admin_area_keeps_the_site_chrome()
    {
        using var client = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await BrowserSignIn.SignInAsync(client));

        var page = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/admin/feeds"));

        Assert.Contains("fg-admin-nav", page, StringComparison.Ordinal);
        Assert.Contains("data-theme-toggle", page, StringComparison.Ordinal);
        Assert.Contains("fg-nav-dropdown", page, StringComparison.Ordinal);
    }
}
