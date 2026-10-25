using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Singulink.Net.Http.Api.Client;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Runs the lifetime options of <see cref="ApiHubConnection"/> (staying connected, the synchronization context of the handlers and disposing) against
/// hubs served by Kestrel, over real WebSockets.
/// </summary>
[PrefixTestClass]
public sealed class StayConnectedTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    // How long the server's clock is advanced for the session rechecks to check a connection.
    private static readonly TimeSpan RecheckDue = TimeSpan.FromMinutes(11);

    public static class LifetimeHubContract
    {
        public static HubMethod<int> Ping { get; } = new();

        public static HubMessage Abort { get; } = new();

        public static HubMessage SendNotification { get; } = new();

        public static HubMessage<string> Notified { get; } = new();

        public static HubMethod<string> RelayQuestion { get; } = new();

        public static HubMethod<string, string> Ask { get; } = new();
    }

    public sealed class LifetimeHub : Hub
    {
        public int Ping() => 1;

        public void Abort() => Context.Abort();

        public Task SendNotification() => Clients.Caller.SendAsync(LifetimeHubContract.Notified, "hello");

        public Task<string> RelayQuestion() => Clients.Caller.InvokeAsync(LifetimeHubContract.Ask, "question", Context.ConnectionAborted);
    }

    public sealed class RejectingHub : Hub
    {
        public override Task OnConnectedAsync() => throw new ForbiddenApiException("Not allowed here.");
    }

    /// <summary>
    /// Refuses the first connection with a transient error that has a retry time.
    /// </summary>
    public sealed class BusyHub(HubConnects connects) : Hub
    {
        public override Task OnConnectedAsync()
        {
            if (connects.Add() is 1)
                throw new ServiceUnavailableApiException("Busy.") { RetryAfter = TimeSpan.FromSeconds(5) };

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Refuses every connection with a transient error without a retry time.
    /// </summary>
    public sealed class AlwaysBusyHub(HubConnects connects) : Hub
    {
        public override Task OnConnectedAsync()
        {
            connects.Add();
            throw new ServiceUnavailableApiException("Busy.");
        }
    }

    /// <summary>
    /// Refuses the first connection with a transient error, and the ones after it with an error that isn't transient.
    /// </summary>
    public sealed class BusyThenRejectingHub(HubConnects connects) : Hub
    {
        public override Task OnConnectedAsync()
        {
            if (connects.Add() is 1)
                throw new ServiceUnavailableApiException("Busy.");

            throw new ForbiddenApiException("Not allowed here.");
        }
    }

    /// <summary>
    /// Breaks the first connection in its <c>OnConnectedAsync</c>, without SignalR's close message, as a network failure does, before the hub accepted it.
    /// </summary>
    public sealed class DroppingHub(HubConnects connects) : Hub
    {
        public override async Task OnConnectedAsync()
        {
            if (connects.Add() is 1)
            {
                // After the client has read the handshake's response.
                await Task.Delay(200);
                Context.GetHttpContext()!.Abort();
            }
        }

        public int Ping() => 1;
    }

    /// <summary>
    /// Counts the connections of the hubs that refuse them, and of the session hubs.
    /// </summary>
    public sealed class HubConnects
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public int Add() => Interlocked.Increment(ref _count);
    }

    public static class SessionHubContract
    {
        public static HubMethod<int> WhoAmI { get; } = new();

        public static HubMessage Abort { get; } = new();

        public static HubMessage Drop { get; } = new();

        public static HubMessage FailWithUserChanged { get; } = new();
    }

    /// <summary>
    /// A hub that validates the session against the store when a client connects, with the specified session options.
    /// </summary>
    public abstract class SessionHubBase(HubConnects connects, SessionAccessOptions sessionOptions) : Hub
    {
        public override async Task OnConnectedAsync()
        {
            connects.Add();
            var token = await Context.GetRequiredSessionTokenAsync<TestSessionToken>(sessionOptions | SessionAccessOptions.ForceValidate);
            Context.Items["UserId"] = token.UserId;
        }

        public int WhoAmI() => (int)Context.Items["UserId"]!;

        public void Abort() => Context.Abort();

        // Breaks the connection without SignalR's close message, as a network failure does, so that the client reconnects automatically.
        public void Drop() => Context.GetHttpContext()!.Abort();

        // Fails with an expected user ID of the service's own, which the error that the client gets doesn't carry.
        public void FailWithUserChanged() => throw new UserChangedApiException("The user changed.") { ExpectedUserId = "9" };
    }

    /// <summary>
    /// A session hub that requires the user ID precondition.
    /// </summary>
    public sealed class UserSessionHub(HubConnects connects) : SessionHubBase(connects, SessionAccessOptions.None);

    /// <summary>
    /// A session hub that accepts a connection without a user ID precondition.
    /// </summary>
    public sealed class AnyUserSessionHub(HubConnects connects) : SessionHubBase(connects, SessionAccessOptions.OptionalUserIdPrecondition);

    [TestMethod]
    public async Task FailedStart_KeepsTryingAndReportsReconnected()
    {
        await using var host = await KestrelHubHost.StartAsync(rejectedHandshakes: 1);
        await using var connection = new KestrelHubClient(host.BaseAddress).CreateLifetimeConnection();
        var events = new EventRecorder(connection);
        connection.StayConnected = true;

        await connection.StartAsync().WaitAsync(Timeout);

        connection.State.ShouldBe(HubConnectionState.Reconnecting);
        (await events.Reconnecting.Task.WaitAsync(Timeout)).ShouldBeOfType<WebSocketException>();

        await events.Reconnected.Task.WaitAsync(Timeout);

        connection.State.ShouldBe(HubConnectionState.Connected);
        (await connection.InvokeAsync(LifetimeHubContract.Ping).WaitAsync(Timeout)).ShouldBe(1);
        events.Closed.Task.IsCompleted.ShouldBeFalse();
        host.HandshakeCount.ShouldBe(2);
    }

    [TestMethod]
    public async Task StartWhileTrying_ReportsReconnectingOnceAndReconnected()
    {
        await using var host = await KestrelHubHost.StartAsync(rejectedHandshakes: 1);
        await using var connection = new KestrelHubClient(host.BaseAddress).CreateLifetimeConnection();
        var events = new EventRecorder(connection);
        int reconnecting = 0;
        connection.StayConnected = true;

        connection.Reconnecting += _ => {
            Interlocked.Increment(ref reconnecting);
            return Task.CompletedTask;
        };

        await connection.StartAsync().WaitAsync(Timeout);
        await events.Reconnecting.Task.WaitAsync(Timeout);

        await connection.StartAsync().WaitAsync(Timeout);

        await events.Reconnected.Task.WaitAsync(Timeout);
        connection.State.ShouldBe(HubConnectionState.Connected);
        (await connection.InvokeAsync(LifetimeHubContract.Ping).WaitAsync(Timeout)).ShouldBe(1);
        Volatile.Read(ref reconnecting).ShouldBe(1);
        host.HandshakeCount.ShouldBe(2);
    }

    [TestMethod]
    public async Task FailedStartWithoutStayConnected_Throws()
    {
        await using var host = await KestrelHubHost.StartAsync(rejectedHandshakes: 1);
        await using var connection = new KestrelHubClient(host.BaseAddress).CreateLifetimeConnection();

        await Should.ThrowAsync<WebSocketException>(() => connection.StartAsync().WaitAsync(Timeout));

        connection.State.ShouldBe(HubConnectionState.Disconnected);
    }

    [TestMethod]
    public async Task ClosedConnection_StartsAgainRightAwayIfItWasStable_AndAfterTheNextDelayIfNot()
    {
        await using var host = await KestrelHubHost.StartAsync();
        var time = new ManualTimeProvider(manualTimers: true);
        await using var connection = CreateTimedConnection(host, "hub", time);
        var events = new EventRecorder(connection);
        connection.StayConnected = true;

        await connection.StartAsync().WaitAsync(Timeout);

        // Connected for 30 seconds: the next attempt is the first one, without a delay.
        time.Advance(TimeSpan.FromSeconds(30));
        await connection.SendAsync(LifetimeHubContract.Abort).WaitAsync(Timeout);

        await events.Reconnecting.Task.WaitAsync(Timeout);
        await events.Reconnected.Task.WaitAsync(Timeout);

        (await connection.InvokeAsync(LifetimeHubContract.Ping).WaitAsync(Timeout)).ShouldBe(1);
        host.HandshakeCount.ShouldBe(2);

        // Connected for less than 30 seconds: the close counts as a failed attempt, so the next one follows 2 seconds later.
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        connection.Reconnected += _ => {
            reconnected.TrySetResult();
            return Task.CompletedTask;
        };

        time.Advance(TimeSpan.FromSeconds(29));
        await connection.SendAsync(LifetimeHubContract.Abort).WaitAsync(Timeout);

        await WaitUntilAsync(() => time.PendingTimerCount is 1);
        host.HandshakeCount.ShouldBe(2);

        time.Advance(TimeSpan.FromSeconds(2));
        await reconnected.Task.WaitAsync(Timeout);

        host.HandshakeCount.ShouldBe(3);
        events.Closed.Task.IsCompleted.ShouldBeFalse();
    }

    [TestMethod]
    public async Task ConnectionThatKeepsClosingRightAway_IsStartedAgainWithGrowingDelays()
    {
        await using var host = await KestrelHubHost.StartAsync();
        var time = new ManualTimeProvider(manualTimers: true);
        await using var connection = CreateTimedConnection(host, "always-busy-hub", time);
        var events = new EventRecorder(connection);
        connection.StayConnected = true;

        await connection.StartAsync().WaitAsync(Timeout);
        (await events.Reconnecting.Task.WaitAsync(Timeout)).ShouldBeOfType<ServiceUnavailableApiException>();

        foreach (var delay in new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30) })
        {
            int connects = host.HubConnects.Count;
            await WaitUntilAsync(() => time.PendingTimerCount is 1);

            time.Advance(delay - TimeSpan.FromMilliseconds(1));
            await Task.Delay(100);
            host.HubConnects.Count.ShouldBe(connects);

            time.Advance(TimeSpan.FromMilliseconds(1));
            await WaitUntilAsync(() => host.HubConnects.Count == connects + 1);
        }

        // The service counts a connection before the client has handled its close. The attempt can end as connected in between, so the state is only
        // checked once the client waits for the next attempt.
        await WaitUntilAsync(() => time.PendingTimerCount is 1);

        connection.State.ShouldBe(HubConnectionState.Reconnecting);
        events.Closed.Task.IsCompleted.ShouldBeFalse();

        await connection.StopAsync().WaitAsync(Timeout);
        (await events.Closed.Task.WaitAsync(Timeout)).ShouldBeNull();
    }

    [TestMethod]
    public async Task ClosedConnectionWithoutStayConnected_IsClosed()
    {
        await using var host = await KestrelHubHost.StartAsync();
        await using var connection = new KestrelHubClient(host.BaseAddress, automaticReconnect: false).CreateLifetimeConnection();
        var events = new EventRecorder(connection);

        await connection.StartAsync().WaitAsync(Timeout);
        await connection.SendAsync(LifetimeHubContract.Abort).WaitAsync(Timeout);

        await events.Closed.Task.WaitAsync(Timeout);

        events.Reconnecting.Task.IsCompleted.ShouldBeFalse();
        connection.State.ShouldBe(HubConnectionState.Disconnected);
    }

    [TestMethod]
    public async Task ApiErrorOnConnect_FailsTheStart_AndStaysClosed()
    {
        await using var host = await KestrelHubHost.StartAsync();
        await using var connection = new KestrelHubClient(host.BaseAddress).CreateConnection("rejecting-hub");
        var events = new EventRecorder(connection);
        connection.StayConnected = true;

        // The hub refuses the connection after the handshake, before the service confirms it, so the start fails with the error.
        var ex = await Should.ThrowAsync<ForbiddenApiException>(() => connection.StartAsync().WaitAsync(Timeout));
        ex.Message.ShouldBe("Not allowed here.");

        connection.State.ShouldBe(HubConnectionState.Disconnected);
        await Task.Delay(100);
        events.Closed.Task.IsCompleted.ShouldBeFalse();
        events.Reconnecting.Task.IsCompleted.ShouldBeFalse();
        host.HandshakeCount.ShouldBe(1);
    }

    [TestMethod]
    public async Task ConnectionLostBeforeTheHubAcceptedIt_FailsTheStart_WithoutReconnecting()
    {
        await using var host = await KestrelHubHost.StartAsync();
        await using var connection = new KestrelHubClient(host.BaseAddress).CreateConnection("dropping-hub");
        var events = new EventRecorder(connection);

        // The underlying connection reconnects automatically after a loss, and it is stopped instead, since its start hasn't ended.
        var ex = await Should.ThrowAsync<Exception>(() => connection.StartAsync().WaitAsync(Timeout));
        ex.ShouldNotBeAssignableTo<OperationCanceledException>();

        await Task.Delay(500);
        int handshakes = host.HandshakeCount;
        await Task.Delay(500);
        host.HandshakeCount.ShouldBe(handshakes);
        connection.State.ShouldBe(HubConnectionState.Disconnected);
        events.Reconnecting.Task.IsCompleted.ShouldBeFalse();
        events.Reconnected.Task.IsCompleted.ShouldBeFalse();
        events.Closed.Task.IsCompleted.ShouldBeFalse();
    }

    [TestMethod]
    public async Task ConnectionLostBeforeTheHubAcceptedIt_WithStayConnected_StartsAgain()
    {
        await using var host = await KestrelHubHost.StartAsync();
        await using var connection = new KestrelHubClient(host.BaseAddress).CreateConnection("dropping-hub");
        var events = new EventRecorder(connection);
        var reconnectedCount = CountReconnected(connection);
        connection.StayConnected = true;

        await connection.StartAsync().WaitAsync(Timeout);

        (await events.Reconnecting.Task.WaitAsync(Timeout)).ShouldNotBeNull();
        await events.Reconnected.Task.WaitAsync(Timeout);
        (await connection.InvokeAsync(LifetimeHubContract.Ping).WaitAsync(Timeout)).ShouldBe(1);

        await Task.Delay(100);
        reconnectedCount().ShouldBe(1);
        events.Closed.Task.IsCompleted.ShouldBeFalse();
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task SessionOfAnotherUserThatTheHubRefusesAfterTheHandshake_IsNotReportedAsReconnected(bool browserPath, bool automaticReconnect)
    {
        await using var host = await KestrelHubHost.StartAsync();
        var time = new ManualTimeProvider(manualTimers: true);
        var client = new SessionHubClient(host.BaseAddress, time, automaticReconnect, browserPath);
        await client.SignInAsync(7);
        client.StartSession();

        await using var connection = client.CreateUserClient(7).CreateConnection("user-session-hub");
        var events = new EventRecorder(connection);
        var reconnectedCount = CountReconnected(connection);
        connection.StayConnected = true;
        await connection.StartAsync().WaitAsync(Timeout);
        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);

        // Connected for 30 seconds, so that the attempt after the close has no delay of its own.
        time.Advance(TimeSpan.FromSeconds(30));

        // The device gets another user's session, without a sign-in that replaces the connection's, and the connection's network fails, after which the
        // underlying connection reconnects, or the service closes it, after which the connection starts again.
        client.ClearSession();
        await client.SignInAsync(8);
        client.StartSession();
        int connects = host.HubConnects.Count;
        await connection.SendAsync(automaticReconnect ? SessionHubContract.Drop : SessionHubContract.Abort).WaitAsync(Timeout);

        // The attempt presents that user's session, which the hub refuses after the handshake, and the connection waits for the next session start.
        await WaitUntilAsync(() =>
            host.HubConnects.Count == connects + 1 && connection.State is HubConnectionState.Reconnecting && time.PendingTimerCount is 1);

        await Task.Delay(200);
        reconnectedCount().ShouldBe(0);
        events.Closed.Task.IsCompleted.ShouldBeFalse();

        // The user signs in again, and the connection connects, which it reports once.
        client.ClearSession();
        await client.SignInAsync(7);
        client.StartSession();
        time.Advance(TimeSpan.FromSeconds(30));
        await events.Reconnected.Task.WaitAsync(Timeout);

        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);
        await Task.Delay(100);
        reconnectedCount().ShouldBe(1);
        host.HubConnects.Count.ShouldBe(connects + 2);
    }

    [TestMethod]
    public async Task TransientApiErrorOnConnectWithRetryAfter_WaitsAtLeastThatLongBeforeStartingAgain()
    {
        await using var host = await KestrelHubHost.StartAsync();
        var time = new ManualTimeProvider(manualTimers: true);
        await using var connection = CreateTimedConnection(host, "busy-hub", time);
        var events = new EventRecorder(connection);
        connection.StayConnected = true;

        await connection.StartAsync().WaitAsync(Timeout);

        (await events.Reconnecting.Task.WaitAsync(Timeout)).ShouldBeOfType<ServiceUnavailableApiException>().RetryAfter.ShouldBe(TimeSpan.FromSeconds(5));

        // The retry time is longer than the 2 seconds before the second attempt.
        await WaitUntilAsync(() => time.PendingTimerCount is 1);
        time.Advance(TimeSpan.FromSeconds(4.9));
        await Task.Delay(100);
        host.HubConnects.Count.ShouldBe(1);

        time.Advance(TimeSpan.FromSeconds(0.1));
        await events.Reconnected.Task.WaitAsync(Timeout);

        // The hub's OnConnectedAsync runs after the handshake that completes the start.
        await WaitUntilAsync(() => host.HubConnects.Count is 2);
        events.Closed.Task.IsCompleted.ShouldBeFalse();
    }

    [TestMethod]
    public async Task ApiErrorThatIsNotTransientOnConnectAfterATransientOne_EndsTheAttempts()
    {
        await using var host = await KestrelHubHost.StartAsync();
        var time = new ManualTimeProvider(manualTimers: true);
        await using var connection = CreateTimedConnection(host, "busy-then-rejecting-hub", time);
        var events = new EventRecorder(connection);
        connection.StayConnected = true;

        await connection.StartAsync().WaitAsync(Timeout);
        await events.Reconnecting.Task.WaitAsync(Timeout);

        await WaitUntilAsync(() => time.PendingTimerCount is 1);
        time.Advance(TimeSpan.FromSeconds(2));

        (await events.Closed.Task.WaitAsync(Timeout)).ShouldBeOfType<ForbiddenApiException>().Message.ShouldBe("Not allowed here.");
        connection.State.ShouldBe(HubConnectionState.Disconnected);
        host.HubConnects.Count.ShouldBe(2);
    }

    [TestMethod]
    public async Task ApiErrorOfAnAttemptToStartAgain_IsTranslated()
    {
        var factory = new FailingConnectionFactory(
            new HttpRequestException("The service can't be reached."), new HubException("403 [denied] Not allowed here."));

        await using var connection = new ApiHubConnection(CreateFailingHubConnection(factory));

        var events = new EventRecorder(connection);
        connection.StayConnected = true;

        await connection.StartAsync().WaitAsync(Timeout);

        (await events.Reconnecting.Task.WaitAsync(Timeout)).ShouldBeOfType<HttpRequestException>();
        var closedError = (await events.Closed.Task.WaitAsync(Timeout)).ShouldBeOfType<ForbiddenApiException>();
        closedError.ErrorCode.ShouldBe("denied");
        closedError.Message.ShouldBe("Not allowed here.");
        factory.Attempts.ShouldBe(2);
    }

    [TestMethod]
    public async Task StopWhileTrying_StopsAndReportsClosed()
    {
        await using var host = await KestrelHubHost.StartAsync(rejectedHandshakes: int.MaxValue);
        await using var connection = new KestrelHubClient(host.BaseAddress).CreateLifetimeConnection();
        var events = new EventRecorder(connection);
        connection.StayConnected = true;

        await connection.StartAsync().WaitAsync(Timeout);
        await events.Reconnecting.Task.WaitAsync(Timeout);

        await connection.StopAsync().WaitAsync(Timeout);

        (await events.Closed.Task.WaitAsync(Timeout)).ShouldBeNull();
        connection.State.ShouldBe(HubConnectionState.Disconnected);

        int handshakes = host.HandshakeCount;
        await Task.Delay(TimeSpan.FromSeconds(3));
        host.HandshakeCount.ShouldBe(handshakes);
    }

    [TestMethod]
    public async Task SessionEndedOnStart_WaitsForNewSessionBeforeTheNextAttempt()
    {
        var factory = new FailingConnectionFactory(SignInRequiredHubError(), new HubException("403 [denied] Not allowed here."));
        var sessionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new ManualTimeProvider(manualTimers: true);
        await using var connection = CreateFailingConnection(factory, () => sessionStarted.Task, time);
        var events = new EventRecorder(connection);
        connection.StayConnected = true;

        await connection.StartAsync().WaitAsync(Timeout);

        (await events.Reconnecting.Task.WaitAsync(Timeout)).ShouldBeOfType<UnauthorizedApiException>().ErrorCode.ShouldBe(ApiErrorCodes.SignInRequired);
        connection.State.ShouldBe(HubConnectionState.Reconnecting);

        // The delay before the next attempt passes, and the attempt still waits for a new session.
        await WaitUntilAsync(() => time.PendingTimerCount is 1);
        time.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(100);

        factory.Attempts.ShouldBe(1);
        connection.State.ShouldBe(HubConnectionState.Reconnecting);
        events.Closed.Task.IsCompleted.ShouldBeFalse();

        sessionStarted.SetResult();

        (await events.Closed.Task.WaitAsync(Timeout)).ShouldBeOfType<ForbiddenApiException>();
        factory.Attempts.ShouldBe(2);
    }

    [TestMethod]
    public async Task SessionStartedSinceTheAttemptBegan_StartsAgainWithoutWaiting()
    {
        var factory = new FailingConnectionFactory(SignInRequiredHubError(), new HubException("403 [denied] Not allowed here."));
        var time = new ManualTimeProvider(manualTimers: true);
        await using var connection = CreateFailingConnection(factory, () => Task.CompletedTask, time);
        var events = new EventRecorder(connection);
        connection.StayConnected = true;

        await connection.StartAsync().WaitAsync(Timeout);
        await events.Reconnecting.Task.WaitAsync(Timeout);

        // The session started already, so the attempt follows at once, without its delay of 2 seconds.
        (await events.Closed.Task.WaitAsync(Timeout)).ShouldBeOfType<ForbiddenApiException>();
        factory.Attempts.ShouldBe(2);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StopWhileWaitingForSession_ReportsClosedAndStaysClosed(bool dispose)
    {
        var factory = new FailingConnectionFactory(SignInRequiredHubError());
        var sessionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = CreateFailingConnection(factory, () => sessionStarted.Task);
        var events = new EventRecorder(connection);
        connection.StayConnected = true;

        await connection.StartAsync().WaitAsync(Timeout);
        await events.Reconnecting.Task.WaitAsync(Timeout);

        if (dispose)
            await connection.DisposeAsync().AsTask().WaitAsync(Timeout);
        else
            await connection.StopAsync().WaitAsync(Timeout);

        (await events.Closed.Task.WaitAsync(Timeout)).ShouldBeNull();
        connection.State.ShouldBe(HubConnectionState.Disconnected);

        sessionStarted.SetResult();
        await Task.Delay(TimeSpan.FromSeconds(3));
        factory.Attempts.ShouldBe(1);

        await connection.DisposeAsync();
    }

    [TestMethod]
    public async Task SessionEndedWithoutApiClient_Throws()
    {
        var factory = new FailingConnectionFactory(SignInRequiredHubError());
        await using var connection = new ApiHubConnection(CreateFailingHubConnection(factory));
        var events = new EventRecorder(connection);
        connection.StayConnected = true;

        var error = await Should.ThrowAsync<UnauthorizedApiException>(() => connection.StartAsync().WaitAsync(Timeout));

        error.ErrorCode.ShouldBe(ApiErrorCodes.SignInRequired);
        connection.State.ShouldBe(HubConnectionState.Disconnected);
        events.Reconnecting.Task.IsCompleted.ShouldBeFalse();
        factory.Attempts.ShouldBe(1);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SessionEnded_StartsAgainOnceTheUserSignsInAgain(bool automaticReconnect)
    {
        await using var host = await KestrelHubHost.StartAsync();
        var time = new ManualTimeProvider(manualTimers: true);
        var client = new SessionHubClient(host.BaseAddress, time, automaticReconnect);
        await client.SignInAsync(7);

        await using var connection = client.CreateUserClient(7).CreateConnection("user-session-hub");
        connection.StayConnected = true;
        await connection.StartAsync().WaitAsync(Timeout);
        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);

        int connects = await EndSessionAsync(host, client, connection, time);

        // The session starts while the attempt's delay of 2 seconds runs, and the attempt follows it at once.
        var reconnected = NextReconnected(connection);
        await client.SignInAsync(7);
        await reconnected.WaitAsync(Timeout);
        host.HubConnects.Count.ShouldBe(connects + 1);

        connection.State.ShouldBe(HubConnectionState.Connected);
        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);
        client.SessionEndedCount.ShouldBe(1);
    }

    [TestMethod]
    public async Task SessionEndedAfterTheGuidesSignIn_EndsTheSessionOnTheDevice()
    {
        var serverClock = new ManualClock();
        await using var host = await KestrelHubHost.StartAsync(clock: serverClock);
        var time = new ManualTimeProvider(manualTimers: true);
        var client = new SessionHubClient(host.BaseAddress, time);
        await client.SignInAsync(7);

        await using var connection = client.CreateUserClient(7).CreateConnection("user-session-hub");
        connection.StayConnected = true;
        await connection.StartAsync().WaitAsync(Timeout);
        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);
        await EndSessionAsync(host, client, connection, time);

        // The guide's sign-in: its response sets the cookie, which starts a session that wakes the connection, and the app then reports the start.
        var reconnected = NextReconnected(connection);
        await client.SignInAsync(7);
        time.Advance(TimeSpan.FromSeconds(2));
        await reconnected.WaitAsync(Timeout);
        client.StartSession();
        await host.WaitUntilTrackedAsync(host.LastSessionId);

        // The app ends the session in its store, such as when the user signs out on all devices, and the connection's next check tells it.
        host.SessionStore.Sessions.Clear();
        serverClock.Advance(RecheckDue);

        await WaitUntilAsync(() => client.SessionEndedCount is 2);
        client.CurrentSessionToken.ShouldBeNull();
    }

    [TestMethod]
    public async Task SessionReplacedBySignInOfAnotherUser_EndsTheConnectionAtItsNextCheck_WhichStartsAgainWithTheNewSession()
    {
        var serverClock = new ManualClock();
        await using var host = await KestrelHubHost.StartAsync(clock: serverClock);
        var time = new ManualTimeProvider(manualTimers: true);
        var client = new SessionHubClient(host.BaseAddress, time);
        await client.SignInAsync(7);

        await using var connection = client.CreateConnection("any-user-session-hub");
        var events = new EventRecorder(connection);
        connection.StayConnected = true;
        await connection.StartAsync().WaitAsync(Timeout);
        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);
        await host.WaitUntilTrackedAsync(host.LastSessionId);

        // Connected for 30 seconds, so that the attempt after the close has no delay of its own.
        time.Advance(TimeSpan.FromSeconds(30));

        // Another user signs in on the device, which replaces the connection's session. The sign-in's response starts a session on the device, without a
        // report of the app's, and the connection stays open until its next check.
        await client.SignInAsync(8);
        await Task.Delay(200);
        events.Reconnecting.Task.IsCompleted.ShouldBeFalse();
        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);

        // The check ends the connection, which starts again at once with the device's new session.
        serverClock.Advance(RecheckDue);
        (await events.Reconnecting.Task.WaitAsync(Timeout)).ShouldBeOfType<UnauthorizedApiException>().ErrorCode.ShouldBe(ApiErrorCodes.SignInRequired);
        await events.Reconnected.Task.WaitAsync(Timeout);

        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(8);
        client.SessionEndedCount.ShouldBe(0);
    }

    [TestMethod]
    public async Task BrowserPath_SignInInAnotherTab_EndsTheConnectionAtItsNextCheck_WhichResumesWithTheBrowsersSession()
    {
        var serverClock = new ManualClock();
        await using var host = await KestrelHubHost.StartAsync(clock: serverClock);
        var time = new ManualTimeProvider(manualTimers: true);
        var tab1 = new SessionHubClient(host.BaseAddress, time, automaticReconnect: false, browserPath: true);
        await tab1.SignInAsync(7);
        tab1.StartSession();

        // The second tab has the first one's cookie, as the tabs of a browser share it.
        var tab2 = new SessionHubClient(host.BaseAddress, time, automaticReconnect: false, browserPath: true, tab1.CurrentSessionToken);
        await using var connection1 = tab1.CreateConnection("any-user-session-hub");
        await using var connection2 = tab2.CreateConnection("any-user-session-hub");
        var events2 = new EventRecorder(connection2);

        foreach (var connection in new[] { connection1, connection2 })
        {
            connection.StayConnected = true;
            await connection.StartAsync().WaitAsync(Timeout);
            (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);
        }

        await host.WaitUntilTrackedAsync(host.LastSessionId, count: 2);

        // Connected for 30 seconds, so that the attempts after the close have no delay of their own.
        time.Advance(TimeSpan.FromSeconds(30));

        // The user signs in again in the first tab, which replaces the session of both connections and reports the start. The second tab gets the new
        // session's cookie, as the browser's tabs share it, and no report.
        await tab1.SignInAsync(7);
        tab1.StartSession();
        await tab2.AdoptCookieAsync(tab1.CurrentSessionToken.ShouldNotBeNull());

        // The connections' next check ends them. The first tab's connection starts again at once, since a session started in its tab.
        var reconnected1 = NextReconnected(connection1);
        serverClock.Advance(RecheckDue);
        await reconnected1.WaitAsync(Timeout);

        // The second tab's connection waits 5 seconds for a session start in its tab, and then tries the browser's cookie, with which it resumes, without
        // ending the session in its tab.
        (await events2.Reconnecting.Task.WaitAsync(Timeout)).ShouldBeOfType<UnauthorizedApiException>().ErrorCode.ShouldBe(ApiErrorCodes.SignInRequired);
        await WaitUntilAsync(() => time.PendingTimerCount is 1);
        time.Advance(TimeSpan.FromSeconds(4));
        await Task.Delay(100);
        events2.Reconnected.Task.IsCompleted.ShouldBeFalse();

        time.Advance(TimeSpan.FromSeconds(1));
        await events2.Reconnected.Task.WaitAsync(Timeout);

        (await connection2.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);
        (await connection1.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);
        tab2.SessionEndedCount.ShouldBe(0);
        tab1.SessionEndedCount.ShouldBe(0);
    }

    [TestMethod]
    public async Task BrowserPath_SignInOfAnotherUserInAnotherTab_IsRefused_AndTheConnectionWaitsUntilTheUserSignsInAgain()
    {
        var serverClock = new ManualClock();
        await using var host = await KestrelHubHost.StartAsync(clock: serverClock);
        var time = new ManualTimeProvider(manualTimers: true);
        var tab1 = new SessionHubClient(host.BaseAddress, time, automaticReconnect: false, browserPath: true);
        await tab1.SignInAsync(7);
        tab1.StartSession();

        // The second tab has the first one's cookie, and its connection the user ID precondition of user 7.
        var tab2 = new SessionHubClient(host.BaseAddress, time, automaticReconnect: false, browserPath: true, tab1.CurrentSessionToken);
        await using var connection = tab2.CreateUserClient(7).CreateConnection("user-session-hub");
        var events = new EventRecorder(connection);
        connection.StayConnected = true;
        await connection.StartAsync().WaitAsync(Timeout);
        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);
        await host.WaitUntilTrackedAsync(host.LastSessionId);
        time.Advance(TimeSpan.FromSeconds(30));

        // Another user signs in in the first tab, and the second tab gets that session's cookie.
        await tab1.SignInAsync(8);
        tab1.StartSession();
        await tab2.AdoptCookieAsync(tab1.CurrentSessionToken.ShouldNotBeNull());

        // The connection's next check ends it. 5 seconds later it tries the browser's cookie, which its hub refuses.
        int connects = host.HubConnects.Count;
        serverClock.Advance(RecheckDue);
        (await events.Reconnecting.Task.WaitAsync(Timeout)).ShouldBeOfType<UnauthorizedApiException>().ErrorCode.ShouldBe(ApiErrorCodes.SignInRequired);
        await WaitUntilAsync(() => time.PendingTimerCount is 1);
        time.Advance(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => host.HubConnects.Count == connects + 1 && time.PendingTimerCount is 1);

        // The connection then waits for a session start in its tab, without trying the cookie again, rather than closing for good.
        time.Advance(TimeSpan.FromMinutes(30));
        await Task.Delay(200);
        host.HubConnects.Count.ShouldBe(connects + 1);
        connection.State.ShouldBe(HubConnectionState.Reconnecting);
        events.Closed.Task.IsCompleted.ShouldBeFalse();
        tab2.SessionEndedCount.ShouldBe(0);

        // The user signs in again in the second tab, which reports the start, and the connection starts again.
        var reconnected = NextReconnected(connection);
        await tab2.SignInAsync(7);
        tab2.StartSession();
        await reconnected.WaitAsync(Timeout);

        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);
        events.Closed.Task.IsCompleted.ShouldBeFalse();
        tab2.SessionEndedCount.ShouldBe(0);
    }

    [TestMethod]
    public async Task BrowserPath_RefusedWithAnotherUsersSession_ARequestOfTheConnectionsUserAfterItsSignIn_StartsItAgainAtOnce()
    {
        var serverClock = new ManualClock();
        await using var host = await KestrelHubHost.StartAsync(clock: serverClock);
        var time = new ManualTimeProvider(manualTimers: true);
        var tab1 = new SessionHubClient(host.BaseAddress, time, automaticReconnect: false, browserPath: true);
        await tab1.SignInAsync(7);
        tab1.StartSession();

        var tab2 = new SessionHubClient(host.BaseAddress, time, automaticReconnect: false, browserPath: true, tab1.CurrentSessionToken);
        var user7 = tab2.CreateUserClient(7);
        await using var connection = user7.CreateConnection("user-session-hub");
        var events = new EventRecorder(connection);
        int connects = await RefuseWithAnotherUsersSessionAsync(host, serverClock, time, tab1, tab2, connection, events);

        // The user signs in again in the first tab, and the second tab gets the cookie, which the connection waits for no timed try of.
        await tab1.SignInAsync(7);
        tab1.StartSession();
        await tab2.AdoptCookieAsync(tab1.CurrentSessionToken.ShouldNotBeNull());
        await Task.Delay(200);
        events.Reconnected.Task.IsCompleted.ShouldBeFalse();

        // A request of the second tab with the connection's user ID precondition succeeds, and the connection starts again at once, without waiting out
        // the delay of 2 seconds after the refused try.
        (await user7.WhoAmIAsync()).ShouldBe(7);
        await events.Reconnected.Task.WaitAsync(Timeout);

        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);
        host.HubConnects.Count.ShouldBe(connects + 1);
        tab2.SessionEndedCount.ShouldBe(0);
    }

    [TestMethod]
    public async Task BrowserPath_RefusedWithAnotherUsersSession_IsCuedOnlyByRequestsWithItsUserIdPrecondition()
    {
        var serverClock = new ManualClock();
        await using var host = await KestrelHubHost.StartAsync(clock: serverClock);
        var time = new ManualTimeProvider(manualTimers: true);
        var tab1 = new SessionHubClient(host.BaseAddress, time, automaticReconnect: false, browserPath: true);
        await tab1.SignInAsync(7);
        tab1.StartSession();

        var tab2 = new SessionHubClient(host.BaseAddress, time, automaticReconnect: false, browserPath: true, tab1.CurrentSessionToken);
        var user7 = tab2.CreateUserClient(7);
        await using var connection = user7.CreateConnection("user-session-hub");
        var events = new EventRecorder(connection);
        int connects = await RefuseWithAnotherUsersSessionAsync(host, serverClock, time, tab1, tab2, connection, events);

        // Requests of the other user and without a precondition succeed with the browser's session of user 8, which cues no try of the connection.
        (await tab2.CreateUserClient(8).WhoAmIAsync()).ShouldBe(8);
        await tab2.PingAsync();
        await Task.Delay(200);
        host.HubConnects.Count.ShouldBe(connects);

        // The user signs in again in the first tab, and a request with the connection's precondition starts it again.
        await tab1.SignInAsync(7);
        tab1.StartSession();
        await tab2.AdoptCookieAsync(tab1.CurrentSessionToken.ShouldNotBeNull());
        await user7.PingAsync();
        await events.Reconnected.Task.WaitAsync(Timeout);

        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);
    }

    [TestMethod]
    public async Task BrowserPath_RefusedWithAnotherUsersSession_RequestsCueItAtMostOnceEvery30Seconds_AndNoTimedTriesFollow()
    {
        var serverClock = new ManualClock();
        await using var host = await KestrelHubHost.StartAsync(clock: serverClock);
        var time = new ManualTimeProvider(manualTimers: true);
        var tab1 = new SessionHubClient(host.BaseAddress, time, automaticReconnect: false, browserPath: true);
        await tab1.SignInAsync(7);
        tab1.StartSession();

        var tab2 = new SessionHubClient(host.BaseAddress, time, automaticReconnect: false, browserPath: true, tab1.CurrentSessionToken);
        var user7 = tab2.CreateUserClient(7);
        await using var connection = user7.CreateConnection("user-session-hub");
        var events = new EventRecorder(connection);
        int connects = await RefuseWithAnotherUsersSessionAsync(host, serverClock, time, tab1, tab2, connection, events);

        // A request with the connection's precondition to an endpoint that doesn't retrieve the session succeeds with the other user's session. The
        // connection tries at once, and the hub refuses it again.
        await user7.PingAsync();
        await WaitUntilAsync(() => host.HubConnects.Count == connects + 1 && time.PendingTimerCount is 1);

        // The next request comes sooner than 30 seconds after that try, which the connection follows once they have passed.
        await user7.PingAsync();
        await WaitUntilAsync(() => time.PendingTimerCount is 2);
        time.Advance(TimeSpan.FromSeconds(29));
        await Task.Delay(200);
        host.HubConnects.Count.ShouldBe(connects + 1);

        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => host.HubConnects.Count == connects + 2 && time.PendingTimerCount is 1);

        // The connection then waits for a session start in its tab or the next request, without trying again on its own.
        time.Advance(TimeSpan.FromMinutes(30));
        await Task.Delay(200);
        host.HubConnects.Count.ShouldBe(connects + 2);
        connection.State.ShouldBe(HubConnectionState.Reconnecting);
        events.Closed.Task.IsCompleted.ShouldBeFalse();
        tab2.SessionEndedCount.ShouldBe(0);
    }

    [TestMethod]
    public async Task BrowserPath_SignOutInAnotherTab_EndsTheSessionAtTheNextCheck_AndTheConnectionTriesTheCookieEvery5Minutes()
    {
        var serverClock = new ManualClock();
        await using var host = await KestrelHubHost.StartAsync(clock: serverClock);
        var time = new ManualTimeProvider(manualTimers: true);
        var tab1 = new SessionHubClient(host.BaseAddress, time, automaticReconnect: false, browserPath: true);
        await tab1.SignInAsync(7);
        tab1.StartSession();

        var tab2 = new SessionHubClient(host.BaseAddress, time, automaticReconnect: false, browserPath: true, tab1.CurrentSessionToken);
        await using var connection = tab2.CreateConnection("any-user-session-hub");
        var events = new EventRecorder(connection);
        var reconnectedCount = CountReconnected(connection);
        connection.StayConnected = true;
        await connection.StartAsync().WaitAsync(Timeout);
        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);
        await host.WaitUntilTrackedAsync(host.LastSessionId);
        time.Advance(TimeSpan.FromSeconds(30));

        // The first tab signs out, which ends the session and deletes the browser's cookie. The test's second tab keeps the ended session's cookie, which the
        // hub refuses as it refuses no cookie, and gets no report of a session start when it gets a cookie later, as in the browser.
        await tab1.SignOutAsync();
        int sessionsEnded = tab2.SessionEndedCount;

        // The connection's next check ends it. 5 seconds later it tries the browser's cookie, which the hub refuses: the session ends in its tab.
        int handshakes = host.HandshakeCount;
        serverClock.Advance(RecheckDue);
        (await events.Reconnecting.Task.WaitAsync(Timeout)).ShouldBeOfType<UnauthorizedApiException>().ErrorCode.ShouldBe(ApiErrorCodes.SignInRequired);
        await WaitUntilAsync(() => time.PendingTimerCount is 1);
        tab2.SessionEndedCount.ShouldBe(sessionsEnded);
        time.Advance(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => tab2.SessionEndedCount == sessionsEnded + 1 && time.PendingTimerCount is 2);
        host.HandshakeCount.ShouldBe(handshakes + 1);

        // It tries the cookie again every 5 minutes, and the hub's refusals don't end the session in the tab again.
        await ExpectNextTryAsync(TimeSpan.FromMinutes(5), handshakes + 2);
        await ExpectNextTryAsync(TimeSpan.FromMinutes(5), handshakes + 3);
        tab2.SessionEndedCount.ShouldBe(sessionsEnded + 1);
        connection.State.ShouldBe(HubConnectionState.Reconnecting);
        events.Closed.Task.IsCompleted.ShouldBeFalse();
        reconnectedCount().ShouldBe(0);

        // The user signs in again in the first tab, and the second tab gets the cookie, with which its connection resumes at its next try, 5 minutes later.
        await tab1.SignInAsync(7);
        tab1.StartSession();
        await tab2.AdoptCookieAsync(tab1.CurrentSessionToken.ShouldNotBeNull());
        time.Advance(TimeSpan.FromMinutes(5));
        await events.Reconnected.Task.WaitAsync(Timeout);

        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);
        tab2.SessionEndedCount.ShouldBe(sessionsEnded + 1);

        // Waits for the try that the specified time leads to, which the hub refuses, after checking that none comes sooner. The attempt waits for a delay
        // of its own, which has passed, as well as for its session wait.
        async Task ExpectNextTryAsync(TimeSpan wait, int expectedHandshakes)
        {
            time.Advance(wait - TimeSpan.FromSeconds(1));
            await Task.Delay(200);
            host.HandshakeCount.ShouldBe(expectedHandshakes - 1);

            time.Advance(TimeSpan.FromSeconds(1));
            await WaitUntilAsync(() => host.HandshakeCount == expectedHandshakes && time.PendingTimerCount is 2);
        }
    }

    [TestMethod]
    public async Task BrowserPath_SignOutAndInAgainInAnotherTab_ARequestOfTheConnectionsUser_StartsItAgainAtOnce()
    {
        var serverClock = new ManualClock();
        await using var host = await KestrelHubHost.StartAsync(clock: serverClock);
        var time = new ManualTimeProvider(manualTimers: true);
        var tab1 = new SessionHubClient(host.BaseAddress, time, automaticReconnect: false, browserPath: true);
        await tab1.SignInAsync(7);
        tab1.StartSession();

        var tab2 = new SessionHubClient(host.BaseAddress, time, automaticReconnect: false, browserPath: true, tab1.CurrentSessionToken);
        var user7 = tab2.CreateUserClient(7);
        await using var connection = user7.CreateConnection("user-session-hub");
        var events = new EventRecorder(connection);
        connection.StayConnected = true;
        await connection.StartAsync().WaitAsync(Timeout);
        await host.WaitUntilTrackedAsync(host.LastSessionId);
        time.Advance(TimeSpan.FromSeconds(30));

        // The first tab signs out. The connection's next check ends it, and its try of the browser's cookie 5 seconds later is refused.
        await tab1.SignOutAsync();
        int sessionsEnded = tab2.SessionEndedCount;
        serverClock.Advance(RecheckDue);
        await events.Reconnecting.Task.WaitAsync(Timeout);
        await WaitUntilAsync(() => time.PendingTimerCount is 1);
        time.Advance(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => tab2.SessionEndedCount == sessionsEnded + 1 && time.PendingTimerCount is 2);

        // The user signs in again in the first tab, and the second tab gets the cookie. The connection's next try is 5 minutes away.
        await tab1.SignInAsync(7);
        tab1.StartSession();
        await tab2.AdoptCookieAsync(tab1.CurrentSessionToken.ShouldNotBeNull());
        await Task.Delay(200);
        events.Reconnected.Task.IsCompleted.ShouldBeFalse();

        // A request of the second tab with the connection's user ID precondition succeeds, and the connection tries the cookie at once, without waiting
        // out the delay of 2 seconds after the refused try, and resumes.
        (await user7.WhoAmIAsync()).ShouldBe(7);
        await events.Reconnected.Task.WaitAsync(Timeout);

        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);
        tab2.SessionEndedCount.ShouldBe(sessionsEnded + 1);
    }

    [TestMethod]
    public async Task BrowserPath_RequestsCueTheConnectionAtMostOnceEvery30Seconds_AndARefusedTryIsHandledLikeATimedOne()
    {
        var serverClock = new ManualClock();
        await using var host = await KestrelHubHost.StartAsync(clock: serverClock);
        var time = new ManualTimeProvider(manualTimers: true);
        var tab1 = new SessionHubClient(host.BaseAddress, time, automaticReconnect: false, browserPath: true);
        await tab1.SignInAsync(7);
        tab1.StartSession();

        var tab2 = new SessionHubClient(host.BaseAddress, time, automaticReconnect: false, browserPath: true, tab1.CurrentSessionToken);
        var user7 = tab2.CreateUserClient(7);
        await using var connection = user7.CreateConnection("user-session-hub");
        var events = new EventRecorder(connection);
        connection.StayConnected = true;
        await connection.StartAsync().WaitAsync(Timeout);
        await host.WaitUntilTrackedAsync(host.LastSessionId);
        time.Advance(TimeSpan.FromSeconds(30));

        // The first tab signs out, and the connection's try of the browser's cookie after its check is refused.
        await tab1.SignOutAsync();
        int sessionsEnded = tab2.SessionEndedCount;
        int handshakes = host.HandshakeCount;
        serverClock.Advance(RecheckDue);
        await events.Reconnecting.Task.WaitAsync(Timeout);
        await WaitUntilAsync(() => time.PendingTimerCount is 1);
        time.Advance(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => tab2.SessionEndedCount == sessionsEnded + 1 && time.PendingTimerCount is 2);
        host.HandshakeCount.ShouldBe(handshakes + 1);
        time.Advance(TimeSpan.FromSeconds(2));

        // A request with the connection's user ID precondition to an endpoint that doesn't retrieve the session succeeds without one. The connection
        // tries the cookie at once, which the hub refuses, without ending the session in the tab again.
        await user7.PingAsync();
        await WaitUntilAsync(() => host.HandshakeCount == handshakes + 2 && time.PendingTimerCount is 2);

        // The next request comes sooner than 30 seconds after that try, which the connection follows once they have passed.
        await user7.PingAsync();
        await WaitUntilAsync(() => time.PendingTimerCount is 3);
        time.Advance(TimeSpan.FromSeconds(29));
        await Task.Delay(200);
        host.HandshakeCount.ShouldBe(handshakes + 2);

        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => host.HandshakeCount == handshakes + 3 && time.PendingTimerCount is 2);

        // The timed tries go on 5 minutes after the refused one.
        time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        await Task.Delay(200);
        host.HandshakeCount.ShouldBe(handshakes + 3);

        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => host.HandshakeCount == handshakes + 4 && time.PendingTimerCount is 2);

        tab2.SessionEndedCount.ShouldBe(sessionsEnded + 1);
        connection.State.ShouldBe(HubConnectionState.Reconnecting);
        events.Closed.Task.IsCompleted.ShouldBeFalse();
    }

    [TestMethod]
    public async Task BrowserPath_RequestWithAUserIdPrecondition_CuesTheConnectionsWithItAndThoseWithoutOne()
    {
        var serverClock = new ManualClock();
        await using var host = await KestrelHubHost.StartAsync(clock: serverClock);
        var time = new ManualTimeProvider(manualTimers: true);
        var tab1 = new SessionHubClient(host.BaseAddress, time, automaticReconnect: false, browserPath: true);
        await tab1.SignInAsync(7);
        tab1.StartSession();

        // The second tab has a connection with the user ID precondition of user 7, and one without a precondition.
        var tab2 = new SessionHubClient(host.BaseAddress, time, automaticReconnect: false, browserPath: true, tab1.CurrentSessionToken);
        var user7 = tab2.CreateUserClient(7);
        await using var withPrecondition = user7.CreateConnection("user-session-hub");
        await using var withoutPrecondition = tab2.CreateConnection("any-user-session-hub");
        var eventsWith = new EventRecorder(withPrecondition);
        var eventsWithout = new EventRecorder(withoutPrecondition);

        foreach (var connection in new[] { withPrecondition, withoutPrecondition })
        {
            connection.StayConnected = true;
            await connection.StartAsync().WaitAsync(Timeout);
        }

        await host.WaitUntilTrackedAsync(host.LastSessionId, count: 2);
        time.Advance(TimeSpan.FromSeconds(30));

        // The first tab signs out, and the tries of the browser's cookie after the connections' checks are refused. Then the user signs in again in the
        // first tab, and the second tab gets the cookie.
        await tab1.SignOutAsync();
        int handshakes = host.HandshakeCount;
        serverClock.Advance(RecheckDue);
        await WaitUntilAsync(() => time.PendingTimerCount is 2);
        time.Advance(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => host.HandshakeCount == handshakes + 2 && time.PendingTimerCount is 4);

        await tab1.SignInAsync(7);
        tab1.StartSession();
        await tab2.AdoptCookieAsync(tab1.CurrentSessionToken.ShouldNotBeNull());
        time.Advance(TimeSpan.FromSeconds(2));

        // A request without a user ID precondition cues neither connection.
        await tab2.PingAsync();
        await Task.Delay(200);
        host.HandshakeCount.ShouldBe(handshakes + 2);

        // A request with another user's precondition cues only the connection without a precondition, which resumes with the browser's session.
        await tab2.CreateUserClient(8).PingAsync();
        await eventsWithout.Reconnected.Task.WaitAsync(Timeout);
        (await withoutPrecondition.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);
        await Task.Delay(200);
        eventsWith.Reconnected.Task.IsCompleted.ShouldBeFalse();

        // A request with the connection's precondition cues it.
        await user7.PingAsync();
        await eventsWith.Reconnected.Task.WaitAsync(Timeout);
        (await withPrecondition.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);
    }

    [TestMethod]
    public async Task BrowserPath_RequestThatBeganBeforeTheConnectionsWait_DoesNotCueIt()
    {
        var serverClock = new ManualClock();
        await using var host = await KestrelHubHost.StartAsync(clock: serverClock);
        var time = new ManualTimeProvider(manualTimers: true);
        var tab1 = new SessionHubClient(host.BaseAddress, time, automaticReconnect: false, browserPath: true);
        await tab1.SignInAsync(7);
        tab1.StartSession();

        var tab2 = new SessionHubClient(host.BaseAddress, time, automaticReconnect: false, browserPath: true, tab1.CurrentSessionToken);
        var user7 = tab2.CreateUserClient(7);
        await using var connection = user7.CreateConnection("user-session-hub");
        var events = new EventRecorder(connection);
        connection.StayConnected = true;
        await connection.StartAsync().WaitAsync(Timeout);
        await host.WaitUntilTrackedAsync(host.LastSessionId);
        time.Advance(TimeSpan.FromSeconds(30));

        // A request of the second tab with the connection's user ID precondition begins, and waits on the service.
        var gatedPing = user7.GatedPingAsync();
        await host.GatedPingReceived.Task.WaitAsync(Timeout);

        // The first tab signs out, the connection's try of the browser's cookie after its check is refused, and the user signs in again in the first tab.
        await tab1.SignOutAsync();
        serverClock.Advance(RecheckDue);
        await events.Reconnecting.Task.WaitAsync(Timeout);
        await WaitUntilAsync(() => time.PendingTimerCount is 1);
        time.Advance(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => time.PendingTimerCount is 2);

        await tab1.SignInAsync(7);
        tab1.StartSession();
        await tab2.AdoptCookieAsync(tab1.CurrentSessionToken.ShouldNotBeNull());
        time.Advance(TimeSpan.FromSeconds(2));

        // The request that began before the connection's wait succeeds now, which doesn't cue the connection. A request that begins now does.
        host.PingGate.SetResult();
        await gatedPing.WaitAsync(Timeout);
        await Task.Delay(200);
        events.Reconnected.Task.IsCompleted.ShouldBeFalse();

        await user7.PingAsync();
        await events.Reconnected.Task.WaitAsync(Timeout);
    }

    [TestMethod]
    public async Task NativePath_RequestWithTheConnectionsUserIdPrecondition_DoesNotStartTheConnectionAgainWithoutASession()
    {
        await using var host = await KestrelHubHost.StartAsync();
        var time = new ManualTimeProvider(manualTimers: true);
        var client = new SessionHubClient(host.BaseAddress, time);
        await client.SignInAsync(7);

        var user7 = client.CreateUserClient(7);
        await using var connection = user7.CreateConnection("user-session-hub");
        connection.StayConnected = true;
        await connection.StartAsync().WaitAsync(Timeout);

        // The session ends, and the connection waits for a new one, which the successful requests of its user don't stand in for.
        int connects = await EndSessionAsync(host, client, connection, time);
        time.Advance(TimeSpan.FromSeconds(2));
        await user7.PingAsync();
        await Task.Delay(200);

        host.HubConnects.Count.ShouldBe(connects);
        connection.State.ShouldBe(HubConnectionState.Reconnecting);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SessionStartAfterQuickCloses_StartsTheConnectionAtOnce_WithTheDelaysOfAStableConnection(bool browserPath)
    {
        var serverClock = new ManualClock();
        await using var host = await KestrelHubHost.StartAsync(clock: serverClock);
        var time = new ManualTimeProvider(manualTimers: true);
        var client = new SessionHubClient(host.BaseAddress, time, automaticReconnect: false, browserPath);
        await client.SignInAsync(7);
        client.StartSession();

        await using var connection = client.CreateConnection("any-user-session-hub");
        var events = new EventRecorder(connection);
        var reconnectedCount = CountReconnected(connection);
        connection.StayConnected = true;
        await connection.StartAsync().WaitAsync(Timeout);
        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);

        // The connection closes soon after it connects, twice, so that its attempts reach the delay of 30 seconds.
        await CloseAndStartAgainAsync(TimeSpan.FromSeconds(2), expectedReconnects: 1);
        await CloseAndStartAgainAsync(TimeSpan.FromSeconds(10), expectedReconnects: 2);

        // Its session ends, and the attempt after the close is refused, which ends the session on the device.
        await host.WaitUntilTrackedAsync(host.LastSessionId);
        host.SessionStore.Sessions.Clear();
        serverClock.Advance(RecheckDue);
        await WaitUntilAsync(() => connection.State is HubConnectionState.Reconnecting && time.PendingTimerCount > 0);

        if (browserPath)
        {
            // The browser's attempt with its cookie waits out the delay of 30 seconds.
            time.Advance(TimeSpan.FromSeconds(30));
        }

        await WaitUntilAsync(() => client.SessionEndedCount is 1);

        // The user signs in again: the attempt follows at once, without the delay that the attempts had reached.
        await client.SignInAsync(7);
        client.StartSession();
        await WaitUntilAsync(() => reconnectedCount() is 3);
        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);

        // The attempts after it are spaced as after a close of a stable connection: the connection closes soon after, and starts again 2 seconds later.
        await CloseAndStartAgainAsync(TimeSpan.FromSeconds(2), expectedReconnects: 4);
        events.Closed.Task.IsCompleted.ShouldBeFalse();

        // Closes the connection on the service, and checks that the attempt after the close waits for the specified delay.
        async Task CloseAndStartAgainAsync(TimeSpan delay, int expectedReconnects)
        {
            await connection.SendAsync(SessionHubContract.Abort).WaitAsync(Timeout);
            await WaitUntilAsync(() => connection.State is HubConnectionState.Reconnecting && time.PendingTimerCount is 1);

            time.Advance(delay - TimeSpan.FromMilliseconds(1));
            await Task.Delay(100);
            reconnectedCount().ShouldBe(expectedReconnects - 1);

            time.Advance(TimeSpan.FromMilliseconds(1));
            await WaitUntilAsync(() => reconnectedCount() == expectedReconnects);
        }
    }

    [TestMethod]
    public async Task BrowserPath_SessionStartedDuringAnAutomaticReconnect_ThenEnded_EndsTheSession()
    {
        var serverClock = new ManualClock();
        await using var host = await KestrelHubHost.StartAsync(clock: serverClock);
        var time = new ManualTimeProvider(manualTimers: true);
        var client = new SessionHubClient(host.BaseAddress, time, automaticReconnect: true, browserPath: true);
        await client.SignInAsync(7);
        client.StartSession();

        // Without StayConnected, the connection reports the end of its session when it closes.
        await using var connection = client.CreateConnection("any-user-session-hub");
        var events = new EventRecorder(connection);
        await connection.StartAsync().WaitAsync(Timeout);
        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);

        // The connection's network fails, and the service refuses the first attempt of SignalR's automatic reconnect.
        int handshakes = host.HandshakeCount;
        host.RejectsHandshakes = true;
        var reconnected = NextReconnected(connection);
        await connection.SendAsync(SessionHubContract.Drop).WaitAsync(Timeout);
        await WaitUntilAsync(() => host.HandshakeCount > handshakes);

        // Meanwhile another user signs in, and the app reports the start. The next attempt, 2 seconds later, connects with the new session.
        await client.SignInAsync(8);
        client.StartSession();
        host.RejectsHandshakes = false;
        await reconnected.WaitAsync(Timeout);
        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(8);
        await host.WaitUntilTrackedAsync(host.LastSessionId);

        // The app ends the new session in its store, and the connection's next check tells it: the report concerns the session that the connection connected
        // with.
        host.SessionStore.Sessions.Clear();
        serverClock.Advance(RecheckDue);

        (await events.Closed.Task.WaitAsync(Timeout)).ShouldBeOfType<UnauthorizedApiException>().ErrorCode.ShouldBe(ApiErrorCodes.SignInRequired);
        await WaitUntilAsync(() => client.SessionEndedCount is 1);
    }

    [TestMethod]
    public async Task BrowserPath_HandshakeHookWhileConnected_DoesNotChangeTheConnectionsSessionStart()
    {
        var serverClock = new ManualClock();
        await using var host = await KestrelHubHost.StartAsync(clock: serverClock);
        var time = new ManualTimeProvider(manualTimers: true);
        var client = new SessionHubClient(host.BaseAddress, time, automaticReconnect: false, browserPath: true);
        await client.SignInAsync(7);

        await using var connection = client.CreateConnection("any-user-session-hub");
        var events = new EventRecorder(connection);
        await connection.StartAsync().WaitAsync(Timeout);
        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);
        await host.WaitUntilTrackedAsync(host.LastSessionId);

        // A session starts after the connection connected, and the hook that reports handshakes runs again, as the access token provider does for the polls
        // of long polling or a refresh of the authentication of an open connection.
        client.StartSession();
        connection.RecordHandshake();

        // The connection's report then still concerns the session from before the start.
        host.SessionStore.Sessions.Clear();
        serverClock.Advance(RecheckDue);

        (await events.Closed.Task.WaitAsync(Timeout)).ShouldBeOfType<UnauthorizedApiException>().ErrorCode.ShouldBe(ApiErrorCodes.SignInRequired);
        client.SessionEndedCount.ShouldBe(0);
    }

    [TestMethod]
    public async Task BrowserPath_AppsAccessTokenProvider_StillGivesTheHandshakesToken()
    {
        await using var host = await KestrelHubHost.StartAsync();
        var time = new ManualTimeProvider(manualTimers: true);
        var client = new SessionHubClient(host.BaseAddress, time, automaticReconnect: false, browserPath: true);
        await client.SignInAsync(7);

        await using var connection = client.CreateConnection(
            "any-user-session-hub", options => options.AccessTokenProvider = () => Task.FromResult<string?>("app-token"));

        await connection.StartAsync().WaitAsync(Timeout);
        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);

        host.LastHandshakeAuthorization.ShouldBe("Bearer app-token");
    }

    [TestMethod]
    public async Task SessionOfAnotherUser_WithUserIdPrecondition_IsRefused_AndWaitsUntilASessionOfTheUserStarts()
    {
        await using var host = await KestrelHubHost.StartAsync();
        var time = new ManualTimeProvider(manualTimers: true);
        var client = new SessionHubClient(host.BaseAddress, time);
        await client.SignInAsync(7);

        await using var connection = client.CreateUserClient(7).CreateConnection("user-session-hub");
        var events = new EventRecorder(connection);
        var reconnectedCount = CountReconnected(connection);
        connection.StayConnected = true;
        await connection.StartAsync().WaitAsync(Timeout);
        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);
        int connects = await EndSessionAsync(host, client, connection, time);

        // Another user signs in, which starts a session: the next attempt is refused, and the connection waits for the next session start.
        await client.SignInAsync(8);
        time.Advance(TimeSpan.FromSeconds(2));
        await ExpectRefusedAttemptAsync(connects + 1);

        // Another sign-in of that user, whose response starts a session, leads to one more refused attempt, and another wait.
        await client.SignInAsync(8);
        await ExpectRefusedAttemptAsync(connects + 2);

        // The user signs in again, and the connection starts again with the user's session.
        var reconnected = NextReconnected(connection);
        await client.SignInAsync(7);
        await reconnected.WaitAsync(Timeout);

        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);
        host.HubConnects.Count.ShouldBe(connects + 3);
        events.Closed.Task.IsCompleted.ShouldBeFalse();
        client.SessionEndedCount.ShouldBe(1);

        // Waits for the attempt that the session start leads to, which the hub refuses, and checks that no attempt follows until the next session start,
        // also after the delay before the next attempt.
        async Task ExpectRefusedAttemptAsync(int expectedConnects)
        {
            await WaitUntilAsync(() => host.HubConnects.Count == expectedConnects && connection.State is HubConnectionState.Reconnecting &&
                time.PendingTimerCount is 1);

            time.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(200);

            host.HubConnects.Count.ShouldBe(expectedConnects);
            connection.State.ShouldBe(HubConnectionState.Reconnecting);
            events.Closed.Task.IsCompleted.ShouldBeFalse();
            reconnectedCount().ShouldBe(0);
        }
    }

    [TestMethod]
    public async Task SessionOfAnotherUser_StartsConnectionWithoutUserIdPreconditionAgain()
    {
        await using var host = await KestrelHubHost.StartAsync();
        var time = new ManualTimeProvider(manualTimers: true);
        var client = new SessionHubClient(host.BaseAddress, time);
        await client.SignInAsync(7);

        await using var connection = client.CreateConnection("any-user-session-hub");
        connection.StayConnected = true;
        await connection.StartAsync().WaitAsync(Timeout);
        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);

        await EndSessionAsync(host, client, connection, time);

        var reconnected = NextReconnected(connection);
        time.Advance(TimeSpan.FromSeconds(2));
        await client.SignInAsync(8);
        await reconnected.WaitAsync(Timeout);

        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(8);
    }

    [TestMethod]
    public async Task SessionOfAnotherUser_RefusesTheStartOfTheUsersConnection_WithTheUsersIdAsTheExpectedUserId()
    {
        await using var host = await KestrelHubHost.StartAsync();
        var client = new SessionHubClient(host.BaseAddress, TimeProvider.System);
        await client.SignInAsync(8);

        await using var connection = client.CreateUserClient(7).CreateConnection("user-session-hub");
        var error = await Should.ThrowAsync<UserChangedApiException>(() => connection.StartAsync().WaitAsync(Timeout));

        error.ExpectedUserId.ShouldBe("7");
    }

    [TestMethod]
    public async Task SessionOfAnotherUser_WithStayConnected_ReportsTheRefusedStartWithTheUsersIdAsTheExpectedUserId()
    {
        await using var host = await KestrelHubHost.StartAsync();
        var time = new ManualTimeProvider(manualTimers: true);
        var client = new SessionHubClient(host.BaseAddress, time);
        await client.SignInAsync(8);

        await using var connection = client.CreateUserClient(7).CreateConnection("user-session-hub");
        var events = new EventRecorder(connection);
        connection.StayConnected = true;
        await connection.StartAsync().WaitAsync(Timeout);

        var error = await events.Reconnecting.Task.WaitAsync(Timeout);
        error.ShouldBeOfType<UserChangedApiException>().ExpectedUserId.ShouldBe("7");

        // The user signs in, and the connection connects.
        await client.SignInAsync(7);
        await events.Reconnected.Task.WaitAsync(Timeout);
        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);
    }

    [TestMethod]
    public async Task SessionOfAnotherUser_ThatTheHubRefusesAnAutomaticReconnectWith_ClosesTheConnectionWithTheUsersIdAsTheExpectedUserId()
    {
        await using var host = await KestrelHubHost.StartAsync();
        var client = new SessionHubClient(host.BaseAddress, TimeProvider.System, automaticReconnect: true);
        await client.SignInAsync(7);

        await using var connection = client.CreateUserClient(7).CreateConnection("user-session-hub");
        var events = new EventRecorder(connection);
        await connection.StartAsync().WaitAsync(Timeout);

        // The device gets another user's session, and the connection's network fails, after which the hub refuses the automatic reconnect.
        client.ClearSession();
        await client.SignInAsync(8);
        await connection.SendAsync(SessionHubContract.Drop).WaitAsync(Timeout);

        var error = await events.Closed.Task.WaitAsync(Timeout);
        error.ShouldBeOfType<UserChangedApiException>().ExpectedUserId.ShouldBe("7");
    }

    [TestMethod]
    public async Task UserChangedErrorOfAHubMethod_CarriesTheUserIdPreconditionOfTheConnection_NotTheServicesExpectedUserId()
    {
        await using var host = await KestrelHubHost.StartAsync();
        var client = new SessionHubClient(host.BaseAddress, TimeProvider.System);
        await client.SignInAsync(7);

        await using var withPrecondition = client.CreateUserClient(7).CreateConnection("user-session-hub");
        await using var withoutPrecondition = client.CreateConnection("any-user-session-hub");
        await withPrecondition.StartAsync().WaitAsync(Timeout);
        await withoutPrecondition.StartAsync().WaitAsync(Timeout);

        var error = await Should.ThrowAsync<UserChangedApiException>(
            () => withPrecondition.InvokeAsync(SessionHubContract.FailWithUserChanged).WaitAsync(Timeout));

        var errorWithoutPrecondition = await Should.ThrowAsync<UserChangedApiException>(
            () => withoutPrecondition.InvokeAsync(SessionHubContract.FailWithUserChanged).WaitAsync(Timeout));

        error.ExpectedUserId.ShouldBe("7");
        error.Message.ShouldBe("The user changed.");
        errorWithoutPrecondition.ExpectedUserId.ShouldBeNull();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RevokedSession_WaitsForNewSessionWithoutStartingAgainBefore(bool automaticReconnect)
    {
        var serverClock = new ManualClock();
        await using var host = await KestrelHubHost.StartAsync(clock: serverClock);
        var time = new ManualTimeProvider(manualTimers: true);
        var client = new SessionHubClient(host.BaseAddress, time, automaticReconnect);
        await client.SignInAsync(7);

        await using var connection = client.CreateUserClient(7).CreateConnection("user-session-hub");
        var events = new EventRecorder(connection);
        var reconnectedCount = CountReconnected(connection);
        connection.StayConnected = true;
        await connection.StartAsync().WaitAsync(Timeout);
        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);
        await host.WaitUntilTrackedAsync(host.LastSessionId);

        int connects = host.HubConnects.Count;
        int handshakes = host.HandshakeCount;

        // Connected for 30 seconds, so that the attempt after the close has no delay of its own.
        time.Advance(TimeSpan.FromSeconds(30));
        host.SessionStore.Sessions.Clear();
        serverClock.Advance(RecheckDue);

        (await events.Reconnecting.Task.WaitAsync(Timeout)).ShouldBeOfType<UnauthorizedApiException>().ErrorCode.ShouldBe(ApiErrorCodes.SignInRequired);
        await WaitUntilAsync(() => client.SessionEndedCount is 1);

        client.CurrentSessionToken.ShouldBeNull();
        connection.State.ShouldBe(HubConnectionState.Reconnecting);

        await Task.Delay(500);
        host.HandshakeCount.ShouldBe(handshakes);
        host.HubConnects.Count.ShouldBe(connects);
        reconnectedCount().ShouldBe(0);
        events.Closed.Task.IsCompleted.ShouldBeFalse();

        await client.SignInAsync(7);
        await events.Reconnected.Task.WaitAsync(Timeout);

        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);
        host.HandshakeCount.ShouldBe(handshakes + 1);

        // The event's handlers run one after another, and the count's runs after the recorder's.
        await WaitUntilAsync(() => reconnectedCount() is 1);
        client.SessionEndedCount.ShouldBe(1);
    }

    [TestMethod]
    public async Task SignedOutSession_EndsTheConnectionAtItsNextCheck_AndWaitsForNewSession()
    {
        var serverClock = new ManualClock();
        await using var host = await KestrelHubHost.StartAsync(clock: serverClock);
        var time = new ManualTimeProvider(manualTimers: true);
        var client = new SessionHubClient(host.BaseAddress, time);
        await client.SignInAsync(7);

        await using var connection = client.CreateUserClient(7).CreateConnection("user-session-hub");
        var events = new EventRecorder(connection);
        var reconnectedCount = CountReconnected(connection);
        connection.StayConnected = true;
        await connection.StartAsync().WaitAsync(Timeout);
        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);
        await host.WaitUntilTrackedAsync(host.LastSessionId);

        int handshakes = host.HandshakeCount;
        time.Advance(TimeSpan.FromSeconds(30));

        // The sign-out's response ends the session on the device. The service ends the session's connection at its next check, as every server does.
        await client.SignOutAsync();
        client.SessionEndedCount.ShouldBe(1);
        await Task.Delay(200);
        events.Reconnecting.Task.IsCompleted.ShouldBeFalse();

        serverClock.Advance(RecheckDue);
        (await events.Reconnecting.Task.WaitAsync(Timeout)).ShouldBeOfType<UnauthorizedApiException>().ErrorCode.ShouldBe(ApiErrorCodes.SignInRequired);
        connection.State.ShouldBe(HubConnectionState.Reconnecting);

        await Task.Delay(500);
        host.HandshakeCount.ShouldBe(handshakes);
        reconnectedCount().ShouldBe(0);
        client.SessionEndedCount.ShouldBe(1);

        await client.SignInAsync(7);
        await events.Reconnected.Task.WaitAsync(Timeout);

        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);
        host.HandshakeCount.ShouldBe(handshakes + 1);

        // The event's handlers run one after another, and the count's runs after the recorder's.
        await WaitUntilAsync(() => reconnectedCount() is 1);
    }

    [TestMethod]
    public async Task SessionEndedByStore_WaitsForNewSessionWithTheStoresError()
    {
        var serverClock = new ManualClock();
        await using var host = await KestrelHubHost.StartAsync(clock: serverClock);
        var time = new ManualTimeProvider(manualTimers: true);
        var client = new SessionHubClient(host.BaseAddress, time);
        await client.SignInAsync(7);

        await using var connection = client.CreateUserClient(7).CreateConnection("user-session-hub");
        var events = new EventRecorder(connection);
        connection.StayConnected = true;
        await connection.StartAsync().WaitAsync(Timeout);
        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);
        await host.WaitUntilTrackedAsync(host.LastSessionId);

        int handshakes = host.HandshakeCount;
        time.Advance(TimeSpan.FromSeconds(30));
        host.SessionStore.GetSessionException = new UnauthorizedApiException("The account is disabled.") { ErrorCode = "account-disabled" };
        serverClock.Advance(RecheckDue);

        var error = (await events.Reconnecting.Task.WaitAsync(Timeout)).ShouldBeOfType<UnauthorizedApiException>();
        error.ErrorCode.ShouldBe("account-disabled");
        error.Message.ShouldBe("The account is disabled.");

        await WaitUntilAsync(() => client.SessionEndedCount is 1);
        client.CurrentSessionToken.ShouldBeNull();
        connection.State.ShouldBe(HubConnectionState.Reconnecting);

        await Task.Delay(500);
        host.HandshakeCount.ShouldBe(handshakes);

        host.SessionStore.GetSessionException = null;
        await client.SignInAsync(7);
        await events.Reconnected.Task.WaitAsync(Timeout);

        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task SessionEndedMessage_DoesNotNeedTheAppsTypeInfoForStrings(bool service, bool client)
    {
        var serverClock = new ManualClock();
        await using var host = await KestrelHubHost.StartAsync(clock: serverClock, payloadResolver: service ? IntPayloadJsonContext.Default : null);
        var time = new ManualTimeProvider(manualTimers: true);
        var sessionClient = client ? new SessionHubClient(host.BaseAddress, time, IntPayloadJsonContext.Default) : new SessionHubClient(host.BaseAddress, time);
        await sessionClient.SignInAsync(7);

        await using var connection = sessionClient.CreateUserClient(7).CreateConnection("user-session-hub");
        var events = new EventRecorder(connection);
        connection.StayConnected = true;
        await connection.StartAsync().WaitAsync(Timeout);
        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);
        await host.WaitUntilTrackedAsync(host.LastSessionId);

        int handshakes = host.HandshakeCount;
        time.Advance(TimeSpan.FromSeconds(30));
        host.SessionStore.Sessions.Clear();
        serverClock.Advance(RecheckDue);

        (await events.Reconnecting.Task.WaitAsync(Timeout)).ShouldBeOfType<UnauthorizedApiException>().ErrorCode.ShouldBe(ApiErrorCodes.SignInRequired);
        await WaitUntilAsync(() => sessionClient.SessionEndedCount is 1);
        host.HandshakeCount.ShouldBe(handshakes);
    }

    [TestMethod]
    public async Task RevokedSessionWithoutStayConnected_ClosesWithTheErrorAndEndsTheSession()
    {
        var serverClock = new ManualClock();
        await using var host = await KestrelHubHost.StartAsync(clock: serverClock);
        var client = new SessionHubClient(host.BaseAddress, TimeProvider.System);
        await client.SignInAsync(7);

        await using var connection = client.CreateUserClient(7).CreateConnection("user-session-hub");
        var events = new EventRecorder(connection);
        await connection.StartAsync().WaitAsync(Timeout);
        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);
        await host.WaitUntilTrackedAsync(host.LastSessionId);

        host.SessionStore.Sessions.Clear();
        serverClock.Advance(RecheckDue);

        (await events.Closed.Task.WaitAsync(Timeout)).ShouldBeOfType<UnauthorizedApiException>().ErrorCode.ShouldBe(ApiErrorCodes.SignInRequired);
        connection.State.ShouldBe(HubConnectionState.Disconnected);
        events.Reconnecting.Task.IsCompleted.ShouldBeFalse();
        client.SessionEndedCount.ShouldBe(1);
        client.CurrentSessionToken.ShouldBeNull();
    }

    [TestMethod]
    public async Task Handlers_RunOnSynchronizationContext()
    {
        await using var host = await KestrelHubHost.StartAsync();
        using var context = new SingleThreadSynchronizationContext();
        await using var connection = new KestrelHubClient(host.BaseAddress, automaticReconnect: false).CreateLifetimeConnection();
        connection.SynchronizationContext = context;

        var notified = new TaskCompletionSource<SynchronizationContext?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource<SynchronizationContext?>(TaskCreationOptions.RunContinuationsAsynchronously);
        SynchronizationContext? askedOn = null;

        connection.On(LifetimeHubContract.Notified, (string _) => notified.TrySetResult(SynchronizationContext.Current));
        connection.On(LifetimeHubContract.Ask, async (string question) => {
            await Task.Yield();
            askedOn = SynchronizationContext.Current;
            return question + "!";
        });

        connection.Closed += _ => {
            closed.TrySetResult(SynchronizationContext.Current);
            return Task.CompletedTask;
        };

        await connection.StartAsync().WaitAsync(Timeout);
        await connection.SendAsync(LifetimeHubContract.SendNotification).WaitAsync(Timeout);

        (await notified.Task.WaitAsync(Timeout)).ShouldBeSameAs(context);
        (await connection.InvokeAsync(LifetimeHubContract.RelayQuestion).WaitAsync(Timeout)).ShouldBe("question!");
        askedOn.ShouldBeSameAs(context);

        await connection.StopAsync().WaitAsync(Timeout);

        (await closed.Task.WaitAsync(Timeout)).ShouldBeSameAs(context);
    }

    [TestMethod]
    public async Task Dispose_WebSocketThatFailsToClose_DoesNotThrow()
    {
        await using var host = await KestrelHubHost.StartAsync();
        var client = new KestrelHubClient(host.BaseAddress);

        var connection = client.CreateLifetimeConnection(options => options.WebSocketFactory = async (context, cancellationToken) => {
            var webSocket = new ClientWebSocket();
            await webSocket.ConnectAsync(context.Uri, cancellationToken);
            return new FailingCloseWebSocket(webSocket);
        });

        await connection.StartAsync().WaitAsync(Timeout);
        (await connection.InvokeAsync(LifetimeHubContract.Ping).WaitAsync(Timeout)).ShouldBe(1);

        await connection.DisposeAsync().AsTask().WaitAsync(Timeout);
    }

    /// <summary>
    /// Creates a connection to a hub of the host whose attempts to start again are timed with the specified time provider.
    /// </summary>
    private static ApiHubConnection CreateTimedConnection(KestrelHubHost host, string path, TimeProvider timeProvider)
    {
        var hubConnection = new HubConnectionBuilder()
            .WithUrl(new Uri(host.BaseAddress, path), options => {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
            })
            .Build();

        return new ApiHubConnection(hubConnection, apiErrorReceived: null, timeProvider: timeProvider);
    }

    /// <summary>
    /// Creates a connection whose connections come from the specified factory, which waits for the tasks that <paramref name="nextSessionStart"/> returns
    /// after its session ended, as if an API client had created it, and whose attempts to start again are timed with the specified time provider.
    /// </summary>
    private static ApiHubConnection CreateFailingConnection(
        FailingConnectionFactory factory, Func<Task> nextSessionStart, TimeProvider? timeProvider = null)
    {
        return new ApiHubConnection(CreateFailingHubConnection(factory), apiErrorReceived: null, nextSessionStart, timeProvider);
    }

    private static HubConnection CreateFailingHubConnection(FailingConnectionFactory factory) => new(
        factory, new JsonHubProtocol(), new UriEndPoint(new Uri("http://localhost/hub")), new ServiceCollection().BuildServiceProvider(),
        NullLoggerFactory.Instance);

    private static HubException SignInRequiredHubError() => new("401 [sign-in-required] Sign in again.");

    /// <summary>
    /// Ends the sessions on the service and aborts the connection, which has been connected for 30 seconds, so that it starts again right away. The service
    /// refuses that attempt, which ends the session on the device, and the next attempt waits for its delay of 2 seconds and a new session. Returns the
    /// number of connections that the session hubs had then.
    /// </summary>
    private static async Task<int> EndSessionAsync(KestrelHubHost host, SessionHubClient client, ApiHubConnection connection, ManualTimeProvider time)
    {
        int sessionsEnded = client.SessionEndedCount;
        int connects = host.HubConnects.Count;

        time.Advance(TimeSpan.FromSeconds(30));
        host.SessionStore.Sessions.Clear();
        await connection.SendAsync(SessionHubContract.Abort).WaitAsync(Timeout);

        await WaitUntilAsync(() =>
            client.SessionEndedCount == sessionsEnded + 1 && connection.State is HubConnectionState.Reconnecting && time.PendingTimerCount is 1);

        await Task.Delay(100);
        host.HubConnects.Count.ShouldBe(connects + 1);
        client.CurrentSessionToken.ShouldBeNull();

        return connects + 1;
    }

    /// <summary>
    /// Starts the connection of the second tab, whose client has the user ID precondition of user 7, has the first tab sign in as user 8, whose cookie the
    /// second tab gets, as the tabs of a browser share it, and waits until the connection's next check ends it and the hub refuses its try of the
    /// browser's cookie 5 seconds later, with another user's session. The connection then waits with the delay of 2 seconds after the refused try. Returns
    /// the number of connections that the session hubs had then.
    /// </summary>
    private static async Task<int> RefuseWithAnotherUsersSessionAsync(
        KestrelHubHost host, ManualClock serverClock, ManualTimeProvider time, SessionHubClient tab1, SessionHubClient tab2, ApiHubConnection connection,
        EventRecorder events)
    {
        connection.StayConnected = true;
        await connection.StartAsync().WaitAsync(Timeout);
        (await connection.InvokeAsync(SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);
        await host.WaitUntilTrackedAsync(host.LastSessionId);
        time.Advance(TimeSpan.FromSeconds(30));

        await tab1.SignInAsync(8);
        tab1.StartSession();
        await tab2.AdoptCookieAsync(tab1.CurrentSessionToken.ShouldNotBeNull());

        int connects = host.HubConnects.Count;
        serverClock.Advance(RecheckDue);
        (await events.Reconnecting.Task.WaitAsync(Timeout)).ShouldBeOfType<UnauthorizedApiException>().ErrorCode.ShouldBe(ApiErrorCodes.SignInRequired);
        await WaitUntilAsync(() => time.PendingTimerCount is 1);
        time.Advance(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => host.HubConnects.Count == connects + 1 && time.PendingTimerCount is 1);

        return connects + 1;
    }

    /// <summary>
    /// Counts the times that the connection reports that it reconnected, from now on.
    /// </summary>
    private static Func<int> CountReconnected(ApiHubConnection connection)
    {
        int reconnected = 0;

        connection.Reconnected += _ => {
            Interlocked.Increment(ref reconnected);
            return Task.CompletedTask;
        };

        return () => Volatile.Read(ref reconnected);
    }

    /// <summary>
    /// Returns a task that completes when the connection next reports that it reconnected.
    /// </summary>
    private static Task NextReconnected(ApiHubConnection connection)
    {
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        connection.Reconnected += _ => {
            reconnected.TrySetResult();
            return Task.CompletedTask;
        };

        return reconnected.Task;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Timeout);

        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    /// <summary>
    /// Records the first occurrence of each event of a connection.
    /// </summary>
    private sealed class EventRecorder
    {
        public EventRecorder(ApiHubConnection connection)
        {
            connection.Closed += error => {
                Closed.TrySetResult(error);
                return Task.CompletedTask;
            };

            connection.Reconnecting += error => {
                Reconnecting.TrySetResult(error);
                return Task.CompletedTask;
            };

            connection.Reconnected += connectionId => {
                Reconnected.TrySetResult(connectionId);
                return Task.CompletedTask;
            };
        }

        public TaskCompletionSource<Exception?> Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<Exception?> Reconnecting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<string?> Reconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// A web application on Kestrel with the lifetime hubs, which can answer the first WebSocket handshakes with a 503, and with session handling, a sign-in
    /// endpoint (<c>sign-in/{userId}</c>), a sign-out endpoint (<c>sign-out</c>), endpoints that answer requests with and without the session
    /// (<c>whoami</c>, <c>ping</c> and <c>gated-ping</c>) and the session hubs. Session handling runs on the specified clock, or the
    /// system's, and the hub payloads use the specified type info resolver, or reflection.
    /// </summary>
    private sealed class KestrelHubHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private int _handshakeCount;
        private long _lastSessionId;
        private volatile bool _rejectsHandshakes;
        private string? _lastHandshakeAuthorization;

        private KestrelHubHost(WebApplication app)
        {
            _app = app;
        }

        public Uri BaseAddress => new(_app.Urls.First());

        public int HandshakeCount => Volatile.Read(ref _handshakeCount);

        /// <summary>
        /// Gets or sets a value indicating whether the host refuses the WebSocket handshakes, with a 503.
        /// </summary>
        public bool RejectsHandshakes { get => _rejectsHandshakes; set => _rejectsHandshakes = value; }

        /// <summary>
        /// Gets the <c>Authorization</c> header of the last WebSocket handshake, if it had one.
        /// </summary>
        public string? LastHandshakeAuthorization => Volatile.Read(ref _lastHandshakeAuthorization);

        /// <summary>
        /// Gets the task source that the gated ping endpoint (<c>gated-ping</c>) waits for before it responds.
        /// </summary>
        public TaskCompletionSource PingGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Gets the task source that completes when the gated ping endpoint receives a request.
        /// </summary>
        public TaskCompletionSource GatedPingReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HubConnects HubConnects => _app.Services.GetRequiredService<HubConnects>();

        public InMemorySessionStore SessionStore => _app.Services.GetRequiredService<InMemorySessionStore>();

        public long LastSessionId => Interlocked.Read(ref _lastSessionId);

        /// <summary>
        /// Waits until the service tracks the specified number of connections of the session, which it does after the hub's <c>OnConnectedAsync</c>, so
        /// that a sign-in or a revalidation that follows reaches them.
        /// </summary>
        public Task WaitUntilTrackedAsync(long sessionId, int count = 1)
        {
            var tracker = _app.Services.GetRequiredService<SessionConnectionTracker<TestSessionToken>>();
            return WaitUntilAsync(() => tracker.CountTracked(token => token.SessionId == sessionId) >= count);
        }

        public static async Task<KestrelHubHost> StartAsync(
            int rejectedHandshakes = 0, TimeProvider? clock = null, IJsonTypeInfoResolver? payloadResolver = null)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();

            if (clock is not null)
                builder.Services.AddSingleton(clock);

            var signalR = builder.Services.AddSignalR(o => o.AddApiExceptionFilter());

            if (payloadResolver is not null)
                signalR.AddJsonProtocol(o => o.PayloadSerializerOptions.TypeInfoResolver = payloadResolver);

            builder.Services.AddSingleton<HubConnects>();
            builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            builder.Services.AddSingleton<IOriginValidator>(new OriginValidator("localhost"));
            builder.Services.AddSingleton<InMemorySessionStore>();
            builder.Services.AddHttpSessionHandling<TestSessionToken, ScopedInMemorySessionStore>();

            var app = builder.Build();
            var host = new KestrelHubHost(app);

            app.UseWebSockets();
            app.Use((context, next) => {
                if (!context.WebSockets.IsWebSocketRequest)
                    return next(context);

                Volatile.Write(ref host._lastHandshakeAuthorization, context.Request.Headers.Authorization.FirstOrDefault());

                if (Interlocked.Increment(ref host._handshakeCount) <= rejectedHandshakes || host.RejectsHandshakes)
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return Task.CompletedTask;
                }

                return next(context);
            });

            app.MapHub<LifetimeHub>("/hub");
            app.MapHub<RejectingHub>("/rejecting-hub");
            app.MapHub<BusyHub>("/busy-hub");
            app.MapHub<AlwaysBusyHub>("/always-busy-hub");
            app.MapHub<BusyThenRejectingHub>("/busy-then-rejecting-hub");
            app.MapHub<DroppingHub>("/dropping-hub");
            app.MapHub<UserSessionHub>("/user-session-hub");
            app.MapHub<AnyUserSessionHub>("/any-user-session-hub");

            app.MapPost("/sign-in/{userId:int}", async (HttpContext context, int userId, InMemorySessionStore store) => {
                long sessionId = Interlocked.Increment(ref host._lastSessionId);
                store.UserStamps.TryAdd(userId, 1);

                await context.GetRequiredSessionContext<TestSessionToken>().SignInAsync(persistent: true, info => {
                    store.Sessions[sessionId] = new TestSessionData {
                        Id = sessionId,
                        UserId = userId,
                        Device = info.Device,
                        IpAddress = info.IpAddress,
                        RefreshedUtc = info.RefreshedUtc,
                        ExpiresUtc = info.ExpiresUtc,
                        Generation = info.Generation,
                        IsPersistent = info.IsPersistent,
                        CreatedUtc = info.CreatedUtc,
                    };

                    return Task.FromResult(new TestSessionToken(sessionId, userId, Stamp: 1));
                });
            });

            app.MapPost("/sign-out", (HttpContext context) => context.GetRequiredSessionContext<TestSessionToken>().SignOutAsync());

            // Retrieves the session, which checks the request's user ID precondition, and returns its user's ID.
            app.MapGet("/whoami", async (HttpContext context) => (await context.GetRequiredSessionTokenAsync<TestSessionToken>()).UserId);

            // Succeed without retrieving the session, whatever the request's user ID precondition, the second once the gate opens.
            app.MapPost("/ping", () => Results.NoContent());
            app.MapPost("/gated-ping", async () => {
                host.GatedPingReceived.TrySetResult();
                await host.PingGate.Task;
                return Results.NoContent();
            });

            // Sets the specified session cookie, as the browser's cookie jar gives one tab the cookie that another tab's sign-in set.
            app.MapPost("/adopt-cookie", (HttpContext context, string value) => {
                string name = context.RequestServices.GetRequiredService<IOptions<SessionHandlingOptions>>().Value.SessionCookieName;
                context.Response.Headers.Append("Set-Cookie", $"{name}={value}; max-age=2592000; path=/; secure; samesite=none; httponly");
            });

            await app.StartAsync();
            return host;
        }

        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }

    private sealed class KestrelHubClient(Uri baseAddress, bool automaticReconnect = true) : SignalRApiClientBase()
    {
        public ApiHubConnection CreateLifetimeConnection(
            Action<Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions>? configureOptions = null) =>
            CreateHubConnection("hub", configureOptions);

        public ApiHubConnection CreateConnection(string path) => CreateHubConnection(path);

        protected override string UserAgent => "KestrelHubClient/1.0";

        protected override IHubConnectionBuilder CreateHubConnectionBuilder()
        {
            if (automaticReconnect)
                return base.CreateHubConnectionBuilder();

            return new HubConnectionBuilder().AddJsonProtocol(options => options.PayloadSerializerOptions = SerializerOptions);
        }

        protected override Uri GetBaseAddress() => baseAddress;
    }

    /// <summary>
    /// A client of the session hubs, which signs in through the host's sign-in endpoint. The connections that it and its child clients create reconnect
    /// automatically only if specified, and their attempts to start again are timed with the specified time provider. Payloads use reflection, or the
    /// specified type info resolver.
    /// </summary>
    private sealed class SessionHubClient : SignalRApiClientBase
    {
        private readonly Uri? _baseAddress;
        private readonly bool _automaticReconnect;
        private int _sessionEndedCount;

        public SessionHubClient(Uri baseAddress, TimeProvider timeProvider, bool automaticReconnect = false)
        {
            _baseAddress = baseAddress;
            _automaticReconnect = automaticReconnect;
            HubConnectionTimeProvider = timeProvider;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SessionHubClient"/> class whose hub connections take the browser's path for the session-ended
        /// reports (see <see cref="SignalRApiClientBase.UsesBrowserSessionPath"/>), with the specified session token, such as the one of another client, as
        /// two tabs of a browser share its cookie.
        /// </summary>
        public SessionHubClient(Uri baseAddress, TimeProvider timeProvider, bool automaticReconnect, bool browserPath, string? sessionToken = null)
            : base(sessionToken, null)
        {
            _baseAddress = baseAddress;
            _automaticReconnect = automaticReconnect;
            HubConnectionTimeProvider = timeProvider;
            UsesBrowserSessionPath = browserPath;
        }

        public SessionHubClient(Uri baseAddress, TimeProvider timeProvider, IJsonTypeInfoResolver payloadResolver) : base(payloadResolver)
        {
            _baseAddress = baseAddress;
            HubConnectionTimeProvider = timeProvider;
        }

        private SessionHubClient(SessionHubClient parent, int userId) : base(parent, userIdPrecondition: userId)
        {
            _automaticReconnect = parent._automaticReconnect;
            HubConnectionTimeProvider = parent.HubConnectionTimeProvider;
            UsesBrowserSessionPath = parent.UsesBrowserSessionPath;
        }

        public int SessionEndedCount => Volatile.Read(ref _sessionEndedCount);

        public string? CurrentSessionToken => SessionToken;

        protected override string UserAgent => "SessionHubClient/1.0";

        public SessionHubClient CreateUserClient(int userId) => new(this, userId);

        public ApiHubConnection CreateConnection(string path) => CreateHubConnection(path);

        public ApiHubConnection CreateConnection(string path, Action<Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions> configureOptions) =>
            CreateHubConnection(path, configureOptions);

        public Task SignInAsync(int userId) => PostAsync($"sign-in/{userId}");

        public Task SignOutAsync() => PostAsync("sign-out");

        public Task<int> WhoAmIAsync() => GetAsync<int>("whoami");

        public Task PingAsync() => PostAsync("ping");

        public Task GatedPingAsync() => PostAsync("gated-ping");

        public Task AdoptCookieAsync(string sessionToken) => PostAsync("adopt-cookie", null, [("value", sessionToken)]);

        public void ClearSession() => ClearSessionToken();

        public void StartSession() => NotifySessionStarted();

        protected override IHubConnectionBuilder CreateHubConnectionBuilder() => _automaticReconnect
            ? base.CreateHubConnectionBuilder()
            : new HubConnectionBuilder().AddJsonProtocol(options => options.PayloadSerializerOptions = SerializerOptions);

        protected override Uri GetBaseAddress() => _baseAddress ?? base.GetBaseAddress();

        protected override void OnSessionEnded() => Interlocked.Increment(ref _sessionEndedCount);
    }

    /// <summary>
    /// A connection factory whose connections fail with the specified errors, one per attempt, and then with the last one.
    /// </summary>
    private sealed class FailingConnectionFactory(params Exception[] errors) : IConnectionFactory
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public ValueTask<ConnectionContext> ConnectAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
        {
            int attempt = Interlocked.Increment(ref _attempts);
            return ValueTask.FromException<ConnectionContext>(errors[Math.Min(attempt, errors.Length) - 1]);
        }
    }

    /// <summary>
    /// A synchronization context that runs the callbacks posted to it, one at a time, on a thread of its own.
    /// </summary>
    private sealed class SingleThreadSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];

        public SingleThreadSynchronizationContext()
        {
            var thread = new Thread(() => {
                SetSynchronizationContext(this);

                foreach (var (callback, state) in _queue.GetConsumingEnumerable())
                    callback(state);
            }) { IsBackground = true };

            thread.Start();
        }

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        public void Dispose() => _queue.CompleteAdding();
    }

    /// <summary>
    /// A WebSocket whose close handshake and disposal fail.
    /// </summary>
    private sealed class FailingCloseWebSocket(WebSocket webSocket) : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => webSocket.CloseStatus;

        public override string? CloseStatusDescription => webSocket.CloseStatusDescription;

        public override WebSocketState State => webSocket.State;

        public override string? SubProtocol => webSocket.SubProtocol;

        public override void Abort() => webSocket.Abort();

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            webSocket.ReceiveAsync(buffer, cancellationToken);

        public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            webSocket.ReceiveAsync(buffer, cancellationToken);

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            webSocket.SendAsync(buffer, messageType, endOfMessage, cancellationToken);

        public override ValueTask SendAsync(
            ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            webSocket.SendAsync(buffer, messageType, endOfMessage, cancellationToken);

        public override void Dispose()
        {
            webSocket.Dispose();
            throw new WebSocketException(WebSocketError.InvalidState);
        }
    }
}
