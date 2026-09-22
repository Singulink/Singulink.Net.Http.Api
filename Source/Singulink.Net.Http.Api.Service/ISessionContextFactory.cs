namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Represents a factory for creating <see cref="SessionContext{TSessionToken}"/> instances for HTTP requests. Register a custom implementation to substitute
/// the session context, for example in application tests.
/// </summary>
/// <typeparam name="TSessionToken">The session token type.</typeparam>
public interface ISessionContextFactory<TSessionToken>
    where TSessionToken : class, ISessionToken
{
    /// <summary>
    /// Creates a <see cref="SessionContext{TSessionToken}"/> for the specified <see cref="HttpContext"/>.
    /// </summary>
    SessionContext<TSessionToken> Create(HttpContext httpContext);
}
