namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// A request against a <see cref="SessionTestHost"/> with helpers for starting the response and inspecting the session cookies it produced.
/// </summary>
public sealed class TestRequest
{
    private readonly SessionTestHost _host;

    public TestRequest(SessionTestHost host, HttpContext context, TestResponseFeature responseFeature)
    {
        _host = host;
        Context = context;
        Response = responseFeature;
        Session = context.GetRequiredSessionContext<TestSessionToken>();
    }

    public HttpContext Context { get; }

    public TestResponseFeature Response { get; }

    public SessionContext<TestSessionToken> Session { get; }

    public ValueTask<TestSessionToken?> GetTokenAsync(SessionAccessOptions options = default) => Session.GetTokenAsync(options);

    /// <summary>
    /// Runs deferred response callbacks (i.e. deferred session token refreshes) and marks the response as started.
    /// </summary>
    public Task StartResponseAsync() => Response.StartAsync();

    /// <summary>
    /// Gets all Set-Cookie headers for the session cookie.
    /// </summary>
    public IReadOnlyList<SetCookieHeader> SessionCookies => Context.Response.Headers.SetCookie
        .Where(v => v is not null)
        .Select(v => SetCookieHeader.Parse(v!))
        .Where(c => c.Name == _host.Options.SessionCookieName)
        .ToList();

    /// <summary>
    /// Gets the single session cookie that was set, failing if there is not exactly one.
    /// </summary>
    public SetCookieHeader IssuedCookie => SessionCookies.ShouldHaveSingleItem();

    /// <summary>
    /// Gets the token from the single session cookie that was set, or <see langword="null"/> if no session cookie was set.
    /// </summary>
    public TestSessionToken? IssuedToken
    {
        get {
            var cookies = SessionCookies;

            if (cookies.Count is 0)
                return null;

            var cookie = cookies.ShouldHaveSingleItem();
            cookie.IsDeletion.ShouldBeFalse("session cookie was cleared, not issued");
            return _host.Unprotect(cookie.Value);
        }
    }

    /// <summary>
    /// Gets a value indicating whether the single session cookie header clears the cookie.
    /// </summary>
    public bool CookieCleared => SessionCookies is [{ IsDeletion: true }];
}

public sealed record SetCookieHeader(
    string Name,
    string Value,
    TimeSpan? MaxAge,
    DateTimeOffset? Expires,
    string? Path,
    string? Domain,
    string? SameSite,
    bool Secure,
    bool HttpOnly)
{
    public bool IsDeletion => Value.Length is 0 && Expires is { } expires && expires < DateTimeOffset.UtcNow;

    public static SetCookieHeader Parse(string header)
    {
        var parts = header.Split(';', StringSplitOptions.TrimEntries);
        int eq = parts[0].IndexOf('=');
        string name = parts[0][..eq];
        string value = parts[0][(eq + 1)..];

        TimeSpan? maxAge = null;
        DateTimeOffset? expires = null;
        string? path = null, domain = null, sameSite = null;
        bool secure = false, httpOnly = false;

        foreach (string attr in parts.Skip(1))
        {
            int attrEq = attr.IndexOf('=');
            string attrName = (attrEq < 0 ? attr : attr[..attrEq]).ToLowerInvariant();
            string? attrValue = attrEq < 0 ? null : attr[(attrEq + 1)..];

            switch (attrName)
            {
                case "max-age": maxAge = TimeSpan.FromSeconds(long.Parse(attrValue!)); break;
                case "expires": expires = DateTimeOffset.Parse(attrValue!, null, System.Globalization.DateTimeStyles.AssumeUniversal); break;
                case "path": path = attrValue; break;
                case "domain": domain = attrValue; break;
                case "samesite": sameSite = attrValue; break;
                case "secure": secure = true; break;
                case "httponly": httpOnly = true; break;
            }
        }

        return new SetCookieHeader(name, value, maxAge, expires, path, domain, sameSite, secure, httpOnly);
    }
}
