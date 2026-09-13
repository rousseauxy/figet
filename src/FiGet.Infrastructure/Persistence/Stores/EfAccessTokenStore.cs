using FiGet.Application.Ports;
using FiGet.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FiGet.Infrastructure.Persistence.Stores;

public sealed class EfAccessTokenStore(FiGetDbContext db) : IAccessTokenStore
{
    public Task<AccessToken?> FindByHashAsync(string hash, CancellationToken cancellationToken) =>
        db.AccessTokens.AsNoTracking().FirstOrDefaultAsync(t => t.Hash == hash, cancellationToken);

    public Task<AccessToken?> FindAsync(int tokenKey, CancellationToken cancellationToken) =>
        db.AccessTokens.AsNoTracking().Include(t => t.Feed).FirstOrDefaultAsync(t => t.Key == tokenKey, cancellationToken);

    public async Task<IReadOnlyList<AccessToken>> ListAsync(CancellationToken cancellationToken) =>
        await db.AccessTokens.AsNoTracking().Include(t => t.Feed).Include(t => t.User).OrderBy(t => t.Key).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AccessToken>> ListOwnedAsync(int userKey, CancellationToken cancellationToken) =>
        await db.AccessTokens.AsNoTracking().Include(t => t.Feed).Where(t => t.UserKey == userKey).OrderBy(t => t.Key).ToListAsync(cancellationToken);

    public async Task AddAsync(AccessToken token, CancellationToken cancellationToken)
    {
        db.AccessTokens.Add(token);
        await db.SaveChangesAsync(cancellationToken);
        db.Entry(token).State = EntityState.Detached;
    }

    public async Task<bool> RevokeAsync(int tokenKey, DateTime utcNow, CancellationToken cancellationToken) =>
        await db.AccessTokens
            .Where(t => t.Key == tokenKey && t.RevokedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedUtc, utcNow), cancellationToken) > 0;

    public async Task<bool> RevokeOwnedAsync(int userKey, int tokenKey, DateTime utcNow, CancellationToken cancellationToken) =>
        await db.AccessTokens
            .Where(t => t.Key == tokenKey && t.UserKey == userKey && t.RevokedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedUtc, utcNow), cancellationToken) > 0;

    public Task TouchAsync(int tokenKey, DateTime utcNow, CancellationToken cancellationToken) =>
        db.AccessTokens
            .Where(t => t.Key == tokenKey)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedUtc, utcNow), cancellationToken);

    public Task<bool> AnyActiveAdminAsync(DateTime utcNow, CancellationToken cancellationToken) =>
        db.AccessTokens.AnyAsync(
            t => t.RevokedUtc == null
                && t.UserKey == null
                && (t.ExpiresUtc == null || t.ExpiresUtc > utcNow)
                && (t.Scopes & TokenScopes.Admin) == TokenScopes.Admin,
            cancellationToken);
}
