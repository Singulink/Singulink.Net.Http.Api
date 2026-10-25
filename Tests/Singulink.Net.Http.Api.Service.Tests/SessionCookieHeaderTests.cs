namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The <c>Set-Cookie</c> headers of the session cookie: a response has one for the cookie, the last one written, so that clients that read only the first
/// header of a cookie, as the toolkit's 3.x clients do, get the right one.
/// </summary>
[PrefixTestClass]
public sealed class SessionCookieHeaderTests
{
    [TestMethod]
    public async Task SignInWithACookieThatCannotBeRead_SendsOnlyTheNewCookie()
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest(rawCookie: "not-a-session-cookie");
        var token = new TestSessionToken(1, 1, 1);

        // Reading the session finds the cookie unreadable and clears it, and the sign-in then issues a new one.
        (await request.GetTokenAsync()).ShouldBeNull();
        await request.Session.SignInAsync(persistent: true, _ => Task.FromResult(token));

        request.IssuedSession.ShouldNotBeNull().Token.ShouldBe(token);
    }

    [TestMethod]
    public async Task SignOutWithACookieThatCannotBeRead_SendsOneDeletion()
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest(rawCookie: "not-a-session-cookie");

        await request.Session.SignOutAsync();

        request.CookieCleared.ShouldBeTrue();
    }

    [TestMethod]
    public async Task SignInAndThenSignOut_SendsOnlyTheDeletion()
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest();

        await request.Session.SignInAsync(persistent: true, _ => Task.FromResult(new TestSessionToken(1, 1, 1)));
        await request.Session.SignOutAsync();

        request.CookieCleared.ShouldBeTrue();
    }

    [TestMethod]
    public async Task SignInAndThenSetToken_SendsOnlyTheLastCookie()
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest();
        var replacement = new TestSessionToken(1, 1, 2);

        await request.Session.SignInAsync(persistent: true, _ => Task.FromResult(new TestSessionToken(1, 1, 1)));
        request.Session.SetToken(replacement);

        request.IssuedSession.ShouldNotBeNull().Token.ShouldBe(replacement);
    }

    [TestMethod]
    public async Task CookieWithTheSessionCookiesNameAndAnotherPath_IsKept()
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest(rawCookie: "not-a-session-cookie");

        // The app deletes a cookie of the same name that an earlier version set on another path.
        request.Context.Response.Cookies.Delete(host.Options.SessionCookieName, new CookieOptions { Path = "/old" });

        (await request.GetTokenAsync()).ShouldBeNull();
        await request.Session.SignInAsync(persistent: true, _ => Task.FromResult(new TestSessionToken(1, 1, 1)));

        request.SessionCookies.Select(c => (c.Path, c.IsDeletion)).ShouldBe([("/old", true), ("/", false)]);
    }

    [TestMethod]
    public async Task SignInAndSignOut_MarkTheirResponses_AndOtherResponsesAreNotMarked()
    {
        var host = new SessionTestHost();
        var signIn = host.CreateRequest();
        await signIn.Session.SignInAsync(persistent: true, _ => Task.FromResult(new TestSessionToken(1, 1, 1)));

        signIn.Context.Response.Headers["Session-Change"].ToString().ShouldBe("sign-in");

        var signedIn = host.CreateSession(sessionId: 3);
        var signOut = host.CreateRequest(signedIn);
        await signOut.Session.SignOutAsync();

        signOut.CookieCleared.ShouldBeTrue();
        signOut.Context.Response.Headers["Session-Change"].ToString().ShouldBe("sign-out");

        // A refresh sets the cookie too, and a validation that finds the session ended deletes it, without the mark.
        var session = host.CreateSession(sessionId: 2, age: TimeSpan.FromMinutes(11));
        var refresh = host.CreateRequest(session);
        (await refresh.GetTokenAsync()).ShouldBe(session.Token);
        await refresh.StartResponseAsync();

        refresh.IssuedSession.ShouldNotBeNull();
        refresh.Context.Response.Headers.ContainsKey("Session-Change").ShouldBeFalse();

        host.Store.Sessions.Clear();
        var ended = host.CreateRequest(session);
        (await ended.GetTokenAsync(SessionAccessOptions.ForceValidate)).ShouldBeNull();

        ended.CookieCleared.ShouldBeTrue();
        ended.Context.Response.Headers.ContainsKey("Session-Change").ShouldBeFalse();
    }

    [TestMethod]
    public async Task CookieOfTheSessionCookiesDomainWithALeadingDot_IsReplaced()
    {
        var host = new SessionTestHost(o => {
            o.SessionCookieName = "session";
            o.CookieDomain = "example.com";
        });

        // Browsers ignore the leading dot of a domain, so this is the session cookie.
        var request = host.CreateRequest();
        request.Context.Response.Headers.Append("Set-Cookie", "session=old; domain=.example.com; path=/");
        await request.Session.SignInAsync(persistent: true, _ => Task.FromResult(new TestSessionToken(1, 1, 1)));

        request.IssuedSession.ShouldNotBeNull().Token.ShouldBe(new TestSessionToken(1, 1, 1));
    }

    [TestMethod]
    public async Task OtherCookies_AreKept()
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest(rawCookie: "not-a-session-cookie");
        request.Context.Response.Cookies.Append("theme", "dark");

        (await request.GetTokenAsync()).ShouldBeNull();
        await request.Session.SignInAsync(persistent: true, _ => Task.FromResult(new TestSessionToken(1, 1, 1)));

        request.IssuedSession.ShouldNotBeNull();
        request.Context.Response.Headers.SetCookie.Count(h => h!.StartsWith("theme=", StringComparison.Ordinal)).ShouldBe(1);
    }
}
