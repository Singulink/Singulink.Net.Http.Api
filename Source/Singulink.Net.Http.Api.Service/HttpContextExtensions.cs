using System.Collections.Concurrent;
using System.Reflection;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Extension methods for <see cref="HttpContext"/>.
/// </summary>
public static class HttpContextExtensions
{
    private static readonly ConcurrentDictionary<ParameterInfo, (bool IsRequired, SessionAccessOptions Options)> _bindSessionTokenParamCache = [];

    /// <summary>
    /// Binds a session token from the HTTP context based on the parameter's nullability and attributes.
    /// </summary>
    public static async ValueTask<TSessionToken?> BindSessionTokenAsync<TSessionToken>(this HttpContext httpContext, ParameterInfo parameter)
        where TSessionToken : class, ISessionToken
    {
        var (isRequired, options) = _bindSessionTokenParamCache.GetOrAdd(parameter, p =>
        {
            var nullabilityInfo = new NullabilityInfoContext().Create(parameter);

            var sessionAccessAttr = p.GetCustomAttribute<SessionAccessAttribute>();
            return (nullabilityInfo.WriteState is NullabilityState.NotNull, sessionAccessAttr?.Options ?? default);
        });

        return isRequired ?
            await httpContext.GetRequiredSessionTokenAsync<TSessionToken>(options) :
            await httpContext.GetSessionTokenAsync<TSessionToken>(options);
    }

    /// <summary>
    /// Gets the session context from the HTTP context.
    /// </summary>
    /// <exception cref="InvalidOperationException">Session context was not found.</exception>
    public static HttpSessionContext<TSessionToken> GetRequiredSessionContext<TSessionToken>(this HttpContext httpContext)
        where TSessionToken : class, ISessionToken
    {
        return httpContext.GetSessionContext<TSessionToken>() ??
            throw new InvalidOperationException($"HttpContext.Items[{typeof(HttpSessionContext<TSessionToken>)}] was not found.");
    }

    /// <summary>
    /// Gets the session context from the HTTP context.
    /// </summary>
    /// <exception cref="InvalidOperationException">Session context was not found.</exception>
    public static HttpSessionContext<TSessionToken>? GetSessionContext<TSessionToken>(this HttpContext httpContext)
        where TSessionToken : class, ISessionToken
    {
        if (httpContext.Items.TryGetValue(typeof(HttpSessionContext<TSessionToken>), out object sessionContextObj))
        {
            if (sessionContextObj is HttpSessionContext<TSessionToken> sessionContext)
                return sessionContext;

            throw new InvalidOperationException($"HttpContext.Items[{typeof(HttpSessionContext<TSessionToken>)}] type mismatch.");
        }

        var factory = httpContext.RequestServices.GetService<IHttpSessionContextFactory<TSessionToken>>();

        if (factory is not null)
        {
            var sessionContext = factory.Create(httpContext);
            httpContext.Items[typeof(HttpSessionContext<TSessionToken>)] = sessionContext;

            return sessionContext;
        }

        return null;
    }

    /// <summary>
    /// Gets the session token from the HTTP context. If the user is not signed in, throws an <see cref="UnauthorizedApiException"/>.
    /// </summary>
    public static async ValueTask<TSessionToken> GetRequiredSessionTokenAsync<TSessionToken>(this HttpContext httpContext, SessionAccessOptions options = default)
        where TSessionToken : class, ISessionToken
    {
        var sessionContext = httpContext.GetRequiredSessionContext<TSessionToken>();
        return await sessionContext.GetRequiredTokenAsync(options);
    }

    /// <summary>
    /// Gets the session token from the HTTP context. If the user is not signed in, returns <see langword="null"/>.
    /// </summary>
    public static async ValueTask<TSessionToken?> GetSessionTokenAsync<TSessionToken>(this HttpContext httpContext, SessionAccessOptions options = default)
        where TSessionToken : class, ISessionToken
    {
        var sessionContext = httpContext.GetSessionContext<TSessionToken>();
        return sessionContext is null ? null : await sessionContext.GetTokenAsync(options);
    }
}
