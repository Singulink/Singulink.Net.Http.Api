namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Session liveness and generation checks performed against the store at request start. Forced validation is used to trigger the checks so that they
/// can be observed independently of refresh timing.
/// </summary>
[PrefixTestClass]
public sealed class SessionValidationTests
{
    private const SessionAccessOptions Validate = SessionAccessOptions.ForceValidate;

    [TestMethod]
    public async Task SessionMissingFromStore_ReturnsNullAndClearsCookie()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession();
        host.Store.Sessions.Clear();
        var request = host.CreateRequest(token);

        (await request.GetTokenAsync(Validate)).ShouldBeNull();

        request.CookieCleared.ShouldBeTrue();
        host.Store.Calls.ShouldBe(["GetSessionData"]);
    }

    [TestMethod]
    public async Task PersistentSessionExpired_InvalidatesSessionAndClearsCookie()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession(age: host.Options.PersistentSessionExpiry + TimeSpan.FromMinutes(1));
        var request = host.CreateRequest(token);

        (await request.GetTokenAsync(Validate)).ShouldBeNull();

        request.CookieCleared.ShouldBeTrue();
        host.Store.Calls.ShouldBe(["GetSessionData", "InvalidateSession"]);
        host.Store.Sessions.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task TemporarySessionExpired_InvalidatesSessionAndClearsCookie()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession(persistent: false, age: host.Options.TempSessionExpiry + TimeSpan.FromMinutes(1));
        var request = host.CreateRequest(token);

        (await request.GetTokenAsync(Validate)).ShouldBeNull();

        host.Store.Sessions.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task SessionExpiryIsSliding_FromStoreRefreshTime()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession(age: host.Options.PersistentSessionExpiry - TimeSpan.FromMinutes(1));
        var request = host.CreateRequest(token);

        (await request.GetTokenAsync(Validate)).ShouldBe(token);
    }

    [TestMethod]
    public async Task PreviousGeneration_WithinGrace_SameDevice_IsAccepted()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession();
        host.SetSessionState(generation: 1, age: TimeSpan.FromSeconds(30));
        var request = host.CreateRequest(token);

        (await request.GetTokenAsync(Validate)).ShouldBe(token);

        host.Store.Calls.ShouldBe(["GetSessionData", "IsTokenStale"]);
        host.Store.Sessions.ShouldContainKey(token.SessionId);
    }

    [TestMethod]
    public async Task PreviousGeneration_WithinGrace_DifferentIpAddress_IsAccepted()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession();
        host.SetSessionState(generation: 1, age: TimeSpan.FromSeconds(30));
        var request = host.CreateRequest(token, ipAddress: SessionTestHost.OtherIp);

        (await request.GetTokenAsync(Validate)).ShouldBe(token);
    }

    [TestMethod]
    public async Task PreviousGeneration_WithinGrace_DifferentDevice_IsInvalidated()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession();
        host.SetSessionState(generation: 1, age: TimeSpan.FromSeconds(30));
        var request = host.CreateRequest(token, device: SessionTestHost.OtherDevice);

        (await request.GetTokenAsync(Validate)).ShouldBeNull();

        request.CookieCleared.ShouldBeTrue();
        host.Store.Calls.ShouldBe(["GetSessionData", "InvalidateSession"]);
        host.Store.Sessions.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task PreviousGeneration_OutsideGrace_IsInvalidated()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession();
        host.SetSessionState(generation: 1, age: host.Options.MultipleRefreshGracePeriod + TimeSpan.FromSeconds(1));
        var request = host.CreateRequest(token);

        (await request.GetTokenAsync(Validate)).ShouldBeNull();

        host.Store.Sessions.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task PreviousGeneration_GracePeriodIsConfigurable()
    {
        var host = new SessionTestHost(o => o.MultipleRefreshGracePeriod = TimeSpan.FromMinutes(10));
        var token = host.CreateSession();
        host.SetSessionState(generation: 1, age: TimeSpan.FromMinutes(5));
        var request = host.CreateRequest(token);

        (await request.GetTokenAsync(Validate)).ShouldBe(token);
    }

    [TestMethod]
    public async Task TwoGenerationsBehind_IsInvalidatedEvenWithinGrace()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession();
        host.SetSessionState(generation: 2, age: TimeSpan.Zero);
        var request = host.CreateRequest(token);

        (await request.GetTokenAsync(Validate)).ShouldBeNull();

        host.Store.Sessions.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task GenerationAheadOfStore_IsInvalidated()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession(generation: 1);
        host.SetSessionState(generation: 0);
        var request = host.CreateRequest(token);

        (await request.GetTokenAsync(Validate)).ShouldBeNull();

        host.Store.Sessions.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task InvalidSession_RequiredTokenThrowsUnauthorized()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession();
        host.Store.Sessions.Clear();
        var request = host.CreateRequest(token);

        await Should.ThrowAsync<UnauthorizedApiException>(() => request.Session.GetRequiredTokenAsync(Validate).AsTask());
        request.CookieCleared.ShouldBeTrue();
    }
}
