using Microsoft.Extensions.Hosting;
using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service.SignIn;

/// <summary>
/// Warns when the application starts if the store of sign-in keeps its values in this process while another store is shared with other processes, which
/// suggests several servers, which would not find each other's sign-in state.
/// </summary>
internal sealed class SignInStoreCheck(IServiceProvider services, ILogger<SignInStoreCheck> logger) : IHostedService
{
    private static readonly string[] OtherKeys = [ShortLivedStoreKeys.RateLimiting, ShortLivedStoreKeys.Uploads];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var store = services.GetRequiredKeyedService<IShortLivedStore>(ShortLivedStoreKeys.SignIn);

        if (store.SharingMode is StoreSharingMode.ProcessLocal && GetSharedStores() is { Count: > 0 } sharedStores)
        {
            logger.LogWarning(
                "The {Store} of sign-in ({StoreType}) keeps its values in this process, but other stores are shared with other processes: {SharedStores}. " +
                "Sign-in codes, device links and passkey ceremonies started on one server are not found on another.",
                nameof(IShortLivedStore), AppShortLivedStore.GetTypeName(store), string.Join(", ", sharedStores));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private List<string> GetSharedStores()
    {
        List<string> sharedStores = [];

        if (services.GetService<IShortLivedStore>() is { SharingMode: StoreSharingMode.Shared } appStore)
            sharedStores.Add($"the app's {nameof(IShortLivedStore)} ({appStore.GetType().Name})");

        foreach (string key in OtherKeys)
        {
            if (services.GetKeyedService<IShortLivedStore>(key) is { SharingMode: StoreSharingMode.Shared } keyedStore)
                sharedStores.Add($"the {nameof(IShortLivedStore)} of '{key}' ({AppShortLivedStore.GetTypeName(keyedStore)})");
        }

        if (services.GetService<IUploadStore>() is { SharingMode: StoreSharingMode.Shared } uploadStore)
            sharedStores.Add($"the {nameof(IUploadStore)} ({uploadStore.GetType().Name})");

        return sharedStores;
    }
}
