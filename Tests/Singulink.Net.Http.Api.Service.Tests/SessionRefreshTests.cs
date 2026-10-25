namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Periodic (sliding) refresh behavior: validation at request start, refresh and cookie reissue at response start.
/// </summary>
[PrefixTestClass]
public sealed class SessionRefreshTests
{
    private static readonly TimeSpan Due = TimeSpan.FromMinutes(11); // Session cookies are due for refresh after 10 minutes by default.

    [TestMethod]
    public async Task Due_ValidatesAtRequestStart_AndRefreshesAtResponseStart()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(age: Due);
        var request = host.CreateRequest(session);

        // The endpoint operates on the token as presented; the refresh happens when the response starts.
        (await request.GetTokenAsync()).ShouldBe(session.Token);

        host.Store.Calls.ShouldBe(["GetSession"]);
        request.SessionCookies.ShouldBeEmpty();

        await request.StartResponseAsync();

        host.Store.Calls.ShouldBe(["GetSession", "RefreshSession"]);

        var issued = request.IssuedSession.ShouldNotBeNull();
        issued.Generation.ShouldBe(1);
        issued.RefreshedUtc.ShouldBe(host.UtcNow);
        issued.ValidFor.ShouldBe(host.Options.PersistentSessionExpiry);
        issued.IsPersistent.ShouldBeTrue();
        issued.Token.ShouldBe(session.Token);

        var data = host.Store.Sessions[session.Token.SessionId];
        data.Generation.ShouldBe(1);
        data.RefreshedUtc.ShouldBe(issued.RefreshedUtc);
        data.ValidFor.ShouldBe(issued.ValidFor);

        host.Store.OpenStores.ShouldBe(0);
    }

    [TestMethod]
    public async Task Due_EachStoreOperationUsesItsOwnScope()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(age: Due);
        var request = host.CreateRequest(session);

        await request.GetTokenAsync();
        host.Store.StoresCreated.ShouldBe(1);
        host.Store.OpenStores.ShouldBe(0);

        await request.StartResponseAsync();
        host.Store.StoresCreated.ShouldBe(2);
        host.Store.OpenStores.ShouldBe(0);
    }

    [TestMethod]
    public async Task Due_RecordsCurrentDeviceAndIpAddressInStore()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(age: Due, device: SessionTestHost.OtherDevice, ipAddress: SessionTestHost.OtherIp);
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync()).ShouldNotBeNull();
        await request.StartResponseAsync();

        var data = host.Store.Sessions[session.Token.SessionId];
        data.Device.ShouldBe(SessionTestHost.DefaultDevice);
        data.IpAddress.ShouldBe(SessionTestHost.DefaultIp);
    }

    [TestMethod]
    public async Task Due_LongUserAgent_StoreReceivesShortenedDevice()
    {
        var host = new SessionTestHost();
        string userAgent = "LongClient/1.0 " + new string('x', 600);
        var session = host.CreateSession(age: Due);
        var request = host.CreateRequest(session, device: userAgent);

        await request.GetTokenAsync();
        await request.StartResponseAsync();

        host.Store.RefreshedSessions.ShouldHaveSingleItem().Device.ShouldBe(userAgent[..512]);
    }

    [TestMethod]
    public async Task Due_ReplacedToken_EndpointGetsReplacement_RefreshKeepsIt()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(age: Due);
        host.BumpUserStamp();
        var request = host.CreateRequest(session);

        // The endpoint never runs on a token the store has replaced: the replacement comes with the same lookup that validates the session.
        var result = (await request.GetTokenAsync()).ShouldNotBeNull();
        result.Stamp.ShouldBe(host.Store.UserStamps[session.Token.UserId]);
        result.BuildCount.ShouldBe(1);
        host.Store.Calls.ShouldBe(["GetSession(replaced)"]);

        await request.StartResponseAsync();

        // The refresh is issued with the replacement token.
        host.Store.Calls.ShouldBe(["GetSession(replaced)", "RefreshSession"]);

        var issued = request.IssuedSession.ShouldNotBeNull();
        issued.Token.ShouldBe(result);
        issued.Generation.ShouldBe(1);
    }

    [TestMethod]
    public async Task Due_PersistentSession_CookieMaxAgeMatchesValidity()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(age: Due, persistent: true);
        var request = host.CreateRequest(session);

        await request.GetTokenAsync();
        await request.StartResponseAsync();

        request.IssuedCookie.MaxAge.ShouldBe(host.Options.PersistentSessionExpiry);
        request.IssuedSession!.ValidFor.ShouldBe(host.Options.PersistentSessionExpiry);
    }

    [TestMethod]
    public async Task Due_TemporarySession_CookieHasNoMaxAge()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(age: Due, persistent: false);
        var request = host.CreateRequest(session);

        await request.GetTokenAsync();
        await request.StartResponseAsync();

        request.IssuedCookie.MaxAge.ShouldBeNull();
        request.IssuedSession!.ValidFor.ShouldBe(host.Options.TempSessionExpiry);
        request.IssuedSession.IsPersistent.ShouldBeFalse();
    }

    [TestMethod]
    public async Task Due_CookieAttributes_AreSecureAndScopedByOptions()
    {
        var host = new SessionTestHost(o => {
            o.SessionCookieName = "session-token";
            o.CookieDomain = ".example.com";
            o.CookiePath = "/api";
        });

        var session = host.CreateSession(age: Due);
        var request = host.CreateRequest(session);

        await request.GetTokenAsync();
        await request.StartResponseAsync();

        var cookie = request.IssuedCookie;
        cookie.HttpOnly.ShouldBeTrue();
        cookie.Secure.ShouldBeTrue();
        cookie.SameSite.ShouldBe("none", StringCompareShould.IgnoreCase);
        cookie.Domain.ShouldBe(".example.com", StringCompareShould.IgnoreCase);
        cookie.Path.ShouldBe("/api");
    }

    [TestMethod]
    public async Task Due_ResponseAlreadyStarted_ValidatesButCannotIssueCookie()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(age: Due);
        var request = host.CreateRequest(session);
        request.Response.MarkStarted();

        (await request.GetTokenAsync()).ShouldBe(session.Token);

        host.Store.Calls.ShouldBe(["GetSession"]);
        request.Response.OnStartingCallbackCount.ShouldBe(0);
        request.SessionCookies.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task Due_ConcurrentRefreshCompletedBeforeResponse_ConditionalWriteFails_IssuesNothing()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(age: Due);
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync()).ShouldBe(session.Token);
        host.Store.ClearCalls();

        // Another request from the same device refreshed the session after this request validated it but before this response started.
        host.SetSessionState(generation: 1, age: TimeSpan.Zero);
        var concurrentRefreshUtc = host.Store.Sessions[session.Token.SessionId].RefreshedUtc;

        await request.StartResponseAsync();

        // The refresh is gated on the validated generation, so it does not overwrite the concurrent refresh, and no cookie is issued. The concurrent
        // request's response delivered the current cookie to the client.
        host.Store.Calls.ShouldBe(["RefreshSession"]);
        request.SessionCookies.ShouldBeEmpty();

        var data = host.Store.Sessions[session.Token.SessionId];
        data.Generation.ShouldBe(1);
        data.RefreshedUtc.ShouldBe(concurrentRefreshUtc);
    }

    [TestMethod]
    public async Task Due_ConcurrentRefreshCompletedBeforeValidation_ReissuesCurrentGenerationWithoutStoreWrite()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(age: Due);

        // Another request from the same device refreshed the session before this request (still holding the previous cookie) was validated.
        host.Clock.Advance(TimeSpan.FromSeconds(10));
        host.SetSessionState(generation: 1, age: TimeSpan.FromSeconds(5));
        var concurrentRefreshUtc = host.Store.Sessions[session.Token.SessionId].RefreshedUtc;

        var request = host.CreateRequest(session);
        (await request.GetTokenAsync()).ShouldBe(session.Token);
        host.Store.Calls.ShouldBe(["GetSession"]);

        await request.StartResponseAsync();

        // The record was just refreshed, so no rotation is due: the cookie is reissued with the record's refresh info, without a store write, once the
        // store still has the session.
        host.Store.Calls.ShouldBe(["GetSession", "GetSession"]);

        var issued = request.IssuedSession.ShouldNotBeNull();
        issued.Token.ShouldBe(session.Token);
        issued.Generation.ShouldBe(1);
        issued.RefreshedUtc.ShouldBe(concurrentRefreshUtc);
        host.Store.Sessions[session.Token.SessionId].Generation.ShouldBe(1);
    }

    [TestMethod]
    public async Task Due_TwoRequestsRefreshSimultaneously_OnlyOneWriteSucceeds_NoDoubleRotation()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(age: Due);
        var requestA = host.CreateRequest(session);
        var requestB = host.CreateRequest(session);

        (await requestA.GetTokenAsync()).ShouldBe(session.Token);
        (await requestB.GetTokenAsync()).ShouldBe(session.Token);

        // Interleave the refreshes: both validated generation 0, and before A's conditional write is evaluated, B performs its entire refresh.
        bool interleaved = false;

        host.Store.BeforeRefreshSession = async () => {
            if (!interleaved)
            {
                interleaved = true;
                await requestB.StartResponseAsync();
            }
        };

        await requestA.StartResponseAsync();

        interleaved.ShouldBeTrue();
        host.Store.Calls.Count(c => c == "RefreshSession").ShouldBe(2);

        // B's write succeeded and rotated the session to generation 1. A's write was gated on generation 0 and failed, so A issues nothing and the store
        // is not rotated twice.
        host.Store.Sessions[session.Token.SessionId].Generation.ShouldBe(1);
        requestB.IssuedSession!.Generation.ShouldBe(1);
        requestA.SessionCookies.ShouldBeEmpty();

        // A request that still holds the original cookie (e.g. a lost response) is accepted within the grace period and brought up to date.
        var requestC = host.CreateRequest(session);
        (await requestC.GetTokenAsync(SessionAccessOptions.ForceValidate)).ShouldBe(session.Token);
        await requestC.StartResponseAsync();
        requestC.IssuedSession!.Generation.ShouldBe(1);
    }

    [TestMethod]
    public async Task Due_ConcurrentRefreshFromDifferentDevice_IssuesNothing()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(age: Due);
        var request = host.CreateRequest(session);

        await request.GetTokenAsync();
        host.SetSessionState(generation: 1, age: TimeSpan.Zero, device: SessionTestHost.OtherDevice);

        await request.StartResponseAsync();

        request.SessionCookies.ShouldBeEmpty();
        host.Store.Calls[^1].ShouldBe("RefreshSession");
    }

    [TestMethod]
    public async Task Due_ConcurrentRefreshAdvancedTwoGenerations_IssuesNothing()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(age: Due);
        var request = host.CreateRequest(session);

        await request.GetTokenAsync();
        host.SetSessionState(generation: 2, age: TimeSpan.Zero);

        await request.StartResponseAsync();

        request.SessionCookies.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task Due_SessionRemovedBeforeResponse_IssuesNothing()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(age: Due);
        var request = host.CreateRequest(session);

        await request.GetTokenAsync();
        host.Store.Sessions.Clear();

        await request.StartResponseAsync();

        request.SessionCookies.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task RefreshAfter_IsConfigurable()
    {
        var host = new SessionTestHost(o => o.RefreshAfter = TimeSpan.FromMinutes(30));
        var notDue = host.CreateSession(sessionId: 1, age: TimeSpan.FromMinutes(29));
        var due = host.CreateSession(sessionId: 2, age: TimeSpan.FromMinutes(31));

        (await host.CreateRequest(notDue).GetTokenAsync()).ShouldBe(notDue.Token);
        host.Store.Calls.ShouldBeEmpty();

        (await host.CreateRequest(due).GetTokenAsync()).ShouldBe(due.Token);
        host.Store.Calls.ShouldBe(["GetSession"]);
    }

    [TestMethod]
    public async Task Due_RefreshIntervalShorterThanGracePeriod_StillRotatesGeneration()
    {
        // Rotation must not depend on the relationship between the refresh interval and the grace period.

        var host = new SessionTestHost(o => o.RefreshAfter = TimeSpan.FromMinutes(1));
        var session = host.CreateSession(age: TimeSpan.FromSeconds(90));
        var request = host.CreateRequest(session);

        await request.GetTokenAsync();
        await request.StartResponseAsync();

        request.IssuedSession!.Generation.ShouldBe(1);
        host.Store.Sessions[session.Token.SessionId].Generation.ShouldBe(1);
    }
}
