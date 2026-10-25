namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The session cookies of requests whose sessions ended while they ran, such as when a sign-in on the device replaced the session: a browser's request
/// keeps its cookie when the session was found ended because the cookie was due, and no request reissues the cookie of a session that ended since it was
/// validated, since their responses can arrive after the sign-in's and would delete or replace the new session's cookie.
/// </summary>
[PrefixTestClass]
public sealed class BrowserSessionCookieTests
{
    private static readonly TimeSpan Due = TimeSpan.FromMinutes(11); // Session cookies are due for refresh after 10 minutes by default.

    [TestMethod]
    public async Task DueCookieOfAnEndedSession_InABrowsersRequest_IsKept()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(age: Due);
        host.Store.Sessions.Clear();
        var request = CreateBrowserRequest(host, session);

        (await request.GetTokenAsync()).ShouldBeNull();
        await request.StartResponseAsync();

        request.SessionCookies.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task DueCookieOfAnExpiredSession_InABrowsersRequest_IsKept()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(age: host.Options.PersistentSessionExpiry + TimeSpan.FromMinutes(1));
        var request = CreateBrowserRequest(host, session);

        (await request.GetTokenAsync()).ShouldBeNull();
        await request.StartResponseAsync();

        request.SessionCookies.ShouldBeEmpty();
        host.Store.Sessions.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task DueCookieOfAnEndedSession_InARequestWithoutFetchMetadata_IsDeleted()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(age: Due);
        host.Store.Sessions.Clear();
        var request = host.CreateRequest(session);

        (await request.GetTokenAsync()).ShouldBeNull();

        request.CookieCleared.ShouldBeTrue();
    }

    [TestMethod]
    public async Task DueCookieOfAnEndedSession_InABrowsersForcedValidation_IsKept()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(age: Due);
        host.Store.Sessions.Clear();
        var request = CreateBrowserRequest(host, session);

        (await request.GetTokenAsync(SessionAccessOptions.ForceValidate)).ShouldBeNull();
        await request.StartResponseAsync();

        request.SessionCookies.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task CookieThatIsNotDue_OfAnEndedSession_InABrowsersForcedValidation_IsDeleted()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        host.Store.Sessions.Clear();
        var request = CreateBrowserRequest(host, session);

        (await request.GetTokenAsync(SessionAccessOptions.ForceValidate)).ShouldBeNull();

        request.CookieCleared.ShouldBeTrue();
    }

    [TestMethod]
    public async Task CookieOfASessionThatTheStoreEndsByThrowing_InABrowsersRequest_IsDeleted()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(age: Due);
        host.Store.GetSessionException = new UnauthorizedApiException("The account is disabled.");
        var request = CreateBrowserRequest(host, session);

        await Should.ThrowAsync<UnauthorizedApiException>(() => request.GetTokenAsync().AsTask());

        request.CookieCleared.ShouldBeTrue();
    }

    [TestMethod]
    public async Task UnreadableCookie_InABrowsersRequest_IsDeleted()
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest(rawCookie: "not-a-session-cookie");
        request.Context.Request.Headers["Sec-Fetch-Site"] = "same-origin";

        (await request.GetTokenAsync()).ShouldBeNull();

        request.CookieCleared.ShouldBeTrue();
    }

    [TestMethod]
    public async Task ReissueOfTheCookieOfASessionThatEndedBeforeTheResponse_IssuesNothing()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(age: Due);

        // Another request refreshed the session, so that this request's cookie is reissued with the record's refresh info at response start.
        host.SetSessionState(generation: 1, age: TimeSpan.FromSeconds(5));
        var request = host.CreateRequest(session);
        (await request.GetTokenAsync()).ShouldBe(session.Token);

        // A sign-in on the device replaces the session before the response starts.
        host.Store.Sessions.Clear();
        await request.StartResponseAsync();

        request.SessionCookies.ShouldBeEmpty();
    }

    private static TestRequest CreateBrowserRequest(SessionTestHost host, TestSessionCookie session)
    {
        var request = host.CreateRequest(session);
        request.Context.Request.Headers["Sec-Fetch-Site"] = "same-origin";

        return request;
    }
}
