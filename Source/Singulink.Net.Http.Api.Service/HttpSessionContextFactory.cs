using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Factory for creating <see cref="HttpSessionContext{TSessionToken}"/> instances.
/// </summary>
/// <typeparam name="TSessionToken">The session token type.</typeparam>
/// <typeparam name="TSessionData">The session storage entry type.</typeparam>
public class HttpSessionContextFactory<TSessionToken, TSessionData> : IHttpSessionContextFactory<TSessionToken>
    where TSessionToken : class, ISessionToken
    where TSessionData : class, ISessionData
{
    private readonly IDataProtector _dataProtector;
    private readonly IOriginValidator _originValidator;
    private readonly ISessionStoreContextFactory<TSessionToken, TSessionData> _sessionStoreContextFactory;
    private readonly SessionHandlingOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="HttpSessionContextFactory{TSessionToken, TSessionData}"/> class.
    /// </summary>
    public HttpSessionContextFactory(
        IDataProtectionProvider dataProtectionProvider,
        IOriginValidator originValidator,
        ISessionStoreContextFactory<TSessionToken, TSessionData> sessionStoreContextFactory,
        IOptions<SessionHandlingOptions> options)
    {
        _dataProtector = dataProtectionProvider.CreateProtector($"Singulink/Session[{typeof(TSessionToken).FullName}]");
        _originValidator = originValidator;
        _sessionStoreContextFactory = sessionStoreContextFactory;
        _options = options.Value;
    }

    /// <inheritdoc cref="IHttpSessionContextFactory{TSessionToken}.Create(HttpContext)"/>
    public virtual HttpSessionContext<TSessionToken> Create(HttpContext httpContext)
    {
        return new HttpSessionContext<TSessionToken, TSessionData>(
            httpContext,
            _dataProtector,
            _originValidator,
            _sessionStoreContextFactory,
            _options);
    }
}
