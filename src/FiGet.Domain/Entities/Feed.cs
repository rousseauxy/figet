namespace FiGet.Domain.Entities;

/// <summary>A package feed. Names are case-insensitive; <see cref="NameLower"/> is the lookup key.</summary>
public sealed class Feed
{
    public int Key { get; set; }

    public required string Name { get; set; }

    public required string NameLower { get; set; }

    public FeedKind Kind { get; set; } = FeedKind.Curated;

    /// <summary>When true, every read endpoint of the feed works without credentials. On an asset directory: downloads.</summary>
    public bool AnonymousRead { get; set; }

    /// <summary>
    /// Asset directories only: when true, folders can be listed, exported and browsed without credentials. A consumer that
    /// knows its paths needs only <see cref="AnonymousRead"/>; with this off, a folder shows nothing to a stranger and a
    /// wrong path is the same 404 as a right one, so nothing can be discovered.
    /// </summary>
    public bool AnonymousList { get; set; }

    /// <summary>
    /// Asset directories only: when set, the directory's content is this folder on the server - a mounted share - read
    /// as it is, with no copy and no row per file. Comes from configuration (<c>FiGet:Feeds:N:Folder</c>), never from a
    /// page, because the operator provides the mount. Null: files are stored by FiGet under random ids.
    /// </summary>
    public string? FolderRoot { get; set; }

    /// <summary>
    /// Folder-backed directories only: whether uploads, folders and deletes through FiGet act on the folder. Off by
    /// default; the share's own permissions decide who writes, and a second way in needs its own reason.
    /// </summary>
    public bool FolderWritable { get; set; }

    /// <summary>Whether the files come from a folder on the server rather than from FiGet's own storage.</summary>
    public bool IsFolderBacked => !string.IsNullOrWhiteSpace(FolderRoot);

    /// <summary>When true, pushing an existing id and version replaces it instead of answering 409.</summary>
    public bool AllowOverwrite { get; set; }

    public PackageDeletionBehavior DeletionBehavior { get; set; } = PackageDeletionBehavior.Unlist;

    /// <summary>
    /// False, the default: once a version of an id has been pushed to this feed, the feed serves that id only from
    /// what is held here and stops asking its upstreams about it. Without that, a module published here and an
    /// unrelated module of the same name on a gallery are merged into one version list, and whichever has the
    /// higher version becomes "latest" - so an install could quietly pull somebody else's package.
    ///
    /// Stored as the opt-out so the safe behaviour is also the column's default: a boolean whose default is true
    /// cannot be saved as false through EF, which treats the CLR default as "not set".
    /// </summary>
    public bool MergePushedIdsWithUpstreams { get; set; }

    /// <summary>The colour of the feed's dot and line on the usage graph, 1 to <see cref="FeedColors.Count"/>. Null: picked from the key.</summary>
    public int? ChartColor { get; set; }

    /// <summary>
    /// Retention for pushed packages: the newest this many stable versions of each package are kept, the rest removed as
    /// the feed's <see cref="DeletionBehavior"/> says. Null: no limit.
    /// </summary>
    public int? RetainStableVersions { get; set; }

    /// <summary>The same for prerelease versions, counted separately. Null: no limit; 0 keeps none but the latest.</summary>
    public int? RetainPrereleaseVersions { get; set; }

    /// <summary>Count the retained versions per major version, so a 1.x line keeps its own newest releases beside 2.x.</summary>
    public bool RetainPerMajorVersion { get; set; }

    /// <summary>A version downloaded within this many days is kept whatever the counts say. Null: downloads do not matter.</summary>
    public int? RetainIfUsedWithinDays { get; set; }

    /// <summary>
    /// Cache pruning on a proxy feed: a cached copy nobody has downloaded for this many days is deleted, file and all. The
    /// upstream still has it, so the next request caches it again. Null: cached copies are kept for ever.
    /// </summary>
    public int? PruneCachedAfterDays { get; set; }

    /// <summary>
    /// The address this feed's clients reach, when it is not the server's public address - an internal host name, a
    /// load balancer. Used in the URLs and commands the pages show; protocol answers keep the public base URL. Null: the
    /// public base URL.
    /// </summary>
    public string? ClientBaseUrl { get; set; }

    /// <summary>Install commands on package pages (see <c>InstructionTemplates</c>). Null: the default.</summary>
    public string? PackageInstructions { get; set; }

    /// <summary>Commands to connect a client, on the feed's page. Null: the default.</summary>
    public string? FeedInstructions { get; set; }

    /// <summary>Download commands on an asset directory's page. Null: the default.</summary>
    public string? FileInstructions { get; set; }

    public DateTime CreatedUtc { get; set; }

    /// <summary>Upstreams of a proxy feed, in the order they are queried. Empty on a curated feed.</summary>
    public List<FeedUpstream> Upstreams { get; set; } = [];
}

public enum FeedKind
{
    Curated,
    Proxy,

    /// <summary>
    /// An asset directory: files by path, served by plain GET, no packages. A feed kind rather than a
    /// separate object because the server being replaced models it that way too, and because tokens,
    /// anonymous read and deletion then apply to it without a second implementation of each. It is
    /// never a package feed: the NuGet endpoints refuse it, and the asset endpoints refuse every other kind.
    /// </summary>
    Assets,
}

public enum PackageDeletionBehavior
{
    Unlist,
    HardDelete,
}
