using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Wires up session handling through the real service registration (ephemeral data protection, origin validator, in-memory store) and creates
/// requests against it.
/// </summary>
public sealed class SessionTestHost
{
    public const string DefaultDevice = "TestClient/1.0";
    public const string OtherDevice = "OtherClient/2.0";

    public static readonly IPAddress DefaultIp = IPAddress.Parse("203.0.113.10");
    public static readonly IPAddress OtherIp = IPAddress.Parse("198.51.100.20");

    private readonly IDataProtector _protector;

    public SessionTestHost(Action<SessionHandlingOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddSingleton<IOriginValidator>(new OriginValidator("example.com", "*.example.com"));
        services.AddHttpSessionHandling<TestSessionToken, TestSessionData, InMemorySessionStore>(configure);

        Services = services.BuildServiceProvider();
        Store = (InMemorySessionStore)Services.GetRequiredService<ISessionStoreContextFactory<TestSessionToken, TestSessionData>>();
        Options = Services.GetRequiredService<IOptions<SessionHandlingOptions>>().Value;

        // Must match the purpose string used by HttpSessionContextFactory.
        _protector = Services.GetRequiredService<IDataProtectionProvider>().CreateProtector($"Singulink/Session[{typeof(TestSessionToken).FullName}]");
    }

    public ServiceProvider Services { get; }

    public InMemorySessionStore Store { get; }

    public SessionHandlingOptions Options { get; }

    public string Protect(TestSessionToken token) => ProtectRaw(JsonSerializer.Serialize(token));

    public string ProtectRaw(string data) => _protector.Protect(data);

    public TestSessionToken Unprotect(string cookieValue) => JsonSerializer.Deserialize<TestSessionToken>(_protector.Unprotect(cookieValue))!;

    /// <summary>
    /// Creates a session in the store and returns the token as it would have been issued at sign-in. <paramref name="age"/> backdates the refresh time
    /// of both the session and the token.
    /// </summary>
    public TestSessionToken CreateSession(
        long sessionId = 1,
        int userId = 1,
        TimeSpan? age = null,
        TimeSpan? validFor = null,
        int generation = 0,
        bool persistent = true,
        string device = DefaultDevice,
        IPAddress? ipAddress = null)
    {
        var refreshedUtc = DateTime.UtcNow - (age ?? TimeSpan.Zero);
        validFor ??= persistent ? Options.PersistentSessionExpiry : Options.TempSessionExpiry;

        Store.UserStamps.TryAdd(userId, 1);

        Store.Sessions[sessionId] = new TestSessionData {
            Id = sessionId,
            UserId = userId,
            Device = device,
            IpAddress = ipAddress ?? DefaultIp,
            RefreshedUtc = refreshedUtc,
            ValidFor = validFor.Value,
            Generation = generation,
            IsPersistent = persistent,
        };

        return new TestSessionToken(sessionId, userId, Store.UserStamps[userId], refreshedUtc, validFor.Value, generation, persistent, BuildCount: 0);
    }

    /// <summary>
    /// Simulates a change to the user's data that makes existing tokens stale.
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
            data.RefreshedUtc = DateTime.UtcNow - age.Value;

        if (device is not null)
            data.Device = device;
    }

    /// <summary>
    /// Creates a request with the specified session token cookie. The user ID precondition query parameter is included by default and matches the token's
    /// user (or "1" if there is no token).
    /// </summary>
    public TestRequest CreateRequest(
        TestSessionToken? token = null,
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

        string? cookieValue = rawCookie ?? (token is null ? null : Protect(token));

        if (cookieValue is not null)
            context.Request.Headers.Cookie = $"{Options.SessionCookieName}={cookieValue}";

        if (origin is not null)
            context.Request.Headers.Origin = origin;

        if (includeUserIdPrecondition)
        {
            string value = userIdPreconditionValue ?? token?.UserId.ToString(CultureInfo.InvariantCulture) ?? "1";
            context.Request.QueryString = new QueryString($"?{Options.UserIdPreconditionQueryName}={value}");
        }

        return new TestRequest(this, context, responseFeature);
    }
}
