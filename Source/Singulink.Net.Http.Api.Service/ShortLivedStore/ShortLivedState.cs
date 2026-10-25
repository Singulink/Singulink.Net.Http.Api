using System.Text.Json.Serialization.Metadata;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Keeps states of one type under one key prefix in the registered <see cref="IShortLivedStore"/>, each for the same lifetime: adds them under new random
/// IDs, and gets, takes and removes them by ID. An ID that does not have the form of one (see <see cref="ShortLivedId.IsWellFormed"/>) is not looked up.
/// </summary>
/// <typeparam name="T">The type of the states.</typeparam>
/// <remarks>
/// States are stored as JSON with the specified <see cref="JsonTypeInfo{T}"/>, as by <see cref="ShortLivedStoreExtensions"/>. A stored value that cannot be
/// read as the state is treated as missing and logged as a warning to the logger, if one is given.
/// </remarks>
public class ShortLivedState<T>
    where T : class
{
    private const string ToolkitKeyPrefix = "httpapi:";

    private readonly IShortLivedStore _store;
    private readonly string _keyPrefix;
    private readonly JsonTypeInfo<T> _typeInfo;
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShortLivedState{T}"/> class.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="keyPrefix">The start of the keys, which keeps these states apart from all others, for example <c>"myapp:setup-ticket:"</c>. The
    /// toolkit's own keys start with <c>httpapi:</c>.</param>
    /// <param name="typeInfo">The JSON type information of the state.</param>
    /// <param name="lifetime">How long each state is kept after it is added.</param>
    /// <param name="logger">The logger that a state that cannot be read is logged to, if any.</param>
    /// <exception cref="ArgumentException"><paramref name="keyPrefix"/> is empty or starts with <c>httpapi:</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lifetime"/> is zero or negative.</exception>
    public ShortLivedState(IShortLivedStore store, string keyPrefix, JsonTypeInfo<T> typeInfo, TimeSpan lifetime, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyPrefix);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);

        if (keyPrefix.StartsWith(ToolkitKeyPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The key prefix '{keyPrefix}' starts with '{ToolkitKeyPrefix}', which the toolkit's own keys start with.", nameof(keyPrefix));
        }

        _store = store;
        _keyPrefix = keyPrefix;
        _typeInfo = typeInfo;
        Lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>
    /// Gets how long each state is kept after it is added.
    /// </summary>
    public TimeSpan Lifetime { get; }

    /// <summary>
    /// Adds a state under a new random ID and returns the ID, which the next step of a flow presents.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The ID: 256 random bits as 43 URL-safe characters (see <see cref="ShortLivedId"/>). It cannot be guessed, so it can serve as the secret
    /// that a client presents to continue the flow.</returns>
    public ValueTask<string> AddAsync(T state, CancellationToken cancellationToken = default)
    {
        return _store.AddAsync(_keyPrefix, state, _typeInfo, Lifetime, cancellationToken);
    }

    /// <summary>
    /// Gets the state with the specified ID, or returns <see langword="null"/> if there is none, or the ID does not have the form of an ID.
    /// </summary>
    /// <param name="id">The ID that <see cref="AddAsync"/> returned.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public ValueTask<T?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        return ShortLivedId.IsWellFormed(id) ? _store.GetAsync(_keyPrefix, id, _typeInfo, _logger, cancellationToken) : default;
    }

    /// <summary>
    /// Gets and removes the state with the specified ID, or returns <see langword="null"/> if there is none, or the ID does not have the form of an ID. A
    /// state is returned at most once, even when it is taken by concurrent calls.
    /// </summary>
    /// <param name="id">The ID that <see cref="AddAsync"/> returned.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public ValueTask<T?> TakeAsync(string id, CancellationToken cancellationToken = default)
    {
        return ShortLivedId.IsWellFormed(id) ? _store.TakeAsync(_keyPrefix, id, _typeInfo, _logger, cancellationToken) : default;
    }

    /// <summary>
    /// Removes the state with the specified ID, if there is one. Nothing is removed if the ID does not have the form of an ID.
    /// </summary>
    /// <param name="id">The ID that <see cref="AddAsync"/> returned.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public ValueTask RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        return ShortLivedId.IsWellFormed(id) ? _store.RemoveAsync(_keyPrefix, id, cancellationToken) : default;
    }
}
