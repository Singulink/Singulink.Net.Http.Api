namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Wraps a short-lived store to run test code before and after each operation, for example to fail an operation or to cancel the request in the middle of
/// a flow. Like a store that makes network calls, it observes the cancellation token each operation is given.
/// </summary>
public sealed class InterceptingShortLivedStore(IShortLivedStore inner) : IShortLivedStore
{
    private readonly List<(string Operation, string KeyPrefix)> _failures = [];

    /// <summary>
    /// Gets or sets the code that runs before each operation with the operation's name (such as <c>"TryAdd"</c>) and key. It can throw to fail the
    /// operation.
    /// </summary>
    public Action<string, string>? Before { get; set; }

    /// <summary>
    /// Gets or sets the code that runs after each operation that succeeded, with the operation's name and key.
    /// </summary>
    public Action<string, string>? After { get; set; }

    /// <summary>
    /// Makes the next operation of the specified name on a key with the specified prefix fail with an <see cref="InvalidOperationException"/>, as a store
    /// that cannot be reached would.
    /// </summary>
    public void FailOnce(string operation, string keyPrefix)
    {
        lock (_failures)
            _failures.Add((operation, keyPrefix));
    }

    public ValueTask<bool> TryAddAsync(string key, string value, TimeSpan lifetime, CancellationToken cancellationToken = default) =>
        RunAsync("TryAdd", key, cancellationToken, () => inner.TryAddAsync(key, value, lifetime, cancellationToken));

    public ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        RunAsync("Get", key, cancellationToken, () => inner.GetAsync(key, cancellationToken));

    public ValueTask<string?> TakeAsync(string key, CancellationToken cancellationToken = default) =>
        RunAsync("Take", key, cancellationToken, () => inner.TakeAsync(key, cancellationToken));

    public async ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        await RunAsync("Remove", key, cancellationToken, async () => {
            await inner.RemoveAsync(key, cancellationToken);
            return true;
        });
    }

    public ValueTask<long> IncrementAsync(string key, long amount, TimeSpan lifetime, CancellationToken cancellationToken = default) =>
        RunAsync("Increment", key, cancellationToken, () => inner.IncrementAsync(key, amount, lifetime, cancellationToken));

    private async ValueTask<T> RunAsync<T>(string operation, string key, CancellationToken cancellationToken, Func<ValueTask<T>> run)
    {
        lock (_failures)
        {
            int index = _failures.FindIndex(f => f.Operation == operation && key.StartsWith(f.KeyPrefix, StringComparison.Ordinal));

            if (index >= 0)
            {
                _failures.RemoveAt(index);
                throw new InvalidOperationException($"The store failed to {operation} '{key}'.");
            }
        }

        Before?.Invoke(operation, key);
        cancellationToken.ThrowIfCancellationRequested();

        var result = await run();
        After?.Invoke(operation, key);

        return result;
    }
}
