namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Extension methods for configuring <see cref="WebApplication"/> and its related interfaces to use HTTP API services.
/// </summary>
public static class WebApplicationBuilderExtensions
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

    /// <inheritdoc cref="UseApiExceptionHandling(IApplicationBuilder)" />
    public static TBuilder UseApiExceptionHandling<TBuilder>(
        this TBuilder app,
        Action<ResponseExceptionEnumerationEndpointFilterOptions> configureEnumerationOptions)
            where TBuilder : IApplicationBuilder, IEndpointRouteBuilder
    {
        app.UseApiExceptionHandling();

        // Add our filter to all endpoints in the application.
        app.MapGroup(string.Empty).UseApiExceptionEnumerationHandling(configureEnumerationOptions);

        return app;
    }

    /// <summary>
    /// Configures the application to use <see cref="ResponseExceptionEnumerationEndpointFilter" /> for handling API exceptions.
    /// </summary>
    public static IEndpointConventionBuilder UseApiExceptionEnumerationHandling(
        this IEndpointConventionBuilder builder,
        Action<ResponseExceptionEnumerationEndpointFilterOptions> configureOptions)
    {
        ResponseExceptionEnumerationEndpointFilterOptions options = new();
        configureOptions(options);

        if (options._enumerableTypes.Count > 0)
        {
            // Note: we have to use an endpoint filter as we need to wrap the I{Async}Enumerable<T> itself.
            builder.Add((endpointBuilder) =>
            {
                bool isDevelopment = endpointBuilder.ApplicationServices.GetRequiredService<IHostEnvironment>().IsDevelopment();
                ResponseExceptionEnumerationEndpointFilter inst = new(options, isDevelopment);
                endpointBuilder.FilterFactories.Add((_, next) => async (context) => await inst.InvokeAsync(context, next));
            });
        }

        return builder;
    }
}
