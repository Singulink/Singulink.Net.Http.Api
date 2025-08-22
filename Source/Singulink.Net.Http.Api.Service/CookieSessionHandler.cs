using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Net.Http.Headers;
using MyCSharp.HttpUserAgentParser;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Base class for handling user sessions using cookies in an HTTP context.
/// </summary>
/// <typeparam name="TUserId">The type of the user ID.</typeparam>
/// <typeparam name="TSessionToken">The type of the session token.</typeparam>
public abstract class CookieSessionHandler<TUserId, TSessionToken>
    where TUserId : notnull, IParsable<TUserId>, IEquatable<TUserId>
    where TSessionToken : class, ISessionToken<TUserId>
{
    private readonly ISigner _signer;

    /// <summary>
    /// Initializes a new instance of the <see cref="CookieSessionHandler{TUserId, TSessionToken}"/> class with the specified HTTP context and signer.
    /// </summary>
    /// <param name="context">The HTTP context for the current request.</param>
    /// <param name="signer">The signer used to verify and sign session cookies.</param>
    protected CookieSessionHandler(HttpContext context, ISigner signer)
    {
        Context = context;
        _signer = signer;
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
    public virtual string? UserIdPreconditionHeaderKey { get; } = "If-User-Id";

    /// <summary>
    /// Gets a value indicating whether the user ID precondition header is required. Ignored if <see cref="UserIdPreconditionHeaderKey"/> is <see
    /// langword="null"/>. If overridden to return <see langword="false"/>, the precondition header is optional and only checked if it is provided in the
    /// request.
    /// </summary>
    public virtual bool IsUserIdPreconditionRequired => true;

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
    /// <param name="forceRefresh">If <see langword="true"/>, forces a refresh of the session token even if it is still valid.</param>
    public async ValueTask<TSessionToken> GetRequiredSessionTokenAsync(bool forceRefresh = false)
    {
        return await GetOptionalSessionTokenAsync().ConfigureAwait(false) ?? throw new UnauthorizedApiException("User not logged in.");
    }

    /// <summary>
    /// Gets the optional session token for the user from the session cookie. If the user is not logged in, returns <see langword="null"/>.
    /// </summary>
    /// <param name="forceRefresh">If <see langword="true"/>, forces a refresh of the session token even if it is still valid.</param>
    public async ValueTask<TSessionToken?> GetOptionalSessionTokenAsync(bool forceRefresh = false)
    {
        string sessionCookie = Context.Request.Cookies[SessionCookieKey];

        if (sessionCookie is null)
            return null;

        // TODO: Optimize with span split instead of allocating string split

        string[] sessionCookieParts = sessionCookie.Split(' ');

        try
        {
            if (sessionCookieParts.Length is not 2)
                throw new UnauthorizedApiException("Invalid session cookie.");

            Span<byte> sessionCookieData = Convert.FromBase64String(sessionCookieParts[0]);
            Span<byte> signature = Convert.FromBase64String(sessionCookieParts[1]);

            if (!_signer.Verify(sessionCookieData, signature))
                throw new UnauthorizedApiException("Invalid session cookie signature.");

            var sessionToken = JsonSerializer.Deserialize<TSessionToken>(sessionCookieData) ?? throw new UnauthorizedApiException("Empty session cookie data.");
            ValidateUserIdPreconditionHeader(sessionToken.UserId);

            if (forceRefresh || sessionToken.RefreshedUtc.Add(RefreshInterval) < DateTime.UtcNow)
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
        byte[] sessionCookieData = JsonSerializer.SerializeToUtf8Bytes(sessionToken);
        byte[] signature = _signer.GetSignature(sessionCookieData);

        string cookie = $"{Convert.ToBase64String(sessionCookieData)} {Convert.ToBase64String(signature)}";

        var options = new CookieOptions {
            HttpOnly = true,
            IsEssential = true,
            Secure = true,
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
    /// Refreshes the session token for the user. Should throw <see cref="UnauthorizedApiException"/> if the session token cannot be refreshed (e.g., user is no
    /// longer logged in or the session has expired).
    /// </summary>
    protected abstract Task<TSessionToken> RefreshSessionTokenAsync(TSessionToken sessionToken);

    private void ValidateUserIdPreconditionHeader(TUserId sessionUserId)
    {
        if (UserIdPreconditionHeaderKey is null)
            return;

        if (!Context.Request.Headers.TryGetValue(UserIdPreconditionHeaderKey, out var userIdValues))
        {
            if (IsUserIdPreconditionRequired)
                throw new UserRequiredApiException($"Request is missing required '{UserIdPreconditionHeaderKey}' precondition header.");

            return;
        }

        if (userIdValues.Count is not 1)
            throw new BadRequestApiException($"Request contains multiple '{UserIdPreconditionHeaderKey}' headers.");

        if (!TUserId.TryParse(userIdValues[0], CultureInfo.InvariantCulture, out var userId))
            throw new BadRequestApiException($"Invalid '{UserIdPreconditionHeaderKey}' header value.");

        if (!userId.Equals(sessionUserId))
            throw new UserChangedApiException($"Request user identified in the '{UserIdPreconditionHeaderKey}' header does not match session user.");
    }
}
