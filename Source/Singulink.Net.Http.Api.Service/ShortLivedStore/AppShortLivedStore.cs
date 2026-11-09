namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The app's <see cref="IShortLivedStore"/> as the store of a key of the toolkit's features (see <see cref="ShortLivedStoreKeys"/>). The container disposes
/// what the factory of a key returns, once for each key, even a store that the app registered as an instance, which it never disposes otherwise, so the
/// factory returns this instead of the app's store.
/// </summary>
internal sealed class AppShortLivedStore(IShortLivedStore store) : IShortLivedStore
{
    public IShortLivedStore Store { get; } = store;

    public StoreSharingMode SharingMode => Store.SharingMode;

    /// <summary>
    /// Gets the name of the type of a store, or of the app's store that it stands for, for messages.
    /// </summary>
    public static string GetTypeName(IShortLivedStore store) => (store is AppShortLivedStore app ? app.Store : store).GetType().Name;

    public ValueTask<ShortLivedStoreUsage> GetUsageAsync(CancellationToken cancellationToken = default) => Store.GetUsageAsync(cancellationToken);

    public ValueTask<bool> TryAddAsync(string key, string value, TimeSpan lifetime, CancellationToken cancellationToken = default) =>
        Store.TryAddAsync(key, value, lifetime, cancellationToken);

    public ValueTask SetAsync(string key, string value, TimeSpan lifetime, CancellationToken cancellationToken = default) =>
        Store.SetAsync(key, value, lifetime, cancellationToken);

    public ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default) => Store.GetAsync(key, cancellationToken);

    public ValueTask<string?> TakeAsync(string key, CancellationToken cancellationToken = default) => Store.TakeAsync(key, cancellationToken);

    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) => Store.RemoveAsync(key, cancellationToken);

    public ValueTask<long> IncrementAsync(string key, long amount, TimeSpan lifetime, CancellationToken cancellationToken = default) =>
        Store.IncrementAsync(key, amount, lifetime, cancellationToken);
}
