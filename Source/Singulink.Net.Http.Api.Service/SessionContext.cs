using System.Net;
using System.Reflection;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Represents the session of the current request: its token, the device and IP address making the request, and signing in and out.
/// </summary>
/// <typeparam name="TSessionToken">The session token type.</typeparam>
public abstract class SessionContext<TSessionToken> : IBindableFromHttpContext<SessionContext<TSessionToken>>
    where TSessionToken : class, ISessionToken
{
    /// <summary>
    /// Gets the device that is making the current request, at most <see cref="SessionHandlingOptions.MaxDeviceLength"/> long (see
    /// <see cref="HttpContextExtensions.GetClientDevice(HttpContext)"/>).
    /// </summary>
    /// <exception cref="BadRequestApiException">The request has no <c>User-Agent</c> header, or an empty one
    /// (<see cref="ApiErrorCodes.UserAgentRequired"/>).</exception>
    public abstract string Device { get; }

    /// <summary>
    /// Gets the IP address of the client that is making the current request, with an IPv4-mapped IPv6 address as the IPv4 address it maps, or
    /// <see langword="null"/> if the address is not known (see <see cref="HttpContextExtensions.GetClientIpAddress(HttpContext)"/>).
    /// </summary>
    public abstract IPAddress? IpAddress { get; }

    /// <summary>
    /// Gets the session token from the current request. If the user is not signed in, throws an <see cref="UnauthorizedApiException"/>.
    /// </summary>
    /// <param name="sessionOptions">Option flags for retrieving the session token.</param>
    /// <remarks>
    /// <inheritdoc cref="GetTokenAsync(SessionAccessOptions)" path="/remarks"/>
    /// </remarks>
    public abstract ValueTask<TSessionToken> GetRequiredTokenAsync(SessionAccessOptions sessionOptions = default);

    /// <summary>
    /// Gets the session token from the current request. If the user is not signed in, returns <see langword="null"/>.
    /// </summary>
    /// <param name="sessionOptions">Option flags for retrieving the session token.</param>
    /// <remarks>
    /// The token is read from the request once and cached for subsequent calls. The session is validated against the session store when the cookie is due
    /// for a refresh, at most once per request, and on every call with <see cref="SessionAccessOptions.ForceValidate"/>, unless the token was set in the
    /// request. After <see cref="SetToken"/> or <see cref="ClearToken"/> is called, subsequent calls return the token that was set (or
    /// <see langword="null"/>). The session context of a SignalR hub connection is the one of the request that established it, for the whole connection.
    /// </remarks>
    public abstract ValueTask<TSessionToken?> GetTokenAsync(SessionAccessOptions sessionOptions = default);

    /// <summary>
    /// Replaces the session token of the current request's session and issues the session cookie with it. Subsequent token retrievals in the current
    /// request return the specified token, and any pending deferred session refresh is cancelled.
    /// </summary>
    /// <exception cref="InvalidOperationException">The request has no session.</exception>
    public abstract void SetToken(TSessionToken sessionToken);

    /// <summary>
    /// Signs in a user by creating a new session with the provided function and issuing the session cookie with the session token that the function returns.
    /// The request's previous session, if it has one, is then ended with <see cref="ISessionStore{TSessionToken}.InvalidateSessionAsync"/>, unless the
    /// function returned its token (compared with <see cref="object.Equals(object?, object?)"/>). The response is marked with a
    /// <c>Session-Change: sign-in</c> header, so that the toolkit's clients take its cookie whatever session the device has by then.
    /// </summary>
    /// <param name="persistent">If set to <see langword="true"/>, the session will persist across browser or application restarts.</param>
    /// <param name="createSessionFunc">A function that creates the session record with the values of the provided <see cref="SignInInfo"/> and returns the
    /// session token. Other values that creating the session needs, e.g. the user's ID, are captured by the function.</param>
    /// <exception cref="ForbiddenApiException">The request's origin is not allowed (<see cref="ApiErrorCodes.OriginBlocked"/>).</exception>
    public abstract Task<TSessionToken> SignInAsync(bool persistent, Func<SignInInfo, Task<TSessionToken>> createSessionFunc);

    /// <summary>
    /// Checks the current request's origin the way <see cref="SignInAsync(bool, Func{SignInInfo, Task{TSessionToken}})"/> does before it calls its
    /// function, without signing in.
    /// </summary>
    /// <remarks>
    /// The default implementation throws if <see cref="IsRequestOriginAllowed"/> returns <see langword="false"/>.
    /// </remarks>
    /// <exception cref="ForbiddenApiException">The request's origin is not allowed (<see cref="ApiErrorCodes.OriginBlocked"/>).</exception>
    /// <exception cref="BadRequestApiException">The request contains multiple origin headers.</exception>
    public virtual void EnsureSignInOriginAllowed()
    {
        if (!IsRequestOriginAllowed())
            throw SessionErrors.OriginBlocked();
    }

    /// <summary>
    /// Signs out: ends the request's session with <see cref="ISessionStore{TSessionToken}.InvalidateSessionAsync"/> and clears the session cookie. The
    /// response is marked with a <c>Session-Change: sign-out</c> header, so that the toolkit's clients take its deletion whatever session the device has by
    /// then, unless the response has started, as in a hub connection, where the cookie can't be cleared either.
    /// </summary>
    /// <remarks>
    /// The user ID precondition is only checked if the request has one.
    /// </remarks>
    /// <exception cref="UserChangedApiException">The request's user ID precondition does not match the session's user.</exception>
    /// <exception cref="ForbiddenApiException">The request's origin is not allowed (<see cref="ApiErrorCodes.OriginBlocked"/>).</exception>
    public abstract Task SignOutAsync();

    /// <summary>
    /// Clears the session cookie of the current request. Subsequent token retrievals in the current request return <see langword="null"/>, and any pending
    /// deferred session refresh is cancelled.
    /// </summary>
    public abstract void ClearToken();

    /// <summary>
    /// Determines whether the origin of the current request is allowed: a request with an <c>Origin</c> header must have one of the allowed origins, and a
    /// request without one is allowed.
    /// </summary>
    /// <exception cref="BadRequestApiException">The request contains multiple origin headers.</exception>
    protected abstract bool IsRequestOriginAllowed();

    /// <summary>
    /// Binds the <see cref="SessionContext{TSessionToken}"/> parameter value.
    /// </summary>
    static ValueTask<SessionContext<TSessionToken>?> IBindableFromHttpContext<SessionContext<TSessionToken>>.BindAsync(
        HttpContext context, ParameterInfo parameter)
    {
        var sessionContext = context.GetSessionContext<TSessionToken>();
        return ValueTask.FromResult<SessionContext<TSessionToken>?>(sessionContext);
    }
}
