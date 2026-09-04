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

        host.Store.Calls.ShouldBe(["GetSessionData", "IsTokenStale"]);
        request.SessionCookies.ShouldBeEmpty();

        await request.StartResponseAsync();

        host.Store.Calls.ShouldBe(["GetSessionData", "IsTokenStale", "GetSessionData", "UpdateSession", "CreateToken(current)"]);

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
        host.Store.Calls.ShouldBe(["GetSessionData", "IsTokenStale", "CreateToken(stale)"]);

        await request.StartResponseAsync();

        // The rotating refresh only applies the new refresh info to the already-current token.
        host.Store.Calls.Skip(3).ShouldBe(["GetSessionData", "UpdateSession", "CreateToken(current)"]);

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

        host.Store.Calls.ShouldBe(["GetSessionData", "IsTokenStale"]);
        request.Response.OnStartingCallbackCount.ShouldBe(0);
        request.SessionCookies.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task Due_ConcurrentRefreshCompletedFirst_IssuesCurrentGenerationWithoutStoreWrite()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession(age: Due);
        var request = host.CreateRequest(token);

        (await request.GetTokenAsync()).ShouldBe(token);
        host.Store.Calls.Clear();

        // Another request from the same device refreshed the session before this response started.
        host.SetSessionState(generation: 1, age: TimeSpan.Zero);
        var concurrentRefreshUtc = host.Store.Sessions[token.SessionId].RefreshedUtc;

        await request.StartResponseAsync();

        host.Store.Calls.ShouldBe(["GetSessionData", "CreateToken(current)"]);

        var issued = request.IssuedToken.ShouldNotBeNull();
        issued.Generation.ShouldBe(1);
        issued.RefreshedUtc.ShouldBe(concurrentRefreshUtc);
        host.Store.Sessions[token.SessionId].Generation.ShouldBe(1);
    }

    [TestMethod]
    public async Task Due_TwoRequestsRefreshSimultaneously_BothIssueSameGenerationWithoutDoubleRotation()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession(age: Due);
        var requestA = host.CreateRequest(token);
        var requestB = host.CreateRequest(token);

        (await requestA.GetTokenAsync()).ShouldBe(token);
        (await requestB.GetTokenAsync()).ShouldBe(token);

        // Interleave the refreshes: A reads the session (generation 0) and, before it writes, B performs its entire refresh against the same read state.
        bool interleaved = false;

        host.Store.BeforeUpdateSession = async () => {
            if (!interleaved)
            {
                interleaved = true;
                await requestB.StartResponseAsync();
            }
        };

        await requestA.StartResponseAsync();

        interleaved.ShouldBeTrue();
        host.Store.Calls.Count(c => c == "UpdateSession").ShouldBe(2);

        // Both computed "read generation + 1", so the store ends one generation ahead and both clients receive that generation.
        host.Store.Sessions[token.SessionId].Generation.ShouldBe(1);
        requestA.IssuedToken!.Generation.ShouldBe(1);
        requestB.IssuedToken!.Generation.ShouldBe(1);

        // A request that still holds the original token (e.g. a lost response) is accepted within the grace period.
        var requestC = host.CreateRequest(token);
        (await requestC.GetTokenAsync(SessionAccessOptions.ForceValidate)).ShouldBe(token);
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
        host.Store.Calls.Last().ShouldBe("GetSessionData");
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
