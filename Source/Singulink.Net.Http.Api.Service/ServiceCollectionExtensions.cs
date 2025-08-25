namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Extension methods for registering HTTP API services in an <see cref="IServiceCollection"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the specified trusted origins and adds CORS services to the service collection.
    /// </summary>
    /// <param name="services">The service collection to add the trusted origins to.</param>
    /// <param name="trustedOrigins">The trusted origins to register. Can use wildcards at the start of the origin to match subdomains (e.g.
    /// <c>*.example.com</c>).</param>
    public static IServiceCollection AddTrustedOrigins(this IServiceCollection services, params string[] trustedOrigins)
    {
        services.AddSingleton<IOriginValidator>(new OriginValidator(trustedOrigins));
        services.AddCors();

        return services;
    }
}

/// <summary>
/// Extension methods for configuring an <see cref="IApplicationBuilder"/> to use HTTP API services.
/// </summary>
public static class ApplicationBuilderExtensions
{
    /// <summary>
    /// Configures the application to use CORS with the trusted origins registered in the service collection.
    /// </summary>
    public static IApplicationBuilder UseTrustedOrigins(this IApplicationBuilder app)
    {
        var originValidator = app.ApplicationServices.GetRequiredService<IOriginValidator>();

        app.UseCors(policy => policy
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
            .SetIsOriginAllowed(originValidator.IsTrusted));

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
