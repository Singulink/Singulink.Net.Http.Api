using System.Net;
using Microsoft.Net.Http.Headers;
using Singulink.Enums;
using SameSiteMode = Microsoft.AspNetCore.Http.SameSiteMode;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Default <see cref="SessionContext{TSessionToken}"/> implementation for HTTP requests. Keeps the session token and the session's refresh info in an
/// encrypted cookie and validates them against the <see cref="ISessionStore{TSessionToken}"/>.
/// </summary>
internal sealed class HttpSessionContext<TSessionToken> : SessionContext<TSessionToken>
    where TSessionToken : class, ISessionToken
{
    private readonly SessionHandler<TSessionToken> _handler;
    private readonly SessionHandlingOptions _options;

    // Per-request state. The cookie is read from the request once. The session is validated against the session store at most once per request when the
    // cookie is due, and on every retrieval that forces validation. A hub connection keeps the state of the request that established it for the whole
    // connection, where concurrent hub method invocations and the connection's session checks validate the session or replace its token under the lock.

    private readonly SemaphoreSlim _validationLock = new(1, 1);

    private SessionCookie<TSessionToken>? _cookie;
    private SessionRecord? _session;
    private bool _cookieRead;
    private bool _sessionValidated;
    private bool _tokenSet;
    private bool _refreshDue;
    private bool _reissueCookie;
    private bool _deferredUpdateRegistered;
    private bool _skipDeferredUpdate;

    // The time at which the cookie was last known to have the session's current generation: when it was read if it was not due then (only a refresh of a
    // due cookie moves the generation on), or when the session was last validated. DateTime.MaxValue if not known.
    private DateTime _cookieCurrentUtc = DateTime.MaxValue;

    internal HttpSessionContext(HttpContext httpContext, SessionHandler<TSessionToken> handler)
    {
        HttpContext = httpContext;
        _handler = handler;
        _options = handler.Options;
    }

    /// <summary>
    /// Gets the HTTP context for the current request.
    /// </summary>
    public HttpContext HttpContext { get; }

    /// <inheritdoc/>
    public override string Device => HttpContext.GetClientDevice();

    /// <inheritdoc/>
    public override IPAddress? IpAddress => HttpContext.GetClientIpAddress();

    /// <inheritdoc/>
    protected override bool IsRequestOriginAllowed()
    {
        // A request with an origin must come from an allowed origin.

        if (HttpContext.Request.Headers.TryGetValue(HeaderNames.Origin, out var originValues))
        {
            if (originValues.Count is not 1)
                throw new BadRequestApiException($"The request has more than one '{HeaderNames.Origin}' header.");

            string origin = originValues[0];
            return origin is not null && _handler.OriginValidator.IsAllowed(origin);
        }

        return true;
    }

    /// <inheritdoc/>
    public override async ValueTask<TSessionToken> GetRequiredTokenAsync(SessionAccessOptions sessionOptions = default)
    {
        return await GetTokenAsync(sessionOptions) ?? throw SessionErrors.SignInRequired();
    }

    /// <inheritdoc/>
    public override async ValueTask<TSessionToken?> GetTokenAsync(SessionAccessOptions sessionOptions = default)
    {
        sessionOptions |= _options.ForcedAccessOptions;
        sessionOptions.ThrowIfFlagsAreNotDefined(nameof(sessionOptions));

        EnsureRequestOriginAllowed(sessionOptions);

        var cookie = ReadCookie();

        if (cookie is null)
            return null;

        ValidateUserIdPrecondition(cookie.Token, sessionOptions.HasAllFlags(SessionAccessOptions.OptionalUserIdPrecondition));

        bool forceValidate = sessionOptions.HasAllFlags(SessionAccessOptions.ForceValidate);

        if (!forceValidate && !IsRefreshDue(cookie))
            return cookie.Token;

        return await ValidateAsync(forceValidate);
    }

    /// <inheritdoc/>
    public override async Task<TSessionToken> SignInAsync(bool persistent, Func<SignInInfo, Task<TSessionToken>> createSessionFunc)
    {
        // Blocks login CSRF: a cross-origin page must not be able to sign the browser in to a session of its choosing.
        EnsureSignInOriginAllowed();

        var utcNow = _handler.UtcNow;

        // A session that starts now is always within its maximum lifetime, which caps the validity period of the new session if it is shorter.
        var validFor = _options.GetValidFor(persistent, createdUtc: utcNow, utcNow)!.Value;

        var signInInfo = new SignInInfo(Device, IpAddress, utcNow, SessionTime.Add(utcNow, validFor), persistent);
        var token = await createSessionFunc(signInInfo);

        var previousCookie = ReadCookie();
        SetCookie(new(token, signInInfo.RefreshedUtc, validFor, signInInfo.Generation, signInInfo.IsPersistent), utcNow);

        // Marks the response as a sign-in's, whose cookie the toolkit's clients take whatever session the device has by then, since a response of the
        // replaced session can arrive first and change it.
        HttpContext.Response.Headers[SessionHeaders.Change] = SessionHeaders.SignIn;

        // The new session replaces the request's previous one, which is ended rather than left in the store, unless the function returned the previous
        // session's token. The new cookie is issued first, so the client keeps the new session if ending the previous one fails. Not cancelled with the
        // request, like a sign-out.
        if (previousCookie is not null && !Equals(previousCookie.Token, token))
        {
            await using var scope = _handler.CreateStoreScope();
            await SessionHandler<TSessionToken>.GetStore(scope).InvalidateSessionAsync(previousCookie.Token, CancellationToken.None);
        }

        return token;
    }

    /// <inheritdoc/>
    public override void EnsureSignInOriginAllowed() => EnsureRequestOriginAllowed(_options.ForcedAccessOptions);

    /// <inheritdoc/>
    public override async Task SignOutAsync()
    {
        // Signing out only needs the token to identify the session, so no session validation or refresh is performed. The user ID precondition is only
        // checked when the request has one, so a client that does not know who is signed in can still sign out.

        EnsureRequestOriginAllowed(_options.ForcedAccessOptions);

        var cookie = ReadCookie();

        if (cookie is not null)
        {
            ValidateUserIdPrecondition(cookie.Token, optional: true);

            // Not cancelled with the request: a client that disconnects must not keep the session it asked to end.
            await using var scope = _handler.CreateStoreScope();
            await SessionHandler<TSessionToken>.GetStore(scope).InvalidateSessionAsync(cookie.Token, CancellationToken.None);
        }

        ClearToken();

        // Marks the response as a sign-out's, whose deletion the toolkit's clients take whatever session the device has by then, since a response of the
        // same session, such as a refresh, can arrive first and change it.
        if (!HttpContext.Response.HasStarted)
            HttpContext.Response.Headers[SessionHeaders.Change] = SessionHeaders.SignOut;
    }

    /// <inheritdoc/>
    public override void SetToken(TSessionToken sessionToken)
    {
        var cookie = ReadCookie() ?? throw SessionErrors.NoSessionToSetTokenOf();

        SetCookie(cookie with { Token = sessionToken }, _handler.UtcNow);
    }

    /// <inheritdoc/>
    public override void ClearToken() => ForgetToken(deleteCookie: true);

    /// <summary>
    /// Drops the session of the request, and deletes the session cookie if <paramref name="deleteCookie"/> is <see langword="true"/> and the response hasn't
    /// started.
    /// </summary>
    private void ForgetToken(bool deleteCookie)
    {
        _cookie = null;
        _cookieRead = true;
        _skipDeferredUpdate = true;

        // The response may have already started (e.g. hub connections and streaming responses), in which case cookies can no longer be sent. The client is
        // responsible for discarding its cookie when it receives the resulting unauthorized response.
        if (!deleteCookie || HttpContext.Response.HasStarted)
            return;

        // Domain/Path must match the values used when the cookie was issued; otherwise the browser keeps the original cookie alongside the deletion attempt.
        // The deletion is appended as a cookie that has expired, which leaves the headers of other cookies to RemoveSessionCookieHeaders.
        RemoveSessionCookieHeaders();
        HttpContext.Response.Cookies.Append(_options.SessionCookieName, string.Empty, new CookieOptions {
            Domain = _options.CookieDomain,
            Path = _options.CookiePath,
            Secure = true,
            SameSite = SameSiteMode.None,
            Expires = DateTimeOffset.UnixEpoch,
        });
    }

    /// <summary>
    /// Gets the session token of the context, or <see langword="null"/> if it has no session.
    /// </summary>
    internal TSessionToken? CurrentToken => Volatile.Read(ref _cookie)?.Token;

    /// <summary>
    /// Gets the request's session token for tracking a hub connection, and the time its session was last known to be valid: now if the request validated
    /// it (or signed in), otherwise when the cookie was issued. Returns <see langword="null"/> if the request has no session.
    /// </summary>
    internal (TSessionToken Token, DateTime CheckedUtc)? GetSessionToTrack()
    {
        if (ReadCookie() is not { } cookie)
            return null;

        return (cookie.Token, _sessionValidated ? _handler.UtcNow : cookie.RefreshedUtc);
    }

    /// <summary>
    /// Validates the session if the cookie is due, as retrieving the token does, without the origin and user ID precondition checks. A hub connection's
    /// context is validated when it connects, so that the generation rule knows when the connection's cookie was current: a request that refreshes the
    /// session later then does not make the connection's cookie look like a replay.
    /// </summary>
    /// <exception cref="UnauthorizedApiException">The session store ended the session.</exception>
    /// <exception cref="BadRequestApiException">The request has no user agent (<see cref="ApiErrorCodes.UserAgentRequired"/>).</exception>
    internal async ValueTask ValidateIfDueAsync()
    {
        if (ReadCookie() is { } cookie && IsRefreshDue(cookie))
            await ValidateAsync(forceValidate: false);
    }

    /// <summary>
    /// Replaces the token of a hub connection's context with one the session store gave the connection's session check, so that the connection's later
    /// token retrievals return it. Nothing changes if the context's token is no longer the one that was checked. The connection's response has started,
    /// so no cookie is issued.
    /// </summary>
    internal async ValueTask ApplyReplacementTokenAsync(TSessionToken checkedToken, TSessionToken replacementToken)
    {
        await _validationLock.WaitAsync();

        try
        {
            var cookie = _cookie;

            if (cookie is not null && ReferenceEquals(cookie.Token, checkedToken))
                Volatile.Write(ref _cookie, cookie with { Token = replacementToken });
        }
        finally
        {
            _validationLock.Release();
        }
    }

    /// <summary>
    /// Validates the session against the session store if it was not validated in the request yet, or again if <paramref name="forceValidate"/> is set and
    /// the token was not set in the request, and registers the deferred cookie update if the cookie needs one. Returns the token, or
    /// <see langword="null"/> if the session has ended.
    /// </summary>
    private async ValueTask<TSessionToken?> ValidateAsync(bool forceValidate)
    {
        await _validationLock.WaitAsync();

        try
        {
            // Read under the lock: a concurrent validation in a hub connection, or the connection's session check, can have replaced it.
            var cookie = _cookie;

            if (cookie is null)
                return null;

            // A token that the app set in this request (by signing in or with SetToken) is not validated again.
            if (!_sessionValidated || (forceValidate && !_tokenSet))
            {
                // The device is needed to validate the session (grace period check) and to refresh it at response start. It is read here so that a missing
                // or empty User-Agent header fails the request up front rather than after the endpoint has already run.
                string device = Device;

                var lookup = await GetValidatedSessionAsync(cookie, device);

                if (lookup is null)
                {
                    // A browser keeps its cookie when the session was found ended because the cookie was due: the response of a request that a sign-in
                    // replaced the session of while it ran can arrive after the sign-in's, and the browser would delete the new session's cookie. The
                    // cookie stays due, so it is looked up again whenever it is used, until a sign-in or sign-out replaces it. The cookie of a forced
                    // validation that isn't due is deleted, since it would be accepted without a lookup until it is due.
                    ForgetToken(deleteCookie: !IsBrowserRequest() || !IsRefreshDue(cookie));
                    return null;
                }

                _sessionValidated = true;
                _session = lookup.Session;
                _cookieCurrentUtc = _handler.UtcNow;

                if (lookup.ReplacementToken is not null || lookup.Session.Generation != cookie.Generation)
                {
                    // The store replaced the token, or the cookie is behind the session record (another request already refreshed the session), so bring
                    // the cookie in line with the store right away: the current request operates on the current token, and the cookie is reissued with
                    // the record's existing refresh info. That is no store write, so it does not count as a refresh and losing the reissued cookie is
                    // harmless. A refresh is still performed at response start if one is due.

                    var session = lookup.Session;
                    var validFor = session.ExpiresUtc - session.RefreshedUtc;
                    cookie = new(lookup.ReplacementToken ?? cookie.Token, session.RefreshedUtc, validFor, session.Generation, session.IsPersistent);

                    Volatile.Write(ref _cookie, cookie);
                    _reissueCookie = true;
                }
            }

            // The cookie now carries the session record's refresh info, so a refresh is due based on when the record was last refreshed rather than when
            // the presented cookie was issued. This matters when a concurrent request has already refreshed the session: the record is current, so no
            // refresh is needed and the reissued cookie is enough.

            _refreshDue = IsRefreshDue(cookie);

            if (_refreshDue || _reissueCookie)
                RegisterDeferredCookieUpdate();

            return cookie.Token;
        }
        finally
        {
            _validationLock.Release();
        }
    }

    /// <summary>
    /// Determines whether a cookie is due for validation and refresh: after <see cref="SessionHandlingOptions.RefreshAfter"/>, or when its validity period
    /// ends if that is sooner, as it is near the end of a maximum session lifetime, so that a cookie is never accepted on its own once its session has
    /// expired.
    /// </summary>
    private bool IsRefreshDue(SessionCookie<TSessionToken> cookie)
    {
        var dueAfter = cookie.ValidFor < _options.RefreshAfter ? cookie.ValidFor : _options.RefreshAfter;
        return _handler.UtcNow > SessionTime.Add(cookie.RefreshedUtc, dueAfter);
    }

    private void EnsureRequestOriginAllowed(SessionAccessOptions sessionOptions)
    {
        if (!sessionOptions.HasAllFlags(SessionAccessOptions.AllowAllOrigins) && !IsRequestOriginAllowed())
            throw SessionErrors.OriginBlocked();
    }

    /// <summary>
    /// Reads the session cookie from the request the first time it is called and caches the result. If the cookie is present but cannot be read, the
    /// cookie is cleared and <see langword="null"/> is returned.
    /// </summary>
    private SessionCookie<TSessionToken>? ReadCookie()
    {
        if (Volatile.Read(ref _cookieRead))
            return _cookie;

        string? value = HttpContext.Request.Cookies[_options.SessionCookieName];
        var cookie = value is null ? null : _handler.Unprotect(value);

        if (cookie is null)
        {
            if (value is not null)
                ClearToken();

            Volatile.Write(ref _cookieRead, true);
            return null;
        }

        if (!IsRefreshDue(cookie))
            _cookieCurrentUtc = _handler.UtcNow;

        // Marked as read after the cookie is set, so that a concurrent first read in a hub connection never sees no cookie.
        _cookie = cookie;
        Volatile.Write(ref _cookieRead, true);

        return cookie;
    }

    /// <summary>
    /// Sets the session cookie of the request and issues it right away. Later token retrievals in the request use it without validating it, and the
    /// deferred cookie update is skipped.
    /// </summary>
    private void SetCookie(SessionCookie<TSessionToken> cookie, DateTime utcNow)
    {
        _cookie = cookie;
        _cookieRead = true;
        _sessionValidated = true;
        _tokenSet = true;
        _skipDeferredUpdate = true;

        WriteCookie(cookie, utcNow);
    }

    private void WriteCookie(SessionCookie<TSessionToken> cookie, DateTime utcNow)
    {
        TimeSpan? maxAge = null;

        if (cookie.IsPersistent)
        {
            // The cookie of a persistent session expires when the session does.
            var remaining = SessionTime.Add(cookie.RefreshedUtc, cookie.ValidFor) - utcNow;
            maxAge = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        var options = new CookieOptions {
            HttpOnly = true,
            IsEssential = true,
            Secure = true,
            SameSite = SameSiteMode.None,
            Domain = _options.CookieDomain,
            Path = _options.CookiePath,
            MaxAge = maxAge,
        };

        RemoveSessionCookieHeaders();
        HttpContext.Response.Cookies.Append(_options.SessionCookieName, _handler.Protect(cookie), options);

        var setCookieHeaders = HttpContext.Response.Headers.SetCookie;
        _handler.CheckCookieSize(setCookieHeaders[setCookieHeaders.Count - 1] ?? string.Empty);
    }

    /// <summary>
    /// Removes the <c>Set-Cookie</c> headers of the session cookie that the response has so far, such as the deletion of a cookie that the request had and
    /// that could not be read, before the cookie is written again, so that the response has only its last one. A client that reads only the first header
    /// of a cookie, as the toolkit's clients before version 4 do, then gets that one. The headers of a cookie with the session cookie's name and another
    /// domain or path, which is another cookie, are kept.
    /// </summary>
    private void RemoveSessionCookieHeaders()
    {
        var headers = HttpContext.Response.Headers;
        var setCookies = headers.SetCookie;

        if (setCookies.Count is 0)
            return;

        string?[] kept = [.. setCookies.Where(header => !IsSessionCookieHeader(header))];

        if (kept.Length != setCookies.Count)
            headers.SetCookie = kept;
    }

    /// <summary>
    /// Gets a value indicating whether a <c>Set-Cookie</c> header sets or deletes the session cookie: a cookie with its name, domain and path. A leading
    /// dot of a domain is ignored, as browsers ignore it.
    /// </summary>
    private bool IsSessionCookieHeader(string? header)
    {
        return header is not null && SetCookieHeaderValue.TryParse(header, out var cookie) &&
            cookie.Name.Equals(_options.SessionCookieName, StringComparison.Ordinal) &&
            string.Equals(cookie.Domain.Value?.TrimStart('.'), _options.CookieDomain?.TrimStart('.'), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(cookie.Path.Value, _options.CookiePath, StringComparison.Ordinal);
    }

    private void ValidateUserIdPrecondition(TSessionToken sessionToken, bool optional)
    {
        if (!HttpContext.Request.Query.TryGetValue(_options.UserIdPreconditionQueryName, out var userIdValues))
        {
            if (optional)
                return;

            throw SessionErrors.UserIdPreconditionMissing(_options.UserIdPreconditionQueryName);
        }

        if (userIdValues.Count is not 1)
            throw new BadRequestApiException($"The request has more than one '{_options.UserIdPreconditionQueryName}' query parameter.");

        if (userIdValues[0] is not { Length: > 0 } userId)
            throw new BadRequestApiException($"The '{_options.UserIdPreconditionQueryName}' query parameter is empty.");

        if (userId != sessionToken.UserId)
            throw SessionErrors.UserIdPreconditionMismatch(_options.UserIdPreconditionQueryName, userId);
    }

    /// <summary>
    /// Registers a deferred update of the session cookie at response start. The update is skipped if the application sets or clears the token itself. If a
    /// refresh is due, the session is refreshed (store write + new cookie) and the refreshed cookie is issued, otherwise the current cookie is reissued
    /// as-is. The state is read when the response starts so that changes made by later token retrievals in the same request are reflected.
    /// </summary>
    private void RegisterDeferredCookieUpdate()
    {
        if (_deferredUpdateRegistered || HttpContext.Response.HasStarted)
            return;

        _deferredUpdateRegistered = true;

        HttpContext.Response.OnStarting(async () =>
        {
            if (_skipDeferredUpdate || _cookie is null)
                return;

            var utcNow = _handler.UtcNow;
            var cookie = _refreshDue ? await RefreshSessionAsync(_cookie, utcNow) : await GetCookieToReissueAsync(_cookie);

            if (cookie is not null)
                WriteCookie(cookie, utcNow);
        });
    }

    /// <summary>
    /// Returns the cookie to reissue at response start if its session is still in the store, or <see langword="null"/> if it ended since it was validated,
    /// such as when a sign-in replaced it while the request ran: the response can arrive after the sign-in's, and its cookie would replace the new session's.
    /// A lookup that fails reissues nothing either, which only leaves the cookie to be brought in line by a later request.
    /// </summary>
    private async Task<SessionCookie<TSessionToken>?> GetCookieToReissueAsync(SessionCookie<TSessionToken> cookie)
    {
        try
        {
            await using var scope = _handler.CreateStoreScope();
            var lookup = await SessionHandler<TSessionToken>.GetStore(scope).GetSessionAsync(cookie.Token, HttpContext.RequestAborted);

            return lookup is null ? null : cookie;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Gets a value indicating whether the request comes from a browser, by its fetch metadata (<c>Sec-Fetch-Site</c>), which current browsers send with
    /// their requests and other HTTP clients don't.
    /// </summary>
    private bool IsBrowserRequest() => HttpContext.Request.Headers.ContainsKey("Sec-Fetch-Site");

    /// <summary>
    /// Validates that the session is still alive (neither expired nor past its maximum lifetime) and that the cookie's generation is acceptable, returning
    /// the lookup result if so. If the session has ended, <see langword="null"/> is returned, after removing it from the store if it was found. If the
    /// store ends the session by throwing an <see cref="UnauthorizedApiException"/>, the session is removed from the store and the cookie cleared before
    /// the exception is rethrown.
    /// </summary>
    private async Task<SessionLookupResult<TSessionToken>?> GetValidatedSessionAsync(SessionCookie<TSessionToken> cookie, string device)
    {
        await using var scope = _handler.CreateStoreScope();
        var store = SessionHandler<TSessionToken>.GetStore(scope);

        SessionLookupResult<TSessionToken>? lookup;

        try
        {
            lookup = await store.GetSessionAsync(cookie.Token, HttpContext.RequestAborted);
        }
        catch (UnauthorizedApiException)
        {
            // Not cancelled with the request, like a sign-out: the session ends, and the store's exception is reported, even if the client disconnects.
            await store.InvalidateSessionAsync(cookie.Token, CancellationToken.None);
            ClearToken();
            throw;
        }

        if (lookup is null)
            return null;

        var session = lookup.Session;
        var utcNow = _handler.UtcNow;

        if (session.IsExpired(_options, utcNow) || !IsGenerationAccepted(session, cookie, device, utcNow))
        {
            await store.InvalidateSessionAsync(cookie.Token, HttpContext.RequestAborted);
            return null;
        }

        return lookup;
    }

    /// <summary>
    /// Determines whether a cookie's generation is accepted: the session's current generation, an older one if the session was refreshed after the cookie
    /// was last known to be current, or the previous one within a grace window from the same device. The window prevents throwing away the session in a
    /// race where another request completes its refresh before this request (or a connection that was established with the previous cookie, i.e. a
    /// reconnecting hub connection) presents the older cookie. The IP address is intentionally not compared since devices (mobile in particular) switch
    /// networks frequently.
    /// </summary>
    private bool IsGenerationAccepted(SessionRecord session, SessionCookie<TSessionToken> cookie, string device, DateTime utcNow)
    {
        if (session.Generation == cookie.Generation)
            return true;

        // Another request refreshed the session after this context's cookie was current: the client's requests refresh the session while one of its hub
        // connections is open, whose context keeps the cookie the connection was established with.
        if (session.Generation > cookie.Generation && session.RefreshedUtc > _cookieCurrentUtc)
            return true;

        return session.Generation == cookie.Generation + 1 &&
            utcNow - session.RefreshedUtc <= _options.PreviousGenerationGracePeriod &&
            session.Device == device;
    }

    /// <summary>
    /// Performs the actual session refresh (conditional store write + new cookie). Called from the deferred OnStarting callback. This method never
    /// invalidates the session: validation is the responsibility of <see cref="GetValidatedSessionAsync"/>, which runs at request start. The store write is
    /// gated on the session generation still being the one that was validated, so if another request refreshed the session in the meantime (or the session
    /// was invalidated) the write does not happen and <see langword="null"/> is returned, which skips issuing a cookie. The concurrent request's response
    /// delivered the current cookie to the client, and the grace period covers anything still in flight. A session that reached its maximum lifetime after
    /// it was validated is not refreshed either, which leaves it to be ended by the next validation.
    /// </summary>
    private async Task<SessionCookie<TSessionToken>?> RefreshSessionAsync(SessionCookie<TSessionToken> cookie, DateTime utcNow)
    {
        if (_session is null)
            return null;

        // The validity period never extends past the maximum lifetime, so neither the record nor the cookie of a persistent session claims the session is
        // valid after it has to end.
        if (_options.GetValidFor(_session.IsPersistent, _session.CreatedUtc, utcNow) is not { } validFor)
            return null;

        var refreshedSession = new SessionRecord {
            Device = Device,
            IpAddress = IpAddress,
            RefreshedUtc = utcNow,
            ExpiresUtc = SessionTime.Add(utcNow, validFor),
            Generation = _session.Generation + 1,
            IsPersistent = _session.IsPersistent,
            CreatedUtc = _session.CreatedUtc,
        };

        await using var scope = _handler.CreateStoreScope();
        var store = SessionHandler<TSessionToken>.GetStore(scope);

        if (!await store.TryRefreshSessionAsync(cookie.Token, refreshedSession, expectedGeneration: _session.Generation, HttpContext.RequestAborted))
            return null;

        return cookie with { RefreshedUtc = utcNow, ValidFor = validFor, Generation = refreshedSession.Generation };
    }
}
