namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Maximum session lifetimes: a session ends a fixed time after sign-in no matter how recently it was refreshed, measured from the creation time the store
/// supplies. The limits are enforced when the session is validated against the store, like the sliding expiry.
/// </summary>
[PrefixTestClass]
public sealed class SessionLifetimeTests
{
    private static readonly TimeSpan Due = TimeSpan.FromMinutes(11); // Session cookies are due for refresh after 10 minutes by default.

    private static readonly TimeSpan TempExpiry = TimeSpan.FromHours(2);
    private static readonly TimeSpan TempLimit = TimeSpan.FromHours(12);
    private static readonly TimeSpan PersistentLimit = TimeSpan.FromDays(90);

    private static readonly TimeSpan JustOver = TimeSpan.FromMinutes(1);

    [TestMethod]
    public async Task TempSession_PastLimit_IsSignedOutAndRemovedFromStore()
    {
        var host = CreateHost();
        var session = host.CreateSession(persistent: false, age: Due, createdAge: TempLimit + JustOver);
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync()).ShouldBeNull();

        request.CookieCleared.ShouldBeTrue();
        host.Store.Calls.ShouldBe(["GetSession", "InvalidateSession"]);
        host.Store.Sessions.ShouldBeEmpty();

        await request.StartResponseAsync();

        host.Store.Calls.Count.ShouldBe(2);
        request.CookieCleared.ShouldBeTrue();
    }

    [TestMethod]
    public async Task TempSession_PastLimit_ForcedValidation_IsSignedOut()
    {
        var host = CreateHost();
        var session = host.CreateSession(persistent: false, createdAge: TempLimit + JustOver);
        var request = host.CreateRequest(session);

        await Should.ThrowAsync<UnauthorizedApiException>(() => request.Session.GetRequiredTokenAsync(SessionAccessOptions.ForceValidate).AsTask());

        request.CookieCleared.ShouldBeTrue();
        host.Store.Sessions.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task TempSession_PastLimit_NotDue_IsUsedUntilNextValidation()
    {
        // A cookie that is not due for a refresh is not validated against the store, so the limit (like the expiry) is only noticed at the next refresh.

        var host = CreateHost();
        var session = host.CreateSession(persistent: false, age: TimeSpan.FromMinutes(5), createdAge: TempLimit + JustOver);
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync()).ShouldBe(session.Token);

        host.Store.Calls.ShouldBeEmpty();
        host.Store.Sessions.ShouldContainKey(session.Token.SessionId);
    }

    [TestMethod]
    public async Task TempSession_CookieCappedAtLimit_IsValidatedOnceLimitIsReached()
    {
        // A refresh near the limit gives the cookie the time left until the limit as its validity period, so the cookie is due for validation when the
        // limit is reached, even though that is sooner than RefreshAfter.

        var host = CreateHost();
        var session = host.CreateSession(persistent: false, age: TimeSpan.FromMinutes(6), validFor: TimeSpan.FromMinutes(5), createdAge: TempLimit + JustOver);
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync()).ShouldBeNull();

        request.CookieCleared.ShouldBeTrue();
        host.Store.Calls.ShouldBe(["GetSession", "InvalidateSession"]);
        host.Store.Sessions.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task TempSession_CookieCappedAtLimit_IsNotValidatedBeforeLimit()
    {
        var host = CreateHost();
        var session = host.CreateSession(persistent: false, age: TimeSpan.FromMinutes(4), validFor: TimeSpan.FromMinutes(5), createdAge: TempLimit - JustOver);
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync()).ShouldBe(session.Token);

        host.Store.Calls.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task TempSession_WithinLimit_KeepsWorkingAndRefreshesWithFullExpiry()
    {
        var host = CreateHost();
        var session = host.CreateSession(persistent: false, age: Due, createdAge: TempLimit - TimeSpan.FromHours(3));
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync()).ShouldBe(session.Token);
        await request.StartResponseAsync();

        host.Store.Calls.ShouldBe(["GetSession", "RefreshSession"]);

        var issued = request.IssuedSession.ShouldNotBeNull();
        issued.Generation.ShouldBe(1);
        issued.ValidFor.ShouldBe(TempExpiry);
        host.Store.Sessions[session.Token.SessionId].ValidFor.ShouldBe(TempExpiry);
    }

    [TestMethod]
    public async Task PersistentLimit_AppliesToPersistentSessionsOnly()
    {
        var host = new SessionTestHost(o => o.MaxPersistentSessionLifetime = PersistentLimit);
        var persistentSession = host.CreateSession(sessionId: 1, persistent: true, age: Due, createdAge: PersistentLimit + JustOver);
        var tempSession = host.CreateSession(sessionId: 2, persistent: false, age: Due, createdAge: PersistentLimit + JustOver);

        (await host.CreateRequest(persistentSession).GetTokenAsync()).ShouldBeNull();
        (await host.CreateRequest(tempSession).GetTokenAsync()).ShouldBe(tempSession.Token);

        host.Store.Sessions.Keys.ShouldHaveSingleItem().ShouldBe(tempSession.Token.SessionId);
    }

    [TestMethod]
    public async Task TempLimit_DoesNotApplyToPersistentSessions()
    {
        var host = CreateHost();
        var persistentSession = host.CreateSession(sessionId: 1, persistent: true, age: Due, createdAge: TempLimit + JustOver);
        var tempSession = host.CreateSession(sessionId: 2, persistent: false, age: Due, createdAge: TempLimit + JustOver);

        (await host.CreateRequest(persistentSession).GetTokenAsync()).ShouldBe(persistentSession.Token);
        (await host.CreateRequest(tempSession).GetTokenAsync()).ShouldBeNull();

        host.Store.Sessions.Keys.ShouldHaveSingleItem().ShouldBe(persistentSession.Token.SessionId);
    }

    [TestMethod]
    public async Task BothLimitsSet_EachSessionKindUsesItsOwnLimit()
    {
        var host = CreateHost(o => o.MaxPersistentSessionLifetime = PersistentLimit);
        var withinPersistentLimit = host.CreateSession(sessionId: 1, persistent: true, age: Due, createdAge: TimeSpan.FromDays(30));
        var pastPersistentLimit = host.CreateSession(sessionId: 2, persistent: true, age: Due, createdAge: PersistentLimit + JustOver);
        var pastTempLimit = host.CreateSession(sessionId: 3, persistent: false, age: Due, createdAge: TempLimit + JustOver);

        (await host.CreateRequest(withinPersistentLimit).GetTokenAsync()).ShouldBe(withinPersistentLimit.Token);
        (await host.CreateRequest(pastPersistentLimit).GetTokenAsync()).ShouldBeNull();
        (await host.CreateRequest(pastTempLimit).GetTokenAsync()).ShouldBeNull();

        host.Store.Sessions.Keys.ShouldHaveSingleItem().ShouldBe(withinPersistentLimit.Token.SessionId);
    }

    [TestMethod]
    public async Task NoLimitConfigured_OldSessionsKeepWorking()
    {
        var host = new SessionTestHost();
        var persistentSession = host.CreateSession(sessionId: 1, persistent: true, age: Due, createdAge: TimeSpan.FromDays(3650));
        var tempSession = host.CreateSession(sessionId: 2, persistent: false, age: Due, createdAge: TimeSpan.FromDays(3650));

        var persistentRequest = host.CreateRequest(persistentSession);
        (await persistentRequest.GetTokenAsync()).ShouldBe(persistentSession.Token);
        await persistentRequest.StartResponseAsync();
        persistentRequest.IssuedSession.ShouldNotBeNull().ValidFor.ShouldBe(host.Options.PersistentSessionExpiry);

        var tempRequest = host.CreateRequest(tempSession);
        (await tempRequest.GetTokenAsync()).ShouldBe(tempSession.Token);
        await tempRequest.StartResponseAsync();
        tempRequest.IssuedSession.ShouldNotBeNull().ValidFor.ShouldBe(host.Options.TempSessionExpiry);

        host.Store.Sessions.Count.ShouldBe(2);
    }

    [TestMethod]
    public async Task CreatedUtc_IsCarriedThroughRefresh()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(age: Due, createdAge: TimeSpan.FromDays(3));
        var createdUtc = host.Store.Sessions[session.Token.SessionId].CreatedUtc;
        host.BumpUserStamp();
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync()).ShouldNotBeNull();
        await request.StartResponseAsync();

        host.Store.Calls.ShouldBe(["GetSession(replaced)", "RefreshSession"]);

        var refreshed = host.Store.RefreshedSessions.ShouldHaveSingleItem();
        refreshed.CreatedUtc.ShouldBe(createdUtc);
        refreshed.Generation.ShouldBe(1);

        host.Store.Sessions[session.Token.SessionId].CreatedUtc.ShouldBe(createdUtc);
    }

    [TestMethod]
    public async Task Refresh_NearLimit_CapsValidityAtLimit()
    {
        var host = CreateHost();
        var session = host.CreateSession(persistent: false, age: Due, createdAge: TempLimit - TimeSpan.FromMinutes(30));
        var limitUtc = host.Store.Sessions[session.Token.SessionId].CreatedUtc + TempLimit;
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync()).ShouldBe(session.Token);
        await request.StartResponseAsync();

        // The refreshed session expires exactly at the limit instead of a full expiry period after the refresh.
        var issued = request.IssuedSession.ShouldNotBeNull();
        issued.Generation.ShouldBe(1);
        issued.ValidFor.ShouldBe(TimeSpan.FromMinutes(30));
        (issued.RefreshedUtc + issued.ValidFor).ShouldBe(limitUtc);

        var data = host.Store.Sessions[session.Token.SessionId];
        data.ValidFor.ShouldBe(issued.ValidFor);
        (data.RefreshedUtc + data.ValidFor).ShouldBe(limitUtc);
    }

    [TestMethod]
    public async Task Refresh_NearLimit_PersistentCookieExpiresAtLimit()
    {
        var host = new SessionTestHost(o => o.MaxPersistentSessionLifetime = PersistentLimit);
        var session = host.CreateSession(persistent: true, age: Due, createdAge: PersistentLimit - TimeSpan.FromDays(1));
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync()).ShouldBe(session.Token);
        await request.StartResponseAsync();

        var issued = request.IssuedSession.ShouldNotBeNull();
        issued.ValidFor.ShouldBe(TimeSpan.FromDays(1));
        request.IssuedCookie.MaxAge.ShouldBe(TimeSpan.FromDays(1));
    }

    [TestMethod]
    public async Task Refresh_LimitReachedAfterValidation_IsSkipped()
    {
        var host = CreateHost();
        var session = host.CreateSession(persistent: false, age: Due, createdAge: TempLimit - TimeSpan.FromMinutes(1));
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync()).ShouldBe(session.Token);

        // Stands in for the endpoint running until the session is past its limit.
        host.Clock.Advance(TimeSpan.FromMinutes(2));

        await request.StartResponseAsync();

        // No store write and no cookie: the session is left for the next validation to end.
        host.Store.Calls.ShouldBe(["GetSession"]);
        request.SessionCookies.ShouldBeEmpty();
        host.Store.Sessions[session.Token.SessionId].Generation.ShouldBe(0);

        var nextRequest = host.CreateRequest(session);
        (await nextRequest.GetTokenAsync()).ShouldBeNull();
        host.Store.Sessions.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task VeryLargeLimit_CreatedUtcInFuture_CountsAsJustCreated()
    {
        var host = new SessionTestHost(o => o.MaxPersistentSessionLifetime = TimeSpan.MaxValue);
        var session = host.CreateSession(persistent: true, age: Due, createdAge: TimeSpan.FromMinutes(-5));
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync()).ShouldBe(session.Token);
        await request.StartResponseAsync();

        request.IssuedSession.ShouldNotBeNull().ValidFor.ShouldBe(host.Options.PersistentSessionExpiry);
    }

    [TestMethod]
    public async Task SignIn_LimitShorterThanExpiry_CapsValidity()
    {
        var host = new SessionTestHost(o => {
            o.MaxTempSessionLifetime = TimeSpan.FromHours(12);
            o.MaxPersistentSessionLifetime = TimeSpan.FromDays(7);
        });

        var tempInfo = await SignInAsync(host, persistent: false);
        var persistentInfo = await SignInAsync(host, persistent: true);

        (tempInfo.ExpiresUtc - tempInfo.CreatedUtc).ShouldBe(TimeSpan.FromHours(12));
        (persistentInfo.ExpiresUtc - persistentInfo.CreatedUtc).ShouldBe(TimeSpan.FromDays(7));
    }

    [TestMethod]
    public async Task SignIn_LimitLongerThanExpiry_UsesExpiry()
    {
        var host = CreateHost(o => o.MaxPersistentSessionLifetime = PersistentLimit);

        var tempInfo = await SignInAsync(host, persistent: false);
        var persistentInfo = await SignInAsync(host, persistent: true);

        (tempInfo.ExpiresUtc - tempInfo.CreatedUtc).ShouldBe(TempExpiry);
        (persistentInfo.ExpiresUtc - persistentInfo.CreatedUtc).ShouldBe(host.Options.PersistentSessionExpiry);
    }

    [TestMethod]
    public async Task StoredExpiry_WithoutLimit_EndsSessionOnlyOnceItHasPassed()
    {
        var host = new SessionTestHost();
        var current = host.CreateSession(sessionId: 1, persistent: false, age: Due, validFor: Due, createdAge: TimeSpan.FromDays(400));
        var expired = host.CreateSession(sessionId: 2, persistent: false, age: Due, validFor: Due - TimeSpan.FromTicks(1));

        (await host.CreateRequest(current).GetTokenAsync()).ShouldBe(current.Token);
        (await host.CreateRequest(expired).GetTokenAsync()).ShouldBeNull();

        host.Store.Sessions.Keys.ShouldBe([1]);
    }

    [TestMethod]
    public async Task StoredExpiry_PastLimitOfOtherKind_DoesNotEndSession()
    {
        var host = CreateHost();
        var session = host.CreateSession(persistent: true, age: Due, createdAge: TempLimit + JustOver);
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync()).ShouldBe(session.Token);
    }

    [TestMethod]
    public async Task StoredExpiry_LimitShortenedAfterItWasWritten_EndsSessionAtLimit()
    {
        // The record's expiry was capped at a longer limit, or none, when it was written. Validation applies the limit that is configured now.

        var host = CreateHost();
        var session = host.CreateSession(persistent: false, age: Due, validFor: TempLimit, createdAge: TempLimit + JustOver);

        (await host.CreateRequest(session).GetTokenAsync()).ShouldBeNull();

        host.Store.Sessions.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task StoredExpiry_VeryLargeValues_DoNotOverflow()
    {
        var host = new SessionTestHost(o => {
            o.PersistentSessionExpiry = TimeSpan.MaxValue;
            o.MaxPersistentSessionLifetime = TimeSpan.MaxValue;
        });

        var session = host.CreateSession(persistent: true, age: Due, validFor: TimeSpan.FromDays(1));
        host.Store.Sessions[session.Token.SessionId].ExpiresUtc = DateTime.MaxValue;
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync()).ShouldBe(session.Token);
        await request.StartResponseAsync();

        host.Store.Sessions[session.Token.SessionId].ExpiresUtc.ShouldBe(DateTime.MaxValue);
        request.IssuedSession.ShouldNotBeNull().Generation.ShouldBe(1);
    }

    /// <summary>
    /// Creates a host configured like an app that gives temporary sessions a short sliding expiry and a maximum lifetime.
    /// </summary>
    private static SessionTestHost CreateHost(Action<SessionHandlingOptions>? configure = null)
    {
        return new SessionTestHost(o => {
            o.TempSessionExpiry = TempExpiry;
            o.MaxTempSessionLifetime = TempLimit;
            configure?.Invoke(o);
        });
    }

    private static async Task<SignInInfo> SignInAsync(SessionTestHost host, bool persistent)
    {
        SignInInfo? signInInfo = null;
        var session = host.CreateSession(sessionId: persistent ? 2 : 1, persistent: persistent);

        await host.CreateRequest().Session.SignInAsync(persistent, info => {
            signInInfo = info;
            return Task.FromResult(session.Token);
        });

        return signInInfo.ShouldNotBeNull();
    }
}
