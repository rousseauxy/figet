namespace FiGet.Domain.Entities;

/// <summary>
/// Another name a feed answers to: usually the name it had before it was renamed, kept so every client registered with the
/// old URL goes on working until it is updated. Names are one set shared by feeds, asset directories and their alternate
/// names, so a name is never ambiguous.
/// </summary>
public sealed class FeedAlias
{
    public int Key { get; set; }

    public int FeedKey { get; set; }

    public required string Name { get; set; }

    public required string NameLower { get; set; }

    public DateTime CreatedUtc { get; set; }

    /// <summary>
    /// When a client last reached the feed by this name, to the hour (noted at most once an hour per calling address). Null:
    /// not since the name was added. What tells an admin the name can go.
    /// </summary>
    public DateTime? LastUsedUtc { get; set; }
}
