namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Endpoint filter attached to endpoints that return <see cref="IAsyncEnumerable{T}"/> results, converting the result into a <see cref="StreamingResponse"/>.
/// </summary>
internal sealed class StreamingResponseEndpointFilter : IEndpointFilter
{
    private readonly Type _itemType;
    private readonly bool _isDevelopment;

    public StreamingResponseEndpointFilter(Type itemType, bool isDevelopment)
    {
        _itemType = itemType;
        _isDevelopment = isDevelopment;
    }

    /// <summary>
    /// Gets the declared item type of the endpoint's enumerable result.
    /// </summary>
    public Type ItemType => _itemType;

    /// <summary>
    /// Gets or sets the keep-alive ping interval. Set once the endpoint's metadata is complete (see <see cref="StreamingResponseEndpointDataSource"/>).
    /// </summary>
    public TimeSpan? PingInterval { get; set; }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        object? result = await next(context);

        if (result is IAsyncEnumerable<object?> source)
            return new StreamingResponseResult(source, _itemType, PingInterval, _isDevelopment);

        return result;
    }
}
