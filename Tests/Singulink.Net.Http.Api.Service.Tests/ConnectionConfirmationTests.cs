using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Singulink.Net.Http.Api.Client;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Tests the message with which the toolkit's service confirms that a hub accepted a connection, which <see cref="ApiHubConnection"/> waits for before it
/// counts the connection as connected, and which other SignalR clients, such as the ones of earlier versions of the toolkit, ignore.
/// </summary>
[PrefixTestClass]
public sealed class ConnectionConfirmationTests
{
    // The name of the message on the wire, which earlier clients receive too.
    private const string ConfirmationMessage = "Singulink.Net.Http.Api.Connected";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static class GatedHubContract
    {
        public static HubMethod<int> Ping { get; } = new();
    }

    /// <summary>
    /// A hub whose <c>OnConnectedAsync</c> waits for the gate, and then refuses the connection if the gate says so.
    /// </summary>
    public sealed class GatedHub(HubGate gate) : Hub
    {
        public override async Task OnConnectedAsync()
        {
            gate.Entered.TrySetResult();

            if (await gate.Opened.Task)
                throw new ForbiddenApiException("Not allowed here.");
        }

        public int Ping() => 1;
    }

    /// <summary>
    /// A hub that refuses the first connection with a transient error, makes the second wait for the gate in its <c>OnConnectedAsync</c>, and accepts the
    /// others.
    /// </summary>
    public sealed class SequencedHub(HubGate gate) : Hub
    {
        public override async Task OnConnectedAsync()
        {
            int connect = Interlocked.Increment(ref gate.Connects);

            if (connect is 1)
                throw new ServiceUnavailableApiException("Busy.");

            if (connect is 2)
            {
                gate.Entered.TrySetResult();
                await gate.Opened.Task;
            }
        }

        public int Ping() => 1;
    }

    public sealed class HubGate
    {
#pragma warning disable SA1401 // Fields should be private
        public int Connects;
#pragma warning restore SA1401

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Its result says whether the hub refuses the connection.
        public TaskCompletionSource<bool> Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Open(bool refuse = false) => Opened.TrySetResult(refuse);
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Service_ConfirmsOnceTheHubsOnConnectedAsyncSucceeded_OnceWhicheverRegistrationsAddIt(bool responseHandling, bool sessionHandling)
    {
        await using var app = await StartHostAsync(responseHandling, sessionHandling);
        var gate = app.Services.GetRequiredService<HubGate>();
        await using var connection = CreateHubConnection(app);
        int confirmations = 0;
        connection.On(ConfirmationMessage, () => Interlocked.Increment(ref confirmations));

        await connection.StartAsync().WaitAsync(Timeout);
        await gate.Entered.Task.WaitAsync(Timeout);
        await Task.Delay(100);
        Volatile.Read(ref confirmations).ShouldBe(0);

        gate.Open();
        await WaitUntilAsync(() => Volatile.Read(ref confirmations) > 0);
        (await connection.InvokeAsync<int>(GatedHubContract.Ping.Name).WaitAsync(Timeout)).ShouldBe(1);

        Volatile.Read(ref confirmations).ShouldBe(1);
    }

    [TestMethod]
    public async Task Service_DoesNotConfirmAConnectionThatTheHubRefuses()
    {
        await using var app = await StartHostAsync(responseHandling: true, sessionHandling: false);
        app.Services.GetRequiredService<HubGate>().Open(refuse: true);
        await using var connection = CreateHubConnection(app);
        int confirmations = 0;
        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

        connection.On(ConfirmationMessage, () => Interlocked.Increment(ref confirmations));
        connection.Closed += error => {
            closed.TrySetResult(error);
            return Task.CompletedTask;
        };

        await connection.StartAsync().WaitAsync(Timeout);

        (await closed.Task.WaitAsync(Timeout)).ShouldBeOfType<HubException>();
        Volatile.Read(ref confirmations).ShouldBe(0);
    }

    [TestMethod]
    public async Task ClientWithoutAHandler_IgnoresTheConfirmation()
    {
        // A client of an earlier version of the toolkit, or another SignalR client, has no handler for the message.
        await using var app = await StartHostAsync(responseHandling: true, sessionHandling: true);
        app.Services.GetRequiredService<HubGate>().Open();
        await using var connection = CreateHubConnection(app);

        await connection.StartAsync().WaitAsync(Timeout);

        (await connection.InvokeAsync<int>(GatedHubContract.Ping.Name).WaitAsync(Timeout)).ShouldBe(1);
        await Task.Delay(200);
        connection.State.ShouldBe(HubConnectionState.Connected);
        (await connection.InvokeAsync<int>(GatedHubContract.Ping.Name).WaitAsync(Timeout)).ShouldBe(1);
    }

    [TestMethod]
    public async Task Start_CompletesOnceTheHubAcceptedTheConnection()
    {
        await using var app = await StartHostAsync(responseHandling: true, sessionHandling: false);
        var gate = app.Services.GetRequiredService<HubGate>();
        await using var connection = new ApiHubConnection(CreateHubConnection(app));

        var start = connection.StartAsync();
        await gate.Entered.Task.WaitAsync(Timeout);

        // The handshake has completed, and the hub hasn't accepted the connection yet.
        await Task.Delay(100);
        start.IsCompleted.ShouldBeFalse();
        connection.UnderlyingConnection.State.ShouldBe(HubConnectionState.Connected);
        connection.State.ShouldBe(HubConnectionState.Connecting);

        gate.Open();
        await start.WaitAsync(Timeout);

        connection.State.ShouldBe(HubConnectionState.Connected);
        (await connection.InvokeAsync(GatedHubContract.Ping).WaitAsync(Timeout)).ShouldBe(1);
    }

    [TestMethod]
    public async Task StartCancelledWhileTheHubConnects_StopsTheConnection()
    {
        await using var app = await StartHostAsync(responseHandling: true, sessionHandling: false);
        var gate = app.Services.GetRequiredService<HubGate>();
        await using var connection = new ApiHubConnection(CreateHubConnection(app));
        int closedCount = 0;
        connection.StayConnected = true;

        connection.Closed += _ => {
            Interlocked.Increment(ref closedCount);
            return Task.CompletedTask;
        };

        using var cancellation = new CancellationTokenSource();
        var start = connection.StartAsync(cancellation.Token);
        await gate.Entered.Task.WaitAsync(Timeout);
        await Task.Delay(100);
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => start.WaitAsync(Timeout));
        connection.State.ShouldBe(HubConnectionState.Disconnected);

        // The confirmation of the stopped connection doesn't count, and its close isn't reported or followed by another attempt.
        gate.Open();
        await Task.Delay(200);
        Volatile.Read(ref closedCount).ShouldBe(0);
        connection.State.ShouldBe(HubConnectionState.Disconnected);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StartOrStopWhileAnAttemptAwaitsItsConfirmation_StopsTheAttemptsConnection(bool start)
    {
        await using var app = await StartHostAsync(responseHandling: true, sessionHandling: false);
        var gate = app.Services.GetRequiredService<HubGate>();
        var time = new ManualTimeProvider(manualTimers: true);
        await using var connection = new ApiHubConnection(CreateHubConnection(app, "sequenced-hub"), apiErrorReceived: null, timeProvider: time);
        int reconnected = 0;
        var closed = new List<Exception?>();
        connection.StayConnected = true;

        connection.Reconnected += _ => {
            Interlocked.Increment(ref reconnected);
            return Task.CompletedTask;
        };

        connection.Closed += error => {
            lock (closed)
                closed.Add(error);

            return Task.CompletedTask;
        };

        // The first start is refused with a transient error, and the attempt after it, 2 seconds later, waits for the hub to accept its connection.
        await connection.StartAsync().WaitAsync(Timeout);
        await WaitUntilAsync(() => time.PendingTimerCount is 1);
        time.Advance(TimeSpan.FromSeconds(2));
        await gate.Entered.Task.WaitAsync(Timeout);

        if (start)
        {
            // The app's start takes the place of the attempts: the attempt's connection is stopped, and the start connects again.
            await connection.StartAsync().WaitAsync(Timeout);

            connection.State.ShouldBe(HubConnectionState.Connected);
            (await connection.InvokeAsync(GatedHubContract.Ping).WaitAsync(Timeout)).ShouldBe(1);
            Volatile.Read(ref gate.Connects).ShouldBe(3);
            await WaitUntilAsync(() => Volatile.Read(ref reconnected) is 1);
        }
        else
        {
            await connection.StopAsync().WaitAsync(Timeout);

            connection.State.ShouldBe(HubConnectionState.Disconnected);
            await WaitUntilAsync(() => { lock (closed) return closed.Count is 1; });
        }

        // The hub's acceptance of the stopped connection changes nothing.
        gate.Open();
        await Task.Delay(200);

        Volatile.Read(ref reconnected).ShouldBe(start ? 1 : 0);

        lock (closed)
            closed.Count.ShouldBe(start ? 0 : 1);

        connection.State.ShouldBe(start ? HubConnectionState.Connected : HubConnectionState.Disconnected);
    }

    [TestMethod]
    public async Task StartRightAfterAStop_IsNotFailedByTheStoppedConnectionsClose()
    {
        await using var app = await StartHostAsync(responseHandling: true, sessionHandling: false);
        app.Services.GetRequiredService<HubGate>().Open();
        await using var connection = new ApiHubConnection(CreateHubConnection(app));
        int closedCount = 0;

        connection.Closed += _ => {
            Interlocked.Increment(ref closedCount);
            return Task.CompletedTask;
        };

        // SignalR raises the close event of a stopped connection after its stop returns, so the start that follows the stop can come first.
        for (int i = 1; i <= 20; i++)
        {
            await connection.StartAsync().WaitAsync(Timeout);
            (await connection.InvokeAsync(GatedHubContract.Ping).WaitAsync(Timeout)).ShouldBe(1);
            await connection.StopAsync().WaitAsync(Timeout);
        }

        await WaitUntilAsync(() => Volatile.Read(ref closedCount) is 20);
    }

    private static async Task<WebApplication> StartHostAsync(bool responseHandling, bool sessionHandling)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<HubGate>();
        builder.Services.AddSignalR(o => o.AddApiExceptionFilter());

        if (responseHandling)
            builder.Services.AddApiResponseHandling();

        if (sessionHandling)
        {
            builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            builder.Services.AddSingleton<IOriginValidator>(new OriginValidator("localhost"));
            builder.Services.AddSingleton<InMemorySessionStore>();
            builder.Services.AddHttpSessionHandling<TestSessionToken, ScopedInMemorySessionStore>();
        }

        var app = builder.Build();

        if (responseHandling)
            app.UseApiResponseHandling();

        app.MapHub<GatedHub>("/gated-hub");
        app.MapHub<SequencedHub>("/sequenced-hub");
        await app.StartAsync();

        return app;
    }

    private static HubConnection CreateHubConnection(WebApplication app, string path = "gated-hub")
    {
        var server = app.GetTestServer();

        return new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, path), options => {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Timeout);

        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}
