using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Extension methods for registering HTTP API services in an <see cref="IServiceCollection"/>.
/// </summary>
public static partial class ServiceCollectionExtensions
{
    private const string SessionTokenSerializationMessage =
        "Session tokens are serialized with reflection-based JSON serialization. Use the AddHttpSessionHandling overload that takes a " +
        "JsonTypeInfo<TSessionToken>, for example from a source-generated JsonSerializerContext.";

    /// <summary>
    /// Registers allowed origins and CORS services.
    /// </summary>
    /// <param name="services">The service collection to add the allowed origins to.</param>
    /// <param name="allowedOrigins">The allowed host patterns, such as <c>example.com</c> or <c>*.example.com</c>. A host matches <c>https</c> origins, and
    /// <c>http</c> origins too for loopback hosts. See <see cref="OriginValidator(string[])"/> for patterns with a scheme or port.</param>
    /// <exception cref="ArgumentException">A pattern is not a valid host pattern.</exception>
    public static IServiceCollection AddAllowedOrigins(this IServiceCollection services, params string[] allowedOrigins)
    {
        services.AddSingleton<IOriginValidator>(new OriginValidator(allowedOrigins));
        services.AddCors();

        return services;
    }

    /// <summary>
    /// Registers the services of <see cref="WebApplicationExtensions.UseApiResponseHandling{TBuilder}(TBuilder)"/>, including the startup filter that
    /// attaches the streaming response conversion to the endpoints when the host builds the middleware pipeline. The app fails to start if it does not call
    /// <see cref="WebApplicationExtensions.UseApiResponseHandling{TBuilder}(TBuilder)"/>.
    /// </summary>
    /// <remarks>
    /// In an app that registers SignalR, it also adds the hub filter that confirms to the toolkit's client that a hub accepted its connection, once the
    /// hub's <c>OnConnectedAsync</c> succeeded (see the remarks of <c>ApiHubConnection</c> in the SignalR client library), as
    /// <see cref="AddHttpSessionHandling{TSessionToken, TSessionStore}(IServiceCollection, JsonTypeInfo{TSessionToken}, Action{SessionHandlingOptions}?)"/>
    /// does.
    /// </remarks>
    public static IServiceCollection AddApiResponseHandling(this IServiceCollection services)
    {
        services.TryAddSingleton<StreamingResponseConversion>();
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, StreamingResponseConversion.StartupFilter>());
        AddConnectionConfirmation(services);

        return services;
    }

    /// <summary>
    /// Registers the default <see cref="ApiExceptionHandler"/>, which reports API exceptions as-is and logs unexpected exceptions with a reference ID that is
    /// reported to the client (or propagates them in development environments). Minimal API endpoints are also set to throw for requests they cannot read,
    /// so that those are reported as API errors too (see <see cref="AddApiExceptionHandler{THandler}(IServiceCollection)"/>).
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
    /// <remarks>
    /// This also configures <see cref="RouteHandlerOptions.ThrowOnBadRequest"/> to be <see langword="true"/> in every environment, so that minimal API
    /// endpoints throw a <see cref="BadHttpRequestException"/> for a request they cannot read (such as a malformed JSON body, a value of the wrong type or
    /// an unsupported content type) instead of answering with an empty response. <see cref="ApiExceptionMiddleware"/> reports it to the client with its
    /// status code and, in the Development environment, ASP.NET Core's message. In other environments the message is a general one for the status, which
    /// names the request's content type for a 415 and has the location that System.Text.Json gives for an error in JSON. Which properties a body must have,
    /// and which can be null, is up to the app's JSON options. Options configurations run in the order they are registered, so a configuration of the
    /// option that the app registers after this call applies, and one that it registers before is overridden. Calling this again does not configure the
    /// option again.
    /// </remarks>
    public static IServiceCollection AddApiExceptionHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(
        this IServiceCollection services)
        where THandler : class, IApiExceptionHandler
    {
        services.AddSingleton<IApiExceptionHandler, THandler>();
        AddThrowOnBadRequest(services);

        return services;
    }

    /// <summary>
    /// Configures the JSON serializer options of minimal API requests and responses (<see cref="Microsoft.AspNetCore.Http.Json.JsonOptions"/>) and of
    /// SignalR hub payloads (<see cref="JsonHubProtocolOptions.PayloadSerializerOptions"/>): inserts <paramref name="resolver"/> at the front of their type
    /// info resolvers, runs <paramref name="configure"/>, then applies the API null handling (see <see
    /// cref="ApiNullHandlingExtensions.ConfigureApiNullHandling(JsonSerializerOptions)"/>).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="resolver">The type info resolver of the app's API types, such as a source-generated <see cref="JsonSerializerContext"/>.</param>
    /// <param name="configure">An optional callback that configures each of the two options instances before the null handling is applied. Resolvers it
    /// adds are covered by the null handling.</param>
    /// <remarks>
    /// The hub payload options are configured whether SignalR is registered before or after this call, and are only used if it is registered.
    /// Configuration of either options instance that is registered after this call runs after it, so resolvers that it adds are not covered by the null
    /// handling.
    /// </remarks>
    public static IServiceCollection AddApiJson(
        this IServiceCollection services, IJsonTypeInfoResolver resolver, Action<JsonSerializerOptions>? configure = null)
    {
        services.ConfigureHttpJsonOptions(options => Configure(options.SerializerOptions));
        services.Configure<JsonHubProtocolOptions>(options => Configure(options.PayloadSerializerOptions));

        return services;

        void Configure(JsonSerializerOptions options)
        {
            options.TypeInfoResolverChain.Insert(0, resolver);
            configure?.Invoke(options);
            options.ConfigureApiNullHandling();
        }
    }

    /// <summary>
    /// Registers services required for enabling HTTP session handling. Session tokens are serialized with reflection-based JSON serialization and
    /// <see cref="JsonSerializerOptions.Default">JsonSerializerOptions.Default</see>.
    /// </summary>
    /// <typeparam name="TSessionToken">The session token type.</typeparam>
    /// <typeparam name="TSessionStore">The session store type, which is registered as a scoped service.</typeparam>
    /// <param name="services">The service collection to add session handling services to.</param>
    /// <param name="configure">An optional action to configure options, which are validated when the app starts.</param>
    /// <remarks>
    /// <para>
    /// Session cookies are protected with ASP.NET Core data protection, and can only be read with the keys they were protected with. If no <see
    /// cref="IDataProtectionProvider"/> is registered yet, this registers data protection with <see
    /// cref="DataProtectionServiceCollectionExtensions.AddDataProtection(IServiceCollection)"/>, whose keys are kept in ASP.NET Core's default location
    /// unless the app configures where they are kept. A provider that is already registered is used as it is. Requests are checked against the allowed
    /// origins (see <see cref="AddAllowedOrigins(IServiceCollection, string[])"/>). The app fails to start if they are not registered.
    /// </para>
    /// <para>
    /// Reflection-based serialization is not compatible with trimming or Native AOT. The <see cref="AddHttpSessionHandling{TSessionToken,
    /// TSessionStore}(IServiceCollection, JsonTypeInfo{TSessionToken}, Action{SessionHandlingOptions}?)"/> overload serializes session tokens with the
    /// specified JSON type info instead.
    /// </para>
    /// </remarks>
    [RequiresUnreferencedCode(SessionTokenSerializationMessage)]
    [RequiresDynamicCode(SessionTokenSerializationMessage)]
    public static IServiceCollection AddHttpSessionHandling<
        TSessionToken,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TSessionStore>(
        this IServiceCollection services,
        Action<SessionHandlingOptions>? configure = null)
        where TSessionToken : class, ISessionToken
        where TSessionStore : class, ISessionStore<TSessionToken>
    {
        var tokenTypeInfo = JsonSerializerOptions.Default.GetTypeInfo<TSessionToken>();
        return services.AddHttpSessionHandling<TSessionToken, TSessionStore>(tokenTypeInfo, configure);
    }

    /// <summary>
    /// Registers services required for enabling HTTP session handling. Session tokens are serialized with the specified JSON type info.
    /// </summary>
    /// <typeparam name="TSessionToken">The session token type.</typeparam>
    /// <typeparam name="TSessionStore">The session store type, which is registered as a scoped service.</typeparam>
    /// <param name="services">The service collection to add session handling services to.</param>
    /// <param name="tokenTypeInfo">The JSON type info for the session token type, for example from a source-generated
    /// <see cref="JsonSerializerContext"/>.</param>
    /// <param name="configure">An optional action to configure options, which are validated when the app starts.</param>
    /// <remarks>
    /// <para>
    /// Session cookies are protected with ASP.NET Core data protection, and can only be read with the keys they were protected with. If no <see
    /// cref="IDataProtectionProvider"/> is registered yet, this registers data protection with <see
    /// cref="DataProtectionServiceCollectionExtensions.AddDataProtection(IServiceCollection)"/>, whose keys are kept in ASP.NET Core's default location
    /// unless the app configures where they are kept. A provider that is already registered is used as it is. Requests are checked against the allowed
    /// origins (see <see cref="AddAllowedOrigins(IServiceCollection, string[])"/>). The app fails to start if they are not registered.
    /// </para>
    /// <para>
    /// In an app that registers SignalR, the sessions of hub connections are revalidated, unless
    /// <see cref="SessionHandlingOptions.RevalidateHubConnections"/> is <see langword="false"/>. It also adds the hub filter that confirms to the toolkit's
    /// client that a hub accepted its connection, as <see cref="AddApiResponseHandling(IServiceCollection)"/> does.
    /// </para>
    /// <para>
    /// Existing session cookies are read with the specified type info, so they can only be read if its JSON settings match the ones they were written with.
    /// The <see cref="AddHttpSessionHandling{TSessionToken, TSessionStore}(IServiceCollection, Action{SessionHandlingOptions}?)"/> overload uses
    /// <see cref="JsonSerializerOptions.Default">JsonSerializerOptions.Default</see>, whose settings a source-generated context without
    /// <see cref="JsonSourceGenerationOptionsAttribute"/> settings also uses.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddHttpSessionHandling<
        TSessionToken,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TSessionStore>(
        this IServiceCollection services,
        JsonTypeInfo<TSessionToken> tokenTypeInfo,
        Action<SessionHandlingOptions>? configure = null)
        where TSessionToken : class, ISessionToken
        where TSessionStore : class, ISessionStore<TSessionToken>
    {
        services.AddLogging();

        // An app's own provider is used as it is, without setting up ASP.NET Core's default key ring as well.
        if (!services.Any(d => d.ServiceType == typeof(IDataProtectionProvider) && !d.IsKeyedService))
            services.AddDataProtection();

        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<SessionHandlingOptions>().ValidateOnStart();
        services.Configure<SessionHandlingOptions>(options => configure?.Invoke(options));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SessionHandlingOptions>, SessionHandlingOptionsValidator>());
        services.AddSingleton(new SessionCookieSerializer<TSessionToken>(tokenTypeInfo));
        services.AddScoped<ISessionStore<TSessionToken>, TSessionStore>();
        services.AddSingleton<SessionHandler<TSessionToken>>();
        services.AddSingleton<ISessionContextFactory<TSessionToken>, HttpSessionContextFactory<TSessionToken>>();
        services.AddSingleton<SessionConnectionTracker<TSessionToken>>();
        services.AddSingleton<ISessionConnectionTracker>(sp => sp.GetRequiredService<SessionConnectionTracker<TSessionToken>>());

        // Only used by an app that registers SignalR.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<HubOptions>, SessionRevalidationHubFilter.Setup>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<JsonHubProtocolOptions>, ToolkitHubJsonSetup>());
        AddConnectionConfirmation(services);

        return services;
    }

    /// <summary>
    /// Registers the hub filter that confirms to the toolkit's client that a hub accepted its connection (see <see cref="ConnectionConfirmationHubFilter"/>),
    /// once, however many of the registrations that add it the app calls. Only used by an app that registers SignalR.
    /// </summary>
    private static void AddConnectionConfirmation(IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<HubOptions>, ConnectionConfirmationHubFilter.Setup>());
    }

    /// <summary>
    /// Configures <see cref="RouteHandlerOptions.ThrowOnBadRequest"/> to be <see langword="true"/>, once, after ASP.NET Core's default (which only throws in
    /// development). Configuration that the app registers afterwards runs after it.
    /// </summary>
    private static void AddThrowOnBadRequest(IServiceCollection services)
    {
        // Registers ASP.NET Core's routing services, and with them its default, if they aren't registered yet, so that it comes before the toolkit's.
        services.AddRoutingCore();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<RouteHandlerOptions>, ThrowOnBadRequestConfiguration>());
    }

    private sealed class ThrowOnBadRequestConfiguration : IConfigureOptions<RouteHandlerOptions>
    {
        public void Configure(RouteHandlerOptions options) => options.ThrowOnBadRequest = true;
    }
}
