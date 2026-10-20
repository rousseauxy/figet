using FiGet.Application.Feeds;
using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using FiGet.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FiGet.Protocol.Management;

/// <summary>
/// Feeds and asset directories over HTTP, under <c>/api/management/feeds</c>: what a script needs to set up a server, or a
/// directory for a new team, without a person on the admin pages. FiGet's own; nothing of the server being replaced is
/// mirrored here. The rules are <see cref="FeedAdminService"/>'s, the same the pages use, and every change is recorded in
/// the audit log under the same action a page would record.
///
/// The caller is a service token with the admin scope (<see cref="ManagementAccess"/>). Upstreams, grants and retention
/// are not here yet: they are still set on the pages.
/// </summary>
public static class FeedManagementEndpoints
{
    public static IEndpointRouteBuilder MapFeedManagement(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/management/feeds");
        group.MapGet("", ListAsync);
        group.MapPost("", CreateAsync).DisableAntiforgery();
        group.MapGet("/{name}", GetAsync);
        group.MapMethods("/{name}", [HttpMethods.Patch], UpdateAsync).DisableAntiforgery();
        group.MapPut("/{name}/folder", SetFolderAsync).DisableAntiforgery();
        group.MapPost("/{name}/rename", RenameAsync).DisableAntiforgery();
        group.MapDelete("/{name}", DeleteAsync);
        return app;
    }

    /// <summary>A new feed or directory. Only the name is required; everything else starts as the create form starts it.</summary>
    public sealed record CreateRequest(
        string? Name,
        string? Kind,
        string? Purpose,
        bool? Gallery,
        bool? AnonymousRead,
        bool? AnonymousList,
        bool? AllowOverwrite,
        string? OnDelete,
        FolderRequest? Folder);

    /// <summary>A directory's content: a share on the mount by name, a folder inside it; an empty share for FiGet's own storage.</summary>
    public sealed record FolderRequest(string? Share, string? Inside, bool? Writes);

    /// <summary>A change of settings. What is left out keeps its value; <c>allowedNetworks: []</c> opens the feed to any address.</summary>
    public sealed record UpdateRequest(
        bool? AnonymousRead,
        bool? AnonymousList,
        bool? AllowOverwrite,
        string? OnDelete,
        bool? MergePushedIds,
        string? Purpose,
        int? ChartColor,
        List<string>? AllowedNetworks);

    public sealed record RenameRequest(string? Name, bool? KeepOldName);

    private sealed record FolderJson(string? Share, string? Inside, bool Writes, bool FromConfiguration);

    private sealed record FeedJson(
        string Name,
        string Kind,
        bool Proxy,
        string Purpose,
        bool AnonymousRead,
        bool? AnonymousList,
        bool? AllowOverwrite,
        string? OnDelete,
        bool? MergePushedIds,
        int? ChartColor,
        IReadOnlyList<string>? AllowedNetworks,
        FolderJson? Folder,
        IReadOnlyList<string> Upstreams,
        IReadOnlyList<string>? Aliases,
        string Url,
        DateTime Created);

    private static async Task<IResult> ListAsync(HttpContext http, IFeedStore feeds, IShareFolders shares, FeedAdminService admin, CancellationToken cancellationToken)
    {
        if (await AdminAsync(http, cancellationToken) is { } error)
        {
            return error;
        }

        var all = await feeds.ListAsync(cancellationToken);
        return Results.Json(all.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(f => Describe(http, f, shares, admin, aliases: null)).ToList(), ManagementAccess.Json);
    }

    private static async Task<IResult> GetAsync(HttpContext http, string name, IFeedStore feeds, IShareFolders shares, FeedAdminService admin, CancellationToken cancellationToken)
    {
        if (await AdminAsync(http, cancellationToken) is { } error)
        {
            return error;
        }

        return await feeds.FindAsync(name, cancellationToken) is { } feed
            ? Results.Json(await DescribeFullAsync(http, feed, feeds, shares, admin, cancellationToken), ManagementAccess.Json)
            : NotFound(name);
    }

    private static async Task<IResult> CreateAsync(HttpContext http, IFeedStore feeds, IShareFolders shares, FeedAdminService admin, AuditLog audit, CancellationToken cancellationToken)
    {
        if (await AdminAsync(http, cancellationToken) is { } error)
        {
            return error;
        }

        if (await ManagementAccess.ReadAsync<CreateRequest>(http, cancellationToken) is not { } request)
        {
            return Refuse(StatusCodes.Status400BadRequest, "Send a JSON object with at least a name; kind is \"package\" (the default) or \"directory\".");
        }

        bool isDirectory;
        switch (request.Kind?.Trim().ToLowerInvariant())
        {
            case null or "" or "package":
                isDirectory = false;
                break;
            case "directory":
                isDirectory = true;
                break;
            default:
                return Refuse(StatusCodes.Status400BadRequest, $"'{request.Kind}' is not a kind. Use package or directory.");
        }

        if (!TryPurpose(request.Purpose, out var purpose) || !TryOnDelete(request.OnDelete, out var onDelete))
        {
            return Refuse(StatusCodes.Status400BadRequest, "purpose is any, powershell, nuget or chocolatey; onDelete is unlist or delete.");
        }

        if (!isDirectory && request.Folder is not null)
        {
            return Refuse(StatusCodes.Status400BadRequest, "folder is for a directory: a package feed keeps its files in FiGet's storage.");
        }

        var outcome = await admin.CreateAsync(
            new FeedDraft
            {
                Name = request.Name ?? "",
                IsDirectory = isDirectory,
                Purpose = purpose ?? FeedPurpose.Any,
                AddGallery = request.Gallery ?? false,
                AnonymousRead = request.AnonymousRead ?? false,
                AnonymousList = request.AnonymousList ?? false,
                AllowOverwrite = request.AllowOverwrite ?? false,
                DeletionBehavior = onDelete ?? PackageDeletionBehavior.Unlist,
                Share = request.Folder?.Share,
                Inside = request.Folder?.Inside,
                FolderWrites = request.Folder?.Writes ?? false,
            },
            cancellationToken);

        switch (outcome.Status)
        {
            case FeedCreateStatus.InvalidName:
                return Refuse(StatusCodes.Status400BadRequest, "name: " + FeedAdminService.NameRule);
            case FeedCreateStatus.NameTaken:
                return Refuse(StatusCodes.Status409Conflict, $"The name '{request.Name?.Trim()}' is already taken by a feed or an asset directory, or is an alternate name of one.");
            case FeedCreateStatus.FolderNotFound:
                return Refuse(StatusCodes.Status400BadRequest, $"'{outcome.Detail}' is not a folder on the shares mount.");
        }

        audit.Record(http, "feed.create", outcome.Feed!.Name, outcome.Detail);
        var created = await DescribeFullAsync(http, outcome.Feed, feeds, shares, admin, cancellationToken);
        return Results.Json(created, ManagementAccess.Json, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> UpdateAsync(HttpContext http, string name, IFeedStore feeds, IShareFolders shares, FeedAdminService admin, AuditLog audit, CancellationToken cancellationToken)
    {
        if (await AdminAsync(http, cancellationToken) is { } error)
        {
            return error;
        }

        if (await feeds.FindAsync(name, cancellationToken) is not { } feed)
        {
            return NotFound(name);
        }

        if (await ManagementAccess.ReadAsync<UpdateRequest>(http, cancellationToken) is not { } request)
        {
            return Refuse(StatusCodes.Status400BadRequest, "Send a JSON object with the settings to change.");
        }

        if (!TryPurpose(request.Purpose, out var purpose) || !TryOnDelete(request.OnDelete, out var onDelete))
        {
            return Refuse(StatusCodes.Status400BadRequest, "purpose is any, powershell, nuget or chocolatey; onDelete is unlist or delete.");
        }

        if (request.ChartColor is < 0 or > FeedColors.Count)
        {
            return Refuse(StatusCodes.Status400BadRequest, $"chartColor is 1 to {FeedColors.Count}, or 0 for automatic.");
        }

        // The networks first: they are the one part that can be refused, and a refusal must leave everything as it was.
        if (request.AllowedNetworks is { } networks)
        {
            var changed = await admin.UpdateNetworksAsync(feed.Key, string.Join('\n', networks), cancellationToken);
            if (changed.Status == FeedChangeStatus.InvalidNetworks)
            {
                return Refuse(StatusCodes.Status400BadRequest, "allowedNetworks: " + changed.Detail);
            }

            if (changed.Status == FeedChangeStatus.Saved)
            {
                audit.Record(http, "feed.networks", feed.Name, changed.Detail);
            }
        }

        if (request is { AnonymousRead: null, AnonymousList: null, AllowOverwrite: null, MergePushedIds: null, ChartColor: null } && purpose is null && onDelete is null)
        {
            return await RefreshedAsync(http, feed.Name, feeds, shares, admin, cancellationToken);
        }

        var saved = await admin.UpdateSettingsAsync(
            feed.Key,
            new FeedSettingsChange
            {
                AnonymousRead = request.AnonymousRead,
                AnonymousList = request.AnonymousList,
                AllowOverwrite = request.AllowOverwrite,
                DeletionBehavior = onDelete,
                MergePushedIdsWithUpstreams = request.MergePushedIds,
                Purpose = purpose,
                ChartColor = request.ChartColor,
            },
            cancellationToken);

        if (!saved.Succeeded)
        {
            return NotFound(name);
        }

        audit.Record(http, "feed.settings", feed.Name, saved.Detail);
        return await RefreshedAsync(http, feed.Name, feeds, shares, admin, cancellationToken);
    }

    private static async Task<IResult> SetFolderAsync(HttpContext http, string name, IFeedStore feeds, IShareFolders shares, FeedAdminService admin, AuditLog audit, CancellationToken cancellationToken)
    {
        if (await AdminAsync(http, cancellationToken) is { } error)
        {
            return error;
        }

        if (await feeds.FindAsync(name, cancellationToken) is not { } feed)
        {
            return NotFound(name);
        }

        if (await ManagementAccess.ReadAsync<FolderRequest>(http, cancellationToken) is not { } request)
        {
            return Refuse(StatusCodes.Status400BadRequest, "Send a JSON object: share (empty for FiGet's own storage), and optionally inside and writes.");
        }

        var outcome = await admin.SetFolderAsync(feed.Key, request.Share, request.Inside, request.Writes ?? false, cancellationToken);
        switch (outcome.Status)
        {
            case FeedChangeStatus.NotFound:
                return NotFound(name);
            case FeedChangeStatus.FolderNotChosenHere:
                return Refuse(StatusCodes.Status409Conflict, feed.Kind == FeedKind.Assets
                    ? "This directory's content is not chosen here: no shares mount is configured, or the server configuration sets its folder."
                    : "Only an asset directory has a folder.");
            case FeedChangeStatus.FolderNotFound:
                return Refuse(StatusCodes.Status400BadRequest, outcome.Detail!);
            case FeedChangeStatus.Saved:
                audit.Record(http, "feed.folder", feed.Name, outcome.Detail);
                break;
        }

        return await RefreshedAsync(http, feed.Name, feeds, shares, admin, cancellationToken);
    }

    private static async Task<IResult> RenameAsync(HttpContext http, string name, IFeedStore feeds, IShareFolders shares, FeedAdminService admin, AuditLog audit, CancellationToken cancellationToken)
    {
        if (await AdminAsync(http, cancellationToken) is { } error)
        {
            return error;
        }

        if (await feeds.FindAsync(name, cancellationToken) is not { } feed)
        {
            return NotFound(name);
        }

        if (await ManagementAccess.ReadAsync<RenameRequest>(http, cancellationToken) is not { Name: not null } request)
        {
            return Refuse(StatusCodes.Status400BadRequest, "Send a JSON object: name, and optionally keepOldName (true keeps the old name working).");
        }

        var keepOldName = request.KeepOldName ?? false;
        var outcome = await admin.RenameAsync(feed.Key, request.Name, keepOldName, cancellationToken);
        switch (outcome)
        {
            case FeedNameChange.Invalid:
                return Refuse(StatusCodes.Status400BadRequest, "name: " + FeedAdminService.NameRule);
            case FeedNameChange.NameTaken:
                return Refuse(StatusCodes.Status409Conflict, $"The name '{request.Name.Trim()}' is already taken by a feed or an asset directory, or is an alternate name of one.");
            case FeedNameChange.NotFound:
                return NotFound(name);
            case FeedNameChange.Done:
                audit.Record(http, "feed.rename", request.Name.Trim(), $"from={feed.Name} keepOldName={keepOldName}");
                break;
        }

        // Unchanged is not an error: the feed is called what the caller wanted.
        return await RefreshedAsync(http, outcome == FeedNameChange.Done ? request.Name.Trim() : feed.Name, feeds, shares, admin, cancellationToken);
    }

    /// <summary>
    /// Deletes a feed or a directory and everything in it. The name has to be repeated in <c>?confirm=</c>, as it is typed on
    /// the page: a script that builds the address from a variable that came out empty or wrong deletes nothing.
    /// </summary>
    private static async Task<IResult> DeleteAsync(HttpContext http, string name, string? confirm, IFeedStore feeds, FeedAdminService admin, AuditLog audit, CancellationToken cancellationToken)
    {
        if (await AdminAsync(http, cancellationToken) is { } error)
        {
            return error;
        }

        if (await feeds.FindAsync(name, cancellationToken) is not { } feed)
        {
            return NotFound(name);
        }

        if (!string.Equals(confirm?.Trim(), feed.Name, StringComparison.OrdinalIgnoreCase))
        {
            return Refuse(StatusCodes.Status400BadRequest, $"Repeat the name to delete it: ?confirm={feed.Name}. Everything it holds goes with it.");
        }

        audit.Record(http, "feed.delete", feed.Name);
        await admin.DeleteAsync(feed.Key, cancellationToken);
        return Results.NoContent();
    }

    private static Task<IResult?> AdminAsync(HttpContext http, CancellationToken cancellationToken) =>
        ManagementAccess.AdminAsync(http, "feed management", "Managing feeds takes a service token with the admin scope.", cancellationToken);

    private static IResult Refuse(int status, string message) => ManagementAccess.Refuse(status, message);

    private static IResult NotFound(string name) =>
        Refuse(StatusCodes.Status404NotFound, $"There is no feed or asset directory named '{name}'.");

    private static async Task<IResult> RefreshedAsync(HttpContext http, string name, IFeedStore feeds, IShareFolders shares, FeedAdminService admin, CancellationToken cancellationToken) =>
        await feeds.FindAsync(name, cancellationToken) is { } feed
            ? Results.Json(await DescribeFullAsync(http, feed, feeds, shares, admin, cancellationToken), ManagementAccess.Json)
            : NotFound(name);

    private static async Task<FeedJson> DescribeFullAsync(HttpContext http, Feed feed, IFeedStore feeds, IShareFolders shares, FeedAdminService admin, CancellationToken cancellationToken) =>
        Describe(http, feed, shares, admin, [.. (await feeds.ListAliasesAsync(feed.Key, cancellationToken)).Select(a => a.Name)]);

    /// <summary>
    /// A feed as a script sees it. Settings that mean nothing to the kind are left out rather than shown with a value that does
    /// nothing. A folder is its share and the folder inside it, never a path on the server.
    /// </summary>
    private static FeedJson Describe(HttpContext http, Feed feed, IShareFolders shares, FeedAdminService admin, IReadOnlyList<string>? aliases)
    {
        var isDirectory = feed.Kind == FeedKind.Assets;
        FolderJson? folder = null;
        if (isDirectory && feed.IsFolderBacked)
        {
            var described = shares.Describe(feed.FolderRoot);
            folder = new FolderJson(described?.Share, described?.Inside, feed.FolderWritable, admin.IsFolderConfigured(feed.Name));
        }

        return new FeedJson(
            feed.Name,
            isDirectory ? "directory" : "package",
            feed.Kind == FeedKind.Proxy,
            feed.Purpose.ToString().ToLowerInvariant(),
            feed.AnonymousRead,
            isDirectory ? feed.AnonymousList : null,
            isDirectory ? null : feed.AllowOverwrite,
            isDirectory ? null : feed.DeletionBehavior == PackageDeletionBehavior.HardDelete ? "delete" : "unlist",
            isDirectory ? null : feed.MergePushedIdsWithUpstreams,
            feed.ChartColor,
            feed.AllowedNetworks is null ? null : feed.AllowedNetworks.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            folder,
            [.. feed.Upstreams.Select(u => u.Name)],
            aliases,
            isDirectory
                ? $"{PublicUrls.Base(http)}/endpoints/{Uri.EscapeDataString(feed.Name)}/content/"
                : $"{PublicUrls.Feed(http, feed.Name)}/v3/index.json",
            DateTime.SpecifyKind(feed.CreatedUtc, DateTimeKind.Utc));
    }

    private static bool TryPurpose(string? text, out FeedPurpose? purpose)
    {
        purpose = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (Enum.TryParse<FeedPurpose>(text.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) && !int.TryParse(text, out _))
        {
            purpose = parsed;
            return true;
        }

        return false;
    }

    private static bool TryOnDelete(string? text, out PackageDeletionBehavior? onDelete)
    {
        onDelete = text?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "unlist" => PackageDeletionBehavior.Unlist,
            "delete" => PackageDeletionBehavior.HardDelete,
            _ => (PackageDeletionBehavior)(-1),
        };

        return onDelete is null || Enum.IsDefined(onDelete.Value);
    }
}
