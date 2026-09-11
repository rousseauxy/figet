using FiGet.Core.Stores;
using FiGet.Persistence.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace FiGet.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>Registers the EF stores. The caller registers <see cref="FiGetDbContext"/> with a provider.</summary>
    public static IServiceCollection AddFiGetStores(this IServiceCollection services)
    {
        services.AddScoped<IFeedStore, EfFeedStore>();
        services.AddScoped<IPackageStore, EfPackageStore>();
        services.AddScoped<IAccessTokenStore, EfAccessTokenStore>();
        return services;
    }
}
