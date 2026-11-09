namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// A short-lived store that the hosts of a test share, as the servers of a service share one: it keeps its values in the store it wraps, such as a
/// <see cref="MemoryShortLivedStore"/>, and reports that it is shared, which hosts that share an upload store require.
/// </summary>
public sealed class DeclaredSharedShortLivedStore(IShortLivedStore inner) : IShortLivedStore
{
    public StoreSharingMode SharingMode => StoreSharingMode.Shared;

    public ValueTask<ShortLivedStoreUsage> GetUsageAsync(CancellationToken cancellationToken = default) => inner.GetUsageAsync(cancellationToken);

    public ValueTask<bool> TryAddAsync(string key, string value, TimeSpan lifetime, CancellationToken cancellationToken = default) =>
        inner.TryAddAsync(key, value, lifetime, cancellationToken);

    public ValueTask SetAsync(string key, string value, TimeSpan lifetime, CancellationToken cancellationToken = default) =>
        inner.SetAsync(key, value, lifetime, cancellationToken);

    public ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default) => inner.GetAsync(key, cancellationToken);

    public ValueTask<string?> TakeAsync(string key, CancellationToken cancellationToken = default) => inner.TakeAsync(key, cancellationToken);

    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) => inner.RemoveAsync(key, cancellationToken);

    public ValueTask<long> IncrementAsync(string key, long amount, TimeSpan lifetime, CancellationToken cancellationToken = default) =>
        inner.IncrementAsync(key, amount, lifetime, cancellationToken);
}
