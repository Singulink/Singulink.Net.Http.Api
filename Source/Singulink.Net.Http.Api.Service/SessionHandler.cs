using System.Globalization;
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
/// Base class for handling user sessions for HTTP APIs.
/// </summary>
/// <typeparam name="TUserId">The type of the user ID.</typeparam>
/// <typeparam name="TSessionToken">The type of the session token.</typeparam>
public abstract class SessionHandler<TUserId, TSessionToken>
    where TUserId : notnull, IParsable<TUserId>, IEquatable<TUserId>
    where TSessionToken : class, ISessionToken<TUserId>
{
    private readonly IDataProtector _dataProtector;
    private readonly IOriginValidator _originValidator;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionHandler{TUserId, TSessionToken}"/> class with the specified HTTP context and signer.
    /// </summary>
    /// <param name="context">The HTTP context for the current request.</param>
    /// <param name="originValidator">The trusted origins that are allowed to have session access.</param>
    /// <param name="dataProtectionProvider">The data protection provider used to verify and sign session cookies.</param>
    protected SessionHandler(HttpContext context, IOriginValidator originValidator, IDataProtectionProvider dataProtectionProvider)
    {
        Context = context;
        _originValidator = originValidator;
        _dataProtector = dataProtectionProvider.CreateProtector($"Singulink.CookieSessionHandler[{typeof(TSessionToken)}]");
    }

    /// <summary>
    /// Gets the HTTP context for the current request.
    /// </summary>
    public HttpContext Context { get; }

    /// <summary>
    /// Gets the key for the session cookie. Default if not overridden is <c>"session"</c>.
    /// </summary>
    public virtual string SessionCookieKey { get; } = "session";

    /// <summary>
    /// Gets the key for the user ID precondition header. Default if not overridden is <c>"If-User-Id"</c>. Can return <see langword="null"/> to disable the
    /// precondition header check entirely.
    /// </summary>
    public virtual string? UserIdPreconditionHeaderName { get; } = "If-User-Id";

    /// <summary>
    /// Gets the refresh interval for the session token.
    /// </summary>
    public abstract TimeSpan RefreshInterval { get; }

    /// <summary>
    /// Gets the expiration time for the session cookie. After this time the session cookie can no longer be refreshed.
    /// </summary>
    public abstract TimeSpan SessionCookieExpiration { get; }

    /// <summary>
    /// Gets the device information from the User-Agent header in the request. Returns a parsed device platform, name and version if it could be identified,
    /// otherwise returns the raw user agent string.
    /// </summary>
    public string Device
    {
        get {
            return field ??= GetDevice();

            string GetDevice()
            {
                if (!Context.Request.Headers.TryGetValue(HeaderNames.UserAgent, out var userAgentValues))
                    throw new BadRequestApiException("User agent required in request headers.");

                string deviceUserAgentValue = userAgentValues[0]?.Trim();

                if (string.IsNullOrEmpty(deviceUserAgentValue))
                    throw new BadRequestApiException("Empty user agent in request headers.");

                var userAgent = HttpUserAgentParser.Parse(deviceUserAgentValue);

                if (userAgent.Name is not null && userAgent.Version is not null && userAgent.Platform?.PlatformType is { } platformType)
                    return $"{platformType} ({userAgent.Name} {userAgent.Version})";

                return deviceUserAgentValue;
            }
        }
    }

    /// <summary>
    /// Gets the IP address of the request. If the IP address cannot be determined, returns <see langword="null"/>.
    /// </summary>
    public IPAddress? IpAddress => Context.Connection.RemoteIpAddress;

    /// <summary>
    /// Gets the required session token for the user from the session cookie. If the user is not logged in, throws an <see cref="UnauthorizedApiException"/>.
    /// </summary>
    /// <param name="sessionOptions">Option flags for retrieving the session token.</param>
    public async ValueTask<TSessionToken> GetRequiredSessionTokenAsync(SessionOptions sessionOptions = default)
    {
        return await GetSessionTokenAsync(sessionOptions).ConfigureAwait(false) ?? throw new UnauthorizedApiException("User not logged in.");
    }

    /// <summary>
    /// Gets the optional session token for the user from the session cookie. If the user is not logged in, returns <see langword="null"/>.
    /// </summary>
    /// <param name="sessionOptions">Option flags for retrieving the session token.</param>
    public async ValueTask<TSessionToken?> GetSessionTokenAsync(SessionOptions sessionOptions = default)
    {
        sessionOptions.ThrowIfFlagsAreNotDefined(nameof(sessionOptions));

        if (!sessionOptions.HasAllFlags(SessionOptions.AllowUntrustedOrigin))
            ValidateTrustedOrigin();

        string sessionCookie = Context.Request.Cookies[SessionCookieKey];

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
                throw new UnauthorizedApiException("Invalid session cookie signature.");
            }

            var sessionToken = JsonSerializer.Deserialize<TSessionToken>(sessionCookieData) ?? throw new UnauthorizedApiException("Empty session cookie data.");
            ValidateUserIdPreconditionHeader(sessionToken.UserId, sessionOptions.HasAllFlags(SessionOptions.OptionalUserIdPrecondition));

            if (sessionOptions.HasAllFlags(SessionOptions.ForceRefresh) || sessionToken.RefreshedUtc.Add(RefreshInterval) < DateTime.UtcNow)
            {
                try
                {
                    sessionToken = await RefreshSessionTokenAsync(sessionToken).ConfigureAwait(false);
                }
                catch (UnauthorizedApiException)
                {
                    ClearSessionCookie();
                    return null;
                }

                SetSessionCookie(sessionToken);
            }

            return sessionToken;
        }
        catch (UnauthorizedApiException)
        {
            ClearSessionCookie();
            throw;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new UnauthorizedApiException("Invalid session cookie.", ex);
        }
    }

    /// <summary>
    /// Sets the session cookie for the user with the specified session token.
    /// </summary>
    public void SetSessionCookie(TSessionToken sessionToken)
    {
        string sessionCookieData = JsonSerializer.Serialize(sessionToken);
        string cookie = _dataProtector.Protect(sessionCookieData);

        var options = new CookieOptions {
            HttpOnly = true,
            IsEssential = true,
            Secure = true,
            SameSite = SameSiteMode.None,
            Expires = sessionToken.Persisted ? DateTimeOffset.UtcNow.Add(SessionCookieExpiration) : null,
        };

        Context.Response.Cookies.Append(SessionCookieKey, cookie, options);
    }

    /// <summary>
    /// Clears the session cookie for the user.
    /// </summary>
    public void ClearSessionCookie()
    {
        Context.Response.Cookies.Delete(SessionCookieKey);
    }

    /// <summary>
    /// Validates the request origin against the trusted origins. Ensures that if an origin is provided in the request headers, it matches a trusted origin.
    /// </summary>
    /// <exception cref="BadRequestApiException">The request contains multiple origin headers.</exception>
    /// <exception cref="UnauthorizedApiException">The request origin is not trusted.</exception>
    public void ValidateTrustedOrigin()
    {
        // If origin is provided, ensure it is a trusted origin

        if (Context.Request.Headers.TryGetValue(HeaderNames.Origin, out var originValues))
        {
            if (originValues.Count is not 1)
                throw new BadRequestApiException($"Request contains multiple '{HeaderNames.Origin}' headers.");

            string origin = originValues[0];

            if (origin is null || !_originValidator.IsTrusted(origin))
                throw new ForbiddenApiException("Cross-origin request from an untrusted origin.");
        }
    }

    /// <summary>
    /// Refreshes the session token for the user. Should throw <see cref="UnauthorizedApiException"/> if the session token cannot be refreshed (e.g., user is no
    /// longer logged in or the session has expired).
    /// </summary>
    protected abstract Task<TSessionToken> RefreshSessionTokenAsync(TSessionToken sessionToken);

    private void ValidateUserIdPreconditionHeader(TUserId sessionUserId, bool optional)
    {
        if (UserIdPreconditionHeaderName is null)
            return;

        if (!Context.Request.Headers.TryGetValue(UserIdPreconditionHeaderName, out var userIdValues))
        {
            if (optional)
                return;

            throw new UserRequiredApiException($"Request is missing required '{UserIdPreconditionHeaderName}' precondition header.");
        }

        if (userIdValues.Count is not 1)
            throw new BadRequestApiException($"Request contains multiple '{UserIdPreconditionHeaderName}' headers.");

        if (!TUserId.TryParse(userIdValues[0], CultureInfo.InvariantCulture, out var userId))
            throw new BadRequestApiException($"Invalid '{UserIdPreconditionHeaderName}' header value.");

        if (!userId.Equals(sessionUserId))
            throw new UserChangedApiException($"Request user identified in the '{UserIdPreconditionHeaderName}' header does not match session user.");
    }
}
