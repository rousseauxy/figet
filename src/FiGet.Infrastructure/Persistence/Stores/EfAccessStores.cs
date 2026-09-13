using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FiGet.Infrastructure.Persistence.Stores;

public sealed class EfGroupStore(FiGetDbContext db) : IGroupStore
{
    public async Task<IReadOnlyList<GroupSummary>> ListAsync(CancellationToken cancellationToken) =>
        await db.Groups
            .AsNoTracking()
            .OrderBy(g => g.NameLower)
            .Select(g => new GroupSummary(g.Key, g.Name, g.Description, db.GroupMembers.Count(m => m.GroupKey == g.Key)))
            .ToListAsync(cancellationToken);

    public Task<Group?> FindAsync(int key, CancellationToken cancellationToken) =>
        db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Key == key, cancellationToken);

    public async Task<bool> AddAsync(Group group, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (await db.Groups.AnyAsync(g => g.NameLower == group.NameLower, cancellationToken))
        {
            return false;
        }

        db.Groups.Add(group);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
        finally
        {
            db.Entry(group).State = EntityState.Detached;
        }
    }

    public async Task<bool> UpdateAsync(int key, string name, string description, CancellationToken cancellationToken)
    {
        var lower = name.ToLowerInvariant();
        if (await db.Groups.AnyAsync(g => g.NameLower == lower && g.Key != key, cancellationToken))
        {
            return false;
        }

        return await db.Groups
            .Where(g => g.Key == key)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.Name, name).SetProperty(g => g.NameLower, lower).SetProperty(g => g.Description, description), cancellationToken) > 0;
    }

    public async Task<bool> DeleteAsync(int key, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.FeedPermissions.Where(p => p.GroupKey == key).ExecuteDeleteAsync(cancellationToken);
        await db.GroupMembers.Where(m => m.GroupKey == key).ExecuteDeleteAsync(cancellationToken);
        var deleted = await db.Groups.Where(g => g.Key == key).ExecuteDeleteAsync(cancellationToken) > 0;
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    public async Task<IReadOnlyList<User>> MembersAsync(int key, CancellationToken cancellationToken) =>
        await db.Users
            .AsNoTracking()
            .Where(u => db.GroupMembers.Any(m => m.GroupKey == key && m.UserKey == u.Key))
            .OrderBy(u => u.UserNameLower)
            .ToListAsync(cancellationToken);

    public async Task<bool> AddMemberAsync(int groupKey, int userKey, CancellationToken cancellationToken)
    {
        if (!await db.Groups.AnyAsync(g => g.Key == groupKey, cancellationToken)
            || !await db.Users.AnyAsync(u => u.Key == userKey, cancellationToken)
            || await db.GroupMembers.AnyAsync(m => m.GroupKey == groupKey && m.UserKey == userKey, cancellationToken))
        {
            return false;
        }

        var member = new GroupMember { GroupKey = groupKey, UserKey = userKey };
        db.GroupMembers.Add(member);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
        finally
        {
            db.Entry(member).State = EntityState.Detached;
        }
    }

    public async Task<bool> RemoveMemberAsync(int groupKey, int userKey, CancellationToken cancellationToken) =>
        await db.GroupMembers.Where(m => m.GroupKey == groupKey && m.UserKey == userKey).ExecuteDeleteAsync(cancellationToken) > 0;

    public async Task<IReadOnlyList<Group>> GroupsOfAsync(int userKey, CancellationToken cancellationToken) =>
        await db.Groups
            .AsNoTracking()
            .Where(g => db.GroupMembers.Any(m => m.GroupKey == g.Key && m.UserKey == userKey))
            .OrderBy(g => g.NameLower)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlySet<int>> ProviderMembersAsync(int groupKey, CancellationToken cancellationToken) =>
        (await db.GroupMembers.AsNoTracking().Where(m => m.GroupKey == groupKey && m.ProviderKey != null).Select(m => m.UserKey).ToListAsync(cancellationToken)).ToHashSet();

    public async Task<IReadOnlyList<GroupProviderLink>> ProviderLinksAsync(int groupKey, CancellationToken cancellationToken) =>
        await db.GroupProviderLinks.AsNoTracking().Where(l => l.GroupKey == groupKey).OrderBy(l => l.ProviderKey).ThenBy(l => l.ProviderGroup).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<GroupProviderLink>> ProviderLinksForProviderAsync(int providerKey, CancellationToken cancellationToken) =>
        await db.GroupProviderLinks.AsNoTracking().Where(l => l.ProviderKey == providerKey).ToListAsync(cancellationToken);

    public async Task<bool> AddProviderLinkAsync(GroupProviderLink link, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (!await db.Groups.AnyAsync(g => g.Key == link.GroupKey, cancellationToken)
            || !await db.OidcProviders.AnyAsync(p => p.Key == link.ProviderKey, cancellationToken)
            || await db.GroupProviderLinks.AnyAsync(l => l.GroupKey == link.GroupKey && l.ProviderKey == link.ProviderKey && l.ProviderGroup == link.ProviderGroup, cancellationToken))
        {
            return false;
        }

        db.GroupProviderLinks.Add(link);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
        finally
        {
            db.Entry(link).State = EntityState.Detached;
        }
    }

    /// <summary>
    /// The link goes; the memberships it made stay until that member's next sign-in with the provider, which removes them.
    /// Removing them here would need the provider's other links to tell which rows another link still justifies.
    /// </summary>
    public async Task<bool> RemoveProviderLinkAsync(int groupKey, int linkKey, CancellationToken cancellationToken) =>
        await db.GroupProviderLinks.Where(l => l.Key == linkKey && l.GroupKey == groupKey).ExecuteDeleteAsync(cancellationToken) > 0;

    public async Task SyncProviderMembershipsAsync(int userKey, int providerKey, IReadOnlySet<int> groupKeys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(groupKeys);
        var wanted = groupKeys.ToList();
        await db.GroupMembers
            .Where(m => m.UserKey == userKey && m.ProviderKey == providerKey && !wanted.Contains(m.GroupKey))
            .ExecuteDeleteAsync(cancellationToken);

        var present = await db.GroupMembers.AsNoTracking().Where(m => m.UserKey == userKey && wanted.Contains(m.GroupKey)).Select(m => m.GroupKey).ToListAsync(cancellationToken);
        var added = wanted.Except(present).Select(g => new GroupMember { GroupKey = g, UserKey = userKey, ProviderKey = providerKey }).ToList();
        if (added.Count == 0)
        {
            return;
        }

        db.GroupMembers.AddRange(added);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A sign-in on another replica added the same membership at the same moment; it is there either way.
        }
        finally
        {
            foreach (var member in added)
            {
                db.Entry(member).State = EntityState.Detached;
            }
        }
    }
}

public sealed class EfFeedPermissionStore(FiGetDbContext db) : IFeedPermissionStore
{
    public async Task<FeedAccessLevel> GrantedAsync(int feedKey, int userKey, CancellationToken cancellationToken)
    {
        var levels = await Grants(userKey).Where(p => p.FeedKey == feedKey).Select(p => p.Level).ToListAsync(cancellationToken);
        return levels.Count == 0 ? FeedAccessLevel.None : levels.Max();
    }

    public async Task<IReadOnlyDictionary<int, FeedAccessLevel>> GrantedOnAllAsync(int userKey, CancellationToken cancellationToken)
    {
        var rows = await Grants(userKey).Select(p => new { p.FeedKey, p.Level }).ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.FeedKey).ToDictionary(g => g.Key, g => g.Max(r => r.Level));
    }

    public async Task<IReadOnlyList<FeedGrant>> ListAsync(int feedKey, CancellationToken cancellationToken)
    {
        var users = await db.FeedPermissions
            .AsNoTracking()
            .Where(p => p.FeedKey == feedKey && p.UserKey != null)
            .Join(db.Users, p => p.UserKey, u => u.Key, (p, u) => new { p, u.UserName, u.UserNameLower })
            .OrderBy(x => x.UserNameLower)
            .Select(x => new FeedGrant(x.p.Key, x.p.FeedKey, x.p.UserKey, null, x.UserName, x.p.Level))
            .ToListAsync(cancellationToken);
        var groups = await db.FeedPermissions
            .AsNoTracking()
            .Where(p => p.FeedKey == feedKey && p.GroupKey != null)
            .Join(db.Groups, p => p.GroupKey, g => g.Key, (p, g) => new { p, g.Name, g.NameLower })
            .OrderBy(x => x.NameLower)
            .Select(x => new FeedGrant(x.p.Key, x.p.FeedKey, null, x.p.GroupKey, x.Name, x.p.Level))
            .ToListAsync(cancellationToken);
        return [.. users, .. groups];
    }

    public async Task SetAsync(int feedKey, int? userKey, int? groupKey, FeedAccessLevel level, CancellationToken cancellationToken)
    {
        if ((userKey is null) == (groupKey is null))
        {
            throw new ArgumentException("A grant is for exactly one of an account or a group.");
        }

        var existing = db.FeedPermissions.Where(p => p.FeedKey == feedKey && p.UserKey == userKey && p.GroupKey == groupKey);
        if (level == FeedAccessLevel.None)
        {
            await existing.ExecuteDeleteAsync(cancellationToken);
            return;
        }

        if (await existing.ExecuteUpdateAsync(s => s.SetProperty(p => p.Level, level), cancellationToken) > 0)
        {
            return;
        }

        var grant = new FeedPermission { FeedKey = feedKey, UserKey = userKey, GroupKey = groupKey, Level = level };
        db.FeedPermissions.Add(grant);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            db.Entry(grant).State = EntityState.Detached;
        }
    }

    public async Task<bool> RemoveAsync(int feedKey, int grantKey, CancellationToken cancellationToken) =>
        await db.FeedPermissions.Where(p => p.Key == grantKey && p.FeedKey == feedKey).ExecuteDeleteAsync(cancellationToken) > 0;

    /// <summary>Every grant that reaches the account: its own, and those of the groups it belongs to.</summary>
    private IQueryable<FeedPermission> Grants(int userKey) =>
        db.FeedPermissions.AsNoTracking().Where(p =>
            p.UserKey == userKey
            || (p.GroupKey != null && db.GroupMembers.Any(m => m.GroupKey == p.GroupKey && m.UserKey == userKey)));
}
