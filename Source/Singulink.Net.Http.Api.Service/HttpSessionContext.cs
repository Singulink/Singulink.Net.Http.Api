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

    private bool _deferredRefreshRegistered;
    private bool _skipDeferredRefresh;

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

        if (!sessionOptions.HasAllFlags(SessionAccessOptions.AllowAllOrigins) && !IsRequestOriginAllowed())
            throw new ForbiddenApiException("Cross-origin request was blocked.");

        string sessionCookie = HttpContext.Request.Cookies[_options.SessionCookieName];

        if (sessionCookie is null)
            return null;

        try
        {
            string sessionCookieData;

            try
            {
                sessionCookieData = _dataProtector.Unprotect(sessionCookie);
            }
            catch (CryptographicException)
            {
                goto InvalidSessionCookie;
            }

            var sessionToken = JsonSerializer.Deserialize<TSessionToken>(sessionCookieData);

            if (sessionToken is null)
                goto InvalidSessionCookie;

            ValidateUserIdPrecondition(sessionToken, sessionOptions.HasAllFlags(SessionAccessOptions.OptionalUserIdPrecondition));

            if (sessionOptions.HasAllFlags(SessionAccessOptions.ForceRefresh) || sessionToken.RefreshedUtc.Add(sessionToken.RefreshAfter) < DateTime.UtcNow)
            {
                if (!await ValidateSessionAsync(sessionToken))
                    goto InvalidSessionCookie;

                RegisterDeferredRefresh(sessionToken);
            }

            return sessionToken;
        }
        catch (Exception ex) when (ex is JsonException or UnauthorizedApiException) { }

        InvalidSessionCookie:

        ClearToken();
        return null;
    }

    /// <inheritdoc/>
    public override async Task<TSessionToken> SignInAsync(bool persistent, Func<SignInInfo, Task<TSessionToken>> createSessionFunc)
    {
        var signInInfo = new SignInInfo(Device, IpAddress, persistent ? _options.PersistentSessionExpiry : _options.TempSessionExpiry, persistent);
        var token = await createSessionFunc(signInInfo);

        // TODO: Clear token? Cache on HTTP context (here and in GetTokenAsync)?

        SetToken(token);
        return token;
    }

    /// <inheritdoc/>
    public override async Task SignOutAsync()
    {
        var sessionToken = await GetTokenAsync();

        if (sessionToken is not null)
        {
            await using var dataAdapterContext = _sessionStoreContextFactory.Create();
            await dataAdapterContext.InvalidateSessionAsync(sessionToken);
            ClearToken();
        }
    }

    /// <inheritdoc/>
    public override void SetToken(TSessionToken sessionToken)
    {
        _skipDeferredRefresh = true;
        SetTokenInternal(sessionToken);
    }

    /// <inheritdoc/>
    public override void ClearToken()
    {
        _skipDeferredRefresh = true;
        HttpContext.Response.Cookies.Delete(_options.SessionCookieName);
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

    private void RegisterDeferredRefresh(TSessionToken sessionToken)
    {
        if (_deferredRefreshRegistered || HttpContext.Response.HasStarted)
            return;

        _deferredRefreshRegistered = true;

        HttpContext.Response.OnStarting(async () =>
        {
            if (_skipDeferredRefresh)
                return;

            var refreshedToken = await RefreshSessionTokenAsync(sessionToken);

            if (refreshedToken is not null)
                SetTokenInternal(refreshedToken);
        });
    }

    private async Task<bool> ValidateSessionAsync(TSessionToken sessionToken)
    {
        await using var storeContext = _sessionStoreContextFactory.Create();
        var sessionData = await storeContext.GetSessionDataAsync(sessionToken);
        var timeSinceDataRefresh = DateTime.UtcNow - sessionData?.RefreshedUtc ?? TimeSpan.MaxValue;

        if (sessionData is null || timeSinceDataRefresh > sessionData.ValidFor)
        {
            if (sessionData is not null)
                await storeContext.InvalidateSessionAsync(sessionToken);

            return false;
        }

        if (sessionData.Generation != sessionToken.Generation)
        {
            // Allow for a small time window where multiple concurrent refreshes from the same device are allowed to prevent throwing away the session in a
            // race condition where another request completes its refresh before the current request's deferred refresh runs.

            if (sessionData.Generation != sessionToken.Generation + 1 ||
                timeSinceDataRefresh > _options.MultipleRefreshGracePeriod ||
                sessionData.Device != Device ||
                !Equals(sessionData.IpAddress, IpAddress))
            {
                await storeContext.InvalidateSessionAsync(sessionToken);
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Performs the actual session token refresh (store write + new token). Called from the deferred OnStarting callback. This method never invalidates the
    /// session — validation is the responsibility of <see cref="ValidateSessionAsync"/> which runs at request start. If the refresh cannot be safely
    /// performed (e.g. session expired or generation advanced by more than 1 or from a different device/IP), it silently returns <see langword="null"/>.
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
            // Another concurrent request already refreshed the session. Allow it if the generation advanced by exactly 1 from the same device/IP — this
            // just means another request's deferred refresh completed before ours. Skip the store write (already done) and produce a token with the current
            // generation so the client gets an up-to-date cookie. Otherwise silently skip — ValidateSessionAsync already ran at request start and the next
            // request will catch any real compromise.

            if (sessionData.Generation != sessionToken.Generation + 1 ||
                sessionData.Device != Device ||
                !Equals(sessionData.IpAddress, IpAddress))
            {
                return null;
            }
        }
        else
        {
            sessionData.Device = Device;
            sessionData.IpAddress = IpAddress;
            sessionData.RefreshedUtc = DateTime.UtcNow;
            sessionData.ValidFor = sessionData.IsPersistent ? _options.PersistentSessionExpiry : _options.TempSessionExpiry;

            if (timeSinceDataRefresh > _options.MultipleRefreshGracePeriod)
                sessionData.Generation++;

            await storeContext.UpdateSessionAsync(sessionData);
        }

        return await storeContext.RefreshTokenAsync(sessionToken, sessionData);
    }
}
