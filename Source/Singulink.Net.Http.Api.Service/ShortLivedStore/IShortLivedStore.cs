namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Stores the short-lived state that services keep between requests: the toolkit's sign-in codes, device links, passkey ceremonies and rate limit counts,
/// and the state of an app's own multi-step flows. All the servers of a service must share one store.
/// </summary>
/// <remarks>
/// <para>
/// Values are opaque strings that expire after a lifetime. A key holds either a value or a counter, and a counter's value is its count as decimal text, so
/// <see cref="GetAsync"/> reads counters too. Expired entries behave as if they did not exist.
/// </para>
/// <para>
/// Every operation must be atomic, also across servers: <see cref="TryAddAsync"/> adds a value for a key once until it expires or is removed,
/// <see cref="TakeAsync"/> returns a value at most once, and <see cref="IncrementAsync"/> counts every increment. Implementations are registered as
/// singletons.
/// </para>
/// <para>
/// The toolkit's own keys start with <c>httpapi:</c>, so apps use other prefixes for their keys. See <see cref="ShortLivedStoreExtensions"/> for adding values
/// under new random keys and for storing typed states.
/// </para>
/// <para>
/// The default implementation, <see cref="MemoryShortLivedStore"/>, keeps values in the memory of the process, so the servers of a service do not share
/// them.
/// </para>
/// </remarks>
public interface IShortLivedStore
{
    /// <summary>
    /// Adds a value under a key unless the key already has a value.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <param name="lifetime">How long the value is kept. Must be positive.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns><see langword="true"/> if the value was added, or <see langword="false"/> if the key already has a value (or a counter).</returns>
    ValueTask<bool> TryAddAsync(string key, string value, TimeSpan lifetime, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the value of a key, or returns <see langword="null"/> if the key has no value. The value of a counter is its count as decimal text.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets and removes the value of a key, or returns <see langword="null"/> if the key has no value. A value is returned at most once, even when it is
    /// taken by concurrent calls.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    ValueTask<string?> TakeAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the value of a key, if it has one.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds an amount to the counter under a key and returns the new count. A key without a value counts from 0. Every increment sets the counter to
    /// expire after the lifetime, counted from the increment.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="amount">The amount to add, which can be negative.</param>
    /// <param name="lifetime">How long the counter is kept after this increment. Must be positive.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="InvalidOperationException">The key holds a value that is not a count.</exception>
    ValueTask<long> IncrementAsync(string key, long amount, TimeSpan lifetime, CancellationToken cancellationToken = default);
}
