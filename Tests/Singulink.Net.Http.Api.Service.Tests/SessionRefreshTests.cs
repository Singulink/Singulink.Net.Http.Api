namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Periodic (sliding) refresh behavior: validation at request start, refresh and cookie re-issue at response start.
/// </summary>
[PrefixTestClass]
public sealed class SessionRefreshTests
{
    private static readonly TimeSpan Due = TimeSpan.FromMinutes(11); // Test tokens are due for refresh after 10 minutes.

    [TestMethod]
    public async Task Due_ValidatesAtRequestStart_AndRefreshesAtResponseStart()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession(age: Due);
        var request = host.CreateRequest(token);
        var start = DateTime.UtcNow;

        // The endpoint operates on the token as presented; the refresh happens when the response starts.
        (await request.GetTokenAsync()).ShouldBe(token);

        host.Store.Calls.ShouldBe(["GetSession"]);
        request.SessionCookies.ShouldBeEmpty();

        await request.StartResponseAsync();

        host.Store.Calls.ShouldBe(["GetSession", "RefreshSession", "CreateToken(current)"]);

        var issued = request.IssuedToken.ShouldNotBeNull();
        issued.Generation.ShouldBe(1);
        issued.RefreshedUtc.ShouldBeGreaterThanOrEqualTo(start);
        issued.ValidFor.ShouldBe(host.Options.PersistentSessionExpiry);
        issued.Stamp.ShouldBe(token.Stamp);
        issued.BuildCount.ShouldBe(0);

        var data = host.Store.Sessions[token.SessionId];
        data.Generation.ShouldBe(1);
        data.RefreshedUtc.ShouldBe(issued.RefreshedUtc);
        data.ValidFor.ShouldBe(issued.ValidFor);

        host.Store.OpenContexts.ShouldBe(0);
    }

    [TestMethod]
    public async Task Due_RecordsCurrentDeviceAndIpAddressInStore()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession(age: Due, device: SessionTestHost.OtherDevice, ipAddress: SessionTestHost.OtherIp);
        var request = host.CreateRequest(token);

        (await request.GetTokenAsync()).ShouldNotBeNull();
        await request.StartResponseAsync();

        var data = host.Store.Sessions[token.SessionId];
        data.Device.ShouldBe(SessionTestHost.DefaultDevice);
        data.IpAddress.ShouldBe(SessionTestHost.DefaultIp);
    }

    [TestMethod]
    public async Task Due_StaleToken_EndpointGetsRebuiltToken_RotationAppliesRefreshInfoOnly()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession(age: Due);
        host.BumpUserStamp();
        var request = host.CreateRequest(token);

        // The endpoint never runs on a token the service knows is stale: it is rebuilt from current data before the endpoint sees it.
        var result = (await request.GetTokenAsync()).ShouldNotBeNull();
        result.Stamp.ShouldBe(host.Store.UserStamps[token.UserId]);
        result.BuildCount.ShouldBe(1);
        result.Generation.ShouldBe(0);
        host.Store.Calls.ShouldBe(["GetSession", "CreateToken(stale)"]);

        await request.StartResponseAsync();

        // The rotating refresh only applies the new refresh info to the already-current token.
        host.Store.Calls.Skip(2).ShouldBe(["RefreshSession", "CreateToken(current)"]);

        var issued = request.IssuedToken.ShouldNotBeNull();
        issued.Stamp.ShouldBe(result.Stamp);
        issued.BuildCount.ShouldBe(1);
        issued.Generation.ShouldBe(1);
    }

    [TestMethod]
    public async Task Due_PersistentSession_CookieMaxAgeMatchesValidity()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession(age: Due, persistent: true);
        var request = host.CreateRequest(token);

        await request.GetTokenAsync();
        await request.StartResponseAsync();

        request.IssuedCookie.MaxAge.ShouldBe(host.Options.PersistentSessionExpiry);
        request.IssuedToken!.ValidFor.ShouldBe(host.Options.PersistentSessionExpiry);
    }

    [TestMethod]
    public async Task Due_TemporarySession_CookieHasNoMaxAge()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession(age: Due, persistent: false);
        var request = host.CreateRequest(token);

        await request.GetTokenAsync();
        await request.StartResponseAsync();

        request.IssuedCookie.MaxAge.ShouldBeNull();
        request.IssuedToken!.ValidFor.ShouldBe(host.Options.TempSessionExpiry);
    }

    [TestMethod]
    public async Task Due_CookieAttributes_AreSecureAndScopedByOptions()
    {
        var host = new SessionTestHost(o => {
            o.CookieDomain = ".example.com";
            o.CookiePath = "/api";
        });

        var token = host.CreateSession(age: Due);
        var request = host.CreateRequest(token);

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
        var token = host.CreateSession(age: Due);
        var request = host.CreateRequest(token);
        request.Response.MarkStarted();

        (await request.GetTokenAsync()).ShouldBe(token);

        host.Store.Calls.ShouldBe(["GetSession"]);
        request.Response.OnStartingCallbackCount.ShouldBe(0);
        request.SessionCookies.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task Due_ConcurrentRefreshCompletedBeforeResponse_ConditionalWriteFails_IssuesNothing()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession(age: Due);
        var request = host.CreateRequest(token);

        (await request.GetTokenAsync()).ShouldBe(token);
        host.Store.Calls.Clear();

        // Another request from the same device refreshed the session after this request validated it but before this response started.
        host.SetSessionState(generation: 1, age: TimeSpan.Zero);
        var concurrentRefreshUtc = host.Store.Sessions[token.SessionId].RefreshedUtc;

        await request.StartResponseAsync();

        // The refresh is gated on the validated generation, so it does not overwrite the concurrent refresh, and no token is issued. The concurrent
        // request's response delivered the current token to the client.
        host.Store.Calls.ShouldBe(["RefreshSession"]);
        request.SessionCookies.ShouldBeEmpty();

        var data = host.Store.Sessions[token.SessionId];
        data.Generation.ShouldBe(1);
        data.RefreshedUtc.ShouldBe(concurrentRefreshUtc);
    }

    [TestMethod]
    public async Task Due_ConcurrentRefreshCompletedBeforeValidation_ReissuesCurrentGenerationWithoutStoreWrite()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession(age: Due);

        // Another request from the same device refreshed the session before this request (still holding the previous token) was validated.
        host.SetSessionState(generation: 1, age: TimeSpan.Zero);
        var concurrentRefreshUtc = host.Store.Sessions[token.SessionId].RefreshedUtc;

        var request = host.CreateRequest(token);
        var result = (await request.GetTokenAsync()).ShouldNotBeNull();

        // The token is brought in line with the record right away (no store write, no rebuild since it is not stale).
        result.Generation.ShouldBe(1);
        result.RefreshedUtc.ShouldBe(concurrentRefreshUtc);
        result.BuildCount.ShouldBe(0);
        host.Store.Calls.ShouldBe(["GetSession", "CreateToken(current)"]);

        await request.StartResponseAsync();

        // The record was just refreshed, so no rotation is due: the current-generation token is simply re-issued.
        host.Store.Calls.Count.ShouldBe(2);
        request.IssuedToken.ShouldBe(result);
        host.Store.Sessions[token.SessionId].Generation.ShouldBe(1);
    }

    [TestMethod]
    public async Task Due_TwoRequestsRefreshSimultaneously_OnlyOneWriteSucceeds_NoDoubleRotation()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession(age: Due);
        var requestA = host.CreateRequest(token);
        var requestB = host.CreateRequest(token);

        (await requestA.GetTokenAsync()).ShouldBe(token);
        (await requestB.GetTokenAsync()).ShouldBe(token);

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
        host.Store.Sessions[token.SessionId].Generation.ShouldBe(1);
        requestB.IssuedToken!.Generation.ShouldBe(1);
        requestA.SessionCookies.ShouldBeEmpty();

        // A request that still holds the original token (e.g. a lost response) is accepted within the grace period and brought up to date.
        var requestC = host.CreateRequest(token);
        (await requestC.GetTokenAsync(SessionAccessOptions.ForceValidate)).ShouldNotBeNull().Generation.ShouldBe(1);
    }

    [TestMethod]
    public async Task Due_ConcurrentRefreshFromDifferentDevice_IssuesNothing()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession(age: Due);
        var request = host.CreateRequest(token);

        await request.GetTokenAsync();
        host.SetSessionState(generation: 1, age: TimeSpan.Zero, device: SessionTestHost.OtherDevice);

        await request.StartResponseAsync();

        request.SessionCookies.ShouldBeEmpty();
        host.Store.Calls.Last().ShouldBe("RefreshSession");
    }

    [TestMethod]
    public async Task Due_ConcurrentRefreshAdvancedTwoGenerations_IssuesNothing()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession(age: Due);
        var request = host.CreateRequest(token);

        await request.GetTokenAsync();
        host.SetSessionState(generation: 2, age: TimeSpan.Zero);

        await request.StartResponseAsync();

        request.SessionCookies.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task Due_SessionRemovedBeforeResponse_IssuesNothing()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession(age: Due);
        var request = host.CreateRequest(token);

        await request.GetTokenAsync();
        host.Store.Sessions.Clear();

        await request.StartResponseAsync();

        request.SessionCookies.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task Due_RefreshIntervalShorterThanGracePeriod_StillRotatesGeneration()
    {
        // Rotation must not depend on the relationship between the token's RefreshAfter and the grace period.

        var host = new SessionTestHost();
        var token = host.CreateSession(age: TimeSpan.FromSeconds(90)) with { RefreshAfter = TimeSpan.FromMinutes(1) };
        var request = host.CreateRequest(token);

        await request.GetTokenAsync();
        await request.StartResponseAsync();

        request.IssuedToken!.Generation.ShouldBe(1);
        host.Store.Sessions[token.SessionId].Generation.ShouldBe(1);
    }
}
