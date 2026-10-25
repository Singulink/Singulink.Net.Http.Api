using Microsoft.Net.Http.Headers;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Extension methods for configuring <see cref="WebApplication"/> and its related interfaces to use HTTP API services.
/// </summary>
public static class WebApplicationExtensions
{
    // The longest that Chrome caches a preflight. Without it, browsers cache one for 5 seconds, so most requests from another origin that need one, such
    // as the chunks of an upload, would each be preceded by one.
    private static readonly TimeSpan PreflightMaxAge = TimeSpan.FromHours(2);

    /// <summary>
    /// Configures the application to use CORS with the allowed origins registered in the service collection. Responses expose their <c>Retry-After</c>,
    /// <c>Reference-Id</c> and <c>Location</c> headers to those origins, and browsers may cache the answers to preflight requests for 2 hours
    /// (<c>Access-Control-Max-Age: 7200</c>).
    /// </summary>
    /// <remarks>
    /// Outside the Development environment, allowed origins that match a loopback host, such as <c>localhost</c>, are logged as a warning.
    /// </remarks>
    public static IApplicationBuilder UseAllowedOrigins(this IApplicationBuilder app)
    {
        var originValidator = app.ApplicationServices.GetRequiredService<IOriginValidator>();

        if (originValidator is OriginValidator { AllowsLoopbackHosts: true } &&
            app.ApplicationServices.GetService<IHostEnvironment>() is { } environment && !environment.IsDevelopment() &&
            app.ApplicationServices.GetService<ILoggerFactory>() is { } loggerFactory)
        {
            loggerFactory.CreateLogger("Singulink.Net.Http.Api.Service.AllowedOrigins").LogWarning(
                "The allowed origins include a loopback host, such as localhost, in the {Environment} environment. Pages served from the machine of a " +
                "user, such as by a development server or another app, can then make requests with the user's session cookie.",
                environment.EnvironmentName);
        }

        app.UseCors(policy => policy
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
            .WithExposedHeaders(HeaderNames.RetryAfter, ResponseExceptionInfo.ReferenceIdHeaderName, HeaderNames.Location)
            .SetPreflightMaxAge(PreflightMaxAge)
            .SetIsOriginAllowed(originValidator.IsAllowed));

        return app;
    }

    /// <summary>
    /// Configures the application to use <see cref="ApiExceptionMiddleware"/> for handling exceptions and to convert endpoint results of type
    /// <see cref="IAsyncEnumerable{T}"/> into <see cref="StreamingResponse"/> streams. <c>T</c> must be a reference type (value type items fail when the
    /// endpoints are built). Requires <see cref="ServiceCollectionExtensions.AddApiResponseHandling(IServiceCollection)"/>.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <remarks>
    /// <para>
    /// The middleware handles the exceptions of the endpoints and of the middleware that comes after it in the pipeline. The streaming conversion applies
    /// to the minimal API route handlers of the application's own route builder, including the ones in route groups, whether they are mapped before or
    /// after this call: it is attached when the host builds the middleware pipeline at startup. Streamed items (including <see langword="null"/> items) are
    /// serialized with the application's JSON options using the endpoint's declared item type, and each record is flushed as soon as it is produced.
    /// Exceptions thrown before the first record is sent produce a regular error response via <see cref="ApiExceptionMiddleware"/>; exceptions thrown after
    /// that are converted into an error record that terminates the stream. Both are mapped using the registered <see cref="IApiExceptionHandler"/> (see
    /// <see cref="ServiceCollectionExtensions.AddApiExceptionHandler{THandler}"/>).
    /// </para>
    /// <para>
    /// Endpoints can be marked with [<see cref="KeepAlivePingAttribute"/>] to send ping records whenever no item has been produced for the specified
    /// interval.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException"><see cref="ServiceCollectionExtensions.AddApiResponseHandling(IServiceCollection)"/> was not
    /// called.</exception>
    public static TBuilder UseApiResponseHandling<TBuilder>(this TBuilder app)
        where TBuilder : IApplicationBuilder, IEndpointRouteBuilder
    {
        var conversion = app.ApplicationServices.GetService<StreamingResponseConversion>() ?? throw new InvalidOperationException(
            "API response handling requires its services. Register them with AddApiResponseHandling.");

        app.UseMiddleware<ApiExceptionMiddleware>();
        conversion.Add(app);

        return app;
    }
}
