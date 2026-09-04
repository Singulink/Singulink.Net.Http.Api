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
    /// Configures the application to use <see cref="ApiExceptionMiddleware"/> for handling exceptions and to convert endpoint results of type
    /// <see cref="IAsyncEnumerable{T}"/> (where <c>T</c> is a reference type) into <see cref="StreamingResponse"/> streams.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <remarks>
    /// <para>
    /// Streaming responses are applied to every matching endpoint already mapped on the application, so this must be called after all endpoints have been
    /// mapped. Streamed items (including <see langword="null"/> items) are serialized with the application's JSON options using the endpoint's declared item
    /// type, and each record is flushed as soon as it is produced. Exceptions thrown before the first record is sent produce a regular error response via
    /// <see cref="ApiExceptionMiddleware"/>; exceptions thrown after that are converted into an error record that terminates the stream. Both are mapped
    /// using the registered <see cref="IApiExceptionHandler"/> (see <see cref="ServiceCollectionExtensions.AddApiExceptionHandler{THandler}"/>).
    /// </para>
    /// <para>
    /// Endpoints can be marked with [<see cref="KeepAlivePingAttribute"/>] to automatically send ping records whenever no item has been produced for the
    /// specified interval, keeping the response connection alive.
    /// </para>
    /// </remarks>
    public static TBuilder UseApiResponseHandling<TBuilder>(this TBuilder app)
        where TBuilder : IApplicationBuilder, IEndpointRouteBuilder
    {
        app.UseMiddleware<ApiExceptionMiddleware>();

        bool isDevelopment = app.ApplicationServices.GetRequiredService<IHostEnvironment>().IsDevelopment();

        // The streaming conversion has to wrap the endpoint result itself, so it must run as an endpoint filter. Endpoint filters can only be attached while
        // an endpoint is being built, so we replace every currently registered endpoint data source with a decorator that rebuilds its endpoints with the
        // filter attached (the same mechanism a route group uses to apply a filter to its children, minus the route prefix). This requires the endpoints to
        // already be mapped, hence the "call after mapping" requirement documented above.
        var sources = app.DataSources.ToArray();
        app.DataSources.Clear();

        foreach (var source in sources)
            app.DataSources.Add(new StreamingResponseEndpointDataSource(source, app.ApplicationServices, isDevelopment));

        return app;
    }
}
