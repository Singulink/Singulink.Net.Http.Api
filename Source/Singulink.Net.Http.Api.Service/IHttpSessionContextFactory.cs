namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Represents a factory for creating <see cref="HttpSessionContext{TSessionToken}"/> instances.
/// </summary>
/// <typeparam name="TSessionToken">The session token type.</typeparam>
public interface IHttpSessionContextFactory<TSessionToken>
    where TSessionToken : class, ISessionToken
{
    /// <summary>
    /// Creates am <see cref="HttpSessionContext{TSessionToken}"/> for the specified <see cref="HttpContext"/>.
    /// </summary>
    HttpSessionContext<TSessionToken> Create(HttpContext httpContext);
}
