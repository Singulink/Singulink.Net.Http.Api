using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Primitives;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Decorates an <see cref="EndpointDataSource" /> so that the <see cref="ResponseSideInfoEnumerationEndpointFilter" /> is applied to every endpoint it
/// produces. This is the same mechanism a route group uses to apply conventions/filters to its children, except no route prefix is added so the endpoints
/// are left otherwise unchanged.
/// </summary>
internal sealed class ResponseSideInfoEnumerationEndpointDataSource : EndpointDataSource
{
    private static readonly RoutePattern EmptyPrefix = RoutePatternFactory.Parse(string.Empty);

    private readonly EndpointDataSource _source;
    private readonly IServiceProvider _applicationServices;
    private readonly Action<EndpointBuilder> _convention;

    public ResponseSideInfoEnumerationEndpointDataSource(
        EndpointDataSource source,
        IServiceProvider applicationServices,
        ResponseSideInfoEnumerationEndpointFilter filter)
    {
        _source = source;
        _applicationServices = applicationServices;
        _convention = builder => builder.FilterFactories.Add((_, next) => context => filter.InvokeAsync(context, next));
    }

    /// <inheritdoc />
    public override IReadOnlyList<Endpoint> Endpoints => _source.GetGroupedEndpoints(new RouteGroupContext()
    {
        Prefix = EmptyPrefix,
        Conventions = [_convention],
        FinallyConventions = [],
        ApplicationServices = _applicationServices,
    });

    /// <inheritdoc />
    public override IReadOnlyList<Endpoint> GetGroupedEndpoints(RouteGroupContext context)
    {
        var underlyingConventions = context.Conventions;

        var conventions = new Action<EndpointBuilder>[underlyingConventions.Count + 1];

        if (underlyingConventions is IList<Action<EndpointBuilder>> l)
        {
            l.CopyTo(conventions, 0);
        }
        else
        {
            for (int i = 0; i < underlyingConventions.Count; i++)
                conventions[i] = underlyingConventions[i];
        }

        conventions[^1] = _convention;

        return _source.GetGroupedEndpoints(new RouteGroupContext()
        {
            Prefix = context.Prefix,
            Conventions = conventions,
            FinallyConventions = context.FinallyConventions,
            ApplicationServices = context.ApplicationServices,
        });
    }

    /// <inheritdoc />
    public override IChangeToken GetChangeToken() => _source.GetChangeToken();
}
