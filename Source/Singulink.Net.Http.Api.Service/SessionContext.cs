using System.Net;
using System.Reflection;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Provides an abstraction for handling user sessions and tokens.
/// </summary>
/// <typeparam name="TSessionToken">The session token type.</typeparam>
public abstract class SessionContext<TSessionToken> : IBindableFromHttpContext<SessionContext<TSessionToken>>
    where TSessionToken : class, ISessionToken
{
    /// <summary>
    /// Gets a string that represents the device information that is making the current request.
    /// </summary>
    public abstract string Device { get; }

    /// <summary>
    /// Gets the IP address of the current request. If the IP address cannot be determined, returns <see langword="null"/>.
    /// </summary>
    public abstract IPAddress? IpAddress { get; }

    /// <summary>
    /// Gets the session token from the current request. If the user is not signed in, throws an <see cref="UnauthorizedApiException"/>.
    /// </summary>
    /// <param name="sessionOptions">Option flags for retrieving the session token.</param>
    public abstract ValueTask<TSessionToken> GetRequiredTokenAsync(SessionAccessOptions sessionOptions = default);

    /// <summary>
    /// Gets the session token from the current request. If the user is not signed in, returns <see langword="null"/>.
    /// </summary>
    /// <param name="sessionOptions">Option flags for retrieving the session token.</param>
    public abstract ValueTask<TSessionToken?> GetTokenAsync(SessionAccessOptions sessionOptions = default);

    /// <summary>
    /// Sets the session cookie for the current request user to the specified session token.
    /// </summary>
    public abstract void SetToken(TSessionToken sessionToken);

    /// <summary>
    /// Signs in a user by creating a new session using the provided function and setting the session cookie to the session token returned by the function.
    /// </summary>
    /// <param name="persistent">If set to <see langword="true"/>, the session will persist across browser or application restarts.</param>
    /// <param name="createSessionFunc">A function that creates a new session token based on the provided <see cref="SignInInfo"/>. Any other parameters
    /// required for session creation (i.e. email, password, etc) should be captured by the function.</param>
    public abstract Task<TSessionToken> SignInAsync(bool persistent, Func<SignInInfo, Task<TSessionToken>> createSessionFunc);

    /// <summary>
    /// Signs out the current request user by invalidating the session and clearing the session cookie.
    /// </summary>
    public abstract Task SignOutAsync();

    /// <summary>
    /// Clears the session cookie for the current request user.
    /// </summary>
    public abstract void ClearToken();

    /// <summary>
    /// Validates the current request origin against allowed origins. If the request contains an origin identifier, it must match one of the allowed origins.
    /// </summary>
    /// <exception cref="BadRequestApiException">The request contains multiple origin headers.</exception>
    public abstract bool IsRequestOriginAllowed();

    /// <summary>
    /// Binds the <see cref="SessionContext{TSessionToken}"/> parameter value.
    /// </summary>
    static ValueTask<SessionContext<TSessionToken>?> IBindableFromHttpContext<SessionContext<TSessionToken>>.BindAsync(
        HttpContext context, ParameterInfo parameter)
    {
        var sessionContext = (SessionContext<TSessionToken>?)context.GetSessionContext<TSessionToken>();
        return ValueTask.FromResult(sessionContext);
    }
}
