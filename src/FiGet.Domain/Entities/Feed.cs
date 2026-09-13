namespace FiGet.Domain.Entities;

/// <summary>A package feed. Names are case-insensitive; <see cref="NameLower"/> is the lookup key.</summary>
public sealed class Feed
{
    public int Key { get; set; }

    public required string Name { get; set; }

    public required string NameLower { get; set; }

    public FeedKind Kind { get; set; } = FeedKind.Curated;

    /// <summary>When true, every read endpoint of the feed works without credentials.</summary>
    public bool AnonymousRead { get; set; }

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
