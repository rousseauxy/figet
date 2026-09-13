using FiGet.Application.Accounts;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;

namespace FiGet.Web.Components.Pages.Admin;

/// <summary>
/// A page of one feed's or asset directory's settings. Each section has a page of its own - settings, upstreams,
/// retention, instructions, access, name and deletion - rather than one page holding all of them, so a save returns to a
/// short page about what was changed, and a page loads only what it shows.
///
/// The feed is looked up by the route's name, an alternate name included, and reads as not found unless the account
/// manages it, the page applies to its kind and, on an admin-only page, the account is an admin. Checked before any form
/// handler runs: every handler returns early without a feed.
/// </summary>
public abstract class FeedAdminPage : ComponentBase
{
    [Parameter]
    public string Name { get; set; } = "";

    [CascadingParameter]
    protected HttpContext HttpContext { get; set; } = default!;

    [Inject]
    protected IFeedStore Feeds { get; set; } = default!;

    [Inject]
    protected FeedAccessService Access { get; set; } = default!;

    /// <summary>The feed or directory this page is about; null when it does not exist for this account.</summary>
    protected Feed? Target { get; private set; }

    protected virtual bool AppliesToAssets => true;

    protected virtual bool AppliesToPackageFeeds => true;

    /// <summary>Renaming and deleting stay with admins: Manage is about a feed's settings and access, not whether it exists.</summary>
    protected virtual bool AdminOnly => false;

    protected bool IsAssets => Target?.Kind == FeedKind.Assets;

    protected string Noun => IsAssets ? "directory" : "feed";

    protected bool IsAdmin => HttpContext.User.IsInRole(FiGetApp.AdminRole);

    /// <summary>Where the buttons return to.</summary>
    protected string ReturnUrl => HttpContext.Request.Path + HttpContext.Request.QueryString;

    protected string PagePath(string page) => FeedAdminPaths.Page(Target!, page);

    /// <summary>Looks the feed up again and checks the account may be here. False, with a 404, when not.</summary>
    protected async Task<bool> LoadFeedAsync()
    {
        var feed = await Feeds.FindAsync(Name, HttpContext.RequestAborted);
        if (feed is not null
            && (!(feed.Kind == FeedKind.Assets ? AppliesToAssets : AppliesToPackageFeeds)
                || (AdminOnly && !IsAdmin)
                || await Access.LevelAsync(feed, FiGetApp.Actor(HttpContext.User), HttpContext.RequestAborted) < FeedAccessLevel.Manage))
        {
            feed = null;
        }

        Target = feed;
        if (feed is null)
        {
            HttpContext.Response.StatusCode = StatusCodes.Status404NotFound;
        }

        return feed is not null;
    }
}

/// <summary>The addresses of a feed's settings pages: under /admin/assets for a directory, /admin/feeds for a package feed.</summary>
public static class FeedAdminPaths
{
    public const string Upstreams = "upstreams";
    public const string Retention = "retention";
    public const string Instructions = "instructions";
    public const string Access = "access";
    public const string Naming = "name";

    public static string Settings(Feed feed) => Settings(feed, feed?.Name ?? "");

    /// <summary>The settings page under a given name: the new one, right after a rename.</summary>
    public static string Settings(Feed feed, string name)
    {
        ArgumentNullException.ThrowIfNull(feed);
        return (feed.Kind == FeedKind.Assets ? "/admin/assets/" : "/admin/feeds/") + Uri.EscapeDataString(name);
    }

    public static string Page(Feed feed, string page) => Settings(feed) + "/" + page;
}
