namespace FiGet.Core.Entities;

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

    public DateTime CreatedUtc { get; set; }
}

public enum FeedKind
{
    Curated,
    Proxy,
}

public enum PackageDeletionBehavior
{
    Unlist,
    HardDelete,
}
