namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Stores the short-lived state that services keep between requests: the toolkit's sign-in codes, device links, passkey ceremonies, rate limit counts and
/// counts of open chunked uploads, and the state of an app's own multi-step flows. Each feature of the toolkit resolves its store with a key of
/// <see cref="ShortLivedStoreKeys"/>, and the app's own flows use the store registered without a key.
/// </summary>
/// <remarks>
/// <para>
/// Values are opaque strings that expire after a lifetime. A key holds either a value or a counter, and a counter's value is its count as decimal text, so
/// <see cref="GetAsync"/> reads counters too. Expired entries behave as if they did not exist.
/// </para>
/// <para>
/// Every operation must be atomic, also across servers: <see cref="TryAddAsync"/> adds a value for a key once until it expires or is removed,
/// <see cref="SetAsync"/> replaces what a key holds without a moment in which it holds nothing, <see cref="TakeAsync"/> returns a value at most once, and
/// <see cref="IncrementAsync"/> counts every increment. Implementations are registered as
/// singletons.
/// </para>
/// <para>
/// An exception that an operation throws, such as while the store can't be reached, fails the request that needs the operation as any exception does: a
/// <see cref="ServiceUnavailableApiException"/> gives a <c>503</c>, and an exception that isn't an <see cref="ApiException"/> a <c>500</c>, unless an
/// <see cref="IApiExceptionHandler"/> maps it otherwise.
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
    /// Gets who sees the store's values: only this process (<see cref="StoreSharingMode.ProcessLocal"/>), as with <see cref="MemoryShortLivedStore"/>, or every
    /// process that uses the same storage (<see cref="StoreSharingMode.Shared"/>).
    /// </summary>
    /// <remarks>
    /// Chunked uploads read it when the application starts: an <see cref="Uploads.IUploadStore"/> that is shared (see
    /// <see cref="Uploads.IUploadStore.SharingMode"/>) requires a store of <see cref="ShortLivedStoreKeys.Uploads"/> that is shared too, and the keys of the
    /// counts of open uploads depend on both values (see the uploads guide). The application fails to start if it is
    /// <see cref="StoreSharingMode.Unspecified"/>, or not a value of <see cref="StoreSharingMode"/>.
    /// </remarks>
    StoreSharingMode SharingMode { get; }

    /// <summary>
    /// Gets how much the store holds and how much it can hold.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <remarks>
    /// Values that the store can't tell are <see langword="null"/>, and a store that can tell none returns <see cref="ShortLivedStoreUsage.Unknown"/>. The
    /// health checks of the toolkit's stores read it when they run (see <see cref="HttpApiHealthChecksBuilderExtensions.AddShortLivedStoreCheck"/>), and
    /// nothing reads it on the path of a request.
    /// </remarks>
    ValueTask<ShortLivedStoreUsage> GetUsageAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a value under a key unless the key already has a value.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <param name="lifetime">How long the value is kept. Must be greater than zero.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns><see langword="true"/> if the value was added, or <see langword="false"/> if the key already has a value (or a counter).</returns>
    ValueTask<bool> TryAddAsync(string key, string value, TimeSpan lifetime, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the value of a key, replacing whatever the key holds, a value or a counter, and sets it to expire after the lifetime, counted from now.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <param name="lifetime">How long the value is kept. Must be greater than zero.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <remarks>
    /// The key has a value throughout: a read at the same time gets the value it held before or the new one.
    /// </remarks>
    ValueTask SetAsync(string key, string value, TimeSpan lifetime, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the value of a key, or returns <see langword="null"/> if the key has no value.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <remarks>
    /// The value of a counter is its count as decimal text.
    /// </remarks>
    ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets and removes the value of a key, or returns <see langword="null"/> if the key has no value.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <remarks>
    /// A value is returned at most once, even when it is taken by concurrent calls.
    /// </remarks>
    ValueTask<string?> TakeAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the value of a key, if it has one.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds an amount to the counter under a key and returns the new count.
    /// </summary>
    /// <remarks>
    /// A key without a value counts from 0. Every increment sets the counter to expire after the lifetime, counted from the increment.
    /// </remarks>
    /// <param name="key">The key.</param>
    /// <param name="amount">The amount to add, which can be negative.</param>
    /// <param name="lifetime">How long the counter is kept after this increment. Must be greater than zero.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="InvalidOperationException">The key holds a value that is not a count.</exception>
    ValueTask<long> IncrementAsync(string key, long amount, TimeSpan lifetime, CancellationToken cancellationToken = default);
}
