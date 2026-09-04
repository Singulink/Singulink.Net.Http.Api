namespace Singulink.Net.Http.Api.Service;

[PrefixTestClass]
public sealed class SessionTokenReadTests
{
    [TestMethod]
    public async Task NoCookie_ReturnsNullWithoutStoreAccess()
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest();

        (await request.GetTokenAsync()).ShouldBeNull();

        request.SessionCookies.ShouldBeEmpty();
        host.Store.Calls.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task NoCookie_Required_ThrowsUnauthorized()
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest();

        await Should.ThrowAsync<UnauthorizedApiException>(() => request.Session.GetRequiredTokenAsync().AsTask());
    }

    [TestMethod]
    public async Task UnreadableCookie_ReturnsNullAndClearsCookie()
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest(rawCookie: "not-a-protected-token");

        (await request.GetTokenAsync()).ShouldBeNull();

        request.CookieCleared.ShouldBeTrue();
        host.Store.Calls.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task CookieFromDifferentKeyRing_ReturnsNullAndClearsCookie()
    {
        var otherHost = new SessionTestHost();
        var otherToken = otherHost.CreateSession();

        var host = new SessionTestHost();
        var request = host.CreateRequest(rawCookie: otherHost.Protect(otherToken));

        (await request.GetTokenAsync()).ShouldBeNull();

        request.CookieCleared.ShouldBeTrue();
        host.Store.Calls.ShouldBeEmpty();
    }

    [TestMethod]
    [DataRow("[1, 2, 3]")]
    [DataRow("null")]
    [DataRow("{ \"SessionId\": \"not-a-number\" }")]
    public async Task MalformedTokenPayload_ReturnsNullAndClearsCookie(string payload)
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest(rawCookie: host.ProtectRaw(payload));

        (await request.GetTokenAsync()).ShouldBeNull();

        request.CookieCleared.ShouldBeTrue();
        host.Store.Calls.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task ValidToken_NotDueForRefresh_ReturnsTokenWithoutStoreAccessOrCookie()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession();
        var request = host.CreateRequest(token);

        (await request.GetTokenAsync()).ShouldBe(token);

        host.Store.Calls.ShouldBeEmpty();

        await request.StartResponseAsync();

        request.SessionCookies.ShouldBeEmpty();
        request.Response.OnStartingCallbackCount.ShouldBe(0);
    }

    [TestMethod]
    public async Task RepeatedCalls_ReadCookieOnceAndReturnSameInstance()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession(age: TimeSpan.FromMinutes(11));
        var request = host.CreateRequest(token);

        var first = await request.GetTokenAsync();
        var second = await request.GetTokenAsync();
        var third = await request.Session.GetRequiredTokenAsync();

        first.ShouldNotBeNull();
        second.ShouldBeSameAs(first);
        third.ShouldBeSameAs(first);

        // Due for refresh, so the session was validated - but only once.
        host.Store.Calls.ShouldBe(["GetSessionData", "IsTokenCurrent"]);
        host.Store.ContextsCreated.ShouldBe(1);
        host.Store.OpenContexts.ShouldBe(0);
    }

    [TestMethod]
    public void Device_IsRawUserAgent_WhenPlatformIsUnknown()
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest(device: SessionTestHost.DefaultDevice, ipAddress: SessionTestHost.OtherIp);

        request.Session.Device.ShouldBe(SessionTestHost.DefaultDevice);
        request.Session.IpAddress.ShouldBe(SessionTestHost.OtherIp);
    }

    [TestMethod]
    public void Device_IsParsedPlatformAndBrowser_WhenRecognized()
    {
        const string chromeOnWindows = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        var host = new SessionTestHost();
        var request = host.CreateRequest(device: chromeOnWindows);

        request.Session.Device.ShouldBe("Windows (Chrome 120.0.0.0)");
    }

    [TestMethod]
    public void Device_MissingUserAgent_ThrowsBadRequest()
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest();
        request.Context.Request.Headers.Remove("User-Agent");

        Should.Throw<BadRequestApiException>(() => request.Session.Device);
    }
}
