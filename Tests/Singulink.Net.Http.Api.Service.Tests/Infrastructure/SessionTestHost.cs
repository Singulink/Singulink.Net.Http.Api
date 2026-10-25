using System.Globalization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Wires up session handling through the real service registration (ephemeral data protection, origin validator, in-memory store, a test clock) and
/// creates requests against it. Session tokens are serialized with reflection, or with the specified JSON type info.
/// </summary>
public sealed class SessionTestHost
{
    public const string DefaultDevice = "TestClient/1.0";
    public const string OtherDevice = "OtherClient/2.0";

    public static readonly IPAddress DefaultIp = IPAddress.Parse("203.0.113.10");
    public static readonly IPAddress OtherIp = IPAddress.Parse("198.51.100.20");

    private readonly IDataProtector _protector;
    private readonly JsonTypeInfo<TestSessionToken>? _tokenTypeInfo;

    public SessionTestHost(Action<SessionHandlingOptions>? configure = null, JsonTypeInfo<TestSessionToken>? tokenTypeInfo = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton<IOriginValidator>(new OriginValidator("example.com", "*.example.com"));
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton(Store);
        services.AddLogging(builder => builder.AddProvider(Logs));

        if (tokenTypeInfo is null)
            services.AddHttpSessionHandling<TestSessionToken, ScopedInMemorySessionStore>(configure);
        else
            services.AddHttpSessionHandling<TestSessionToken, ScopedInMemorySessionStore>(tokenTypeInfo, configure);

        _tokenTypeInfo = tokenTypeInfo;

        Services = services.BuildServiceProvider();
        Options = Services.GetRequiredService<IOptions<SessionHandlingOptions>>().Value;
        _protector = Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Options.DataProtectionPurpose ?? TestSessionCookies.Purpose);
    }

    public ServiceProvider Services { get; }

    public InMemorySessionStore Store { get; } = new();

    public SessionHandlingOptions Options { get; }

    public TestClock Clock { get; } = new();

    public CapturingLoggerProvider Logs { get; } = new();

    public DateTime UtcNow => Clock.GetUtcNow().UtcDateTime;

    public string Protect(TestSessionCookie cookie) => ProtectRaw(TestSessionCookies.ToJson(cookie, _tokenTypeInfo));

    public string ProtectRaw(string data) => _protector.Protect(data);

    public string UnprotectRaw(string cookieValue) => _protector.Unprotect(cookieValue);

    public TestSessionCookie Unprotect(string cookieValue) => TestSessionCookies.FromJson(UnprotectRaw(cookieValue), _tokenTypeInfo);

    /// <summary>
    /// Creates a session in the store and returns the session cookie as it would have been issued at sign-in. <paramref name="age"/> backdates the refresh
    /// time of both the session and the cookie. <paramref name="createdAge"/> backdates the creation time of the session, which defaults to the refresh
    /// time.
    /// </summary>
    public TestSessionCookie CreateSession(
        long sessionId = 1,
        int userId = 1,
        TimeSpan? age = null,
        TimeSpan? validFor = null,
        int generation = 0,
        bool persistent = true,
        string device = DefaultDevice,
        IPAddress? ipAddress = null,
        TimeSpan? createdAge = null)
    {
        var utcNow = UtcNow;
        var refreshedUtc = utcNow - (age ?? TimeSpan.Zero);
        var createdUtc = createdAge is null ? refreshedUtc : utcNow - createdAge.Value;
        validFor ??= persistent ? Options.PersistentSessionExpiry : Options.TempSessionExpiry;

        Store.UserStamps.TryAdd(userId, 1);

        Store.Sessions[sessionId] = new TestSessionData {
            Id = sessionId,
            UserId = userId,
            Device = device,
            IpAddress = ipAddress ?? DefaultIp,
            RefreshedUtc = refreshedUtc,
            ExpiresUtc = refreshedUtc + validFor.Value,
            Generation = generation,
            IsPersistent = persistent,
            CreatedUtc = createdUtc,
        };

        var token = new TestSessionToken(sessionId, userId, Store.UserStamps[userId]);
        return new TestSessionCookie(token, refreshedUtc, validFor.Value, generation, persistent);
    }

    /// <summary>
    /// Simulates a change to the user's data that makes existing tokens out of date.
    /// </summary>
    public void BumpUserStamp(int userId = 1) => Store.UserStamps[userId]++;

    /// <summary>
    /// Simulates another request having refreshed the session (e.g. a concurrent request or a hub reconnect race).
    /// </summary>
    public void SetSessionState(long sessionId = 1, int? generation = null, TimeSpan? age = null, string? device = null)
    {
        var data = Store.Sessions[sessionId];

        if (generation is not null)
            data.Generation = generation.Value;

        if (age is not null)
            data.RefreshedUtc = UtcNow - age.Value;

        if (device is not null)
            data.Device = device;
    }

    /// <summary>
    /// Creates a request with the specified session cookie. The user ID precondition query parameter is included by default and matches the cookie's
    /// user (or "1" if there is no cookie).
    /// </summary>
    public TestRequest CreateRequest(
        TestSessionCookie? cookie = null,
        string? rawCookie = null,
        bool includeUserIdPrecondition = true,
        string? userIdPreconditionValue = null,
        string? origin = null,
        string device = DefaultDevice,
        IPAddress? ipAddress = null)
    {
        var context = new DefaultHttpContext();
        var responseFeature = new TestResponseFeature();

        context.Features.Set<IHttpResponseFeature>(responseFeature);
        context.RequestServices = Services;
        context.Request.Headers.UserAgent = device;
        context.Connection.RemoteIpAddress = ipAddress ?? DefaultIp;

        string? cookieValue = rawCookie ?? (cookie is null ? null : Protect(cookie));

        if (cookieValue is not null)
            context.Request.Headers.Cookie = $"{Options.SessionCookieName}={cookieValue}";

        if (origin is not null)
            context.Request.Headers.Origin = origin;

        if (includeUserIdPrecondition)
        {
            string value = userIdPreconditionValue ?? cookie?.Token.UserId.ToString(CultureInfo.InvariantCulture) ?? "1";
            context.Request.QueryString = new QueryString($"?{Options.UserIdPreconditionQueryName}={value}");
        }

        return new TestRequest(this, context, responseFeature);
    }
}
