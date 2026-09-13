using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FiGet.Infrastructure.Persistence.Stores;

public sealed class EfUserStore(FiGetDbContext db) : IUserStore
{
    public Task<User?> FindAsync(int key, CancellationToken cancellationToken) =>
        db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Key == key, cancellationToken);

    public Task<User?> FindByUserNameAsync(string userName, CancellationToken cancellationToken)
    {
        var lower = userName.ToLowerInvariant();
        return db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserNameLower == lower, cancellationToken);
    }

    public async Task<IReadOnlyList<User>> ListAsync(CancellationToken cancellationToken) =>
        await db.Users.AsNoTracking().OrderBy(u => u.UserNameLower).ToListAsync(cancellationToken);

    public Task<bool> AnyAsync(CancellationToken cancellationToken) => db.Users.AnyAsync(cancellationToken);

    public Task<int> CountActiveSuperAdminsAsync(CancellationToken cancellationToken) =>
        db.Users.CountAsync(u => u.Role == UserRole.SuperAdmin && !u.IsDisabled, cancellationToken);

    public async Task<bool> AddAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (await db.Users.AnyAsync(u => u.UserNameLower == user.UserNameLower, cancellationToken))
        {
            return false;
        }

        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            // The same name added by another request or replica at the same moment.
            db.Entry(user).State = EntityState.Detached;
            return false;
        }
        finally
        {
            db.Entry(user).State = EntityState.Detached;
        }
    }

    public async Task<bool> UpdateAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        return await db.Users
            .Where(u => u.Key == user.Key)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(u => u.DisplayName, user.DisplayName)
                    .SetProperty(u => u.Email, user.Email)
                    .SetProperty(u => u.PasswordHash, user.PasswordHash)
                    .SetProperty(u => u.Role, user.Role)
                    .SetProperty(u => u.IsDisabled, user.IsDisabled)
                    .SetProperty(u => u.MustChangePassword, user.MustChangePassword)
                    .SetProperty(u => u.SecurityStamp, user.SecurityStamp)
                    .SetProperty(u => u.FailedSignIns, user.FailedSignIns)
                    .SetProperty(u => u.LockedUntilUtc, user.LockedUntilUtc)
                    .SetProperty(u => u.LastSignInUtc, user.LastSignInUtc),
                cancellationToken) > 0;
    }

    /// <summary>The account with its grants, memberships and personal keys.</summary>
    public async Task<bool> DeleteAsync(int key, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.AccessTokens.Where(t => t.UserKey == key).ExecuteDeleteAsync(cancellationToken);
        await db.FeedPermissions.Where(p => p.UserKey == key).ExecuteDeleteAsync(cancellationToken);
        await db.GroupMembers.Where(m => m.UserKey == key).ExecuteDeleteAsync(cancellationToken);
        var deleted = await db.Users.Where(u => u.Key == key).ExecuteDeleteAsync(cancellationToken) > 0;
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }
}
