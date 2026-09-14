namespace FiGet.Domain.Entities;

/// <summary>What a counted use of a feed was.</summary>
public enum FeedUsageKind : byte
{
    /// <summary>A package or a file downloaded, including one served from the cache of a proxy feed.</summary>
    Download = 1,

    /// <summary>A search, or a client looking up one package id's versions: once per search, not once per page.</summary>
    Search = 2,
}

/// <summary>
/// How often a feed was used in one hour, for the usage graph on the feed lists. Anonymous by construction: a feed, an
/// hour, a kind and a number - no account, no address, no package id - so it cannot become a record of who used what.
/// </summary>
public sealed class FeedUsage
{
    public int FeedKey { get; set; }

    /// <summary>The start of the hour, UTC.</summary>
    public DateTime HourUtc { get; set; }

    public FeedUsageKind Kind { get; set; }

    public long Count { get; set; }
}

/// <summary>
/// The colour a feed is drawn in: one of a fixed set, which the stylesheet defines for light and dark alike as
/// <c>--fg-series-1</c> to <c>--fg-series-8</c>. A fixed set rather than any colour, so every line stays readable on both
/// backgrounds and next to the others.
/// </summary>
public static class FeedColors
{
    public const int Count = 8;

    /// <summary>The colour chosen for the feed, or one picked from its key, so it stays the same without anyone choosing.</summary>
    public static int Of(Feed feed)
    {
        ArgumentNullException.ThrowIfNull(feed);
        return feed.ChartColor is >= 1 and <= Count ? feed.ChartColor.Value : ((feed.Key - 1) % Count + Count) % Count + 1;
    }
}
