using System.Text.Json;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The JSON in session cookies with each session handling registration: reflection-based, and with a specified JSON type info. The library's fields are
/// written next to the token, which is written with the registration's JSON settings.
/// </summary>
[PrefixTestClass]
public sealed class SessionTokenSerializationTests
{
    private static readonly DateTime FixedUtc = new(2026, 10, 21, 8, 30, 0, DateTimeKind.Utc);

    [TestMethod]
    public void WritesLibraryFieldsNextToToken()
    {
        var host = new SessionTestHost();
        host.Clock.Advance(new DateTimeOffset(FixedUtc) - host.Clock.GetUtcNow());
        var session = host.CreateSession(sessionId: 7, generation: 3);
        var request = host.CreateRequest(session);

        request.Session.SetToken(session.Token);

        host.UnprotectRaw(request.IssuedCookie.Value)
            .ShouldBe("""{"r":"2026-10-21T08:30:00Z","v":"30.00:00:00","g":3,"p":true,"t":{"SessionId":7,"UserId":1,"Stamp":1,"BuildCount":0}}""");
    }

    [TestMethod]
    public async Task ReflectionRegistration_ReadsDefaultOptionsFormat()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();

        string refreshed = JsonSerializer.Serialize(session.RefreshedUtc);
        string payload = $$$"""{"r":{{{refreshed}}},"v":"30.00:00:00","g":0,"p":true,"t":{"SessionId":1,"UserId":1,"Stamp":1}}""";
        var request = host.CreateRequest(rawCookie: host.ProtectRaw(payload));

        (await request.GetTokenAsync()).ShouldBe(session.Token);
    }

    [TestMethod]
    public async Task ReadsFieldsInAnyOrder_AndIgnoresUnknownFields()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();

        string refreshed = JsonSerializer.Serialize(session.RefreshedUtc);
        string payload = $$$"""{"t":{"SessionId":1,"UserId":1,"Stamp":1},"x":1,"p":true,"g":0,"v":"30.00:00:00","r":{{{refreshed}}}}""";
        var request = host.CreateRequest(rawCookie: host.ProtectRaw(payload));

        (await request.GetTokenAsync(SessionAccessOptions.ForceValidate)).ShouldBe(session.Token);
    }

    [TestMethod]
    public async Task TypeInfoRegistration_RoundTripsWithTypeInfoSettings()
    {
        var host = new SessionTestHost(tokenTypeInfo: CamelCaseTestSessionTokenJsonContext.Default.TestSessionToken);
        var session = host.CreateSession();
        var request = host.CreateRequest(session);

        request.Session.SetToken(session.Token);
        string cookie = request.IssuedCookie.Value;

        host.UnprotectRaw(cookie).ShouldEndWith("""
            "t":{"sessionId":1,"userId":1,"stamp":1,"buildCount":0}}
            """);

        var nextRequest = host.CreateRequest(rawCookie: cookie);
        (await nextRequest.GetTokenAsync()).ShouldBe(session.Token);
    }

    [TestMethod]
    public async Task TypeInfoRegistration_RefreshedCookie_IsWrittenWithTypeInfoSettings()
    {
        var host = new SessionTestHost(tokenTypeInfo: CamelCaseTestSessionTokenJsonContext.Default.TestSessionToken);
        var session = host.CreateSession(age: TimeSpan.FromMinutes(11));
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync()).ShouldBe(session.Token);
        await request.StartResponseAsync();

        host.UnprotectRaw(request.IssuedCookie.Value).ShouldContain("""
            "t":{"sessionId":1,
            """);
        request.IssuedSession.ShouldNotBeNull().Generation.ShouldBe(1);
    }

    [TestMethod]
    public async Task TypeInfoRegistration_DefaultSettings_ReadsReflectionRegistrationCookies()
    {
        var host = new SessionTestHost(tokenTypeInfo: TestSessionTokenJsonContext.Default.TestSessionToken);
        var session = host.CreateSession();
        var request = host.CreateRequest(rawCookie: host.ProtectRaw(TestSessionCookies.ToJson(session)));

        (await request.GetTokenAsync()).ShouldBe(session.Token);
    }

    [TestMethod]
    public async Task TypeInfoRegistration_MalformedPayload_ReturnsNullAndClearsCookie()
    {
        var host = new SessionTestHost(tokenTypeInfo: TestSessionTokenJsonContext.Default.TestSessionToken);
        var request = host.CreateRequest(rawCookie: host.ProtectRaw("[1, 2, 3]"));

        (await request.GetTokenAsync()).ShouldBeNull();

        request.CookieCleared.ShouldBeTrue();
        host.Store.Calls.ShouldBeEmpty();
    }
}
