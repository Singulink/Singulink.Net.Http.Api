using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Endpoint filter that handles exceptions thrown during request processing of enumerable results.
/// </summary>
public sealed class ResponseExceptionEnumerationEndpointFilter(ResponseExceptionEnumerationEndpointFilterOptions options, bool isDevelopment) : IEndpointFilter
{
    private readonly ResponseExceptionEnumerationEndpointFilterOptions.HelperBase[] _helpers = [.. options._enumerableTypes];
    private readonly ConditionalWeakTable<Type, ResponseExceptionEnumerationEndpointFilterOptions.HelperBase?> _lookupCache = [];
    private readonly bool _isDevelopment = isDevelopment;
    private readonly Action<Exception>? _unhandledExceptionCallback = options._unhandledExceptionCallback;

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        // Get the return value of the endpoint
        object? result = await next(context);

        // Check if they are candidates for wrapping
        if (result is IAsyncEnumerable<ISupportsResponseException?> asyncEnumerable)
        {
            Type t = asyncEnumerable.GetType();

            // Check the cache for a helper for this type
            if (!_lookupCache.TryGetValue(t, out var cachedHelper))
            {
                cachedHelper = null;

                // Loop through all helpers to find one that can wrap this type
                foreach (var helper in _helpers.AsSpan())
                {
                    if (helper.TryWrap(asyncEnumerable, _isDevelopment, _unhandledExceptionCallback, out var wrapped))
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
                bool success = cachedHelper.TryWrap(asyncEnumerable, _isDevelopment, _unhandledExceptionCallback, out var wrapped);
                Debug.Assert(success, "The cached helper should always be able to wrap the type it was cached for.");
                result = wrapped;
            }
        }
        else if (result is IEnumerable<ISupportsResponseException?> enumerable)
        {
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
}
