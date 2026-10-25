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
        var session = host.CreateSession();
        host.Store.Sessions.Clear();
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync(Validate)).ShouldBeNull();

        // The store ended the session, so it is not asked to invalidate it.
        request.CookieCleared.ShouldBeTrue();
        host.Store.Calls.ShouldBe(["GetSession"]);
    }

    [TestMethod]
    public async Task StoreThrowsUnauthorized_InvalidatesSessionClearsCookieAndRethrows()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        var request = host.CreateRequest(session);
        var disabled = new UnauthorizedApiException("The account is disabled.") { ErrorCode = "account-disabled" };
        host.Store.GetSessionException = disabled;

        (await Should.ThrowAsync<UnauthorizedApiException>(() => request.GetTokenAsync(Validate).AsTask())).ShouldBeSameAs(disabled);

        request.CookieCleared.ShouldBeTrue();
        host.Store.Calls.ShouldBe(["GetSession(threw)", "InvalidateSession"]);
        host.Store.Sessions.ShouldBeEmpty();

        (await request.GetTokenAsync()).ShouldBeNull();
    }

    [TestMethod]
    public async Task StoreThrowsUnauthorized_ClientDisconnects_StillInvalidatesSessionAndRethrows()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        var request = host.CreateRequest(session);
        using var aborted = new CancellationTokenSource();
        request.Context.RequestAborted = aborted.Token;

        var disabled = new UnauthorizedApiException("The account is disabled.");
        host.Store.GetSessionException = disabled;
        host.Store.GetSessionThrowing = aborted.Cancel;

        (await Should.ThrowAsync<UnauthorizedApiException>(() => request.GetTokenAsync(Validate).AsTask())).ShouldBeSameAs(disabled);

        request.CookieCleared.ShouldBeTrue();
        host.Store.Calls.ShouldBe(["GetSession(threw)", "InvalidateSession"]);
        host.Store.Sessions.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task StoreThrowsOtherException_KeepsSessionAndCookie()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        var request = host.CreateRequest(session);
        host.Store.GetSessionException = new ForbiddenApiException("No.");

        await Should.ThrowAsync<ForbiddenApiException>(() => request.GetTokenAsync(Validate).AsTask());

        request.SessionCookies.ShouldBeEmpty();
        host.Store.Sessions.ShouldContainKey(session.Token.SessionId);
    }

    [TestMethod]
    public async Task PersistentSessionExpired_InvalidatesSessionAndClearsCookie()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(age: host.Options.PersistentSessionExpiry + TimeSpan.FromMinutes(1));
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync(Validate)).ShouldBeNull();

        request.CookieCleared.ShouldBeTrue();
        host.Store.Calls.ShouldBe(["GetSession", "InvalidateSession"]);
        host.Store.Sessions.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task TemporarySessionExpired_InvalidatesSessionAndClearsCookie()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(persistent: false, age: host.Options.TempSessionExpiry + TimeSpan.FromMinutes(1));
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync(Validate)).ShouldBeNull();

        host.Store.Sessions.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task SessionExpiryIsSliding_FromStoreRefreshTime()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(age: host.Options.PersistentSessionExpiry - TimeSpan.FromMinutes(1));
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync(Validate)).ShouldBe(session.Token);
    }

    [TestMethod]
    public async Task SessionExpiry_UsesStoreRefreshTimeAndValidity_NotTheCookies()
    {
        // The cookie is old, but the record was kept alive in the store (for example by a SignalR connection), so the session is valid.

        var host = new SessionTestHost();
        var session = host.CreateSession(persistent: false, age: host.Options.TempSessionExpiry + TimeSpan.FromHours(1));
        var data = host.Store.Sessions[session.Token.SessionId];
        data.ExpiresUtc = data.RefreshedUtc + host.Options.TempSessionExpiry + TimeSpan.FromHours(2);
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync()).ShouldBe(session.Token);
        await request.StartResponseAsync();

        request.IssuedSession.ShouldNotBeNull().Generation.ShouldBe(1);
    }

    [TestMethod]
    public async Task PreviousGeneration_WithinGrace_SameDevice_IsAccepted()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        host.SetSessionState(generation: 1, age: TimeSpan.FromSeconds(30));
        var request = host.CreateRequest(session);

        // The cookie is accepted and brought in line with the record's generation and refresh info without a store write.
        (await request.GetTokenAsync(Validate)).ShouldBe(session.Token);

        host.Store.Calls.ShouldBe(["GetSession"]);
        host.Store.Sessions.ShouldContainKey(session.Token.SessionId);

        // The reissued cookie carries the current generation so the client catches up.
        await request.StartResponseAsync();

        var issued = request.IssuedSession.ShouldNotBeNull();
        issued.Generation.ShouldBe(1);
        issued.RefreshedUtc.ShouldBe(host.Store.Sessions[session.Token.SessionId].RefreshedUtc);
        issued.Token.ShouldBe(session.Token);
        host.Store.Calls.ShouldBe(["GetSession", "GetSession"]);
    }

    [TestMethod]
    public async Task PreviousGeneration_WithinGrace_SameLongDevice_IsAccepted()
    {
        // The store has the shortened device from the refresh, so the grace check compares it with the request's shortened device.

        var host = new SessionTestHost();
        string userAgent = "LongClient/1.0 " + new string('x', 600);
        var session = host.CreateSession(device: userAgent[..host.Options.MaxDeviceLength]);
        host.SetSessionState(generation: 1, age: TimeSpan.FromSeconds(30));
        var request = host.CreateRequest(session, device: userAgent);

        (await request.GetTokenAsync(Validate)).ShouldBe(session.Token);
    }

    [TestMethod]
    public async Task PreviousGeneration_WithinGrace_DifferentIpAddress_IsAccepted()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        host.SetSessionState(generation: 1, age: TimeSpan.FromSeconds(30));
        var request = host.CreateRequest(session, ipAddress: SessionTestHost.OtherIp);

        (await request.GetTokenAsync(Validate)).ShouldBe(session.Token);
    }

    [TestMethod]
    public async Task PreviousGeneration_WithinGrace_DifferentDevice_IsInvalidated()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        host.SetSessionState(generation: 1, age: TimeSpan.FromSeconds(30));
        var request = host.CreateRequest(session, device: SessionTestHost.OtherDevice);

        (await request.GetTokenAsync(Validate)).ShouldBeNull();

        request.CookieCleared.ShouldBeTrue();
        host.Store.Calls.ShouldBe(["GetSession", "InvalidateSession"]);
        host.Store.Sessions.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task PreviousGeneration_OutsideGrace_IsInvalidated()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        host.SetSessionState(generation: 1, age: host.Options.PreviousGenerationGracePeriod + TimeSpan.FromSeconds(1));
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync(Validate)).ShouldBeNull();

        host.Store.Sessions.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task PreviousGeneration_GracePeriodIsConfigurable()
    {
        var host = new SessionTestHost(o => o.PreviousGenerationGracePeriod = TimeSpan.FromMinutes(10));
        var session = host.CreateSession();
        host.SetSessionState(generation: 1, age: TimeSpan.FromMinutes(5));
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync(Validate)).ShouldBe(session.Token);
    }

    [TestMethod]
    public async Task TwoGenerationsBehind_IsInvalidatedEvenWithinGrace()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        host.SetSessionState(generation: 2, age: TimeSpan.Zero);
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync(Validate)).ShouldBeNull();

        host.Store.Sessions.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task GenerationAheadOfStore_IsInvalidated()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(generation: 1);
        host.SetSessionState(generation: 0);
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync(Validate)).ShouldBeNull();

        host.Store.Sessions.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task InvalidSession_RequiredTokenThrowsUnauthorized()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        host.Store.Sessions.Clear();
        var request = host.CreateRequest(session);

        var ex = await Should.ThrowAsync<UnauthorizedApiException>(() => request.Session.GetRequiredTokenAsync(Validate).AsTask());
        ex.ErrorCode.ShouldBe(ApiErrorCodes.SignInRequired);
        request.CookieCleared.ShouldBeTrue();
    }

    [TestMethod]
    public async Task StoreReceivesRequestCancellationToken()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        var request = host.CreateRequest(session);
        using var cts = new CancellationTokenSource();
        request.Context.RequestAborted = cts.Token;
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => request.GetTokenAsync(Validate).AsTask());
    }
}
