using FiGet.Domain.Entities;

namespace FiGet.Application.Ports;

/// <summary>A group as a list shows it.</summary>
public sealed record GroupSummary(int Key, string Name, string Description, int Members);

public interface IGroupStore
{
    Task<IReadOnlyList<GroupSummary>> ListAsync(CancellationToken cancellationToken);

    Task<Group?> FindAsync(int key, CancellationToken cancellationToken);

    /// <summary>Adds the group. False when the name is taken.</summary>
    Task<bool> AddAsync(Group group, CancellationToken cancellationToken);

    /// <summary>Renames and re-describes. False when it no longer exists or the new name is taken.</summary>
    Task<bool> UpdateAsync(int key, string name, string description, CancellationToken cancellationToken);

    /// <summary>Deletes the group with its memberships and every permission granted to it.</summary>
    Task<bool> DeleteAsync(int key, CancellationToken cancellationToken);

    /// <summary>The group's members, ordered by user name.</summary>
    Task<IReadOnlyList<User>> MembersAsync(int key, CancellationToken cancellationToken);

    /// <summary>False when the account is already a member or either does not exist.</summary>
    Task<bool> AddMemberAsync(int groupKey, int userKey, CancellationToken cancellationToken);

    Task<bool> RemoveMemberAsync(int groupKey, int userKey, CancellationToken cancellationToken);

    /// <summary>The groups an account belongs to, ordered by name.</summary>
    Task<IReadOnlyList<Group>> GroupsOfAsync(int userKey, CancellationToken cancellationToken);
}

/// <summary>One grant on a feed, with the name of whoever it is for.</summary>
public sealed record FeedGrant(int Key, int FeedKey, int? UserKey, int? GroupKey, string Name, FeedAccessLevel Level);

public interface IFeedPermissionStore
{
    /// <summary>
    /// The highest level granted on the feed to the account, directly or through any of its groups; None when there is no
    /// grant. Roles and anonymous read are not this store's to know.
    /// </summary>
    Task<FeedAccessLevel> GrantedAsync(int feedKey, int userKey, CancellationToken cancellationToken);

    /// <summary>The same for every feed the account has any grant on, keyed by feed.</summary>
    Task<IReadOnlyDictionary<int, FeedAccessLevel>> GrantedOnAllAsync(int userKey, CancellationToken cancellationToken);

    /// <summary>The grants on one feed, accounts first, then groups, each by name.</summary>
    Task<IReadOnlyList<FeedGrant>> ListAsync(int feedKey, CancellationToken cancellationToken);

    /// <summary>Grants a level to exactly one of an account or a group, replacing any level it had; None removes it.</summary>
    Task SetAsync(int feedKey, int? userKey, int? groupKey, FeedAccessLevel level, CancellationToken cancellationToken);

    /// <summary>Removes one grant of this feed. False when it is not a grant of this feed.</summary>
    Task<bool> RemoveAsync(int feedKey, int grantKey, CancellationToken cancellationToken);
}
