namespace Singulink.Net.Http.Api.Service;

[PrefixTestClass]
public sealed class ForceValidateTests
{
    private const SessionAccessOptions Validate = SessionAccessOptions.ForceValidate;
    private static readonly TimeSpan Due = TimeSpan.FromMinutes(11);

    [TestMethod]
    public async Task CurrentToken_ChecksStoreOnce_IssuesNothing()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        var request = host.CreateRequest(session);

        var result = await request.GetTokenAsync(Validate);

        result.ShouldBe(session.Token);
        host.Store.Calls.ShouldBe(["GetSession"]);

        await request.StartResponseAsync();

        request.SessionCookies.ShouldBeEmpty();
        request.Response.OnStartingCallbackCount.ShouldBe(0);
        host.Store.Calls.Count.ShouldBe(1);
    }

    [TestMethod]
    public async Task ReplacedToken_ReturnsReplacementImmediately_ReissuesCookieWithoutRotation()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(age: TimeSpan.FromMinutes(3));
        host.BumpUserStamp();
        var request = host.CreateRequest(session);

        var result = (await request.GetTokenAsync(Validate)).ShouldNotBeNull();

        result.Stamp.ShouldBe(host.Store.UserStamps[session.Token.UserId]);
        result.BuildCount.ShouldBe(1);
        host.Store.Calls.ShouldBe(["GetSession(replaced)"]);

        await request.StartResponseAsync();

        // The cookie is reissued with the replacement and the session's existing refresh info (no store write, no generation change), once the store still
        // has the session.
        host.Store.Calls.ShouldBe(["GetSession(replaced)", "GetSession"]);
        request.IssuedSession.ShouldBe(session with { Token = result });
        host.Store.Sessions[session.Token.SessionId].Generation.ShouldBe(session.Generation);
        host.Store.OpenStores.ShouldBe(0);
    }

    [TestMethod]
    public async Task ReplacedToken_RepeatedCalls_ValidateTheReplacementAndReturnSameInstance()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        host.BumpUserStamp();
        var request = host.CreateRequest(session);

        var first = await request.GetTokenAsync(Validate);
        var second = await request.GetTokenAsync(Validate);
        var third = await request.GetTokenAsync();

        first.ShouldNotBe(session.Token);
        second.ShouldBeSameAs(first);
        third.ShouldBeSameAs(first);
        host.Store.Calls.ShouldBe(["GetSession(replaced)", "GetSession"]);
    }

    [TestMethod]
    public async Task RepeatedCall_SessionEndedInBetween_ReturnsNullAndClearsCookie()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync(Validate)).ShouldBe(session.Token);

        host.Store.Sessions.Remove(session.Token.SessionId, out _);

        (await request.GetTokenAsync(Validate)).ShouldBeNull();
        (await request.GetTokenAsync()).ShouldBeNull();
        host.Store.Calls.ShouldBe(["GetSession", "GetSession"]);
        request.CookieCleared.ShouldBeTrue();
    }

    [TestMethod]
    public async Task RepeatedCall_SessionRefreshedByAnotherRequest_IsAccepted()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync(Validate)).ShouldBe(session.Token);

        // Another request refreshes the session, and this one validates it again after the previous generation's grace period.
        host.Clock.Advance(host.Options.PreviousGenerationGracePeriod + TimeSpan.FromMinutes(1));
        host.SetSessionState(generation: 1, age: host.Options.PreviousGenerationGracePeriod + TimeSpan.FromSeconds(30));

        (await request.GetTokenAsync(Validate)).ShouldBe(session.Token);
        host.Store.Sessions.ShouldContainKey(session.Token.SessionId);
    }

    [TestMethod]
    public async Task AfterSetToken_DoesNotValidateTheSetToken()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync(Validate)).ShouldBe(session.Token);

        var newToken = session.Token with { Stamp = 99 };
        request.Session.SetToken(newToken);

        (await request.GetTokenAsync(Validate)).ShouldBeSameAs(newToken);
        host.Store.Calls.ShouldBe(["GetSession"]);
    }

    [TestMethod]
    public async Task ReplacedToken_AndDueForRefresh_ReplacedNowAndRotatedAtResponseStart()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(age: Due);
        host.BumpUserStamp();
        var request = host.CreateRequest(session);

        var result = (await request.GetTokenAsync(Validate)).ShouldNotBeNull();
        result.BuildCount.ShouldBe(1);

        await request.StartResponseAsync();

        host.Store.Calls.ShouldBe(["GetSession(replaced)", "RefreshSession"]);

        var issued = request.IssuedSession.ShouldNotBeNull();
        issued.Generation.ShouldBe(1);
        issued.Token.ShouldBe(result);
    }

    [TestMethod]
    public async Task LaterForcedCall_UpgradesEarlierUnvalidatedRead()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        host.BumpUserStamp();
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync()).ShouldBe(session.Token);
        host.Store.Calls.ShouldBeEmpty();

        var forced = (await request.GetTokenAsync(Validate)).ShouldNotBeNull();
        forced.BuildCount.ShouldBe(1);
        host.Store.Calls.ShouldBe(["GetSession(replaced)"]);

        (await request.GetTokenAsync()).ShouldBeSameAs(forced);

        await request.StartResponseAsync();
        request.IssuedSession.ShouldNotBeNull().Token.ShouldBe(forced);
    }

    [TestMethod]
    public async Task LaterForcedCall_AfterDueRead_ValidatesAlreadyReplacedToken()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(age: Due);
        host.BumpUserStamp();
        var request = host.CreateRequest(session);

        var replaced = (await request.GetTokenAsync()).ShouldNotBeNull();
        replaced.BuildCount.ShouldBe(1);
        host.Store.Calls.ShouldBe(["GetSession(replaced)"]);

        var forced = (await request.GetTokenAsync(Validate)).ShouldNotBeNull();
        forced.ShouldBeSameAs(replaced);
        host.Store.Calls.ShouldBe(["GetSession(replaced)", "GetSession"]);

        await request.StartResponseAsync();

        host.Store.Calls.ShouldBe(["GetSession(replaced)", "GetSession", "RefreshSession"]);

        var issued = request.IssuedSession.ShouldNotBeNull();
        issued.Generation.ShouldBe(1);
        issued.Token.ShouldBe(forced);
    }

    [TestMethod]
    public async Task ForcedAccessOptions_ForceValidate_AppliesToEveryRetrieval()
    {
        var host = new SessionTestHost(o => o.ForcedAccessOptions = Validate);
        var session = host.CreateSession();
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync()).ShouldBe(session.Token);

        host.Store.Calls.ShouldBe(["GetSession"]);
    }
}
