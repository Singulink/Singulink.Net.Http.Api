using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Singulink.Net.Http.Api.Service;

/// <content>
/// Registration of the short-lived store.
/// </content>
public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="MemoryShortLivedStore"/> as the <see cref="IShortLivedStore"/>, unless a store is already registered. Services that run on several
    /// servers register an <see cref="IShortLivedStore"/> backed by shared storage as a singleton instead, before or after calling this method.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="configure">An optional action to configure the options of the memory store, such as <see cref="MemoryShortLivedStoreOptions.MaxSize"/>.
    /// </param>
    public static IServiceCollection AddShortLivedStore(this IServiceCollection services, Action<MemoryShortLivedStoreOptions>? configure = null)
    {
        services.AddLogging();
        services.AddOptions<MemoryShortLivedStoreOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IShortLivedStore, MemoryShortLivedStore>();

        if (configure is not null)
            services.Configure(configure);

        return services;
    }
}
