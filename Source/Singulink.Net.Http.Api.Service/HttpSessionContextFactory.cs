namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Default factory for creating <see cref="SessionContext{TSessionToken}"/> instances for HTTP requests.
/// </summary>
internal sealed class HttpSessionContextFactory<TSessionToken>(SessionHandler<TSessionToken> handler) : ISessionContextFactory<TSessionToken>
    where TSessionToken : class, ISessionToken
{
    public SessionContext<TSessionToken> Create(HttpContext httpContext) => new HttpSessionContext<TSessionToken>(httpContext, handler);
}
