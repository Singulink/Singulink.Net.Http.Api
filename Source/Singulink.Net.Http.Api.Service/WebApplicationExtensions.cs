namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Extension methods for configuring <see cref="WebApplication"/> and its related interfaces to use HTTP API services.
/// </summary>
public static class WebApplicationExtensions
{
    /// <summary>
    /// Configures the application to use CORS with the allowed origins registered in the service collection.
    /// </summary>
    public static IApplicationBuilder UseAllowedOrigins(this IApplicationBuilder app)
    {
        var originValidator = app.ApplicationServices.GetRequiredService<IOriginValidator>();

        app.UseCors(policy => policy
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
            .SetIsOriginAllowed(originValidator.IsAllowed));

        return app;
    }

    /// <summary>
    /// Configures the application to use <see cref="ApiExceptionMiddleware"/> for handling API exceptions.
    /// </summary>
    public static IApplicationBuilder UseApiExceptionHandling(this IApplicationBuilder app)
    {
        return app.UseMiddleware<ApiExceptionMiddleware>();
    }

    /// <summary>
    /// <inheritdoc cref="UseApiExceptionHandling(IApplicationBuilder)" path="/summary" />
    /// </summary>
    /// <remarks>
    /// <para>
    /// This overload also registers a <see cref="ResponseExceptionEnumerationEndpointFilter" /> that applies to every endpoint already mapped on the
    /// application, so it must be called after all endpoints have been mapped.
    /// </para>
    /// <para>
    /// By default, suppressed exceptions are logged to trace; this behaviour can be customized by calling
    /// <see cref="ResponseExceptionEnumerationEndpointFilterOptions.AddExceptionObserver(Action{Exception})" />.
    /// </para>
    /// </remarks>
    public static TBuilder UseApiExceptionHandling<TBuilder>(
        this TBuilder app,
        Action<ResponseExceptionEnumerationEndpointFilterOptions> configureEnumerationOptions)
            where TBuilder : IApplicationBuilder, IEndpointRouteBuilder
    {
        app.UseApiExceptionHandling();

        ResponseExceptionEnumerationEndpointFilterOptions options = new();
        configureEnumerationOptions(options);

        if (options._enumerableTypes.Count is 0)
            return app;

        bool isDevelopment = app.ApplicationServices.GetRequiredService<IHostEnvironment>().IsDevelopment();
        ResponseExceptionEnumerationEndpointFilter filter = new(options, isDevelopment);

        // The filter has to wrap the I{Async}Enumerable<T> result itself, so it must run as an endpoint filter. Endpoint filters can only be attached while
        // an endpoint is being built, so we replace every currently registered endpoint data source with a decorator that rebuilds its endpoints with the
        // filter attached (the same mechanism a route group uses to apply a filter to its children, minus the route prefix). This requires the endpoints to
        // already be mapped, hence the "call after mapping" requirement documented above.
        var sources = app.DataSources.ToArray();
        app.DataSources.Clear();

        foreach (var source in sources)
        {
            app.DataSources.Add(new ResponseExceptionEnumerationEndpointDataSource(source, app.ApplicationServices, filter));
        }

        return app;
    }
}
