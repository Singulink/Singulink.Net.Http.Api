using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Default factory for creating <see cref="SessionContext{TSessionToken}"/> instances for HTTP requests.
/// </summary>
internal sealed class HttpSessionContextFactory<TSessionToken> : ISessionContextFactory<TSessionToken>
    where TSessionToken : class, ISessionToken
{
    private readonly IDataProtector _dataProtector;
    private readonly IOriginValidator _originValidator;
    private readonly ISessionStoreContextFactory<TSessionToken> _sessionStoreContextFactory;
    private readonly SessionHandlingOptions _options;

    public HttpSessionContextFactory(
        IDataProtectionProvider dataProtectionProvider,
        IOriginValidator originValidator,
        ISessionStoreContextFactory<TSessionToken> sessionStoreContextFactory,
        IOptions<SessionHandlingOptions> options)
    {
        _dataProtector = dataProtectionProvider.CreateProtector($"Singulink/Session[{typeof(TSessionToken).FullName}]");
        _originValidator = originValidator;
        _sessionStoreContextFactory = sessionStoreContextFactory;
        _options = options.Value;
    }

    public SessionContext<TSessionToken> Create(HttpContext httpContext)
    {
        return new HttpSessionContext<TSessionToken>(
            httpContext,
            _dataProtector,
            _originValidator,
            _sessionStoreContextFactory,
            _options);
    }
}
