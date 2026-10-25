using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

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

        var ex = await Should.ThrowAsync<UnauthorizedApiException>(() => request.Session.GetRequiredTokenAsync().AsTask());
        ex.ErrorCode.ShouldBe(ApiErrorCodes.SignInRequired);
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
        var otherSession = otherHost.CreateSession();

        var host = new SessionTestHost();
        var request = host.CreateRequest(rawCookie: otherHost.Protect(otherSession));

        (await request.GetTokenAsync()).ShouldBeNull();

        request.CookieCleared.ShouldBeTrue();
        host.Store.Calls.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task DefaultPurpose_IncludesTokenTypeFullName()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        var protector = host.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("Singulink/Session[Singulink.Net.Http.Api.Service.TestSessionToken]");

        var request = host.CreateRequest(rawCookie: protector.Protect(TestSessionCookies.ToJson(session)));

        (await request.GetTokenAsync()).ShouldBe(session.Token);
    }

    [TestMethod]
    public async Task CustomPurpose_ReadsOnlyCookiesProtectedWithIt()
    {
        var host = new SessionTestHost(o => o.DataProtectionPurpose = "Singulink/Session[Old.Namespace.SessionToken]");
        var session = host.CreateSession();
        var provider = host.Services.GetRequiredService<IDataProtectionProvider>();
        string json = TestSessionCookies.ToJson(session);

        var oldPurposeRequest = host.CreateRequest(rawCookie: provider.CreateProtector("Singulink/Session[Old.Namespace.SessionToken]").Protect(json));
        (await oldPurposeRequest.GetTokenAsync()).ShouldBe(session.Token);

        var defaultPurposeRequest = host.CreateRequest(rawCookie: provider.CreateProtector(TestSessionCookies.Purpose).Protect(json));
        (await defaultPurposeRequest.GetTokenAsync()).ShouldBeNull();
        defaultPurposeRequest.CookieCleared.ShouldBeTrue();

        // Issued cookies are protected with the custom purpose.
        var signInRequest = host.CreateRequest();
        await signInRequest.Session.SignInAsync(persistent: true, _ => Task.FromResult(session.Token));
        provider.CreateProtector("Singulink/Session[Old.Namespace.SessionToken]").Unprotect(signInRequest.IssuedCookie.Value).ShouldContain("\"t\":");
    }

    [TestMethod]
    [DataRow("[1, 2, 3]")]
    [DataRow("null")]
    [DataRow("{ \"SessionId\": \"not-a-number\" }")]
    [DataRow("""{"r":"2026-01-01T00:00:00Z","v":"1.00:00:00","g":0,"p":true}""")]
    [DataRow("""{"r":"2026-01-01T00:00:00Z","v":"1.00:00:00","g":0,"p":true,"t":null}""")]
    [DataRow("""{"r":"2026-01-01T00:00:00Z","v":"1.00:00:00","g":0,"p":true,"t":{"SessionId":"x"}}""")]
    [DataRow("""{"v":"1.00:00:00","g":0,"p":true,"t":{"SessionId":1,"UserId":1,"Stamp":1}}""")]
    [DataRow("""{"r":"yesterday","v":"1.00:00:00","g":0,"p":true,"t":{"SessionId":1,"UserId":1,"Stamp":1}}""")]
    public async Task MalformedTokenPayload_ReturnsNullAndClearsCookie(string payload)
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest(rawCookie: host.ProtectRaw(payload));

        (await request.GetTokenAsync()).ShouldBeNull();

        request.CookieCleared.ShouldBeTrue();
        host.Store.Calls.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task VersionThreeCookie_IsNotReadAndIsCleared()
    {
        // Version 3 kept the refresh info in the token itself. Those cookies are not read: users sign in again after the upgrade.

        var host = new SessionTestHost();
        host.CreateSession();

        string payload = FormattableString.Invariant($$"""
            {
                "SessionId": 1, "UserId": 1, "Stamp": 1, "RefreshedUtc": "{{host.UtcNow:O}}", "ValidFor": "30.00:00:00", "Generation": 0,
                "IsPersistent": true
            }
            """);

        var request = host.CreateRequest(rawCookie: host.ProtectRaw(payload));

        (await request.GetTokenAsync()).ShouldBeNull();

        request.CookieCleared.ShouldBeTrue();
        host.Store.Calls.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task ValidToken_NotDueForRefresh_ReturnsTokenWithoutStoreAccessOrCookie()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync()).ShouldBe(session.Token);

        host.Store.Calls.ShouldBeEmpty();

        await request.StartResponseAsync();

        request.SessionCookies.ShouldBeEmpty();
        request.Response.OnStartingCallbackCount.ShouldBe(0);
    }

    [TestMethod]
    public async Task RepeatedCalls_ReadCookieOnceAndReturnSameInstance()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(age: TimeSpan.FromMinutes(11));
        var request = host.CreateRequest(session);

        var first = await request.GetTokenAsync();
        var second = await request.GetTokenAsync();
        var third = await request.Session.GetRequiredTokenAsync();

        first.ShouldNotBeNull();
        second.ShouldBeSameAs(first);
        third.ShouldBeSameAs(first);

        // Due for refresh, so the session was validated - but only once.
        host.Store.Calls.ShouldBe(["GetSession"]);
        host.Store.StoresCreated.ShouldBe(1);
        host.Store.OpenStores.ShouldBe(0);
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
    public void Device_LongUserAgent_IsShortenedToMaxDeviceLength()
    {
        var host = new SessionTestHost();
        string userAgent = "LongClient/1.0 " + new string('x', 600);
        var request = host.CreateRequest(device: userAgent);

        request.Session.Device.ShouldBe(userAgent[..512]);
    }

    [TestMethod]
    public void Device_MaxDeviceLength_IsConfigurable()
    {
        var host = new SessionTestHost(o => o.MaxDeviceLength = 10);

        host.CreateRequest(device: "LongClient/1.0").Session.Device.ShouldBe("LongClient");
        host.CreateRequest(device: "Short/1.0").Session.Device.ShouldBe("Short/1.0");
    }

    [TestMethod]
    public void Device_Shortening_DoesNotSplitSurrogatePairs()
    {
        var host = new SessionTestHost(o => o.MaxDeviceLength = 5);

        host.CreateRequest(device: "Abcd😀xyz").Session.Device.ShouldBe("Abcd");
        host.CreateRequest(device: "Abc😀xyz").Session.Device.ShouldBe("Abc😀");
    }

    [TestMethod]
    public void Device_MissingUserAgent_ThrowsBadRequest()
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest();
        request.Context.Request.Headers.Remove("User-Agent");

        Should.Throw<BadRequestApiException>(() => request.Session.Device).ErrorCode.ShouldBe(ApiErrorCodes.UserAgentRequired);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void Device_EmptyUserAgent_ThrowsBadRequest(string userAgent)
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest(device: userAgent);

        Should.Throw<BadRequestApiException>(() => request.Session.Device).ErrorCode.ShouldBe(ApiErrorCodes.UserAgentRequired);
    }
}
