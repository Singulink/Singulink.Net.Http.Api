using Microsoft.Extensions.Logging;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Session cookies over the 4,096 bytes that browsers accept are logged as errors, and still sent.
/// </summary>
[PrefixTestClass]
public sealed class SessionCookieSizeTests
{
    private const int BrowserLimit = 4096;

    [TestMethod]
    public async Task SignIn_LargeCookie_LogsErrorAndStillSendsCookie()
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest();
        var token = new TestSessionToken(1, 1, 1) { Padding = new string('x', 5000) };

        await request.Session.SignInAsync(persistent: true, _ => Task.FromResult(token));

        request.IssuedSession.ShouldNotBeNull().Token.ShouldBe(token);

        int size = SetCookieSize(request);
        size.ShouldBeGreaterThan(BrowserLimit);

        var log = ErrorLogs(host).ShouldHaveSingleItem();
        log.Level.ShouldBe(LogLevel.Error);
        log.Message.ShouldContain($"{size} bytes");
        log.Message.ShouldContain("4,096");
        log.Message.ShouldContain("silently drop");
        log.Message.ShouldContain(typeof(TestSessionToken).FullName!);
    }

    [TestMethod]
    public async Task Refresh_LargeCookie_LogsError()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(age: TimeSpan.FromMinutes(11));
        session = session with { Token = session.Token with { Padding = new string('x', 5000) } };
        var request = host.CreateRequest(session);

        await request.GetTokenAsync();
        await request.StartResponseAsync();

        request.IssuedSession.ShouldNotBeNull().Generation.ShouldBe(1);
        ErrorLogs(host).ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Error);
    }

    [TestMethod]
    public async Task SmallCookie_LogsNothing()
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest();

        await request.Session.SignInAsync(persistent: true, _ => Task.FromResult(new TestSessionToken(1, 1, 1) { Padding = new string('x', 1000) }));

        SetCookieSize(request).ShouldBeLessThan(BrowserLimit);
        ErrorLogs(host).ShouldBeEmpty();
    }

    [TestMethod]
    public async Task Size_IncludesAttributes()
    {
        // Finds a token whose cookie name and value fit in the limit but whose attributes take the whole header over it.

        const string path = "/a-long-cookie-path-that-adds-to-the-size-of-the-set-cookie-header";

        for (int padding = 2500; padding < 3500; padding += 4)
        {
            var host = new SessionTestHost(o => {
                o.SessionCookieName = "session-token";
                o.CookiePath = path;
            });
            var request = host.CreateRequest();

            await request.Session.SignInAsync(persistent: true, _ => Task.FromResult(new TestSessionToken(1, 1, 1) { Padding = new string('x', padding) }));

            var cookie = request.IssuedCookie;
            int nameAndValueSize = cookie.Name.Length + 1 + cookie.Value.Length;
            int size = SetCookieSize(request);

            if (nameAndValueSize <= BrowserLimit && size > BrowserLimit)
            {
                ErrorLogs(host).ShouldHaveSingleItem().Message.ShouldContain($"{size} bytes");
                return;
            }
        }

        throw new ShouldAssertException("No padding produced a cookie whose attributes take it over the limit.");
    }

    private static List<LogEntry> ErrorLogs(SessionTestHost host) => [.. host.Logs.Entries.Where(e => e.Level >= LogLevel.Warning)];

    private static int SetCookieSize(TestRequest request) => request.Context.Response.Headers.SetCookie.ShouldHaveSingleItem()!.Length;
}
