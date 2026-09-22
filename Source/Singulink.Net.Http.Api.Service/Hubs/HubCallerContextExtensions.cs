using Microsoft.AspNetCore.SignalR;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Extension methods for accessing the session from a <see cref="HubCallerContext"/>.
/// </summary>
public static class HubCallerContextExtensions
{
    /// <summary>
    /// Gets the session context for the hub connection.
    /// </summary>
    /// <exception cref="InvalidOperationException">The hub connection does not have an HTTP context or session handling is not registered.</exception>
    public static SessionContext<TSessionToken> GetRequiredSessionContext<TSessionToken>(this HubCallerContext context)
        where TSessionToken : class, ISessionToken
    {
        return context.GetRequiredHttpContext().GetRequiredSessionContext<TSessionToken>();
    }

    /// <summary>
    /// Gets the session token for the hub connection. If the user is not signed in, throws an <see cref="UnauthorizedApiException"/> (which is reported to
    /// the client as an API error when the <see cref="ApiExceptionHubFilter"/> is registered).
    /// </summary>
    /// <param name="context">The hub caller context.</param>
    /// <param name="sessionOptions">Option flags for retrieving the session token.</param>
    /// <exception cref="InvalidOperationException">The hub connection does not have an HTTP context or session handling is not registered.</exception>
    public static ValueTask<TSessionToken> GetRequiredSessionTokenAsync<TSessionToken>(this HubCallerContext context, SessionAccessOptions sessionOptions = default)
        where TSessionToken : class, ISessionToken
    {
        return context.GetRequiredHttpContext().GetRequiredSessionTokenAsync<TSessionToken>(sessionOptions);
    }

    /// <summary>
    /// Gets the session token for the hub connection. If the user is not signed in, returns <see langword="null"/>.
    /// </summary>
    /// <param name="context">The hub caller context.</param>
    /// <param name="sessionOptions">Option flags for retrieving the session token.</param>
    /// <exception cref="InvalidOperationException">The hub connection does not have an HTTP context.</exception>
    public static ValueTask<TSessionToken?> GetSessionTokenAsync<TSessionToken>(this HubCallerContext context, SessionAccessOptions sessionOptions = default)
        where TSessionToken : class, ISessionToken
    {
        return context.GetRequiredHttpContext().GetSessionTokenAsync<TSessionToken>(sessionOptions);
    }

    private static HttpContext GetRequiredHttpContext(this HubCallerContext context)
    {
        return context.GetHttpContext() ?? throw new InvalidOperationException("The hub connection does not have an HTTP context.");
    }
}
