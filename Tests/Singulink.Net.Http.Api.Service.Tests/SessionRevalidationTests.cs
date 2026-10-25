using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Singulink.Net.Http.Api.Client;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// <see cref="SessionHandlingOptions.RevalidateHubConnections"/>: the sessions of open hub connections are rechecked about once a minute, once
/// <see cref="SessionHandlingOptions.RefreshAfter"/> has passed since they were last checked. Ended sessions close their connections, and live sessions are
/// kept alive. The tests run on a manual clock, so the checks only run when a test advances it.
/// </summary>
[PrefixTestClass]
public sealed class SessionRevalidationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Due = TimeSpan.FromMinutes(11);

    // How long a connection whose session ended stays open after it was told, for the client to close it.
    private static readonly TimeSpan CloseDelay = TimeSpan.FromSeconds(5);

    public static class SessionHubContract
    {
        public static HubMethod<int> WhoAmI { get; } = new();

        public static HubMethod<int> Stamp { get; } = new();

        public static HubMethod<int> ValidatedStamp { get; } = new();
    }

    public static class OpenHubContract
    {
        public static HubMethod<string> Ping { get; } = new();
    }

    public sealed class SessionHub(HubEvents events) : Hub
    {
        public override async Task OnConnectedAsync()
        {
            var token = await Context.GetRequiredSessionTokenAsync<TestSessionToken>(SessionAccessOptions.OptionalUserIdPrecondition);
            Context.Items["UserId"] = token.UserId;
        }

        public override Task OnDisconnectedAsync(Exception? exception)
        {
            events.Disconnected.Add(Context.ConnectionId);
            return Task.CompletedTask;
        }

        public int WhoAmI() => (int)Context.Items["UserId"]!;

        public async Task<int> Stamp() =>
            (await Context.GetRequiredSessionTokenAsync<TestSessionToken>(SessionAccessOptions.OptionalUserIdPrecondition)).Stamp;

        public async Task<int> ValidatedStamp()
        {
            var options = SessionAccessOptions.OptionalUserIdPrecondition | SessionAccessOptions.ForceValidate;
            return (await Context.GetRequiredSessionTokenAsync<TestSessionToken>(options)).Stamp;
        }
    }

    /// <summary>
    /// A hub that reads the session only in its methods, not when a connection connects.
    /// </summary>
    public sealed class LazyHub : Hub
    {
        public async Task<int> ValidatedStamp()
        {
            var options = SessionAccessOptions.OptionalUserIdPrecondition | SessionAccessOptions.ForceValidate;
            return (await Context.GetRequiredSessionTokenAsync<TestSessionToken>(options)).Stamp;
        }

        public async Task<int> Stamp() =>
            (await Context.GetRequiredSessionTokenAsync<TestSessionToken>(SessionAccessOptions.OptionalUserIdPrecondition)).Stamp;

        public string Ping() => "pong";
    }

    public static class LazyHubContract
    {
        public static HubMethod<int> ValidatedStamp { get; } = new();

        public static HubMethod<int> Stamp { get; } = new();
    }

    public sealed class OpenHub : Hub
    {
        public string Ping() => "pong";
    }

    public interface ITypedHubClient
    {
        Task Notified(string text);
    }

    /// <summary>
    /// A strongly typed hub that requires a session.
    /// </summary>
    public sealed class TypedHub : Hub<ITypedHubClient>
    {
        public override Task OnConnectedAsync() => Context.GetRequiredSessionTokenAsync<TestSessionToken>(SessionAccessOptions.OptionalUserIdPrecondition).AsTask();

        public string Ping() => "pong";
    }

    public sealed class HubEvents
    {
        public ConcurrentBag<string> Disconnected { get; } = [];
    }

    [TestMethod]
    public async Task EndedSession_ClosesConnectionWithSignInRequired_AndReconnectIsRejected()
    {
        await using var fixture = await Fixture.StartAsync();
        var session = fixture.CreateSession();
        var connection = await fixture.ConnectAsync("session-hub", session);

        // A connection without an API client to share a session with stays closed.
        connection.Hub.StayConnected = true;

        fixture.Store.Sessions.Clear();
        fixture.Clock.Advance(Due);

        var closedError = (await connection.Closed.WaitAsync(Timeout)).ShouldBeOfType<UnauthorizedApiException>();
        closedError.ErrorCode.ShouldBe(ApiErrorCodes.SignInRequired);
        connection.Hub.State.ShouldBe(HubConnectionState.Disconnected);
        fixture.Store.Calls.ShouldBe(["GetSession"]);

        // The session check of the hub rejects the reconnect.
        var error = await fixture.ConnectRefusedAsync("session-hub", session);
        error.ShouldBeOfType<UnauthorizedApiException>().ErrorCode.ShouldBe(ApiErrorCodes.SignInRequired);
    }

    [TestMethod]
    public async Task StoreThrowsUnauthorized_ClosesConnectionWithTheStoresError_AndInvalidatesSession()
    {
        await using var fixture = await Fixture.StartAsync();
        var session = fixture.CreateSession();
        var connection = await fixture.ConnectAsync("session-hub", session);

        fixture.Store.GetSessionException = new UnauthorizedApiException("The account is disabled.") { ErrorCode = "account-disabled" };
        fixture.Clock.Advance(Due);

        var closedError = (await connection.Closed.WaitAsync(Timeout)).ShouldBeOfType<UnauthorizedApiException>();
        closedError.ErrorCode.ShouldBe("account-disabled");
        closedError.Message.ShouldBe("The account is disabled.");
        fixture.Store.Calls.ShouldBe(["GetSession(threw)", "InvalidateSession"]);
        fixture.Store.Sessions.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task ExpiredSession_ClosesConnectionWithSignInRequired_AndIsInvalidated()
    {
        await using var fixture = await Fixture.StartAsync();
        var session = fixture.CreateSession(validFor: TimeSpan.FromMinutes(5));
        var connection = await fixture.ConnectAsync("session-hub", session);

        fixture.Clock.Advance(Due);

        (await connection.Closed.WaitAsync(Timeout)).ShouldBeOfType<UnauthorizedApiException>().ErrorCode.ShouldBe(ApiErrorCodes.SignInRequired);
        fixture.Store.Calls.ShouldBe(["GetSession", "InvalidateSession"]);
        fixture.Store.Sessions.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task EndedSession_OfStronglyTypedHub_ClosesConnectionWithSignInRequired()
    {
        await using var fixture = await Fixture.StartAsync();
        var session = fixture.CreateSession();
        var connection = await fixture.ConnectAsync("typed-hub", session);

        fixture.Store.Sessions.Clear();
        fixture.Clock.Advance(Due);

        (await connection.Closed.WaitAsync(Timeout)).ShouldBeOfType<UnauthorizedApiException>().ErrorCode.ShouldBe(ApiErrorCodes.SignInRequired);
    }

    [TestMethod]
    public async Task EndedSession_ClosesTheSessionsConnectionsThatAreNotDueToo()
    {
        await using var fixture = await Fixture.StartAsync();
        var session = fixture.CreateSession();
        var due = await fixture.ConnectAsync("session-hub", session);

        // A connection with a later cookie of the same session token, which is not due when the first one is checked.
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        var notDue = await fixture.ConnectAsync("session-hub", session with { RefreshedUtc = fixture.Clock.UtcNow });

        fixture.Store.Sessions.Clear();
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));

        (await due.Closed.WaitAsync(Timeout)).ShouldBeOfType<UnauthorizedApiException>().ErrorCode.ShouldBe(ApiErrorCodes.SignInRequired);
        (await notDue.Closed.WaitAsync(Timeout)).ShouldBeOfType<UnauthorizedApiException>().ErrorCode.ShouldBe(ApiErrorCodes.SignInRequired);
        fixture.Store.Calls.ShouldBe(["GetSession"]);
    }

    [TestMethod]
    public async Task SignOut_LeavesTheSessionsConnectionsOpenUntilTheirNextCheck()
    {
        await using var fixture = await Fixture.StartAsync();
        var session = fixture.CreateSession();
        var connection = await fixture.ConnectAsync("session-hub", session);

        using (var request = fixture.CreateHttpRequest("/sign-out", session, HttpMethod.Post))
        using (var response = await fixture.Host.Client.SendAsync(request))
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Every server handles the end the same way: the connection is closed when its check is due, and not before.
        await ExpectOpenUntilTheNextCheckAsync(fixture, connection);

        (await connection.Closed.WaitAsync(Timeout)).ShouldBeOfType<UnauthorizedApiException>().ErrorCode.ShouldBe(ApiErrorCodes.SignInRequired);
        fixture.Store.Calls.ShouldBe(["InvalidateSession", "GetSession"]);
    }

    [TestMethod]
    public async Task SignInThatReplacesTheSession_LeavesItsConnectionsOpenUntilTheirNextCheck()
    {
        await using var fixture = await Fixture.StartAsync();
        var session = fixture.CreateSession();
        var connection = await fixture.ConnectAsync("session-hub", session);

        using (var request = fixture.CreateHttpRequest("/sign-in", session, HttpMethod.Post))
        using (var response = await fixture.Host.Client.SendAsync(request))
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

        fixture.Store.Sessions.Keys.ShouldBe([9]);
        await ExpectOpenUntilTheNextCheckAsync(fixture, connection);

        // The check can't tell a replaced session from one that ended otherwise.
        (await connection.Closed.WaitAsync(Timeout)).ShouldBeOfType<UnauthorizedApiException>().ErrorCode.ShouldBe(ApiErrorCodes.SignInRequired);
    }

    [TestMethod]
    public async Task SessionThatTheAppRemovesFromItsStore_EndsItsConnectionsAtTheirNextCheck_AndLeavesTheOthersOpen()
    {
        await using var fixture = await Fixture.StartAsync();
        var connection = await fixture.ConnectAsync("session-hub", fixture.CreateSession(sessionId: 1));
        var otherSession = await fixture.ConnectAsync("session-hub", fixture.CreateSession(sessionId: 2));

        // The app ends one of the user's sessions in its store itself.
        fixture.Store.Sessions.TryRemove(1, out _);
        await ExpectOpenUntilTheNextCheckAsync(fixture, connection);

        (await connection.Closed.WaitAsync(Timeout)).ShouldBeOfType<UnauthorizedApiException>().ErrorCode.ShouldBe(ApiErrorCodes.SignInRequired);
        await WaitUntilAsync(() => fixture.Store.Calls.Count(c => c is "GetSession") is 2);
        (await otherSession.Hub.InvokeAsync(SessionHubContract.WhoAmI)).ShouldBe(1);
        otherSession.Closed.IsCompleted.ShouldBeFalse();
    }

    [TestMethod]
    public async Task RequestThatFindsSessionEndedByStore_LeavesItsConnectionsOpenUntilTheirNextCheck()
    {
        await using var fixture = await Fixture.StartAsync();
        var session = fixture.CreateSession(age: Due);
        var connection = await fixture.ConnectAsync("session-hub", session);

        fixture.Store.GetSessionException = new UnauthorizedApiException("The account is disabled.") { ErrorCode = "account-disabled" };

        using (var request = fixture.CreateHttpRequest("/me", session))
        using (var response = await fixture.Host.Client.SendAsync(request))
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        await ExpectOpenUntilTheNextCheckAsync(fixture, connection);

        var closedError = (await connection.Closed.WaitAsync(Timeout)).ShouldBeOfType<UnauthorizedApiException>();
        closedError.ErrorCode.ShouldBe("account-disabled");
        closedError.Message.ShouldBe("The account is disabled.");
    }

    [TestMethod]
    public async Task EndedSession_ClientThatIgnoresTheMessage_IsAbortedAfterTheDelay_AndItsCallsFailMeanwhile()
    {
        await using var fixture = await Fixture.StartAsync();
        var session = fixture.CreateSession();
        var (hub, closed) = await fixture.ConnectWithoutToolkitAsync("session-hub", session);

        var sessionEnded = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<string>("Singulink.Net.Http.Api.SessionEnded", sessionEnded.SetResult);

        fixture.Store.Sessions.Clear();
        fixture.Clock.Advance(Due);

        (await sessionEnded.Task.WaitAsync(Timeout)).ShouldBe("401 [sign-in-required] The user is not signed in.");

        var ex = await Should.ThrowAsync<HubException>(() => hub.InvokeAsync<int>(SessionHubContract.WhoAmI.Name));
        ex.Message.ShouldContain("HubException: 401 [sign-in-required] The user is not signed in.");

        fixture.Clock.Advance(CloseDelay - TimeSpan.FromMilliseconds(1));
        await Task.Delay(100);
        closed.IsCompleted.ShouldBeFalse();
        hub.State.ShouldBe(HubConnectionState.Connected);

        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
        (await closed.WaitAsync(Timeout)).ShouldBeNull();
    }

    [TestMethod]
    public async Task LiveSession_ConnectionStaysOpen_AndSessionIsKeptAlive()
    {
        await using var fixture = await Fixture.StartAsync(o => o.TempSessionExpiry = TimeSpan.FromMinutes(15));
        var session = fixture.CreateSession(persistent: false);
        var connection = await fixture.ConnectAsync("session-hub", session);
        var connectedUtc = fixture.Clock.UtcNow;
        var data = fixture.Store.Sessions[1];

        // Not checked until RefreshAfter has passed since the session was last validated.
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));

        await WaitUntilAsync(() => fixture.Store.Calls.Count is 2);
        fixture.Store.Calls.ShouldBe(["GetSession", "RefreshSession"]);

        // The session is valid for a full expiry period from the check. Its refresh time and generation are kept, since the connection cannot deliver a
        // new cookie.
        data.RefreshedUtc.ShouldBe(connectedUtc);
        data.Generation.ShouldBe(0);
        data.ValidFor.ShouldBe(TimeSpan.FromMinutes(11 + 15));
        (await connection.Hub.InvokeAsync(SessionHubContract.WhoAmI)).ShouldBe(1);

        fixture.Clock.Advance(Due);
        await WaitUntilAsync(() => fixture.Store.Calls.Count is 4);
        data.ValidFor.ShouldBe(TimeSpan.FromMinutes(22 + 15));

        // Past the cookie's own expiry, a request with the cookie still has a valid session, since validation judges the session by the store's record.
        fixture.Clock.Advance(TimeSpan.FromMinutes(8));

        using var request = fixture.CreateHttpRequest("/me", session);
        using var response = await fixture.Host.Client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("Set-Cookie").ShouldHaveSingleItem().ShouldStartWith(TestSessionCookies.CookieName + "=");
        data.Generation.ShouldBe(1);
    }

    [TestMethod]
    public async Task LiveSession_IsNotKeptAlivePastMaxLifetime()
    {
        await using var fixture = await Fixture.StartAsync(o => {
            o.TempSessionExpiry = TimeSpan.FromMinutes(15);
            o.MaxTempSessionLifetime = TimeSpan.FromMinutes(30);
        });

        var session = fixture.CreateSession(persistent: false);
        var connection = await fixture.ConnectAsync("session-hub", session);
        var data = fixture.Store.Sessions[1];

        fixture.Clock.Advance(Due);
        await WaitUntilAsync(() => fixture.Store.Calls.Count is 2);
        data.ValidFor.ShouldBe(TimeSpan.FromMinutes(26));

        fixture.Clock.Advance(Due);
        await WaitUntilAsync(() => fixture.Store.Calls.Count is 4);
        data.ValidFor.ShouldBe(TimeSpan.FromMinutes(30));

        fixture.Clock.Advance(Due);
        await connection.Closed.WaitAsync(Timeout);
        fixture.Store.Calls[^1].ShouldBe("InvalidateSession");
    }

    [TestMethod]
    public async Task GenerationChangedDuringCheck_RereadsAndRetries()
    {
        await using var fixture = await Fixture.StartAsync();
        var session = fixture.CreateSession(persistent: false);
        var connection = await fixture.ConnectAsync("session-hub", session);
        var data = fixture.Store.Sessions[1];
        int refreshes = 0;

        // A request refreshes the session between the check's lookup and its update.
        fixture.Store.BeforeRefreshSession = () => {
            if (Interlocked.Increment(ref refreshes) is 1)
                data.Generation = 1;

            return Task.CompletedTask;
        };

        fixture.Clock.Advance(Due);

        await WaitUntilAsync(() => fixture.Store.Calls.Count is 4);
        fixture.Store.Calls.ShouldBe(["GetSession", "RefreshSession", "GetSession", "RefreshSession"]);

        data.Generation.ShouldBe(1);
        data.ValidFor.ShouldBe(Due + fixture.Options.TempSessionExpiry);
        fixture.Store.RefreshedSessions.Select(s => s.Generation).ShouldBe([0, 1]);
        (await connection.Hub.InvokeAsync(SessionHubContract.WhoAmI)).ShouldBe(1);
    }

    [TestMethod]
    public async Task ReplacedToken_IsUsedForLaterChecks()
    {
        await using var fixture = await Fixture.StartAsync();
        var session = fixture.CreateSession();
        await fixture.ConnectAsync("session-hub", session);
        fixture.Store.UserStamps[1]++;

        fixture.Clock.Advance(Due);
        await WaitUntilAsync(() => fixture.Store.Calls.Count is 2);

        fixture.Clock.Advance(Due);
        await WaitUntilAsync(() => fixture.Store.Calls.Count is 4);

        fixture.Store.Calls.ShouldBe(["GetSession(replaced)", "RefreshSession", "GetSession", "RefreshSession"]);
    }

    [TestMethod]
    public async Task ReplacedToken_IsUsedByHubMethods()
    {
        await using var fixture = await Fixture.StartAsync();

        // The cookie is due when the connection is established, so the hub's session check validates the session then and not again.
        var session = fixture.CreateSession(age: Due);
        var connection = await fixture.ConnectAsync("session-hub", session);
        fixture.Store.UserStamps[1]++;

        fixture.Clock.Advance(Due);
        await WaitUntilAsync(() => fixture.Store.Calls.Count is 3);
        fixture.Store.Calls.ShouldBe(["GetSession", "GetSession(replaced)", "RefreshSession"]);

        (await connection.Hub.InvokeAsync(SessionHubContract.Stamp)).ShouldBe(2);
    }

    [TestMethod]
    public async Task ForceValidate_ValidatesOnEveryHubMethodCall()
    {
        await using var fixture = await Fixture.StartAsync(revalidate: false);
        var session = fixture.CreateSession();
        var connection = await fixture.ConnectAsync("session-hub", session);

        (await connection.Hub.InvokeAsync(SessionHubContract.ValidatedStamp)).ShouldBe(1);
        fixture.Store.UserStamps[1]++;
        (await connection.Hub.InvokeAsync(SessionHubContract.ValidatedStamp)).ShouldBe(2);
        (await connection.Hub.InvokeAsync(SessionHubContract.Stamp)).ShouldBe(2);

        fixture.Store.Sessions.Clear();

        var ex = await Should.ThrowAsync<UnauthorizedApiException>(() => connection.Hub.InvokeAsync(SessionHubContract.ValidatedStamp));
        ex.ErrorCode.ShouldBe(ApiErrorCodes.SignInRequired);
        fixture.Store.Calls.ShouldBe(["GetSession", "GetSession(replaced)", "GetSession"]);
    }

    [TestMethod]
    public async Task ForceValidate_AfterRequestsRefreshedSession_KeepsSession()
    {
        await using var fixture = await Fixture.StartAsync(revalidate: false);
        var session = fixture.CreateSession();
        var connection = await fixture.ConnectAsync("session-hub", session);

        // A request refreshes the session the connection was established with, and the connection uses it after the previous generation's grace period.

        fixture.Clock.Advance(Due);

        using (var request = fixture.CreateHttpRequest("/me", session))
        using (var response = await fixture.Host.Client.SendAsync(request))
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

        fixture.Store.Sessions[1].Generation.ShouldBe(1);
        fixture.Clock.Advance(fixture.Options.PreviousGenerationGracePeriod + TimeSpan.FromMinutes(1));

        (await connection.Hub.InvokeAsync(SessionHubContract.ValidatedStamp)).ShouldBe(1);
        (await connection.Hub.InvokeAsync(SessionHubContract.ValidatedStamp)).ShouldBe(1);

        fixture.Store.Sessions.ShouldContainKey(1);
        fixture.Store.Calls.ShouldNotContain("InvalidateSession");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DueCookieAtConnect_HubReadsSessionAfterRequestRefreshedIt_KeepsSession(bool forceValidate)
    {
        // The hub does not read the session when the connection connects. The filter validates the due cookie then, so a request that refreshes the
        // session afterwards does not make the connection's cookie look like a replay once the previous generation's grace period is over.

        await using var fixture = await Fixture.StartAsync();
        var session = fixture.CreateSession(age: Due);
        var connection = await fixture.ConnectAsync("lazy-hub", session);

        fixture.Store.Calls.ShouldBe(["GetSession"]);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));

        using (var request = fixture.CreateHttpRequest("/me", session))
        using (var response = await fixture.Host.Client.SendAsync(request))
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

        fixture.Store.Sessions[1].Generation.ShouldBe(1);
        fixture.Clock.Advance(fixture.Options.PreviousGenerationGracePeriod + TimeSpan.FromSeconds(10));

        var method = forceValidate ? LazyHubContract.ValidatedStamp : LazyHubContract.Stamp;
        (await connection.Hub.InvokeAsync(method)).ShouldBe(1);

        fixture.Store.Sessions.ShouldContainKey(1);
        fixture.Store.Calls.ShouldNotContain("InvalidateSession");
    }

    [TestMethod]
    public async Task EndedSessionAtConnect_ConnectionHasNoSession()
    {
        await using var fixture = await Fixture.StartAsync();
        var session = fixture.CreateSession(age: Due);
        fixture.Store.Sessions.TryRemove(1, out _);

        // The connection has no session, so it isn't tracked.
        var connection = await fixture.ConnectAsync("lazy-hub", session, waitUntilTracked: false);

        var ex = await Should.ThrowAsync<UnauthorizedApiException>(() => connection.Hub.InvokeAsync(LazyHubContract.Stamp));
        ex.ErrorCode.ShouldBe(ApiErrorCodes.SignInRequired);
        fixture.Store.Calls.ShouldBe(["GetSession"]);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StoreThrowsUnauthorizedAtConnect_ReachesClient_InEitherFilterOrder(bool revalidationFirst)
    {
        await using var fixture = await Fixture.StartAsync(revalidationFirst: revalidationFirst);
        var session = fixture.CreateSession(age: Due);
        fixture.Store.GetSessionException = new UnauthorizedApiException("The account is disabled.") { ErrorCode = "account-disabled" };

        var error = await fixture.ConnectRefusedAsync("lazy-hub", session);

        var ex = error.ShouldBeOfType<UnauthorizedApiException>();
        ex.ErrorCode.ShouldBe("account-disabled");
        ex.Message.ShouldBe("The account is disabled.");
        fixture.Store.Calls.ShouldBe(["GetSession(threw)", "InvalidateSession"]);
    }

    [TestMethod]
    public async Task AnonymousConnection_IsNotTracked()
    {
        await using var fixture = await Fixture.StartAsync();
        var session = fixture.CreateSession();
        await fixture.ConnectAsync("session-hub", session);
        var anonymous = await fixture.ConnectAsync("open-hub", cookie: null);

        fixture.Clock.Advance(Due);
        await WaitUntilAsync(() => fixture.Store.Calls.Count is 2);

        fixture.Store.Calls.ShouldBe(["GetSession", "RefreshSession"]);
        (await anonymous.Hub.InvokeAsync(OpenHubContract.Ping)).ShouldBe("pong");
    }

    [TestMethod]
    public async Task ConnectionWithRecentCheck_IsNotChecked()
    {
        await using var fixture = await Fixture.StartAsync();
        var first = fixture.CreateSession(sessionId: 1);
        await fixture.ConnectAsync("session-hub", first);

        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        var second = fixture.CreateSession(sessionId: 2);
        await fixture.ConnectAsync("session-hub", second);
        var secondValidFor = fixture.Store.Sessions[2].ValidFor;

        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        await WaitUntilAsync(() => fixture.Store.Calls.Count is 2);

        fixture.Store.Sessions[1].ValidFor.ShouldBeGreaterThan(fixture.Options.PersistentSessionExpiry);
        fixture.Store.Sessions[2].ValidFor.ShouldBe(secondValidFor);
    }

    [TestMethod]
    public async Task DisconnectedConnection_IsNoLongerTracked()
    {
        await using var fixture = await Fixture.StartAsync();
        var stays = await fixture.ConnectAsync("session-hub", fixture.CreateSession(sessionId: 1));
        var leaves = await fixture.ConnectAsync("session-hub", fixture.CreateSession(sessionId: 2));
        var leavesValidFor = fixture.Store.Sessions[2].ValidFor;

        string connectionId = leaves.Hub.ConnectionId!;
        await leaves.Hub.StopAsync();
        await WaitUntilAsync(() => fixture.Events.Disconnected.Contains(connectionId));

        fixture.Clock.Advance(Due);
        await WaitUntilAsync(() => fixture.Store.Calls.Count is 2);

        fixture.Store.Sessions[2].ValidFor.ShouldBe(leavesValidFor);
        (await stays.Hub.InvokeAsync(SessionHubContract.WhoAmI)).ShouldBe(1);
    }

    [TestMethod]
    public async Task TurnedOff_ConnectionOfEndedSession_StaysOpenAndIsNotChecked()
    {
        await using var fixture = await Fixture.StartAsync(revalidate: false);
        var session = fixture.CreateSession(age: Due);
        var connection = await fixture.ConnectAsync("open-hub", session);

        fixture.Store.Sessions.Clear();
        fixture.Clock.Advance(Due + TimeSpan.FromMinutes(1));
        await Task.Delay(100);

        (await connection.Hub.InvokeAsync(OpenHubContract.Ping)).ShouldBe("pong");
        fixture.Store.Calls.ShouldBeEmpty();
    }

    /// <summary>
    /// Checks that a connection of the session hub stays open, and its hub calls work, while the checks that run before its own check is due pass it by,
    /// and then advances the clock to its check.
    /// </summary>
    private static async Task ExpectOpenUntilTheNextCheckAsync(Fixture fixture, Connection connection)
    {
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(100);

        connection.Closed.IsCompleted.ShouldBeFalse();
        (await connection.Hub.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(1);

        fixture.Clock.Advance(Due - TimeSpan.FromMinutes(5));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new ShouldAssertException("The condition was not met in time.");

            await Task.Delay(10);
        }
    }

    private static HubConnection CreateHubConnection(TestWebHost host, string path, string? cookie)
    {
        var server = host.App.GetTestServer();

        var hubConnection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, path), options => {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.WebSockets;
                options.Headers["User-Agent"] = SessionTestHost.DefaultDevice;

                if (cookie is not null)
                    options.Headers["Cookie"] = $"{TestSessionCookies.CookieName}={cookie}";

                options.WebSocketFactory = async (context, cancellationToken) => {
                    var client = server.CreateWebSocketClient();

                    client.ConfigureRequest = request => {
                        foreach (var (name, value) in context.Options.Headers)
                            request.Headers[name] = value;
                    };

                    return await client.ConnectAsync(context.Uri, cancellationToken);
                };
            })
            .Build();

        return hubConnection;
    }

    private static Task<Exception?> ObserveClosed(ApiHubConnection connection)
    {
        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

        connection.Closed += error => {
            closed.TrySetResult(error);
            return Task.CompletedTask;
        };

        return closed.Task;
    }

    private sealed record Connection(ApiHubConnection Hub, Task<Exception?> Closed);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly List<ApiHubConnection> _connections = [];
        private readonly List<HubConnection> _hubConnections = [];

        private Fixture(TestWebHost host, ManualClock clock, InMemorySessionStore store, HubEvents events)
        {
            Host = host;
            Clock = clock;
            Store = store;
            Events = events;
            Options = host.App.Services.GetRequiredService<IOptions<SessionHandlingOptions>>().Value;
            Tracker = host.App.Services.GetRequiredService<SessionConnectionTracker<TestSessionToken>>();
        }

        /// <summary>
        /// Gets the tracker of the connections' sessions, which tracks a connection after the hub's <c>OnConnectedAsync</c>.
        /// </summary>
        public SessionConnectionTracker<TestSessionToken> Tracker { get; }

        public TestWebHost Host { get; }

        public SessionHandlingOptions Options { get; }

        public ManualClock Clock { get; }

        public InMemorySessionStore Store { get; }

        public HubEvents Events { get; }

        public static async Task<Fixture> StartAsync(Action<SessionHandlingOptions>? configure = null, bool revalidate = true, bool revalidationFirst = false)
        {
            var clock = new ManualClock();
            var store = new InMemorySessionStore();
            var events = new HubEvents();

            var host = await TestWebHost.StartAsync(
                app => {
                    app.MapHub<SessionHub>("/session-hub");
                    app.MapHub<OpenHub>("/open-hub");
                    app.MapHub<LazyHub>("/lazy-hub");
                    app.MapHub<TypedHub>("/typed-hub");
                    app.MapGet("/me", async (HttpContext context) =>
                        (await context.GetRequiredSessionTokenAsync<TestSessionToken>(SessionAccessOptions.OptionalUserIdPrecondition)).UserId);
                    app.MapPost("/sign-out", (HttpContext context) => context.GetRequiredSessionContext<TestSessionToken>().SignOutAsync());
                    app.MapPost("/sign-in", async (HttpContext context, InMemorySessionStore sessions) => {
                        await context.GetRequiredSessionContext<TestSessionToken>().SignInAsync(persistent: true, info => {
                            sessions.Sessions[9] = new TestSessionData {
                                Id = 9,
                                UserId = 1,
                                Device = info.Device,
                                IpAddress = info.IpAddress,
                                RefreshedUtc = info.RefreshedUtc,
                                ExpiresUtc = info.ExpiresUtc,
                                IsPersistent = info.IsPersistent,
                                CreatedUtc = info.CreatedUtc,
                            };

                            return Task.FromResult(new TestSessionToken(9, 1, 1));
                        });
                    });
                },
                configureServices: s => {
                    s.AddSingleton<TimeProvider>(clock);
                    s.AddSingleton(store);
                    s.AddSingleton(events);
                    s.AddSingleton<IOriginValidator>(new OriginValidator("localhost"));

                    // The filters run in the order they are added: session handling adds the revalidation filter when it is registered.

                    if (!revalidationFirst)
                        s.AddSignalR(o => o.AddApiExceptionFilter());

                    s.AddHttpSessionHandling<TestSessionToken, ScopedInMemorySessionStore>(o => {
                        o.RevalidateHubConnections = revalidate;
                        configure?.Invoke(o);
                    });

                    if (revalidationFirst)
                        s.AddSignalR(o => o.AddApiExceptionFilter());
                });

            return new Fixture(host, clock, store, events);
        }

        /// <summary>
        /// Creates a session in the store, refreshed now or <paramref name="age"/> ago, and returns its cookie.
        /// </summary>
        public TestSessionCookie CreateSession(long sessionId = 1, TimeSpan? validFor = null, bool persistent = true, TimeSpan? age = null)
        {
            var utcNow = Clock.UtcNow - (age ?? TimeSpan.Zero);
            validFor ??= persistent ? Options.PersistentSessionExpiry : Options.TempSessionExpiry;

            Store.UserStamps.TryAdd(1, 1);
            Store.Sessions[sessionId] = new TestSessionData {
                Id = sessionId,
                UserId = 1,
                Device = SessionTestHost.DefaultDevice,
                IpAddress = SessionTestHost.DefaultIp,
                RefreshedUtc = utcNow,
                ExpiresUtc = utcNow + validFor.Value,
                IsPersistent = persistent,
                CreatedUtc = utcNow,
            };

            return new TestSessionCookie(new TestSessionToken(sessionId, 1, 1), utcNow, validFor.Value, 0, persistent);
        }

        /// <summary>
        /// Connects to a hub with the specified session cookie. Unless told not to, waits until the connection is open, which is after the hub's
        /// <c>OnConnectedAsync</c> has run, and, if its session is to be tracked (<paramref name="waitUntilTracked"/>, with rechecks on), until the
        /// service tracks it, which the confirmation of the connection doesn't wait for.
        /// </summary>
        public async Task<Connection> ConnectAsync(string path, TestSessionCookie? cookie, bool waitUntilOpen = true, bool waitUntilTracked = true)
        {
            string? cookieValue = cookie is null ? null : TestSessionCookies.Protect(Host.App.Services, cookie);
            var hub = new ApiHubConnection(CreateHubConnection(Host, path, cookieValue));
            var closed = ObserveClosed(hub);
            _connections.Add(hub);

            long sessionId = cookie?.Token.SessionId ?? 0;
            int tracked = CountTracked(sessionId);

            await hub.StartAsync();

            if (waitUntilOpen)
            {
                if (path is "session-hub")
                    await hub.InvokeAsync(SessionHubContract.WhoAmI);
                else
                    await hub.InvokeAsync(OpenHubContract.Ping);  // The lazy and typed hubs' Ping has the same name.

                if (waitUntilTracked && cookie is not null && Options.RevalidateHubConnections)
                    await WaitUntilAsync(() => CountTracked(sessionId) > tracked);
            }

            return new Connection(hub, closed);
        }

        /// <summary>
        /// Connects to a hub that refuses the connection with the specified session cookie, and returns the error that the start fails with, once the hub's
        /// <c>OnConnectedAsync</c> failed.
        /// </summary>
        public async Task<Exception> ConnectRefusedAsync(string path, TestSessionCookie? cookie)
        {
            string? cookieValue = cookie is null ? null : TestSessionCookies.Protect(Host.App.Services, cookie);
            var hub = new ApiHubConnection(CreateHubConnection(Host, path, cookieValue));
            _connections.Add(hub);

            return await Should.ThrowAsync<Exception>(() => hub.StartAsync().WaitAsync(Timeout));
        }

        /// <summary>
        /// Gets the number of connections of the specified session that the service tracks.
        /// </summary>
        public int CountTracked(long sessionId) => Tracker.CountTracked(token => token.SessionId == sessionId);

        /// <summary>
        /// Connects to the session hub with the specified session cookie through a SignalR connection that the toolkit's client doesn't wrap, and waits until
        /// the connection is open and, with rechecks on, until the service tracks it.
        /// </summary>
        public async Task<(HubConnection Hub, Task<Exception?> Closed)> ConnectWithoutToolkitAsync(string path, TestSessionCookie cookie)
        {
            var hub = CreateHubConnection(Host, path, TestSessionCookies.Protect(Host.App.Services, cookie));
            var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _hubConnections.Add(hub);

            hub.Closed += error => {
                closed.TrySetResult(error);
                return Task.CompletedTask;
            };

            int tracked = CountTracked(cookie.Token.SessionId);
            await hub.StartAsync();
            await hub.InvokeAsync<int>(SessionHubContract.WhoAmI.Name);

            if (Options.RevalidateHubConnections)
                await WaitUntilAsync(() => CountTracked(cookie.Token.SessionId) > tracked);

            return (hub, closed.Task);
        }

        public HttpRequestMessage CreateHttpRequest(string path, TestSessionCookie cookie, HttpMethod? method = null)
        {
            var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
            request.Headers.Add("Cookie", $"{TestSessionCookies.CookieName}={TestSessionCookies.Protect(Host.App.Services, cookie)}");
            request.Headers.Add("User-Agent", SessionTestHost.DefaultDevice);

            return request;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var connection in _connections)
                await connection.DisposeAsync();

            foreach (var connection in _hubConnections)
                await connection.DisposeAsync();

            await Host.DisposeAsync();
        }
    }
}
