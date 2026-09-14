namespace FiGet.Domain.Entities;

/// <summary>
/// One row per name a feed or asset directory answers to: its own name, and every alternate name a rename left behind.
/// The table exists for its primary key. Feeds and alternate names live in two tables, and no index across two tables
/// can stop an admin renaming one feed while another creates a feed of that name in the same instant; every write of a
/// name goes through here in the same transaction, so the database refuses the second one.
/// </summary>
public sealed class FeedName
{
    public required string NameLower { get; set; }

    public int FeedKey { get; set; }

    /// <summary>True for a name kept from before a rename; false for the feed's own name.</summary>
    public bool IsAlias { get; set; }
}
