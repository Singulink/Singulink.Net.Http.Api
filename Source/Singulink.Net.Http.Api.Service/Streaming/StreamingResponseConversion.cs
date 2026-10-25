namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Applies the streaming response conversion to the endpoints of the route builders that <see cref="WebApplicationExtensions.UseApiResponseHandling"/> is
/// called on, by replacing their endpoint data sources with <see cref="StreamingResponseEndpointDataSource"/> decorators when the host builds the
/// middleware pipeline.
/// </summary>
/// <remarks>
/// <para>
/// The host runs its startup filters when it builds the middleware pipeline at startup, after the endpoints are mapped. A <see cref="WebApplication"/>
/// configures its own pipeline in the innermost step: that adds the routing middleware if the app does not, adds the app's data sources to the
/// <see cref="EndpointDataSource"/> service that API descriptions and link generation use, and builds the middleware, which is when a routing middleware
/// reads the data sources. The filter replaces the data sources before that, so every routing middleware of the app and the service get the decorators,
/// wherever the call is in the pipeline and whether the endpoints are mapped before or after it.
/// </para>
/// <para>
/// A <c>UseEndpoints</c> call on the app adds the data sources of the app to the service right away, whether its callback maps endpoints or not, which
/// would list their endpoints twice once they are replaced (unconverted and converted), so it fails at startup instead.
/// </para>
/// <para>
/// An app that registers the services but never calls <see cref="WebApplicationExtensions.UseApiResponseHandling"/>, which adds the app's route builder,
/// fails at startup too: without the exception middleware, every API exception would be answered with a <c>500 Internal Server Error</c>.
/// </para>
/// </remarks>
internal sealed class StreamingResponseConversion(IHostEnvironment environment)
{
    private readonly Lock _lock = new();
    private readonly List<IEndpointRouteBuilder> _routeBuilders = [];

    /// <summary>
    /// Adds a route builder whose endpoints are converted.
    /// </summary>
    public void Add(IEndpointRouteBuilder routeBuilder)
    {
        lock (_lock)
        {
            if (!_routeBuilders.Contains(routeBuilder))
                _routeBuilders.Add(routeBuilder);
        }
    }

    private void DecorateDataSources()
    {
        IEndpointRouteBuilder[] routeBuilders;

        lock (_lock)
            routeBuilders = [.. _routeBuilders];

        if (routeBuilders.Length is 0)
        {
            throw new InvalidOperationException(
                "API response handling is registered with AddApiResponseHandling, but UseApiResponseHandling is not called, so API exceptions would be " +
                "answered with a 500 Internal Server Error. Call UseApiResponseHandling on the app when configuring the pipeline.");
        }

        bool isDevelopment = environment.IsDevelopment();

        foreach (var routeBuilder in routeBuilders)
        {
            var dataSources = routeBuilder.DataSources;

            if (dataSources.All(s => s is StreamingResponseEndpointDataSource))
                continue;

            // This relies on the EndpointDataSource service being a CompositeEndpointDataSource whose DataSources is the list that UseEndpoints appends to.
            // If that changes, the check finds nothing and goes quiet.
            if (routeBuilder.ServiceProvider.GetService<EndpointDataSource>() is CompositeEndpointDataSource service &&
                service.DataSources.Any(dataSources.Contains))
            {
                throw new InvalidOperationException(
                    "API response handling does not support UseEndpoints on the app, which registers the app's endpoints with its EndpointDataSource " +
                    "service before they are converted at startup, whether they are mapped in its callback or on the app. Remove the UseEndpoints call, " +
                    "and map the endpoints on the app.");
            }

            var services = routeBuilder.ServiceProvider;

            var decorated = dataSources
                .Select(s => s as StreamingResponseEndpointDataSource ?? new StreamingResponseEndpointDataSource(s, services, isDevelopment))
                .ToArray();

            dataSources.Clear();

            foreach (var dataSource in decorated)
                dataSources.Add(dataSource);
        }
    }

    /// <summary>
    /// Replaces the data sources before the rest of the pipeline is configured.
    /// </summary>
    internal sealed class StartupFilter(StreamingResponseConversion conversion) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app => {
                conversion.DecorateDataSources();
                next(app);
            };
        }
    }
}
