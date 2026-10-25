using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Extension methods for <see cref="IShortLivedStore"/>: adding values under new random keys, storing typed states as JSON, and counting attempts in fixed
/// windows.
/// </summary>
/// <remarks>
/// <para>
/// A state is kept under a key made of a prefix, which keeps the states of one flow apart from all others (for example <c>"myapp:setup-ticket:"</c>), and an
/// ID within it: a new random one from <see cref="AddAsync{T}(IShortLivedStore, string, T, JsonTypeInfo{T}, TimeSpan, CancellationToken)"/>, or one of the
/// app's choosing. <see cref="ShortLivedId.IsWellFormed"/> tells whether a value has the form of a random one.
/// </para>
/// <para>
/// The typed methods take the <see cref="JsonTypeInfo{T}"/> of the state, typically from a source-generated <c>JsonSerializerContext</c>, so they work in
/// trimmed and AOT-compiled services. A stored value that cannot be read as the state, for example one stored by an earlier version of the service during
/// a rolling deployment, is treated as missing and logged to the logger given to the method, if any.
/// </para>
/// <para>
/// <see cref="AcquirePermitAsync"/> counts attempts at something, such as sending emails to an address, in fixed windows, so that a limit holds across all
/// the servers that share the store.
/// </para>
/// </remarks>
public static class ShortLivedStoreExtensions
{
    private const int MaxAddAttempts = 5;

    /// <summary>
    /// Adds a value under a new random key and returns the random part of the key. The value is stored under <paramref name="keyPrefix"/> followed by the
    /// returned value, which the next step of a flow presents.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="keyPrefix">The start of the key, which keeps the values of one flow apart from all others, for example <c>"myapp:setup-ticket:"</c>.
    /// </param>
    /// <param name="value">The value.</param>
    /// <param name="lifetime">How long the value is kept. Must be positive.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The random part of the key: 256 random bits as 43 URL-safe characters. It cannot be guessed, so it can serve as the secret that a client
    /// presents to continue the flow.</returns>
    public static async ValueTask<string> AddAsync(
        this IShortLivedStore store, string keyPrefix, string value, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);

        // A new random key is never taken, so a refusal can only come from a broken store, which should not loop forever.
        for (int attempt = 0; attempt < MaxAddAttempts; attempt++)
        {
            string key = ShortLivedId.Create();

            if (await store.TryAddAsync(keyPrefix + key, value, lifetime, cancellationToken))
                return key;
        }

        throw new InvalidOperationException("The short-lived store did not add a value under a new key.");
    }

    /// <summary>
    /// Adds a state, as JSON, under a new random key and returns the random part of the key. The state is stored under <paramref name="keyPrefix"/>
    /// followed by the returned value, which the next step of a flow presents.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="keyPrefix">The start of the key, which keeps the states of one flow apart from all others, for example <c>"myapp:setup-ticket:"</c>.
    /// </param>
    /// <param name="state">The state.</param>
    /// <param name="typeInfo">The JSON type information of the state.</param>
    /// <param name="lifetime">How long the state is kept. Must be positive.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The random part of the key: 256 random bits as 43 URL-safe characters. It cannot be guessed, so it can serve as the secret that a client
    /// presents to continue the flow.</returns>
    public static ValueTask<string> AddAsync<T>(
        this IShortLivedStore store, string keyPrefix, T state, JsonTypeInfo<T> typeInfo, TimeSpan lifetime, CancellationToken cancellationToken = default)
        where T : class
    {
        return store.AddAsync(keyPrefix, JsonSerializer.Serialize(state, typeInfo), lifetime, cancellationToken);
    }

    /// <summary>
    /// Adds a state, as JSON, under an ID of the app's choosing unless the key already has a value.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="keyPrefix">The start of the key, which keeps the states of one flow apart from all others.</param>
    /// <param name="id">The ID of the state within the prefix.</param>
    /// <param name="state">The state.</param>
    /// <param name="typeInfo">The JSON type information of the state.</param>
    /// <param name="lifetime">How long the state is kept. Must be positive.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns><see langword="true"/> if the state was added, or <see langword="false"/> if the key already has a value.</returns>
    public static ValueTask<bool> TryAddAsync<T>(
        this IShortLivedStore store,
        string keyPrefix,
        string id,
        T state,
        JsonTypeInfo<T> typeInfo,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
        where T : class
    {
        return store.TryAddAsync(keyPrefix + id, JsonSerializer.Serialize(state, typeInfo), lifetime, cancellationToken);
    }

    /// <summary>
    /// Gets a state, or returns <see langword="null"/> if it does not exist or cannot be read as the state, which is logged as a warning to
    /// <paramref name="logger"/>, if one is given.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="keyPrefix">The start of the key, as the state was added with.</param>
    /// <param name="id">The ID of the state within the prefix.</param>
    /// <param name="typeInfo">The JSON type information of the state.</param>
    /// <param name="logger">The logger that a state that cannot be read is logged to, if any.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public static async ValueTask<T?> GetAsync<T>(
        this IShortLivedStore store,
        string keyPrefix,
        string id,
        JsonTypeInfo<T> typeInfo,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        return Read(await store.GetAsync(keyPrefix + id, cancellationToken), typeInfo, logger);
    }

    /// <summary>
    /// Gets and removes a state, or returns <see langword="null"/> if it does not exist or cannot be read as the state, which is logged as a warning to
    /// <paramref name="logger"/>, if one is given. A state is returned at most once, even when it is taken by concurrent calls.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="keyPrefix">The start of the key, as the state was added with.</param>
    /// <param name="id">The ID of the state within the prefix.</param>
    /// <param name="typeInfo">The JSON type information of the state.</param>
    /// <param name="logger">The logger that a state that cannot be read is logged to, if any.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public static async ValueTask<T?> TakeAsync<T>(
        this IShortLivedStore store,
        string keyPrefix,
        string id,
        JsonTypeInfo<T> typeInfo,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        return Read(await store.TakeAsync(keyPrefix + id, cancellationToken), typeInfo, logger);
    }

    /// <summary>
    /// Removes a state, if it exists.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="keyPrefix">The start of the key, as the state was added with.</param>
    /// <param name="id">The ID of the state within the prefix.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public static ValueTask RemoveAsync(this IShortLivedStore store, string keyPrefix, string id, CancellationToken cancellationToken = default)
    {
        return store.RemoveAsync(keyPrefix + id, cancellationToken);
    }

    /// <summary>
    /// Acquires a permit from a fixed window count, unless <paramref name="permitLimit"/> permits were already acquired in the current window. Windows start
    /// at fixed times on the clock (a one-hour window starts at every full hour), so every server that shares the store counts the same window.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="keyPrefix">The start of the key, which keeps the counts of one limit apart from all others, for example <c>"myapp:setup-emails:"</c>.
    /// </param>
    /// <param name="key">What the limit applies to, such as an email address. The store is given a hash of it rather than the value itself, so keys are
    /// compared exactly: <c>A@example.com</c> and <c>a@example.com</c> are counted apart.</param>
    /// <param name="permitLimit">The number of permits that can be acquired in each window. Must be positive.</param>
    /// <param name="window">The length of a window. Must be positive.</param>
    /// <param name="timeProvider">The clock that windows are measured by, such as the registered <see cref="TimeProvider"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <remarks>
    /// Every attempt is counted, including attempts that acquire no permit.
    /// </remarks>
    public static async ValueTask<FixedWindowPermit> AcquirePermitAsync(
        this IShortLivedStore store,
        string keyPrefix,
        string key,
        int permitLimit,
        TimeSpan window,
        TimeProvider timeProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(permitLimit, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);

        // The hash gives the key a fixed length and keeps values such as email addresses out of the store.
        var fixedWindow = FixedWindow.Get($"{keyPrefix}{ShortLivedId.Hash(key)}:", window.Ticks, timeProvider);
        long count = await store.IncrementAsync(fixedWindow.Key, 1, fixedWindow.KeyLifetime, cancellationToken);

        return count <= permitLimit ? new FixedWindowPermit(true, null) : new FixedWindowPermit(false, fixedWindow.Remaining);
    }

    private static T? Read<T>(string? json, JsonTypeInfo<T> typeInfo, ILogger? logger)
        where T : class
    {
        if (json is null)
            return null;

        try
        {
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException ex)
        {
            if (logger is not null)
                logger.LogWarning(ex, "A stored {StateType} could not be read, so it was treated as missing.", typeof(T).Name);
            else if (Trace.Listeners.Count > 0)
                Trace.TraceWarning($"[Singulink.Net.Http.Api] A stored {typeof(T).Name} could not be read, so it was treated as missing: {ex.Message}");

            return null;
        }
    }
}
