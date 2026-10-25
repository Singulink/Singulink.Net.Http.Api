namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Application-driven token changes: sign-in, sign-out, set and clear.
/// </summary>
[PrefixTestClass]
public sealed class SessionTokenMutationTests
{
    private static readonly TimeSpan Due = TimeSpan.FromMinutes(11);

    [TestMethod]
    public async Task SetToken_IssuesCookieImmediately_CancelsDeferredRefresh_AndBecomesCurrentToken()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(age: Due);
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync()).ShouldBe(session.Token);

        var newToken = session.Token with { Stamp = 99 };
        request.Session.SetToken(newToken);

        // The session's refresh info is kept; only the token changes.
        request.IssuedSession.ShouldBe(session with { Token = newToken });
        (await request.GetTokenAsync()).ShouldBeSameAs(newToken);

        await request.StartResponseAsync();

        // Still just the one cookie and no refresh was performed.
        request.IssuedSession.ShouldBe(session with { Token = newToken });
        host.Store.Calls.ShouldBe(["GetSession"]);
    }

    [TestMethod]
    public async Task SetToken_BeforeTokenWasRead_UsesRequestCookiesSession()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(generation: 3);
        var request = host.CreateRequest(session);
        var newToken = session.Token with { Stamp = 99 };

        request.Session.SetToken(newToken);

        request.IssuedSession.ShouldBe(session with { Token = newToken });
        (await request.GetTokenAsync()).ShouldBeSameAs(newToken);
        (await request.GetTokenAsync(SessionAccessOptions.ForceValidate)).ShouldBeSameAs(newToken);
        host.Store.Calls.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task SetToken_AfterReissueFromStore_KeepsStoresRefreshInfo()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        host.SetSessionState(generation: 1, age: TimeSpan.FromSeconds(30));
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync(SessionAccessOptions.ForceValidate)).ShouldBe(session.Token);

        var newToken = session.Token with { Stamp = 99 };
        request.Session.SetToken(newToken);
        await request.StartResponseAsync();

        var issued = request.IssuedSession.ShouldNotBeNull();
        issued.Token.ShouldBe(newToken);
        issued.Generation.ShouldBe(1);
        issued.RefreshedUtc.ShouldBe(host.Store.Sessions[session.Token.SessionId].RefreshedUtc);
    }

    [TestMethod]
    public void SetToken_WithoutSession_Throws()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();

        Should.Throw<InvalidOperationException>(() => host.CreateRequest().Session.SetToken(session.Token));
        Should.Throw<InvalidOperationException>(() => host.CreateRequest(rawCookie: "garbage").Session.SetToken(session.Token));

        var clearedRequest = host.CreateRequest(session);
        clearedRequest.Session.ClearToken();
        Should.Throw<InvalidOperationException>(() => clearedRequest.Session.SetToken(session.Token));
    }

    [TestMethod]
    public async Task ClearToken_DeletesCookie_CancelsDeferredRefresh_AndSubsequentRetrievalsReturnNull()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(age: Due);
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync()).ShouldBe(session.Token);

        request.Session.ClearToken();

        request.CookieCleared.ShouldBeTrue();
        (await request.GetTokenAsync()).ShouldBeNull();

        await request.StartResponseAsync();

        request.CookieCleared.ShouldBeTrue();
        host.Store.Calls.ShouldBe(["GetSession"]);
    }

    [TestMethod]
    public async Task SignIn_Persistent_PassesSignInInfo_AndIssuesPersistentCookie()
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest(ipAddress: SessionTestHost.OtherIp);
        var token = new TestSessionToken(1, 1, 1);
        SignInInfo? signInInfo = null;

        var result = await request.Session.SignInAsync(persistent: true, info => {
            signInInfo = info;
            return Task.FromResult(token);
        });

        result.ShouldBeSameAs(token);

        signInInfo.ShouldNotBeNull();
        signInInfo.Device.ShouldBe(SessionTestHost.DefaultDevice);
        signInInfo.IpAddress.ShouldBe(SessionTestHost.OtherIp);
        signInInfo.CreatedUtc.ShouldBe(host.UtcNow);
        signInInfo.RefreshedUtc.ShouldBe(host.UtcNow);
        signInInfo.ExpiresUtc.ShouldBe(host.UtcNow + host.Options.PersistentSessionExpiry);
        signInInfo.Generation.ShouldBe(0);
        signInInfo.IsPersistent.ShouldBeTrue();

        // The library adds the session's starting values to the cookie.
        request.IssuedSession.ShouldBe(new TestSessionCookie(token, host.UtcNow, host.Options.PersistentSessionExpiry, 0, IsPersistent: true));
        request.IssuedCookie.MaxAge.ShouldBe(host.Options.PersistentSessionExpiry);
        (await request.GetTokenAsync()).ShouldBeSameAs(token);
    }

    [TestMethod]
    public async Task SignIn_Temporary_PassesSignInInfo_AndIssuesSessionCookie()
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest();
        var token = new TestSessionToken(1, 1, 1);
        SignInInfo? signInInfo = null;

        await request.Session.SignInAsync(persistent: false, info => {
            signInInfo = info;
            return Task.FromResult(token);
        });

        signInInfo.ShouldNotBeNull();
        signInInfo.ExpiresUtc.ShouldBe(host.UtcNow + host.Options.TempSessionExpiry);
        signInInfo.IsPersistent.ShouldBeFalse();

        request.IssuedSession.ShouldBe(new TestSessionCookie(token, host.UtcNow, host.Options.TempSessionExpiry, 0, IsPersistent: false));
        request.IssuedCookie.MaxAge.ShouldBeNull();
    }

    [TestMethod]
    public async Task SignIn_LongUserAgent_PassesShortenedDevice()
    {
        var host = new SessionTestHost(o => o.MaxDeviceLength = 20);
        var request = host.CreateRequest(device: "LongClient/1.0 " + new string('x', 100));
        SignInInfo? signInInfo = null;

        await request.Session.SignInAsync(persistent: true, info => {
            signInInfo = info;
            return Task.FromResult(new TestSessionToken(1, 1, 1));
        });

        signInInfo.ShouldNotBeNull().Device.ShouldBe("LongClient/1.0 xxxxx");
    }

    [TestMethod]
    public async Task SignIn_WithExistingSession_ReplacesCookieAndEndsPreviousSession()
    {
        var host = new SessionTestHost();
        var oldSession = host.CreateSession(sessionId: 1, generation: 5);
        var request = host.CreateRequest(oldSession);
        var token = new TestSessionToken(2, 1, 1);

        await request.Session.SignInAsync(persistent: true, _ => Task.FromResult(token));

        var issued = request.IssuedSession.ShouldNotBeNull();
        issued.Token.ShouldBe(token);
        issued.Generation.ShouldBe(0);
        (await request.GetTokenAsync()).ShouldBeSameAs(token);

        host.Store.Calls.ShouldBe(["InvalidateSession"]);
        host.Store.Sessions.ShouldNotContainKey(oldSession.Token.SessionId);
    }

    [TestMethod]
    public async Task SignIn_ReturnsPreviousSessionToken_KeepsSession()
    {
        // An app that reuses the session of the device returns the token of the request's session: it is not ended.

        var host = new SessionTestHost();
        var oldSession = host.CreateSession(sessionId: 1, generation: 5);
        var request = host.CreateRequest(oldSession);

        await request.Session.SignInAsync(persistent: true, _ => Task.FromResult(oldSession.Token with { }));

        request.IssuedSession.ShouldNotBeNull().Token.ShouldBe(oldSession.Token);
        host.Store.Calls.ShouldBeEmpty();
        host.Store.Sessions.ShouldContainKey(oldSession.Token.SessionId);
    }

    [TestMethod]
    public async Task SignIn_EndingPreviousSessionFails_IssuesNewCookie()
    {
        var host = new SessionTestHost();
        var oldSession = host.CreateSession(sessionId: 1);
        var request = host.CreateRequest(oldSession);
        var token = new TestSessionToken(2, 1, 1);
        host.Store.InvalidateSessionException = new InvalidOperationException("The database is unavailable.");

        await Should.ThrowAsync<InvalidOperationException>(() => request.Session.SignInAsync(persistent: true, _ => Task.FromResult(token)));

        request.IssuedSession.ShouldNotBeNull().Token.ShouldBe(token);
        host.Store.Calls.ShouldBe(["InvalidateSession(threw)"]);
    }

    [TestMethod]
    public async Task SignIn_Fails_KeepsPreviousSession()
    {
        var host = new SessionTestHost();
        var oldSession = host.CreateSession(sessionId: 1);
        var request = host.CreateRequest(oldSession);

        await Should.ThrowAsync<UnauthorizedApiException>(() => request.Session.SignInAsync(
            persistent: true, _ => throw new UnauthorizedApiException("Wrong password.")));

        host.Store.Calls.ShouldBeEmpty();
        host.Store.Sessions.ShouldContainKey(oldSession.Token.SessionId);
        request.SessionCookies.ShouldBeEmpty();
    }

    [TestMethod]
    public void SignInInfo_Constructor_SetsStartingValues()
    {
        var createdUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var info = new SignInInfo(SessionTestHost.DefaultDevice, SessionTestHost.DefaultIp, createdUtc, createdUtc.AddHours(2), isPersistent: false);

        info.Device.ShouldBe(SessionTestHost.DefaultDevice);
        info.IpAddress.ShouldBe(SessionTestHost.DefaultIp);
        info.CreatedUtc.ShouldBe(createdUtc);
        info.RefreshedUtc.ShouldBe(createdUtc);
        info.ExpiresUtc.ShouldBe(createdUtc.AddHours(2));
        info.Generation.ShouldBe(0);
        info.IsPersistent.ShouldBeFalse();
    }

    [TestMethod]
    public void SignInInfo_Constructor_ExpiryNotAfterCreation_Throws()
    {
        // Catches the creation and expiry times passed the wrong way round.

        var createdUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Should.Throw<ArgumentOutOfRangeException>(() => new SignInInfo("device", null, createdUtc.AddHours(2), createdUtc, isPersistent: false))
            .ParamName.ShouldBe("expiresUtc");
        Should.Throw<ArgumentOutOfRangeException>(() => new SignInInfo("device", null, createdUtc, createdUtc, isPersistent: false));
    }

    [TestMethod]
    [DataRow("https://example.com")]
    [DataRow("https://app.example.com")]
    [DataRow(null)]
    public async Task SignIn_AllowedOrNoOrigin_SignsIn(string? origin)
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest(origin: origin);
        var token = new TestSessionToken(1, 1, 1);

        (await request.Session.SignInAsync(persistent: true, _ => Task.FromResult(token))).ShouldBeSameAs(token);

        request.IssuedSession.ShouldNotBeNull().Token.ShouldBe(token);
    }

    [TestMethod]
    public async Task SignIn_DisallowedOrigin_ThrowsForbiddenWithoutCreatingSession()
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest(origin: "https://evil.test");
        bool called = false;

        var ex = await Should.ThrowAsync<ForbiddenApiException>(() => request.Session.SignInAsync(persistent: true, _ => {
            called = true;
            return Task.FromResult(new TestSessionToken(1, 1, 1));
        }));

        ex.ErrorCode.ShouldBe(ApiErrorCodes.OriginBlocked);
        called.ShouldBeFalse();
        request.SessionCookies.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task SignInWithCode_DisallowedOrigin_ThrowsForbiddenWithoutCreatingSession()
    {
        var signIn = new SignInTestContext();
        var host = new SessionTestHost();
        var request = host.CreateRequest(origin: "https://evil.test");
        var pkce = Client.Pkce.Create();
        string code = await signIn.Codes.CreateAsync("1", pkce.Challenge);
        bool called = false;

        var ex = await Should.ThrowAsync<ForbiddenApiException>(() => signIn.Codes.SignInAsync(
            request.Session, code, pkce.Verifier, persistent: true, (_, _) => {
                called = true;
                return Task.FromResult(new TestSessionToken(1, 1, 1));
            }));

        ex.ErrorCode.ShouldBe(ApiErrorCodes.OriginBlocked);
        called.ShouldBeFalse();
        request.SessionCookies.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task SignIn_DisallowedOrigin_AllowAllOriginsForced_SignsIn()
    {
        var host = new SessionTestHost(o => o.ForcedAccessOptions = SessionAccessOptions.AllowAllOrigins);
        var request = host.CreateRequest(origin: "https://evil.test");
        var token = new TestSessionToken(1, 1, 1);

        (await request.Session.SignInAsync(persistent: true, _ => Task.FromResult(token))).ShouldBeSameAs(token);
    }

    [TestMethod]
    public async Task SignInWithCode_DisallowedOrigin_AllowAllOriginsForced_SignsIn()
    {
        var signIn = new SignInTestContext();
        var host = new SessionTestHost(o => o.ForcedAccessOptions = SessionAccessOptions.AllowAllOrigins);
        var request = host.CreateRequest(origin: "https://evil.test");
        var pkce = Client.Pkce.Create();
        string code = await signIn.Codes.CreateAsync("1", pkce.Challenge);
        var token = new TestSessionToken(1, 1, 1);

        (await signIn.Codes.SignInAsync(request.Session, code, pkce.Verifier, persistent: true, (_, _) => Task.FromResult(token)))
            .ShouldBeSameAs(token);
    }

    [TestMethod]
    [DataRow("https://example.com")]
    [DataRow(null)]
    public void EnsureSignInOriginAllowed_AllowedOrNoOrigin_DoesNotThrow(string? origin)
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest(origin: origin);

        request.Session.EnsureSignInOriginAllowed();

        request.SessionCookies.ShouldBeEmpty();
    }

    [TestMethod]
    public void EnsureSignInOriginAllowed_DisallowedOrigin_ThrowsForbidden()
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest(origin: "https://evil.test");

        Should.Throw<ForbiddenApiException>(() => request.Session.EnsureSignInOriginAllowed()).ErrorCode.ShouldBe(ApiErrorCodes.OriginBlocked);
    }

    [TestMethod]
    public void EnsureSignInOriginAllowed_DisallowedOrigin_AllowAllOriginsForced_DoesNotThrow()
    {
        var host = new SessionTestHost(o => o.ForcedAccessOptions = SessionAccessOptions.AllowAllOrigins);
        var request = host.CreateRequest(origin: "https://evil.test");

        request.Session.EnsureSignInOriginAllowed();
    }

    [TestMethod]
    public async Task SignOut_InvalidatesSessionAndClearsCookie_WithoutValidatingOrRefreshing()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(age: Due);
        var request = host.CreateRequest(session);

        await request.Session.SignOutAsync();

        host.Store.Calls.ShouldBe(["InvalidateSession"]);
        host.Store.Sessions.ShouldBeEmpty();
        request.CookieCleared.ShouldBeTrue();
        host.Store.OpenStores.ShouldBe(0);

        (await request.GetTokenAsync()).ShouldBeNull();

        await request.StartResponseAsync();

        request.CookieCleared.ShouldBeTrue();
        host.Store.Calls.ShouldBe(["InvalidateSession"]);
    }

    [TestMethod]
    public async Task SignOut_IsNotCancelledWithTheRequest()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        var request = host.CreateRequest(session);
        using var cts = new CancellationTokenSource();
        request.Context.RequestAborted = cts.Token;
        cts.Cancel();

        await request.Session.SignOutAsync();

        host.Store.Sessions.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task SignOut_AfterTokenWasRead_DoesNotReadOrValidateAgain()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(age: Due);
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync()).ShouldBe(session.Token);
        await request.Session.SignOutAsync();

        host.Store.Calls.ShouldBe(["GetSession", "InvalidateSession"]);
        host.Store.Sessions.ShouldBeEmpty();

        await request.StartResponseAsync();

        request.CookieCleared.ShouldBeTrue();
    }

    [TestMethod]
    public async Task SignOut_NoCookie_ClearsCookieWithoutStoreAccess()
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest();

        await request.Session.SignOutAsync();

        request.CookieCleared.ShouldBeTrue();
        host.Store.Calls.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task SignOut_UnreadableCookie_ClearsCookieWithoutStoreAccess()
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest(rawCookie: "garbage");

        await request.Session.SignOutAsync();

        request.CookieCleared.ShouldBeTrue();
        host.Store.Calls.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task SignOut_WithoutUserIdPrecondition_InvalidatesSessionAndClearsCookie()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        var request = host.CreateRequest(session, includeUserIdPrecondition: false);

        await request.Session.SignOutAsync();

        host.Store.Sessions.ShouldBeEmpty();
        request.CookieCleared.ShouldBeTrue();
    }

    [TestMethod]
    public async Task SignOut_UserIdPreconditionMismatch_ThrowsAndKeepsSession()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        var request = host.CreateRequest(session, userIdPreconditionValue: "2");

        await Should.ThrowAsync<UserChangedApiException>(() => request.Session.SignOutAsync());

        host.Store.Sessions.ShouldContainKey(session.Token.SessionId);
        request.SessionCookies.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task SignOut_DisallowedOrigin_ThrowsForbiddenAndKeepsSession()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        var request = host.CreateRequest(session, origin: "https://evil.test");

        (await Should.ThrowAsync<ForbiddenApiException>(() => request.Session.SignOutAsync())).ErrorCode.ShouldBe(ApiErrorCodes.OriginBlocked);

        host.Store.Sessions.ShouldContainKey(session.Token.SessionId);
    }
}
