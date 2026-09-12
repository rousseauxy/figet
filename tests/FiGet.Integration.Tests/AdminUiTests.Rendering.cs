using System.Net;
using FiGet.Integration.Tests.Infrastructure;

namespace FiGet.Integration.Tests;

/// <summary>
/// Guards the rendering split: the public read-only view is statically rendered and ships no framework,
/// and only a signed-in reader gets an interactive component.
///
/// This is asserted rather than assumed because nothing else would notice it changing. A circuit is
/// server state held for as long as somebody keeps the page open, and the public surface is reachable
/// without credentials — so "anonymous pages download no framework" is a property worth failing a build
/// over, not a detail of how a component happens to be declared today.
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

    [Fact]
    public async Task The_anonymous_view_ships_no_framework()
    {
        using var client = CreateBrowser();

        var page = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/feeds/public"));

        Assert.DoesNotContain(Framework, page, StringComparison.Ordinal);
        Assert.DoesNotContain(InteractiveMarker, page, StringComparison.Ordinal);

        // Still a usable page. Asserted on the plain GET form's field name rather than on the table,
        // because whether this feed holds any packages depends on what the other tests in the shared
        // fixture have pushed by now — and the grid uses the same placeholder text, so that would not
        // have told the two views apart anyway.
        Assert.Contains("name=\"q\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Signing_in_brings_the_interactive_grid()
    {
        using var client = CreateBrowser();
        HttpAssert.Status(HttpStatusCode.Redirect, await SignInAsync(client, FiGetServerFixture.AdminToken));

        var page = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/feeds/public"));

        Assert.Contains(Framework, page, StringComparison.Ordinal);
        // The component prerendered rather than failing quietly and leaving the page bare.
        Assert.Contains(InteractiveMarker, page, StringComparison.Ordinal);
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

        HttpAssert.Status(HttpStatusCode.Redirect, await SignInAsync(client, FiGetServerFixture.AdminToken));
        var page = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/feeds/public"));

        Assert.Contains("fg-nav-dropdown", page, StringComparison.Ordinal);
        Assert.Contains("data-nav-menu", page, StringComparison.Ordinal);
        Assert.Contains("<summary>", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/admin/feeds\"", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/admin/tokens\"", page, StringComparison.Ordinal);
        Assert.Contains("Sign out", page, StringComparison.Ordinal);
        Assert.Contains("fg-nav-dropdown-version", page, StringComparison.Ordinal);

        // Nothing stamps a version here, and the SDK's default 1.0.0 counts as nothing said, so the menu
        // must admit that rather than announce a release nobody cut.
        Assert.Contains("version not set", page, StringComparison.Ordinal);
        Assert.DoesNotContain(">1.0.0<", page, StringComparison.Ordinal);
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
        HttpAssert.Status(HttpStatusCode.Redirect, await SignInAsync(client, FiGetServerFixture.AdminToken));

        var page = await HttpAssert.SuccessBodyAsync(await client.GetAsync("/admin/feeds"));

        Assert.Contains("fg-admin-nav", page, StringComparison.Ordinal);
        Assert.Contains("data-theme-toggle", page, StringComparison.Ordinal);
        Assert.Contains("fg-nav-dropdown", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reconnect dialog exists only where a circuit does. It is not merely cosmetic: without it a
    /// dropped circuit leaves a page that looks alive and ignores every click.
    /// </summary>
    [Fact]
    public async Task The_reconnect_dialog_is_only_rendered_for_a_signed_in_reader()
    {
        using var client = CreateBrowser();
        Assert.DoesNotContain(
            "components-reconnect-modal",
            await HttpAssert.SuccessBodyAsync(await client.GetAsync("/feeds/public")),
            StringComparison.Ordinal);

        HttpAssert.Status(HttpStatusCode.Redirect, await SignInAsync(client, FiGetServerFixture.AdminToken));
        Assert.Contains(
            "components-reconnect-modal",
            await HttpAssert.SuccessBodyAsync(await client.GetAsync("/feeds/public")),
            StringComparison.Ordinal);
    }
}
