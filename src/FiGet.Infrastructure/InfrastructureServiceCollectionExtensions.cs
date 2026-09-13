using FiGet.Application.Ports;
using FiGet.Infrastructure.Persistence.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Binds the persistence ports to their EF implementations. The caller registers
    /// <c>FiGetDbContext</c> with a provider, because only the host knows which database this is.
    /// </summary>
    public static IServiceCollection AddFiGetStores(this IServiceCollection services)
    {
        services.AddScoped<IFeedStore, EfFeedStore>();
        services.AddScoped<IPackageStore, EfPackageStore>();
        services.AddScoped<IAccessTokenStore, EfAccessTokenStore>();
        services.AddScoped<IUpstreamIndexStore, EfUpstreamIndexStore>();
        services.AddScoped<ISettingStore, EfSettingStore>();
        services.AddScoped<IAssetStore, EfAssetStore>();
        return services;
    }
}
