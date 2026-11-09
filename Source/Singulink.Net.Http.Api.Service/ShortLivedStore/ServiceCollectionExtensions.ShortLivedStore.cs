using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service;

/// <content>
/// Registration of the short-lived store.
/// </content>
public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="MemoryShortLivedStore"/> as the <see cref="IShortLivedStore"/>, unless a store is already registered, and the stores of the
    /// toolkit's features, which they resolve with their keys (see <see cref="ShortLivedStoreKeys"/>).
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="configure">An optional action to configure the options of the memory store that is registered without a key, such as
    /// <see cref="MemoryShortLivedStoreOptions.MaxSize"/>. The memory stores of the features' keys have the named options of their keys.</param>
    /// <remarks>
    /// Services that run on several servers register an <see cref="IShortLivedStore"/> backed by shared storage as a singleton instead, before or after
    /// calling this method, which every feature then uses too.
    /// </remarks>
    public static IServiceCollection AddShortLivedStore(this IServiceCollection services, Action<MemoryShortLivedStoreOptions>? configure = null)
    {
        services.AddLogging();
        services.AddMetrics();
        services.AddOptions<MemoryShortLivedStoreOptions>();
        services.TryAddTransient<IOptionsFactory<MemoryShortLivedStoreOptions>, MemoryShortLivedStoreOptionsFactory>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<DefaultShortLivedStore>();

        services.TryAddSingleton<IShortLivedStore>(static sp => {
            var store = new MemoryShortLivedStore(
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<IOptions<MemoryShortLivedStoreOptions>>(),
                sp.GetRequiredService<ILogger<MemoryShortLivedStore>>());

            sp.GetRequiredService<DefaultShortLivedStore>().Store = store;
            HttpApiMetrics.Get(sp)?.AddStore(null, store);
            return store;
        });

        // One instance for each key: a memory store of its own while the app's store is the default one, and otherwise the app's store, which the container
        // must not dispose for each key (see AppShortLivedStore).
        services.TryAddKeyedSingleton<IShortLivedStore>(KeyedService.AnyKey, static (sp, key) => {
            var store = sp.GetRequiredService<IShortLivedStore>();

            if (!ReferenceEquals(store, sp.GetRequiredService<DefaultShortLivedStore>().Store))
                return new AppShortLivedStore(store);

            // A key of another type than string has the named options of its text, so that it doesn't get the options of the store without a key.
            var options = sp.GetRequiredService<IOptionsMonitor<MemoryShortLivedStoreOptions>>().Get(key?.ToString() ?? Options.DefaultName);
            var logger = sp.GetRequiredService<ILogger<MemoryShortLivedStore>>();
            var keyStore = new MemoryShortLivedStore(sp.GetRequiredService<TimeProvider>(), Options.Create(options), logger);
            HttpApiMetrics.Get(sp)?.AddStore(key?.ToString(), keyStore);

            return keyStore;
        });

        if (configure is not null)
            services.Configure(configure);

        HttpApiHealthCheckSource.Register(services, "short-lived-store", b => b.AddShortLivedStoreCheck());
        return services;
    }

    /// <summary>
    /// Records the memory store that <see cref="AddShortLivedStore"/> registers without a key, once it is created, so that the stores of the keys can tell
    /// it from a store of the app's own.
    /// </summary>
    internal sealed class DefaultShortLivedStore
    {
        public IShortLivedStore? Store { get; set; }
    }
}
