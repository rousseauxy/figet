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
