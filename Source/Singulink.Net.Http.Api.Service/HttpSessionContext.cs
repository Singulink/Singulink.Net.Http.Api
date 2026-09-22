using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Net.Http.Headers;
using MyCSharp.HttpUserAgentParser;
using Singulink.Enums;
using SameSiteMode = Microsoft.AspNetCore.Http.SameSiteMode;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Default <see cref="SessionContext{TSessionToken}"/> implementation for HTTP requests. Stores the session token in an encrypted cookie and validates it
/// against an <see cref="ISessionStoreContext{TSessionToken}"/>.
/// </summary>
internal sealed class HttpSessionContext<TSessionToken> : SessionContext<TSessionToken>
    where TSessionToken : class, ISessionToken
{
    private readonly IDataProtector _dataProtector;
    private readonly IOriginValidator _originValidator;
    private readonly ISessionStoreContextFactory<TSessionToken> _sessionStoreContextFactory;
    private readonly SessionHandlingOptions _options;

    // Per-request token state. The token is read from the request cookie and validated against the session store at most once per request.

    private TSessionToken? _token;
    private SessionRecord? _session;
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
        ISessionStoreContextFactory<TSessionToken> sessionStoreContextFactory,
        SessionHandlingOptions options)
    {
        HttpContext = httpContext;
        _originValidator = originValidator;
        _dataProtector = dataProtector;
        _sessionStoreContextFactory = sessionStoreContextFactory;
        _options = options;
    }

    /// <summary>
    /// Gets the HTTP context for the current request.
    /// </summary>
    public HttpContext HttpContext { get; }

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

    /// <inheritdoc/>
    public override IPAddress? IpAddress => HttpContext.Connection.RemoteIpAddress;

    /// <inheritdoc/>
    protected override bool IsRequestOriginAllowed()
    {
        // If origin is provided, ensure it is an allowed origin

        if (HttpContext.Request.Headers.TryGetValue(HeaderNames.Origin, out var originValues))
        {
            if (originValues.Count is not 1)
                throw new BadRequestApiException($"Request contains multiple '{HeaderNames.Origin}' headers.");

            string origin = originValues[0];
            return origin is not null && _originValidator.IsAllowed(origin);
        }

        return true;
    }

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

        if (!forceValidate && !IsRefreshDue(sessionToken))
            return sessionToken;

        if (!_sessionValidated)
        {
            // The device is needed to validate the session (grace period check) and to refresh it at response start. It is read here so that a missing or
            // empty User-Agent header fails the request up front rather than after the endpoint has already run.
            string device = Device;

            await using var storeContext = _sessionStoreContextFactory.Create();
            var lookup = await GetValidatedSessionAsync(storeContext, sessionToken, device);

            if (lookup is null)
            {
                ClearToken();
                return null;
            }

            _sessionValidated = true;
            _session = lookup.Session;

            if (lookup.IsTokenStale || lookup.Session.Generation != sessionToken.Generation)
            {
                // The token is either stale (its information is out of date) or one generation behind the session record (a concurrent request already
                // refreshed the session), so bring it in line with the store right away. A stale token is rebuilt from the latest store data so that the
                // current request operates on current information rather than the stale token it was sent with. In both cases the record's existing
                // refresh info is applied (no store write) so this does not count as a refresh and losing the re-issued cookie is harmless. A rotating
                // refresh is still performed separately at response start if one is due, which then only needs to apply the new refresh info to the
                // already-current token.

                sessionToken = await storeContext.CreateTokenAsync(sessionToken, lookup.Session, lookup.IsTokenStale);

                _token = sessionToken;
                _reissueToken = true;
            }
        }

        // The token now carries the session record's refresh info, so a refresh is due based on when the record was last refreshed rather than when the
        // presented token was issued. This matters when a concurrent request has already refreshed the session: the record is current, so no refresh is
        // needed and the re-issued token is enough.

        if (IsRefreshDue(sessionToken))
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

    private static bool IsRefreshDue(TSessionToken sessionToken) => sessionToken.RefreshedUtc.Add(sessionToken.RefreshAfter) < DateTime.UtcNow;

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
    /// Validates that the session is still alive and that the token generation is acceptable, returning the lookup result if so. If the session is invalid
    /// it is removed from the store and <see langword="null"/> is returned.
    /// </summary>
    private async Task<SessionLookupResult?> GetValidatedSessionAsync(
        ISessionStoreContext<TSessionToken> storeContext, TSessionToken sessionToken, string device)
    {
        var lookup = await storeContext.GetSessionAsync(sessionToken);
        var session = lookup?.Session;
        var timeSinceRefresh = DateTime.UtcNow - session?.RefreshedUtc ?? TimeSpan.MaxValue;

        if (session is null || timeSinceRefresh > session.ValidFor)
        {
            if (session is not null)
                await storeContext.InvalidateSessionAsync(sessionToken);

            return null;
        }

        if (session.Generation != sessionToken.Generation)
        {
            // Allow a grace window where a token from the previous generation is still accepted from the same device to prevent throwing away the session
            // in a race condition where another request completes its refresh before this request (or a connection that was established with the previous
            // token, i.e. a reconnecting hub connection) presents the older token. The IP address is intentionally not compared since devices (mobile in
            // particular) switch networks frequently.

            if (session.Generation != sessionToken.Generation + 1 ||
                timeSinceRefresh > _options.MultipleRefreshGracePeriod ||
                session.Device != device)
            {
                await storeContext.InvalidateSessionAsync(sessionToken);
                return null;
            }
        }

        return lookup;
    }

    /// <summary>
    /// Performs the actual session token refresh (conditional store write + new token). Called from the deferred OnStarting callback. This method never
    /// invalidates the session: validation is the responsibility of <see cref="GetValidatedSessionAsync"/>, which runs at request start. The store write is
    /// gated on the session generation still being the one that was validated, so if another request refreshed the session in the meantime (or the session
    /// was invalidated) the write does not happen and <see langword="null"/> is returned, which skips issuing a cookie. The concurrent request's response
    /// delivered the current token to the client, and the grace period covers anything still in flight.
    /// </summary>
    private async Task<TSessionToken?> RefreshSessionTokenAsync(TSessionToken sessionToken)
    {
        if (_session is null)
            return null;

        var refreshedSession = new SessionRecord(
            Device,
            IpAddress,
            DateTime.UtcNow,
            _session.IsPersistent ? _options.PersistentSessionExpiry : _options.TempSessionExpiry,
            _session.Generation + 1,
            _session.IsPersistent);

        await using var storeContext = _sessionStoreContextFactory.Create();

        if (!await storeContext.TryRefreshSessionAsync(sessionToken, refreshedSession, expectedGeneration: _session.Generation))
            return null;

        // The token is always current at this point: a stale token is rebuilt when the session is validated at request start, so the refresh only needs
        // to apply the new refresh info.
        return await storeContext.CreateTokenAsync(sessionToken, refreshedSession, isStale: false);
    }
}
