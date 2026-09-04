using System.Reflection;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Primitives;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Decorates an <see cref="EndpointDataSource"/> so that endpoints returning <see cref="IAsyncEnumerable{T}"/> results are converted into streaming
/// responses. This is the same mechanism a route group uses to apply conventions to its children, except no route prefix is added so the endpoints are left
/// otherwise unchanged.
/// </summary>
/// <remarks>
/// Endpoint filters can only be attached by a regular convention, which runs before the handler's attributes and inferred metadata are added to the
/// endpoint. Metadata is only complete in a finally convention, but by then the request delegate (and its filters) has already been built. The work is
/// therefore split: the regular convention attaches the filter based on the handler's declared return type, and the finally convention completes the
/// filter's configuration from the attributes and fixes up the response metadata.
/// </remarks>
internal sealed class StreamingResponseEndpointDataSource : EndpointDataSource
{
    private static readonly RoutePattern EmptyPrefix = RoutePatternFactory.Parse(string.Empty);

    private readonly EndpointDataSource _source;
    private readonly IServiceProvider _applicationServices;
    private readonly Action<EndpointBuilder> _convention;
    private readonly Action<EndpointBuilder> _finallyConvention;

    public StreamingResponseEndpointDataSource(EndpointDataSource source, IServiceProvider applicationServices, bool isDevelopment)
    {
        _source = source;
        _applicationServices = applicationServices;
        _convention = builder => AttachFilter(builder, isDevelopment);
        _finallyConvention = CompleteFilter;
    }

    /// <inheritdoc/>
    public override IReadOnlyList<Endpoint> Endpoints => _source.GetGroupedEndpoints(new RouteGroupContext {
        Prefix = EmptyPrefix,
        Conventions = [_convention],
        FinallyConventions = [_finallyConvention],
        ApplicationServices = _applicationServices,
    });

    /// <inheritdoc/>
    public override IReadOnlyList<Endpoint> GetGroupedEndpoints(RouteGroupContext context)
    {
        return _source.GetGroupedEndpoints(new RouteGroupContext {
            Prefix = context.Prefix,
            Conventions = Append(context.Conventions, _convention),
            FinallyConventions = Append(context.FinallyConventions, _finallyConvention),
            ApplicationServices = context.ApplicationServices,
        });

        static Action<EndpointBuilder>[] Append(IReadOnlyList<Action<EndpointBuilder>> conventions, Action<EndpointBuilder> convention)
        {
            var result = new Action<EndpointBuilder>[conventions.Count + 1];

            for (int i = 0; i < conventions.Count; i++)
                result[i] = conventions[i];

            result[^1] = convention;
            return result;
        }
    }

    /// <inheritdoc/>
    public override IChangeToken GetChangeToken() => _source.GetChangeToken();

    private static void AttachFilter(EndpointBuilder builder, bool isDevelopment)
    {
        var methodInfo = builder.Metadata.OfType<MethodInfo>().FirstOrDefault();
        var itemType = methodInfo is null ? null : GetStreamingItemType(methodInfo.ReturnType);

        if (itemType is null)
            return;

        if (itemType.IsValueType)
        {
            throw new InvalidOperationException(
                $"Endpoint '{builder.DisplayName}' returns an 'IAsyncEnumerable<{itemType.Name}>' result with a value type item, which cannot be streamed. " +
                "Wrap the item in a record or class.");
        }

        var filter = new StreamingResponseEndpointFilter(itemType, isDevelopment);
        builder.FilterFactories.Add((_, next) => context => filter.InvokeAsync(context, next));

        // Stash the filter in the metadata so the finally convention can find and complete it. It is removed again there.
        builder.Metadata.Add(filter);
    }

    private static void CompleteFilter(EndpointBuilder builder)
    {
        var filter = builder.Metadata.OfType<StreamingResponseEndpointFilter>().FirstOrDefault();
        var pingInterval = builder.Metadata.OfType<KeepAlivePingAttribute>().FirstOrDefault()?.Interval;

        if (filter is null)
        {
            if (pingInterval is not null)
            {
                throw new InvalidOperationException(
                    $"[{nameof(KeepAlivePingAttribute)}] on endpoint '{builder.DisplayName}' is only supported on endpoints that return " +
                    $"'IAsyncEnumerable<T>' results.");
            }

            return;
        }

        builder.Metadata.Remove(filter);
        filter.PingInterval = pingInterval;

        // Replace the inferred success response metadata (a JSON array of the enumerable type) with the streaming content type and item type.

        for (int i = builder.Metadata.Count - 1; i >= 0; i--)
        {
            if (builder.Metadata[i] is IProducesResponseTypeMetadata { StatusCode: StatusCodes.Status200OK })
                builder.Metadata.RemoveAt(i);
        }

        builder.Metadata.Add(new ProducesResponseTypeMetadata(StatusCodes.Status200OK, filter.ItemType, [StreamingResponse.MediaType]));
    }

    /// <summary>
    /// Gets the item type for endpoint return types of the form <c>IAsyncEnumerable&lt;T&gt;</c>, <c>Task&lt;IAsyncEnumerable&lt;T&gt;&gt;</c> or
    /// <c>ValueTask&lt;IAsyncEnumerable&lt;T&gt;&gt;</c>, otherwise <see langword="null"/>.
    /// </summary>
    private static Type? GetStreamingItemType(Type returnType)
    {
        if (!returnType.IsGenericType)
            return null;

        var definition = returnType.GetGenericTypeDefinition();

        if (definition == typeof(Task<>) || definition == typeof(ValueTask<>))
        {
            returnType = returnType.GetGenericArguments()[0];

            if (!returnType.IsGenericType)
                return null;

            definition = returnType.GetGenericTypeDefinition();
        }

        return definition == typeof(IAsyncEnumerable<>) ? returnType.GetGenericArguments()[0] : null;
    }
}
