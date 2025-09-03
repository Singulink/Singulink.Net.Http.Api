namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Extension methods for configuring <see cref="IApplicationBuilder"/> to use HTTP API services.
/// </summary>
public static class ApplicationBuilderExtensions
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
}
