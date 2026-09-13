namespace FiGet.Domain.Entities;

/// <summary>What someone may do with one feed or asset directory. Ordered: each level includes the ones before it.</summary>
public enum FeedAccessLevel
{
    None = 0,

    /// <summary>List and download.</summary>
    Read = 1,

    /// <summary>Push, upload, pull, unlist, relist, and delete versions or files.</summary>
    Publish = 2,

    /// <summary>The feed's settings, its upstreams, and who has access to it.</summary>
    Manage = 3,
}

/// <summary>A named set of accounts that permissions can be granted to together.</summary>
public sealed class Group
{
    public int Key { get; set; }

    public required string Name { get; set; }

    public required string NameLower { get; set; }

    public string Description { get; set; } = "";

    public DateTime CreatedUtc { get; set; }
}

public sealed class GroupMember
{
    public int GroupKey { get; set; }

    public int UserKey { get; set; }
}

/// <summary>A level on one feed, granted to exactly one of an account or a group.</summary>
public sealed class FeedPermission
{
    public int Key { get; set; }

    public int FeedKey { get; set; }

    public int? UserKey { get; set; }

    public int? GroupKey { get; set; }

    public FeedAccessLevel Level { get; set; }
}
