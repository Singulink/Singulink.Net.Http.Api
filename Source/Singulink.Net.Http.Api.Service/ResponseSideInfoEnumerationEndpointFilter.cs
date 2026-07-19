using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Endpoint filter that communicates response side info (such as exceptions thrown during request processing and periodic pings) for enumerable results.
/// </summary>
public sealed class ResponseSideInfoEnumerationEndpointFilter(ResponseSideInfoEnumerationEndpointFilterOptions options, bool isDevelopment) : IEndpointFilter
{
    private readonly ResponseSideInfoEnumerationEndpointFilterOptions.HelperBase[] _helpers = [.. options._enumerableTypes];
    private readonly ConditionalWeakTable<Type, ResponseSideInfoEnumerationEndpointFilterOptions.HelperBase?> _lookupCache = [];
    private readonly ConditionalWeakTable<Endpoint, StrongBox<TimeSpan?>> _pingIntervalCache = [];
    private readonly bool _isDevelopment = isDevelopment;
    private readonly Action<Exception>? _unhandledExceptionCallback = options._unhandledExceptionCallback;

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        // Get the return value of the endpoint
        object? result = await next(context);

        // Check if they are candidates for wrapping
        if (result is IAsyncEnumerable<ISupportsResponseSideInfo?> asyncEnumerable)
        {
            TimeSpan? pingInterval = GetPingInterval(context.HttpContext.GetEndpoint());

            Type t = asyncEnumerable.GetType();

            // Check the cache for a helper for this type
            if (!_lookupCache.TryGetValue(t, out var cachedHelper))
            {
                cachedHelper = null;

                // Loop through all helpers to find one that can wrap this type
                foreach (var helper in _helpers.AsSpan())
                {
                    if (helper.TryWrap(asyncEnumerable, _isDevelopment, _unhandledExceptionCallback, pingInterval, out var wrapped))
                    {
                        cachedHelper = helper;
                        result = wrapped;
                        break;
                    }
                }

                // Cache the result for future lookups
                _lookupCache.Add(t, cachedHelper);
            }
            else if (cachedHelper is { })
            {
                // Use the cached helper to wrap the enumerable
                bool success = cachedHelper.TryWrap(asyncEnumerable, _isDevelopment, _unhandledExceptionCallback, pingInterval, out var wrapped);
                Debug.Assert(success, "The cached helper should always be able to wrap the type it was cached for.");
                result = wrapped;
            }
        }
        else if (result is IEnumerable<ISupportsResponseSideInfo?> enumerable)
        {
            if (GetPingInterval(context.HttpContext.GetEndpoint()) is not null)
            {
                throw new InvalidOperationException(
                    $"[{nameof(KeepAlivePingAttribute)}] is only supported on endpoints that return 'IAsyncEnumerable<T>' results. " +
                    $"Synchronous 'IEnumerable<T>' results cannot send pings.");
            }

            Type t = enumerable.GetType();

            // Check the cache for a helper for this type
            if (!_lookupCache.TryGetValue(t, out var cachedHelper))
            {
                cachedHelper = null;

                // Loop through all helpers to find one that can wrap this type
                foreach (var helper in _helpers.AsSpan())
                {
                    if (helper.TryWrap(enumerable, _isDevelopment, _unhandledExceptionCallback, out var wrapped))
                    {
                        cachedHelper = helper;
                        result = wrapped;
                        break;
                    }
                }

                // Cache the result for future lookups
                _lookupCache.Add(t, cachedHelper);
            }
            else if (cachedHelper is { })
            {
                // Use the cached helper to wrap the enumerable
                bool success = cachedHelper.TryWrap(enumerable, _isDevelopment, _unhandledExceptionCallback, out var wrapped);
                Debug.Assert(success, "The cached helper should always be able to wrap the type it was cached for.");
                result = wrapped;
            }
        }

        // Return our result
        return result;
    }

    private TimeSpan? GetPingInterval(Endpoint? endpoint)
    {
        if (endpoint is null)
            return null;

        if (!_pingIntervalCache.TryGetValue(endpoint, out var cached))
        {
            cached = new StrongBox<TimeSpan?>(endpoint.Metadata.GetMetadata<KeepAlivePingAttribute>()?.Interval);
            _pingIntervalCache.AddOrUpdate(endpoint, cached);
        }

        return cached.Value;
    }
}
