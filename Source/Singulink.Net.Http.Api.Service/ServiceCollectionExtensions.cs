namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Extension methods for registering HTTP API services in an <see cref="IServiceCollection"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers allowed origins and CORS services.
    /// </summary>
    /// <param name="services">The service collection to add the allowed origins to.</param>
    /// <param name="allowedOrigins">The allowed origins to register. Can use wildcards at the start of the origin to match subdomains (e.g.
    /// <c>*.example.com</c>).</param>
    public static IServiceCollection AddAllowedOrigins(this IServiceCollection services, params string[] allowedOrigins)
    {
        services.AddSingleton<IOriginValidator>(new OriginValidator(allowedOrigins));
        services.AddCors();

        return services;
    }

    /// <summary>
    /// Registers the default <see cref="ApiExceptionHandler"/>, which reports API exceptions as-is and logs unexpected exceptions with a reference ID that is
    /// reported to the client (or propagates them in development environments).
    /// </summary>
    public static IServiceCollection AddApiExceptionHandler(this IServiceCollection services)
    {
        return services.AddApiExceptionHandler<DefaultApiExceptionHandler>();
    }

    /// <summary>
    /// Registers an <see cref="IApiExceptionHandler"/> that maps exceptions thrown during request processing to the errors reported to clients. Derive from
    /// <see cref="ApiExceptionHandler"/> to customize the default behavior with application-specific exception mappings.
    /// </summary>
    /// <typeparam name="THandler">The handler type.</typeparam>
    public static IServiceCollection AddApiExceptionHandler<THandler>(this IServiceCollection services)
        where THandler : class, IApiExceptionHandler
    {
        services.AddSingleton<IApiExceptionHandler, THandler>();
        return services;
    }

    /// <summary>
    /// Registers services required for enabling HTTP session handling.
    /// </summary>
    /// <typeparam name="TSessionToken">The session token type.</typeparam>
    /// <typeparam name="TSessionData">The session storage entry type.</typeparam>
    /// <typeparam name="TSessionStoreContextFactory">The factory type for creating session store contexts.</typeparam>
    /// <param name="services">The service collection to add session handling services to.</param>
    /// <param name="configure">An optional action to configure options.</param>
    public static IServiceCollection AddHttpSessionHandling<TSessionToken, TSessionData, TSessionStoreContextFactory>(
        this IServiceCollection services,
        Action<SessionHandlingOptions>? configure = null)
        where TSessionToken : class, ISessionToken
        where TSessionData : class, ISessionData
        where TSessionStoreContextFactory : class, ISessionStoreContextFactory<TSessionToken, TSessionData>
    {
        services.Configure<SessionHandlingOptions>(options => configure?.Invoke(options));
        services.AddSingleton<ISessionStoreContextFactory<TSessionToken, TSessionData>, TSessionStoreContextFactory>();
        services.AddSingleton<IHttpSessionContextFactory<TSessionToken>, HttpSessionContextFactory<TSessionToken, TSessionData>>();

        return services;
    }
}
