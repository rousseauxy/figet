using FiGet.Domain.Entities;

namespace FiGet.Application.Ports;

public interface IAccessTokenStore
{
    Task<AccessToken?> FindByHashAsync(string hash, CancellationToken cancellationToken);

    Task<AccessToken?> FindAsync(int tokenKey, CancellationToken cancellationToken);

    /// <summary>Every token, service tokens and personal keys, with their feed and owner.</summary>
    Task<IReadOnlyList<AccessToken>> ListAsync(CancellationToken cancellationToken);

    /// <summary>The personal keys of one account, with their feed.</summary>
    Task<IReadOnlyList<AccessToken>> ListOwnedAsync(int userKey, CancellationToken cancellationToken);

    Task AddAsync(AccessToken token, CancellationToken cancellationToken);

    Task<bool> RevokeAsync(int tokenKey, DateTime utcNow, CancellationToken cancellationToken);

    /// <summary>Revokes a key only when it belongs to <paramref name="userKey"/>. False for anyone else's.</summary>
    Task<bool> RevokeOwnedAsync(int userKey, int tokenKey, DateTime utcNow, CancellationToken cancellationToken);

    Task TouchAsync(int tokenKey, DateTime utcNow, CancellationToken cancellationToken);

    Task<bool> AnyActiveAdminAsync(DateTime utcNow, CancellationToken cancellationToken);
}
