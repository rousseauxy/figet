using System.Globalization;
using FiGet.Application.Ports;
using FiGet.Domain.Feeds;
using FiGet.Domain.Entities;

namespace FiGet.Application.Feeds;

/// <summary>
/// Which feeds the host's configuration names (<c>FiGet:Feeds</c>). Bound by the host, like <c>ShareFolderSettings</c>, so
/// the rules below do not read configuration themselves.
/// </summary>
public sealed class ConfiguredFeedSettings
{
    /// <summary>Every name configuration creates on start when no feed or alternate name has it.</summary>
    public IReadOnlySet<string> Names { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Directories whose folder configuration sets: the start re-applies it, so a choice made elsewhere would not last.</summary>
    public IReadOnlySet<string> WithFolder { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>What a new feed or asset directory is asked to be. A directory ignores the package settings, a feed the folder.</summary>
public sealed record FeedDraft
{
    public required string Name { get; init; }

    /// <summary>An asset directory rather than a package feed.</summary>
    public bool IsDirectory { get; init; }

    public FeedPurpose Purpose { get; init; }

    /// <summary>Package feeds: whether the purpose's public gallery becomes the feed's upstream, which makes it a proxy feed.</summary>
    public bool AddGallery { get; init; }

    public bool AnonymousRead { get; init; }

    /// <summary>Directories: whether folders can be listed without credentials.</summary>
    public bool AnonymousList { get; init; }

    public bool AllowOverwrite { get; init; }

    public PackageDeletionBehavior DeletionBehavior { get; init; } = PackageDeletionBehavior.Unlist;

    /// <summary>Directories: the share it serves, by name; empty or null for FiGet's own storage.</summary>
    public string? Share { get; init; }

    /// <summary>Directories: a folder inside the share, segments separated by slashes.</summary>
    public string? Inside { get; init; }

    /// <summary>Directories on a share: whether uploads and deletes through FiGet act on the folder.</summary>
    public bool FolderWrites { get; init; }
}

public enum FeedCreateStatus
{
    Created,

    /// <summary>Not a valid feed name (<see cref="FeedNames.IsValid"/>).</summary>
    InvalidName,

    /// <summary>A feed, a directory or an alternate name of one already has the name.</summary>
    NameTaken,

    /// <summary>The share or the folder inside it is not on the shares mount right now.</summary>
    FolderNotFound,
}

/// <summary>
/// The outcome of <see cref="FeedAdminService.CreateAsync"/>. <see cref="Detail"/> is the line the audit log records when it
/// was created, and the folder asked for when that was not found.
/// </summary>
public sealed record FeedCreation(FeedCreateStatus Status, Feed? Feed = null, string? Detail = null);

/// <summary>A feed's settings; null leaves a value as it is.</summary>
public sealed record FeedSettingsChange
{
    public bool? AnonymousRead { get; init; }

    public bool? AnonymousList { get; init; }

    public bool? AllowOverwrite { get; init; }

    public PackageDeletionBehavior? DeletionBehavior { get; init; }

    public bool? MergePushedIdsWithUpstreams { get; init; }

    public FeedPurpose? Purpose { get; init; }

    /// <summary>1 to <see cref="FeedColors.Count"/>; 0 for automatic.</summary>
    public int? ChartColor { get; init; }
}

public enum FeedChangeStatus
{
    Saved,

    /// <summary>The values were already what was asked.</summary>
    Unchanged,

    NotFound,

    /// <summary>The allowed-networks list is not addresses and CIDR ranges, or is too long; the detail says which.</summary>
    InvalidNetworks,

    /// <summary>The share or the folder inside it is not on the shares mount right now.</summary>
    FolderNotFound,

    /// <summary>No shares mount is configured, the target is not a directory, or configuration sets its folder.</summary>
    FolderNotChosenHere,
}

/// <summary>The outcome of a change to a feed, with what went wrong or the line the audit log records.</summary>
public sealed record FeedChange(FeedChangeStatus Status, string? Detail = null)
{
    public bool Succeeded => Status is FeedChangeStatus.Saved or FeedChangeStatus.Unchanged;
}

/// <summary>
/// Creating, configuring, renaming and deleting feeds and asset directories. These rules lived in the admin pages, so a
/// script could not make a directory without a person; the pages and the management API now both come here, and neither
/// decides anything of its own. Recording to the audit log stays with the caller, which knows who asked; the line to
/// record comes back with the outcome, so both write the same one.
/// </summary>
public sealed class FeedAdminService(
    IFeedStore feeds,
    IShareFolders shares,
    IPackageStorage packageStorage,
    IAssetStorage assetStorage,
    ConfiguredFeedSettings configured,
    TimeProvider time)
{
    /// <summary>What an administrator is told about a name that is not valid.</summary>
    public const string NameRule = "Use letters, digits, dot, dash or underscore, starting with a letter or digit (at most 64).";

    /// <summary>
    /// Creates a package feed or an asset directory. A package feed is curated, or a proxy when the purpose's gallery is
    /// added with it: added here rather than afterwards, so a feed meant to proxy one is never, even for a moment, an empty
    /// curated feed its clients are already pointed at. Settings that mean nothing to the kind are not carried over.
    /// </summary>
    public async Task<FeedCreation> CreateAsync(FeedDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var name = draft.Name?.Trim() ?? "";
        if (!FeedNames.IsValid(name))
        {
            return new FeedCreation(FeedCreateStatus.InvalidName);
        }

        string? folderRoot = null;
        var (share, inside, chosen) = Folder(draft.Share, draft.Inside);
        if (draft.IsDirectory && share.Length > 0)
        {
            // The share is a name, and the resolver is the only way from it to a path: anything that is not on the list right
            // now - a path, a name with a separator, a folder that is gone - creates nothing.
            folderRoot = shares.Resolve(share, inside);
            if (folderRoot is null)
            {
                return new FeedCreation(FeedCreateStatus.FolderNotFound, Detail: chosen);
            }
        }

        var folderWrites = folderRoot is not null && draft.FolderWrites;
        var purpose = draft.IsDirectory || !Enum.IsDefined(draft.Purpose) ? FeedPurpose.Any : draft.Purpose;
        var gallery = !draft.IsDirectory && draft.AddGallery ? FeedUpstream.KnownFor(purpose) : null;
        var kind = draft.IsDirectory ? FeedKind.Assets : gallery is null ? FeedKind.Curated : FeedKind.Proxy;
        var deletion = draft.IsDirectory || !Enum.IsDefined(draft.DeletionBehavior) ? PackageDeletionBehavior.Unlist : draft.DeletionBehavior;

        var feed = new Feed
        {
            Name = name,
            NameLower = name.ToLowerInvariant(),
            Kind = kind,
            Purpose = purpose,
            AnonymousRead = draft.AnonymousRead,
            AnonymousList = draft.IsDirectory && draft.AnonymousList,
            FolderRoot = folderRoot,
            FolderWritable = folderWrites,
            AllowOverwrite = !draft.IsDirectory && draft.AllowOverwrite,
            DeletionBehavior = deletion,
            CreatedUtc = time.GetUtcNow().UtcDateTime,
            Upstreams = gallery is null ? [] : [new FeedUpstream { Name = gallery.Name, Url = gallery.Url, Kind = gallery.Kind }],
        };

        if (!await feeds.CreateAsync(feed, cancellationToken))
        {
            return new FeedCreation(FeedCreateStatus.NameTaken);
        }

        var detail = $"kind={kind} purpose={purpose} upstream={gallery?.Name ?? "none"} anonymousRead={feed.AnonymousRead} anonymousList={feed.AnonymousList} allowOverwrite={feed.AllowOverwrite} onDelete={deletion}"
            + (draft.IsDirectory ? $" folder={(folderRoot is null ? "none" : chosen)} folderWrites={folderWrites}" : "");
        return new FeedCreation(FeedCreateStatus.Created, await feeds.FindAsync(name, cancellationToken) ?? feed, detail);
    }

    /// <summary>
    /// Changes a feed's settings; what is left null keeps its value. Recorded with the values, not just that something
    /// changed: "who loosened this feed" is the question a settings audit is asked.
    /// </summary>
    public async Task<FeedChange> UpdateSettingsAsync(int feedKey, FeedSettingsChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (await FindAsync(feedKey, cancellationToken) is not { } feed)
        {
            return new FeedChange(FeedChangeStatus.NotFound);
        }

        var isDirectory = feed.Kind == FeedKind.Assets;
        var anonymousRead = change.AnonymousRead ?? feed.AnonymousRead;
        var anonymousList = change.AnonymousList ?? feed.AnonymousList;
        var allowOverwrite = change.AllowOverwrite ?? feed.AllowOverwrite;
        var deletion = change.DeletionBehavior is { } asked && Enum.IsDefined(asked) ? asked : feed.DeletionBehavior;
        var merge = change.MergePushedIdsWithUpstreams ?? feed.MergePushedIdsWithUpstreams;
        var purpose = isDirectory ? FeedPurpose.Any : change.Purpose is { } wanted && Enum.IsDefined(wanted) ? wanted : feed.Purpose;
        var color = change.ChartColor is { } chosen ? (chosen is >= 1 and <= FeedColors.Count ? chosen : null) : feed.ChartColor;

        if (!await feeds.UpdateSettingsAsync(feed.Key, anonymousRead, anonymousList, allowOverwrite, deletion, merge, color, purpose, cancellationToken))
        {
            return new FeedChange(FeedChangeStatus.NotFound);
        }

        return new FeedChange(
            FeedChangeStatus.Saved,
            $"anonymousRead={anonymousRead} anonymousList={anonymousList} allowOverwrite={allowOverwrite} onDelete={deletion} mergePushedIds={merge} purpose={purpose} chartColor={(color is { } c ? c.ToString(CultureInfo.InvariantCulture) : "automatic")}");
    }

    /// <summary>The addresses a feed may be reached from, as text: one address or CIDR range per line or comma; empty for any.</summary>
    public async Task<FeedChange> UpdateNetworksAsync(int feedKey, string? text, CancellationToken cancellationToken)
    {
        if ((text ?? "").Length > FeedNetworks.MaxLength)
        {
            return new FeedChange(FeedChangeStatus.InvalidNetworks, $"The list is longer than {FeedNetworks.MaxLength} characters.");
        }

        if (!FeedNetworks.TryParse(text, out _, out var invalid))
        {
            return new FeedChange(FeedChangeStatus.InvalidNetworks, $"'{invalid}' is not an address or a range in CIDR notation (10.0.0.0/8; a range starts at its own boundary).");
        }

        var normalised = FeedNetworks.Normalise(text);
        return await feeds.UpdateAllowedNetworksAsync(feedKey, normalised, cancellationToken)
            ? new FeedChange(FeedChangeStatus.Saved, $"networks={(normalised is null ? "any" : normalised.Replace('\n', ' '))}")
            : new FeedChange(FeedChangeStatus.NotFound);
    }

    /// <summary>Whether a directory's content may be chosen through FiGet: a shares mount exists and configuration does not set it.</summary>
    public bool CanChooseFolder(Feed feed)
    {
        ArgumentNullException.ThrowIfNull(feed);
        return feed.Kind == FeedKind.Assets && shares.IsConfigured && !configured.WithFolder.Contains(feed.Name);
    }

    /// <summary>Whether configuration sets the folder of a directory of this name, on every start.</summary>
    public bool IsFolderConfigured(string name) => configured.WithFolder.Contains(name);

    /// <summary>Whether configuration names this, which recreates a feed of that name on start when nothing else has it.</summary>
    public bool IsConfigured(string name) => configured.Names.Contains(name);

    /// <summary>Points a directory at a share on the mount, a folder inside one, or back at FiGet's own storage (empty share).</summary>
    public async Task<FeedChange> SetFolderAsync(int feedKey, string? share, string? inside, bool writes, CancellationToken cancellationToken)
    {
        if (await FindAsync(feedKey, cancellationToken) is not { } feed)
        {
            return new FeedChange(FeedChangeStatus.NotFound);
        }

        if (!CanChooseFolder(feed))
        {
            return new FeedChange(FeedChangeStatus.FolderNotChosenHere, "The content of this directory is not chosen here.");
        }

        var (name, folder, chosen) = Folder(share, inside);
        string? root = null;
        if (name.Length > 0 && (root = shares.Resolve(name, folder)) is null)
        {
            return new FeedChange(FeedChangeStatus.FolderNotFound, $"'{chosen}' is not a folder on the shares mount.");
        }

        var folderWrites = root is not null && writes;
        return await feeds.UpdateFolderAsync(feed.Key, root, folderWrites, cancellationToken)
            ? new FeedChange(FeedChangeStatus.Saved, $"folder={(root is null ? "none" : chosen)} folderWrites={folderWrites}")
            : new FeedChange(FeedChangeStatus.Unchanged);
    }

    /// <summary>
    /// Renames a feed. With <paramref name="keepOldName"/> the old name stays as an alternate name, so clients using it keep
    /// working. The audit detail is the old name.
    /// </summary>
    public async Task<FeedNameChange> RenameAsync(int feedKey, string? name, bool keepOldName, CancellationToken cancellationToken)
    {
        var trimmed = name?.Trim() ?? "";
        return FeedNames.IsValid(trimmed)
            ? await feeds.RenameAsync(feedKey, trimmed, keepOldName, time.GetUtcNow().UtcDateTime, cancellationToken)
            : FeedNameChange.Invalid;
    }

    /// <summary>
    /// Deletes a feed or a directory with everything it holds. Files first: a row without its file is visible and fixable,
    /// an orphaned file is neither. False when it no longer exists.
    /// </summary>
    public async Task<bool> DeleteAsync(int feedKey, CancellationToken cancellationToken)
    {
        if (await FindAsync(feedKey, cancellationToken) is not { } feed)
        {
            return false;
        }

        await packageStorage.DeleteFeedAsync(feed.Key, cancellationToken);
        await assetStorage.DeleteFeedAsync(feed.Key, cancellationToken);
        return await feeds.DeleteAsync(feed.Key, cancellationToken);
    }

    private async Task<Feed?> FindAsync(int feedKey, CancellationToken cancellationToken) =>
        (await feeds.ListAsync(cancellationToken)).FirstOrDefault(f => f.Key == feedKey);

    /// <summary>A share and a folder inside it as posted, trimmed, and how the pair reads in a message.</summary>
    private static (string Share, string Inside, string Chosen) Folder(string? share, string? inside)
    {
        var name = (share ?? "").Trim();
        var folder = (inside ?? "").Trim().Trim('/', '\\');
        return (name, folder, folder.Length == 0 ? name : name + "/" + folder);
    }
}
