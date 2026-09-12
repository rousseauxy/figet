using FiGet.Domain.Entities;

namespace FiGet.Application.Ports;

public interface IAccessTokenStore
{
    Task<AccessToken?> FindByHashAsync(string hash, CancellationToken cancellationToken);

    Task<AccessToken?> FindAsync(int tokenKey, CancellationToken cancellationToken);

    Task<IReadOnlyList<AccessToken>> ListAsync(CancellationToken cancellationToken);

    Task AddAsync(AccessToken token, CancellationToken cancellationToken);

    Task<bool> RevokeAsync(int tokenKey, DateTime utcNow, CancellationToken cancellationToken);

    Task TouchAsync(int tokenKey, DateTime utcNow, CancellationToken cancellationToken);

    Task<bool> AnyActiveAdminAsync(DateTime utcNow, CancellationToken cancellationToken);
}
