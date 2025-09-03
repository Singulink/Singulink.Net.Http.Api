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
