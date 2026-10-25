using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The services that session handling shares between requests (and hub connections): cookie protection, the session store, options and the clock.
/// </summary>
internal sealed class SessionHandler<TSessionToken>
    where TSessionToken : class, ISessionToken
{
    /// <summary>
    /// The size of the largest cookie that browsers accept, counting its name, value and attributes.
    /// </summary>
    private const int MaxBrowserCookieSize = 4096;

    private readonly IDataProtector _dataProtector;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SessionCookieSerializer<TSessionToken> _serializer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    public SessionHandler(
        IDataProtectionProvider dataProtectionProvider,
        IOriginValidator originValidator,
        IServiceScopeFactory scopeFactory,
        SessionCookieSerializer<TSessionToken> serializer,
        IOptions<SessionHandlingOptions> options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        Options = options.Value;
        OriginValidator = originValidator;

        _dataProtector = dataProtectionProvider.CreateProtector(Options.DataProtectionPurpose ?? $"Singulink/Session[{typeof(TSessionToken).FullName}]");
        _scopeFactory = scopeFactory;
        _serializer = serializer;
        _timeProvider = timeProvider;
        _logger = loggerFactory.CreateLogger("Singulink.Net.Http.Api.Service.SessionHandling");
    }

    public SessionHandlingOptions Options { get; }

    public IOriginValidator OriginValidator { get; }

    public TimeProvider TimeProvider => _timeProvider;

    public DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public string Protect(SessionCookie<TSessionToken> cookie) => _dataProtector.Protect(_serializer.Serialize(cookie));

    /// <summary>
    /// Reads a session cookie value, or returns <see langword="null"/> if it cannot be read.
    /// </summary>
    public SessionCookie<TSessionToken>? Unprotect(string value)
    {
        try
        {
            return _serializer.Deserialize(_dataProtector.Unprotect(value));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Logs an error if a <c>Set-Cookie</c> header of the session cookie is larger than browsers accept. The cookie is still sent: clients outside browsers
    /// accept it.
    /// </summary>
    public void CheckCookieSize(string setCookieHeader)
    {
        int size = Encoding.UTF8.GetByteCount(setCookieHeader);

        if (size > MaxBrowserCookieSize)
        {
            _logger.LogError(
                "The session cookie is {CookieSize} bytes, which is over the 4,096-byte limit of browsers: browsers will silently drop it, and their users " +
                "cannot stay signed in. Reduce the size of the session token of type {SessionTokenType}.",
                size,
                typeof(TSessionToken).FullName);
        }
    }

    /// <summary>
    /// Creates the service scope of a session store operation.
    /// </summary>
    public AsyncServiceScope CreateStoreScope() => _scopeFactory.CreateAsyncScope();

    public static ISessionStore<TSessionToken> GetStore(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<ISessionStore<TSessionToken>>();
}
