using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Net.Http.Headers;
using MyCSharp.HttpUserAgentParser;
using Singulink.Enums;
using SameSiteMode = Microsoft.AspNetCore.Http.SameSiteMode;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Provides an abstraction for handling user sessions and tokens in an HTTP context.
/// </summary>
public abstract class HttpSessionContext<TSessionToken> : SessionContext<TSessionToken>, IBindableFromHttpContext<HttpSessionContext<TSessionToken>>
    where TSessionToken : class, ISessionToken
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HttpSessionContext{TSessionToken}"/> class.
    /// </summary>
    protected HttpSessionContext(IOriginValidator originValidator)
    {
        OriginValidator = originValidator;
    }

    /// <summary>
    /// Gets the device information from the User-Agent header in the request. Returns a parsed device platform, name and version if it could be identified,
    /// otherwise returns the raw user agent string.
    /// </summary>
    public override string Device
    {
        get {
            return field ??= GetDeviceFromUserAgent();

            string GetDeviceFromUserAgent()
            {
                if (!HttpContext.Request.Headers.TryGetValue(HeaderNames.UserAgent, out var values))
                    throw new BadRequestApiException("User agent required in request headers.");

                string value = values[0]?.Trim();

                if (string.IsNullOrEmpty(value))
                    throw new BadRequestApiException("Empty user agent in request headers.");

                var userAgent = HttpUserAgentParser.Parse(value);

                if (userAgent.Name is not null && userAgent.Version is not null && userAgent.Platform?.PlatformType is { } platformType)
                    return $"{platformType} ({userAgent.Name} {userAgent.Version})";

                return value;
            }
        }
    }

    /// <summary>
    /// Gets the HTTP context for the current request.
    /// </summary>
    public abstract HttpContext HttpContext { get; }

    /// <summary>
    /// Gets the IP address of the current request. If the IP address cannot be determined, returns <see langword="null"/>.
    /// </summary>
    public override IPAddress? IpAddress => HttpContext.Connection.RemoteIpAddress;

    /// <summary>
    /// Gets the origin validator used to validate request origins.
    /// </summary>
    protected IOriginValidator OriginValidator { get; }

    /// <inheritdoc/>
    public override bool IsRequestOriginAllowed()
    {
        // If origin is provided, ensure it is an allowed origin

        if (HttpContext.Request.Headers.TryGetValue(HeaderNames.Origin, out var originValues))
        {
            if (originValues.Count is not 1)
                throw new BadRequestApiException($"Request contains multiple '{HeaderNames.Origin}' headers.");

            string origin = originValues[0];
            return origin is not null && OriginValidator.IsAllowed(origin);
        }

        return true;
    }

    /// <summary>
    /// Binds the <see cref="SessionContext{TSessionToken}"/> parameter value.
    /// </summary>
    static ValueTask<HttpSessionContext<TSessionToken>?> IBindableFromHttpContext<HttpSessionContext<TSessionToken>>.BindAsync(
        HttpContext context, ParameterInfo parameter)
    {
        return ValueTask.FromResult(context.GetSessionContext<TSessionToken>());
    }
}

/// <summary>
/// Provides methods for handling user session tokens.
/// </summary>
public sealed class HttpSessionContext<TSessionToken, TSessionData> : HttpSessionContext<TSessionToken>
    where TSessionToken : class, ISessionToken
    where TSessionData : class, ISessionData
{
    private readonly IDataProtector _dataProtector;
    private readonly ISessionStoreContextFactory<TSessionToken, TSessionData> _sessionStoreContextFactory;
    private readonly SessionHandlingOptions _options;

    // Per-request token state. The token is read from the request cookie and validated against the session store at most once per request.

    private TSessionToken? _token;
    private bool _tokenRead;
    private bool _sessionValidated;
    private bool _refreshToken;
    private bool _reissueToken;
    private bool _deferredUpdateRegistered;
    private bool _skipDeferredUpdate;

    internal HttpSessionContext(
        HttpContext httpContext,
        IDataProtector dataProtector,
        IOriginValidator originValidator,
        ISessionStoreContextFactory<TSessionToken, TSessionData> sessionStoreContextFactory,
        SessionHandlingOptions options)
        : base(originValidator)
    {
        HttpContext = httpContext;
        _dataProtector = dataProtector;
        _sessionStoreContextFactory = sessionStoreContextFactory;
        _options = options;
    }

    /// <inheritdoc/>
    public override HttpContext HttpContext { get; }

    /// <inheritdoc/>
    public override async ValueTask<TSessionToken> GetRequiredTokenAsync(SessionAccessOptions sessionOptions = default)
    {
        return await GetTokenAsync(sessionOptions) ?? throw new UnauthorizedApiException("User is not signed in.");
    }

    /// <inheritdoc/>
    public override async ValueTask<TSessionToken?> GetTokenAsync(SessionAccessOptions sessionOptions = default)
    {
        sessionOptions |= _options.ForcedAccessOptions;
        sessionOptions.ThrowIfFlagsAreNotDefined(nameof(sessionOptions));

        EnsureRequestOriginAllowed(sessionOptions);

        var sessionToken = ReadToken();

        if (sessionToken is null)
            return null;

        ValidateUserIdPrecondition(sessionToken, sessionOptions.HasAllFlags(SessionAccessOptions.OptionalUserIdPrecondition));

        bool forceValidate = sessionOptions.HasAllFlags(SessionAccessOptions.ForceValidate);
        bool refreshDue = sessionToken.RefreshedUtc.Add(sessionToken.RefreshAfter) < DateTime.UtcNow;

        if (!forceValidate && !refreshDue)
            return sessionToken;

        if (!_sessionValidated)
        {
            await using var storeContext = _sessionStoreContextFactory.Create();
            var sessionData = await GetValidatedSessionDataAsync(storeContext, sessionToken);

            if (sessionData is null)
            {
                ClearToken();
                return null;
            }

            _sessionValidated = true;

            if (await storeContext.IsTokenStaleAsync(sessionToken))
            {
                // Token information is out of date, so create a new token from the latest store data right away so that the current request operates on
                // current information rather than the stale token it was sent with. The session's existing refresh info is used (same generation, no store
                // write) so this does not count as a refresh and losing the re-issued cookie is harmless. A rotating refresh is still performed separately
                // at response start if one is due, which then only needs to apply the new refresh info to the already-current token.

                sessionToken = await storeContext.CreateTokenAsync(sessionToken, sessionData, isStale: true);

                _token = sessionToken;
                _reissueToken = true;
            }
        }

        if (refreshDue)
            _refreshToken = true;

        if (_refreshToken || _reissueToken)
            RegisterDeferredTokenUpdate();

        return sessionToken;
    }

    /// <inheritdoc/>
    public override async Task<TSessionToken> SignInAsync(bool persistent, Func<SignInInfo, Task<TSessionToken>> createSessionFunc)
    {
        var signInInfo = new SignInInfo(Device, IpAddress, persistent ? _options.PersistentSessionExpiry : _options.TempSessionExpiry, persistent);
        var token = await createSessionFunc(signInInfo);

        SetToken(token);
        return token;
    }

    /// <inheritdoc/>
    public override async Task SignOutAsync()
    {
        // Signing out only needs the token to identify the session, so no session validation or refresh is performed.

        var sessionOptions = _options.ForcedAccessOptions;
        EnsureRequestOriginAllowed(sessionOptions);

        var sessionToken = ReadToken();

        if (sessionToken is not null)
        {
            ValidateUserIdPrecondition(sessionToken, sessionOptions.HasAllFlags(SessionAccessOptions.OptionalUserIdPrecondition));

            await using var storeContext = _sessionStoreContextFactory.Create();
            await storeContext.InvalidateSessionAsync(sessionToken);
        }

        ClearToken();
    }

    /// <inheritdoc/>
    public override void SetToken(TSessionToken sessionToken)
    {
        _token = sessionToken;
        _tokenRead = true;
        _sessionValidated = true;
        _skipDeferredUpdate = true;

        SetTokenInternal(sessionToken);
    }

    /// <inheritdoc/>
    public override void ClearToken()
    {
        _token = null;
        _tokenRead = true;
        _skipDeferredUpdate = true;

        // The response may have already started (e.g. hub connections and streaming responses), in which case cookies can no longer be sent. The client is
        // responsible for discarding its token when it receives the resulting unauthorized response.
        if (HttpContext.Response.HasStarted)
            return;

        // Domain/Path must match the values used when the cookie was issued; otherwise the browser keeps the original cookie alongside the deletion attempt.
        HttpContext.Response.Cookies.Delete(_options.SessionCookieName, new CookieOptions {
            Domain = _options.CookieDomain,
            Path = _options.CookiePath,
            Secure = true,
            SameSite = SameSiteMode.None,
        });
    }

    private void EnsureRequestOriginAllowed(SessionAccessOptions sessionOptions)
    {
        if (!sessionOptions.HasAllFlags(SessionAccessOptions.AllowAllOrigins) && !IsRequestOriginAllowed())
            throw new ForbiddenApiException("Cross-origin request was blocked.");
    }

    /// <summary>
    /// Reads the session token from the request cookie the first time it is called and caches the result. If the cookie is present but cannot be read,
    /// the cookie is cleared and <see langword="null"/> is returned.
    /// </summary>
    private TSessionToken? ReadToken()
    {
        if (_tokenRead)
            return _token;

        _tokenRead = true;

        string? sessionCookie = HttpContext.Request.Cookies[_options.SessionCookieName];

        if (sessionCookie is null)
            return null;

        try
        {
            string sessionCookieData = _dataProtector.Unprotect(sessionCookie);
            _token = JsonSerializer.Deserialize<TSessionToken>(sessionCookieData);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException) { }

        if (_token is null)
            ClearToken();

        return _token;
    }

    private void SetTokenInternal(TSessionToken sessionToken)
    {
        string sessionCookieData = JsonSerializer.Serialize(sessionToken);
        string cookie = _dataProtector.Protect(sessionCookieData);

        var options = new CookieOptions {
            HttpOnly = true,
            IsEssential = true,
            Secure = true,
            SameSite = SameSiteMode.None,
            Domain = _options.CookieDomain,
            Path = _options.CookiePath,
            MaxAge = sessionToken.IsPersistent ? sessionToken.ValidFor : null,
        };

        HttpContext.Response.Cookies.Append(_options.SessionCookieName, cookie, options);
    }

    private void ValidateUserIdPrecondition(TSessionToken sessionToken, bool optional)
    {
        if (!HttpContext.Request.Query.TryGetValue(_options.UserIdPreconditionQueryName, out var userIdValues))
        {
            if (optional)
                return;

            throw new UserRequiredApiException($"Request is missing required '{_options.UserIdPreconditionQueryName}' precondition query parameter.");
        }

        if (userIdValues.Count is not 1)
            throw new BadRequestApiException($"Request contains multiple '{_options.UserIdPreconditionQueryName}' query parameter values.");

        if (userIdValues[0] is not { Length: > 0 } userId)
            throw new BadRequestApiException($"Empty user ID in '{_options.UserIdPreconditionQueryName}' query parameter.");

        if (userId != sessionToken.UserId)
            throw new UserChangedApiException($"Request user identified in the '{_options.UserIdPreconditionQueryName}' query parameter does not match session user.");
    }

    /// <summary>
    /// Registers a deferred update of the session cookie at response start. The update is skipped if the application sets or clears the token itself. If a
    /// refresh was requested, the session is refreshed (store write + new token) and the refreshed token is issued, otherwise the current token is re-issued
    /// as-is. The token state is read when the response starts so that changes made by later token retrievals in the same request are reflected.
    /// </summary>
    private void RegisterDeferredTokenUpdate()
    {
        if (_deferredUpdateRegistered || HttpContext.Response.HasStarted)
            return;

        _deferredUpdateRegistered = true;

        HttpContext.Response.OnStarting(async () =>
        {
            if (_skipDeferredUpdate || _token is null)
                return;

            var updatedToken = _refreshToken ? await RefreshSessionTokenAsync(_token) : _token;

            if (updatedToken is not null)
                SetTokenInternal(updatedToken);
        });
    }

    /// <summary>
    /// Validates that the session is still alive and that the token generation is acceptable, returning the session data if so. If the session is invalid
    /// it is removed from the store and <see langword="null"/> is returned.
    /// </summary>
    private async Task<TSessionData?> GetValidatedSessionDataAsync(ISessionStoreContext<TSessionToken, TSessionData> storeContext, TSessionToken sessionToken)
    {
        var sessionData = await storeContext.GetSessionDataAsync(sessionToken);
        var timeSinceDataRefresh = DateTime.UtcNow - sessionData?.RefreshedUtc ?? TimeSpan.MaxValue;

        if (sessionData is null || timeSinceDataRefresh > sessionData.ValidFor)
        {
            if (sessionData is not null)
                await storeContext.InvalidateSessionAsync(sessionToken);

            return null;
        }

        if (sessionData.Generation != sessionToken.Generation)
        {
            // Allow a grace window where a token from the previous generation is still accepted from the same device to prevent throwing away the session
            // in a race condition where another request completes its refresh before this request (or a connection that was established with the previous
            // token, i.e. a reconnecting hub connection) presents the older token. The IP address is intentionally not compared since devices (mobile in
            // particular) switch networks frequently.

            if (sessionData.Generation != sessionToken.Generation + 1 ||
                timeSinceDataRefresh > _options.MultipleRefreshGracePeriod ||
                sessionData.Device != Device)
            {
                await storeContext.InvalidateSessionAsync(sessionToken);
                return null;
            }
        }

        return sessionData;
    }

    /// <summary>
    /// Performs the actual session token refresh (store write + new token). Called from the deferred OnStarting callback. This method never invalidates the
    /// session — validation is the responsibility of <see cref="GetValidatedSessionDataAsync"/> which runs at request start. If the refresh cannot be safely
    /// performed (e.g. session expired or generation advanced by more than 1 or from a different device), it silently returns <see langword="null"/>.
    /// </summary>
    private async Task<TSessionToken?> RefreshSessionTokenAsync(TSessionToken sessionToken)
    {
        await using var storeContext = _sessionStoreContextFactory.Create();
        var sessionData = await storeContext.GetSessionDataAsync(sessionToken);
        var timeSinceDataRefresh = DateTime.UtcNow - sessionData?.RefreshedUtc ?? TimeSpan.MaxValue;

        if (sessionData is null || timeSinceDataRefresh > sessionData.ValidFor)
            return null;

        if (sessionData.Generation != sessionToken.Generation)
        {
            // Another concurrent request already refreshed the session. Allow it if the generation advanced by exactly 1 from the same device — this just
            // means another request's deferred refresh completed before ours. Skip the store write (already done) and produce a token with the current
            // generation so the client gets an up-to-date cookie. Otherwise silently skip — GetValidatedSessionDataAsync already ran at request start and
            // the next request will catch any real compromise.

            if (sessionData.Generation != sessionToken.Generation + 1 || sessionData.Device != Device)
                return null;
        }
        else
        {
            sessionData.Device = Device;
            sessionData.IpAddress = IpAddress;
            sessionData.RefreshedUtc = DateTime.UtcNow;
            sessionData.ValidFor = sessionData.IsPersistent ? _options.PersistentSessionExpiry : _options.TempSessionExpiry;
            sessionData.Generation++;

            await storeContext.UpdateSessionAsync(sessionData);
        }

        // The token is always current at this point: a stale token is rebuilt when the session is validated at request start, so the refresh only needs
        // to apply the new refresh info.
        return await storeContext.CreateTokenAsync(sessionToken, sessionData, isStale: false);
    }
}
