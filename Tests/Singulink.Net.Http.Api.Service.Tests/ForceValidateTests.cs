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
        var token = host.CreateSession();
        var request = host.CreateRequest(token);

        var result = await request.GetTokenAsync(Validate);

        result.ShouldBe(token);
        host.Store.Calls.ShouldBe(["GetSessionData", "IsTokenCurrent"]);

        await request.StartResponseAsync();

        request.SessionCookies.ShouldBeEmpty();
        request.Response.OnStartingCallbackCount.ShouldBe(0);
        host.Store.Calls.Count.ShouldBe(2);
    }

    [TestMethod]
    public async Task StaleToken_ReturnsRebuiltTokenImmediately_ReissuesCookieWithoutRotation()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession();
        host.BumpUserStamp();
        var request = host.CreateRequest(token);

        var result = (await request.GetTokenAsync(Validate)).ShouldNotBeNull();

        // Rebuilt from the latest data, but keeps the session's existing refresh info (no store write, no generation change).
        result.Stamp.ShouldBe(host.Store.UserStamps[token.UserId]);
        result.BuildCount.ShouldBe(1);
        result.Generation.ShouldBe(token.Generation);
        result.RefreshedUtc.ShouldBe(token.RefreshedUtc);
        host.Store.Calls.ShouldBe(["GetSessionData", "IsTokenCurrent", "CreateToken(stale)"]);

        await request.StartResponseAsync();

        host.Store.Calls.Count.ShouldBe(3);
        request.IssuedToken.ShouldBe(result);
        host.Store.Sessions[token.SessionId].Generation.ShouldBe(token.Generation);
        host.Store.OpenContexts.ShouldBe(0);
    }

    [TestMethod]
    public async Task StaleToken_RepeatedCalls_ReturnSameRebuiltInstance()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession();
        host.BumpUserStamp();
        var request = host.CreateRequest(token);

        var first = await request.GetTokenAsync(Validate);
        var second = await request.GetTokenAsync(Validate);
        var third = await request.GetTokenAsync();

        second.ShouldBeSameAs(first);
        third.ShouldBeSameAs(first);
        host.Store.Calls.ShouldBe(["GetSessionData", "IsTokenCurrent", "CreateToken(stale)"]);
    }

    [TestMethod]
    public async Task StaleToken_AndDueForRefresh_RebuiltNowAndRotatedAtResponseStart()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession(age: Due);
        host.BumpUserStamp();
        var request = host.CreateRequest(token);

        var result = (await request.GetTokenAsync(Validate)).ShouldNotBeNull();
        result.BuildCount.ShouldBe(1);
        result.Generation.ShouldBe(0);

        await request.StartResponseAsync();

        // The rebuilt token is current, so the rotating refresh does not rebuild again.
        host.Store.Calls.ShouldBe([
            "GetSessionData", "IsTokenCurrent", "CreateToken(stale)",
            "GetSessionData", "UpdateSession", "CreateToken(current)"]);

        var issued = request.IssuedToken.ShouldNotBeNull();
        issued.Generation.ShouldBe(1);
        issued.Stamp.ShouldBe(result.Stamp);
        issued.BuildCount.ShouldBe(1);
    }

    [TestMethod]
    public async Task LaterForcedCall_UpgradesEarlierUnvalidatedRead()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession();
        host.BumpUserStamp();
        var request = host.CreateRequest(token);

        (await request.GetTokenAsync()).ShouldBe(token);
        host.Store.Calls.ShouldBeEmpty();

        var forced = (await request.GetTokenAsync(Validate)).ShouldNotBeNull();
        forced.BuildCount.ShouldBe(1);
        host.Store.Calls.ShouldBe(["GetSessionData", "IsTokenCurrent", "CreateToken(stale)"]);

        (await request.GetTokenAsync()).ShouldBeSameAs(forced);

        await request.StartResponseAsync();
        request.IssuedToken.ShouldBe(forced);
    }

    [TestMethod]
    public async Task LaterForcedCall_AfterDueRead_RebuildsWithoutRecheckingStore()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession(age: Due);
        host.BumpUserStamp();
        var request = host.CreateRequest(token);

        (await request.GetTokenAsync()).ShouldBe(token);
        host.Store.Calls.ShouldBe(["GetSessionData", "IsTokenCurrent"]);

        var forced = (await request.GetTokenAsync(Validate)).ShouldNotBeNull();
        forced.BuildCount.ShouldBe(1);
        host.Store.Calls.ShouldBe(["GetSessionData", "IsTokenCurrent", "CreateToken(stale)"]);

        await request.StartResponseAsync();

        host.Store.Calls.Skip(3).ShouldBe(["GetSessionData", "UpdateSession", "CreateToken(current)"]);

        var issued = request.IssuedToken.ShouldNotBeNull();
        issued.Generation.ShouldBe(1);
        issued.Stamp.ShouldBe(forced.Stamp);
    }

    [TestMethod]
    public async Task ForcedAccessOptions_ForceValidate_AppliesToEveryRetrieval()
    {
        var host = new SessionTestHost(o => o.ForcedAccessOptions = Validate);
        var token = host.CreateSession();
        var request = host.CreateRequest(token);

        (await request.GetTokenAsync()).ShouldBe(token);

        host.Store.Calls.ShouldBe(["GetSessionData", "IsTokenCurrent"]);
    }
}
