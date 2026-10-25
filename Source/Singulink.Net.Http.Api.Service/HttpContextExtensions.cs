using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using MyCSharp.HttpUserAgentParser;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Extension methods for <see cref="HttpContext"/>.
/// </summary>
public static class HttpContextExtensions
{
    private static readonly ConcurrentDictionary<ParameterInfo, (bool IsRequired, SessionAccessOptions Options)> _bindSessionTokenParamCache = [];

    // The HTTP context of a SignalR connection is shared by the connection's hub method invocations, which can run concurrently, so the values that are
    // kept in its items are created and read under a lock.
    private static readonly ConditionalWeakTable<HttpContext, Lock> _itemLocks = [];

    private static readonly object ClientDeviceKey = new();

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
    /// Gets the device that is making the request, as session handling records it (see <see cref="SessionContext{TSessionToken}.Device"/>): the platform,
    /// name and version of the browser or app from the <c>User-Agent</c> header if they can be identified, otherwise the header itself, shortened to
    /// <see cref="SessionHandlingOptions.MaxDeviceLength"/>. The device is read the first time and kept for the rest of the request, or of the SignalR
    /// connection that the request established.
    /// </summary>
    /// <exception cref="BadRequestApiException">The request has no <c>User-Agent</c> header, or an empty one
    /// (<see cref="ApiErrorCodes.UserAgentRequired"/>).</exception>
    public static string GetClientDevice(this HttpContext httpContext)
    {
        lock (_itemLocks.GetOrCreateValue(httpContext))
        {
            if (httpContext.Items.TryGetValue(ClientDeviceKey, out object? deviceObj) && deviceObj is string cachedDevice)
                return cachedDevice;

            var options = httpContext.RequestServices?.GetService<IOptions<SessionHandlingOptions>>()?.Value;
            string device = ReadClientDevice(httpContext, options?.MaxDeviceLength ?? SessionHandlingOptions.DefaultMaxDeviceLength);
            httpContext.Items[ClientDeviceKey] = device;

            return device;
        }
    }

    /// <summary>
    /// Gets the IP address of the client that is making the request (<see cref="ConnectionInfo.RemoteIpAddress"/>), with an IPv4-mapped IPv6 address,
    /// such as <c>::ffff:203.0.113.7</c>, as the IPv4 address it maps, or <see langword="null"/> if the address is not known.
    /// </summary>
    public static IPAddress? GetClientIpAddress(this HttpContext httpContext)
    {
        var address = httpContext.Connection.RemoteIpAddress;
        return address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;
    }

    /// <summary>
    /// Gets the session context from the HTTP context.
    /// </summary>
    /// <exception cref="InvalidOperationException">Session handling is not registered for the session token type.</exception>
    public static SessionContext<TSessionToken> GetRequiredSessionContext<TSessionToken>(this HttpContext httpContext)
        where TSessionToken : class, ISessionToken
    {
        return httpContext.GetSessionContext<TSessionToken>() ?? throw new InvalidOperationException(
            $"There is no session context for the session token type '{typeof(TSessionToken)}': session handling is not registered, or it is registered " +
            "for another session token type. Register it with AddHttpSessionHandling.");
    }

    /// <summary>
    /// Gets the session context from the HTTP context, or <see langword="null"/> if session handling is not registered. The context is created the first time
    /// and kept for the rest of the request, or of the SignalR connection that the request established.
    /// </summary>
    /// <exception cref="InvalidOperationException">The HTTP context's items hold another object under the session context's key.</exception>
    public static SessionContext<TSessionToken>? GetSessionContext<TSessionToken>(this HttpContext httpContext)
        where TSessionToken : class, ISessionToken
    {
        // Concurrent hub method invocations of a SignalR connection all get the same session context.
        lock (_itemLocks.GetOrCreateValue(httpContext))
        {
            if (httpContext.Items.TryGetValue(typeof(SessionContext<TSessionToken>), out object sessionContextObj))
            {
                if (sessionContextObj is SessionContext<TSessionToken> sessionContext)
                    return sessionContext;

                throw new InvalidOperationException($"HttpContext.Items[{typeof(SessionContext<TSessionToken>)}] type mismatch.");
            }

            var factory = httpContext.RequestServices.GetService<ISessionContextFactory<TSessionToken>>();

            if (factory is not null)
            {
                var sessionContext = factory.Create(httpContext);
                httpContext.Items[typeof(SessionContext<TSessionToken>)] = sessionContext;

                return sessionContext;
            }

            return null;
        }
    }

    /// <summary>
    /// Gets the session token from the HTTP context. If the user is not signed in, throws an <see cref="UnauthorizedApiException"/>.
    /// </summary>
    public static async ValueTask<TSessionToken> GetRequiredSessionTokenAsync<TSessionToken>(
        this HttpContext httpContext, SessionAccessOptions options = default)
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

    private static string ReadClientDevice(HttpContext httpContext, int maxLength)
    {
        if (!httpContext.Request.Headers.TryGetValue(HeaderNames.UserAgent, out var values))
            throw new BadRequestApiException("The request has no 'User-Agent' header.") { ErrorCode = ApiErrorCodes.UserAgentRequired };

        string value = values[0]?.Trim();

        if (string.IsNullOrEmpty(value))
            throw new BadRequestApiException("The request has an empty 'User-Agent' header.") { ErrorCode = ApiErrorCodes.UserAgentRequired };

        var userAgent = HttpUserAgentParser.Parse(value);
        string device = value;

        if (userAgent.Name is not null && userAgent.Version is not null && userAgent.Platform?.PlatformType is { } platformType)
            device = $"{platformType} ({userAgent.Name} {userAgent.Version})";

        if (device.Length <= maxLength)
            return device;

        // Don't split a surrogate pair.
        return device[..(char.IsHighSurrogate(device[maxLength - 1]) ? maxLength - 1 : maxLength)];
    }
}
