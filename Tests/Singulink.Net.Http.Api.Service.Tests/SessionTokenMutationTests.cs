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
        var token = host.CreateSession(age: Due);
        var request = host.CreateRequest(token);

        (await request.GetTokenAsync()).ShouldBe(token);

        var newToken = token with { Stamp = 99, Generation = 5, RefreshedUtc = DateTime.UtcNow };
        request.Session.SetToken(newToken);

        request.IssuedToken.ShouldBe(newToken);
        (await request.GetTokenAsync()).ShouldBeSameAs(newToken);

        await request.StartResponseAsync();

        // Still just the one cookie and no refresh was performed.
        request.IssuedToken.ShouldBe(newToken);
        host.Store.Calls.ShouldBe(["GetSessionData", "IsTokenStale"]);
    }

    [TestMethod]
    public async Task SetToken_WithoutRequestCookie_SubsequentRetrievalsReturnIt()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession();
        var request = host.CreateRequest();

        request.Session.SetToken(token);

        (await request.GetTokenAsync()).ShouldBeSameAs(token);
        (await request.GetTokenAsync(SessionAccessOptions.ForceValidate)).ShouldBeSameAs(token);
        host.Store.Calls.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task ClearToken_DeletesCookie_CancelsDeferredRefresh_AndSubsequentRetrievalsReturnNull()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession(age: Due);
        var request = host.CreateRequest(token);

        (await request.GetTokenAsync()).ShouldBe(token);

        request.Session.ClearToken();

        request.CookieCleared.ShouldBeTrue();
        (await request.GetTokenAsync()).ShouldBeNull();

        await request.StartResponseAsync();

        request.CookieCleared.ShouldBeTrue();
        host.Store.Calls.ShouldBe(["GetSessionData", "IsTokenStale"]);
    }

    [TestMethod]
    public async Task SignIn_Persistent_PassesSignInInfo_AndIssuesPersistentCookie()
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest(ipAddress: SessionTestHost.OtherIp);
        var token = host.CreateSession(persistent: true);
        SignInInfo? signInInfo = null;

        var result = await request.Session.SignInAsync(persistent: true, info => {
            signInInfo = info;
            return Task.FromResult(token);
        });

        result.ShouldBeSameAs(token);

        signInInfo.ShouldNotBeNull();
        signInInfo.Device.ShouldBe(SessionTestHost.DefaultDevice);
        signInInfo.IpAddress.ShouldBe(SessionTestHost.OtherIp);
        signInInfo.SessionExpiry.ShouldBe(host.Options.PersistentSessionExpiry);
        signInInfo.IsPersistent.ShouldBeTrue();

        request.IssuedToken.ShouldBe(token);
        request.IssuedCookie.MaxAge.ShouldBe(token.ValidFor);
        (await request.GetTokenAsync()).ShouldBeSameAs(token);
    }

    [TestMethod]
    public async Task SignIn_Temporary_PassesSignInInfo_AndIssuesSessionCookie()
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest();
        var token = host.CreateSession(persistent: false);
        SignInInfo? signInInfo = null;

        await request.Session.SignInAsync(persistent: false, info => {
            signInInfo = info;
            return Task.FromResult(token);
        });

        signInInfo.ShouldNotBeNull();
        signInInfo.SessionExpiry.ShouldBe(host.Options.TempSessionExpiry);
        signInInfo.IsPersistent.ShouldBeFalse();

        request.IssuedToken.ShouldBe(token);
        request.IssuedCookie.MaxAge.ShouldBeNull();
    }

    [TestMethod]
    public async Task SignOut_InvalidatesSessionAndClearsCookie_WithoutValidatingOrRefreshing()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession(age: Due);
        var request = host.CreateRequest(token);

        await request.Session.SignOutAsync();

        host.Store.Calls.ShouldBe(["InvalidateSession"]);
        host.Store.Sessions.ShouldBeEmpty();
        request.CookieCleared.ShouldBeTrue();
        host.Store.OpenContexts.ShouldBe(0);

        (await request.GetTokenAsync()).ShouldBeNull();

        await request.StartResponseAsync();

        request.CookieCleared.ShouldBeTrue();
        host.Store.Calls.ShouldBe(["InvalidateSession"]);
    }

    [TestMethod]
    public async Task SignOut_AfterTokenWasRead_DoesNotReadOrValidateAgain()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession(age: Due);
        var request = host.CreateRequest(token);

        (await request.GetTokenAsync()).ShouldBe(token);
        await request.Session.SignOutAsync();

        host.Store.Calls.ShouldBe(["GetSessionData", "IsTokenStale", "InvalidateSession"]);
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
    public async Task SignOut_UserIdPreconditionMismatch_ThrowsAndKeepsSession()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession();
        var request = host.CreateRequest(token, userIdPreconditionValue: "2");

        await Should.ThrowAsync<UserChangedApiException>(() => request.Session.SignOutAsync());

        host.Store.Sessions.ShouldContainKey(token.SessionId);
        request.SessionCookies.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task SignOut_DisallowedOrigin_ThrowsForbiddenAndKeepsSession()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession();
        var request = host.CreateRequest(token, origin: "https://evil.test");

        await Should.ThrowAsync<ForbiddenApiException>(() => request.Session.SignOutAsync());

        host.Store.Sessions.ShouldContainKey(token.SessionId);
    }
}
