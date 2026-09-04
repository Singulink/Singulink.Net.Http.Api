using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Singulink.Net.Http.Api.Client;

namespace Singulink.Net.Http.Api.Service;

[PrefixTestClass]
public sealed class ApiHubConnectionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Shared contract between the test hub and its clients (as would be declared in a data contracts assembly).
    /// </summary>
    public static class TestHubContract
    {
        // Client to server:
        public static HubMessage Ping { get; } = new();

        public static HubMessage<string> Echo { get; } = new();

        public static HubMethod<int, int> Double { get; } = new();

        public static HubMethod<string, int, string> Repeat { get; } = new();

        public static HubMethod<string> RelayAnswer { get; } = new();

        public static HubMethod<IAsyncEnumerable<int>, int> Sum { get; } = new();

        public static HubMessage<string> FailWith { get; } = new();

        public static HubMethod<string, int> FailMethodWith { get; } = new();

        // Server to client:
        public static HubMessage Pinged { get; } = new();

        public static HubMessage<string, int> Echoed { get; } = new();

        public static HubMethod<string, string> AskClient { get; } = new();

        // Server to client streams:
        public static HubStream<int, int> Count { get; } = new();

        public static HubStream<int, string, int> CountThenFail { get; } = new();

        // Naming variants:
        public static readonly HubMessage<int> FieldDefined = new();

        public static HubMessage<int> Explicit { get; } = new("custom-name");
    }

    public sealed class TestHub : Hub
    {
        public Task Ping() => Clients.Caller.SendAsync(TestHubContract.Pinged);

        public Task Echo(string text) => Clients.Caller.SendAsync(TestHubContract.Echoed, text, text.Length);

        public int Double(int value) => value * 2;

        public string Repeat(string text, int count) => string.Concat(Enumerable.Repeat(text, count));

        public Task<string> RelayAnswer() => Clients.Caller.InvokeAsync(TestHubContract.AskClient, "question", Context.ConnectionAborted);

        public async Task<int> Sum(IAsyncEnumerable<int> values)
        {
            int sum = 0;

            await foreach (int value in values)
                sum += value;

            return sum;
        }

        public Task FailWith(string kind) => throw CreateException(kind);

        public int FailMethodWith(string kind) => throw CreateException(kind);

        public async IAsyncEnumerable<int> Count(int to, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (int i = 1; i <= to; i++)
            {
                await Task.Yield();
                yield return i;
            }
        }

        public async IAsyncEnumerable<int> CountThenFail(int to, string kind, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (int i in Count(to, cancellationToken))
                yield return i;

            throw CreateException(kind);
        }

        private static Exception CreateException(string kind) => kind switch {
            "forbidden" => new ForbiddenApiException("no access") { ErrorCode = "denied" },
            "notfound" => new NotFoundApiException("missing"),
            _ => new InvalidOperationException("secret details"),
        };
    }

    public sealed class RejectingHub : Hub
    {
        public override Task OnConnectedAsync() => throw new ForbiddenApiException("not allowed here") { ErrorCode = "denied" };
    }

    public static class SessionHubContract
    {
        public static HubMethod<int> WhoAmI { get; } = new();
    }

    public sealed class SessionHub : Hub
    {
        public override async Task OnConnectedAsync()
        {
            var token = await Context.GetRequiredSessionTokenAsync<TestSessionToken>(SessionAccessOptions.OptionalUserIdPrecondition);
            Context.Items["UserId"] = token.UserId;
        }

        public int WhoAmI() => (int)Context.Items["UserId"]!;
    }

    [TestMethod]
    public void Names_AreDerivedFromDeclaringMember()
    {
        TestHubContract.Ping.Name.ShouldBe("Ping");
        TestHubContract.Echo.Name.ShouldBe("Echo");
        TestHubContract.Echoed.Name.ShouldBe("Echoed");
        TestHubContract.Double.Name.ShouldBe("Double");
        TestHubContract.Repeat.Name.ShouldBe("Repeat");
        TestHubContract.AskClient.Name.ShouldBe("AskClient");
        TestHubContract.Count.Name.ShouldBe("Count");
        TestHubContract.FieldDefined.Name.ShouldBe("FieldDefined");
        TestHubContract.Explicit.Name.ShouldBe("custom-name");
        TestHubContract.Echo.ToString().ShouldBe("Echo");
    }

    [TestMethod]
    public void EmptyName_Throws()
    {
        Should.Throw<ArgumentException>(() => new HubMessage(string.Empty));
        Should.Throw<ArgumentException>(() => new HubMethod<int>(string.Empty));
        Should.Throw<ArgumentException>(() => new HubStream<int>(string.Empty));
    }

    [TestMethod]
    public async Task Message_ClientToServer_AndServerToClient()
    {
        await using var host = await StartHubHostAsync();
        await using var connection = await ConnectAsync(host);

        var echoed = new TaskCompletionSource<(string Text, int Length)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pinged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var echoedHandler = connection.On(TestHubContract.Echoed, (string text, int length) => echoed.TrySetResult((text, length)));
        using var pingedHandler = connection.On(TestHubContract.Pinged, () => pinged.TrySetResult());

        await connection.SendAsync(TestHubContract.Echo, "hello");
        (await echoed.Task.WaitAsync(Timeout)).ShouldBe(("hello", 5));

        await connection.InvokeAsync(TestHubContract.Ping);
        await pinged.Task.WaitAsync(Timeout);
    }

    [TestMethod]
    public async Task Method_ClientToServer_ReturnsResult()
    {
        await using var host = await StartHubHostAsync();
        await using var connection = await ConnectAsync(host);

        (await connection.InvokeAsync(TestHubContract.Double, 21)).ShouldBe(42);
        (await connection.InvokeAsync(TestHubContract.Repeat, "ab", 3)).ShouldBe("ababab");
    }

    [TestMethod]
    public async Task Method_ServerToClient_ReturnsClientResult()
    {
        await using var host = await StartHubHostAsync();
        await using var connection = await ConnectAsync(host);

        using var handler = connection.On(TestHubContract.AskClient, (string question) => question + "!");
        (await connection.InvokeAsync(TestHubContract.RelayAnswer)).ShouldBe("question!");
    }

    [TestMethod]
    public async Task Method_ServerToClient_AsyncHandler()
    {
        await using var host = await StartHubHostAsync();
        await using var connection = await ConnectAsync(host);

        using var handler = connection.On(TestHubContract.AskClient, async (string question) => {
            await Task.Yield();
            return question.ToUpperInvariant();
        });

        (await connection.InvokeAsync(TestHubContract.RelayAnswer)).ShouldBe("QUESTION");
    }

    [TestMethod]
    public async Task Stream_ServerToClient()
    {
        await using var host = await StartHubHostAsync();
        await using var connection = await ConnectAsync(host);

        var items = new List<int>();

        await foreach (int item in connection.StreamAsync(TestHubContract.Count, 4))
            items.Add(item);

        items.ShouldBe([1, 2, 3, 4]);
    }

    [TestMethod]
    public async Task Stream_ClientToServer_ViaEnumerableArgument()
    {
        await using var host = await StartHubHostAsync();
        await using var connection = await ConnectAsync(host);

        static async IAsyncEnumerable<int> ValuesAsync()
        {
            for (int i = 1; i <= 5; i++)
            {
                await Task.Yield();
                yield return i;
            }
        }

        (await connection.InvokeAsync(TestHubContract.Sum, ValuesAsync())).ShouldBe(15);
    }

    [TestMethod]
    public async Task ApiException_FromHubMessage_IsReportedAsTypedException()
    {
        await using var host = await StartHubHostAsync();
        await using var connection = await ConnectAsync(host);

        var ex = await Should.ThrowAsync<ForbiddenApiException>(() => connection.InvokeAsync(TestHubContract.FailWith, "forbidden"));
        ex.Message.ShouldBe("no access");
        ex.ErrorCode.ShouldBe("denied");

        var notFound = await Should.ThrowAsync<NotFoundApiException>(() => connection.InvokeAsync(TestHubContract.FailMethodWith, "notfound"));
        notFound.Message.ShouldBe("missing");
        notFound.ErrorCode.ShouldBeNull();

        // The connection is still usable afterwards.
        (await connection.InvokeAsync(TestHubContract.Double, 1)).ShouldBe(2);
    }

    [TestMethod]
    public async Task UnexpectedException_WithHandler_IsLoggedAndReportedAsServerError()
    {
        await using var host = await StartHubHostAsync(s => s.AddApiExceptionHandler());
        await using var connection = await ConnectAsync(host);

        var ex = await Should.ThrowAsync<ServerErrorApiException>(() => connection.InvokeAsync(TestHubContract.FailMethodWith, "unexpected"));
        ex.Message.ShouldStartWith("An unexpected error has occurred and has been logged. Reference ID: ");
        ex.Message.ShouldNotContain("secret details");

        var log = host.WarningLogs.Where(l => l.Category.Contains("DefaultApiExceptionHandler")).ShouldHaveSingleItem();
        log.Level.ShouldBe(LogLevel.Error);
        log.Exception.ShouldBeOfType<InvalidOperationException>();
    }

    [TestMethod]
    public async Task UnexpectedException_WithoutHandler_IsReportedAsHubException()
    {
        await using var host = await StartHubHostAsync();
        await using var connection = await ConnectAsync(host);

        var ex = await Should.ThrowAsync<HubException>(() => connection.InvokeAsync(TestHubContract.FailMethodWith, "unexpected"));
        ex.Message.ShouldNotContain("secret details");
    }

    [TestMethod]
    public async Task ApiException_DuringStream_IsReportedAfterItems()
    {
        await using var host = await StartHubHostAsync();
        await using var connection = await ConnectAsync(host);

        var items = new List<int>();

        var ex = await Should.ThrowAsync<ForbiddenApiException>(async () => {
            await foreach (int item in connection.StreamAsync(TestHubContract.CountThenFail, 2, "forbidden"))
                items.Add(item);
        });

        ex.ErrorCode.ShouldBe("denied");
        items.ShouldBe([1, 2]);
    }

    [TestMethod]
    public async Task ApiException_WhileConnecting_IsReportedViaClosedEvent()
    {
        await using var host = await StartHubHostAsync();
        await using var connection = CreateConnection(host, "rejecting-hub");

        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += error => {
            closed.TrySetResult(error);
            return Task.CompletedTask;
        };

        // The handshake completes before OnConnectedAsync runs, so the failure surfaces as the connection closing with the error.
        await connection.StartAsync();

        var ex = (await closed.Task.WaitAsync(Timeout)).ShouldBeOfType<ForbiddenApiException>();
        ex.Message.ShouldBe("not allowed here");
        ex.ErrorCode.ShouldBe("denied");
        connection.State.ShouldBe(HubConnectionState.Disconnected);
    }

    [TestMethod]
    public async Task Closed_WithoutError_IsRaisedOnStop()
    {
        await using var host = await StartHubHostAsync();
        await using var connection = await ConnectAsync(host);

        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += error => {
            closed.TrySetResult(error);
            return Task.CompletedTask;
        };

        connection.ConnectionId.ShouldNotBeNull();
        await connection.StopAsync();

        (await closed.Task.WaitAsync(Timeout)).ShouldBeNull();
        connection.State.ShouldBe(HubConnectionState.Disconnected);
    }

    [TestMethod]
    public async Task SessionToken_FromHubCallerContext()
    {
        var store = new InMemorySessionStore();
        await using var host = await StartHubHostAsync(s => {
            s.AddSingleton<IOriginValidator>(new OriginValidator("localhost"));
            s.AddSingleton(store);
            s.AddHttpSessionHandling<TestSessionToken, TestSessionData, InMemorySessionStoreFactory>();
        });

        var utcNow = DateTime.UtcNow;
        store.UserStamps[7] = 1;
        store.Sessions[1] = new TestSessionData { Id = 1, UserId = 7, Device = "TestClient/1.0", RefreshedUtc = utcNow, ValidFor = TimeSpan.FromDays(30), IsPersistent = true };
        var token = new TestSessionToken(1, 7, 1, utcNow, TimeSpan.FromDays(30), 0, true, 0);

        var protector = host.App.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector($"Singulink/Session[{typeof(TestSessionToken).FullName}]");
        string cookie = protector.Protect(JsonSerializer.Serialize(token));

        await using var connection = CreateConnection(host, "session-hub", options => {
            options.Headers["Cookie"] = $"session-token={cookie}";
            options.Headers["User-Agent"] = "TestClient/1.0";
        });

        await connection.StartAsync();
        (await connection.InvokeAsync(SessionHubContract.WhoAmI)).ShouldBe(7);
    }

    [TestMethod]
    public async Task SessionToken_FromHubCallerContext_NotSignedIn_ClosesWithUnauthorized()
    {
        var store = new InMemorySessionStore();
        await using var host = await StartHubHostAsync(s => {
            s.AddSingleton<IOriginValidator>(new OriginValidator("localhost"));
            s.AddSingleton(store);
            s.AddHttpSessionHandling<TestSessionToken, TestSessionData, InMemorySessionStoreFactory>();
        });

        await using var connection = CreateConnection(host, "session-hub", options => options.Headers["User-Agent"] = "TestClient/1.0");

        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += error => {
            closed.TrySetResult(error);
            return Task.CompletedTask;
        };

        await connection.StartAsync();

        (await closed.Task.WaitAsync(Timeout)).ShouldBeOfType<UnauthorizedApiException>();
    }

    private static Task<TestWebHost> StartHubHostAsync(Action<IServiceCollection>? configureServices = null)
    {
        return TestWebHost.StartAsync(
            app => {
                app.MapHub<TestHub>("/hub");
                app.MapHub<RejectingHub>("/rejecting-hub");
                app.MapHub<SessionHub>("/session-hub");
            },
            configureServices: s => {
                s.AddSignalR(o => o.AddApiExceptionFilter());
                configureServices?.Invoke(s);
            });
    }

    private static ApiHubConnection CreateConnection(TestWebHost host, string path, Action<HttpConnectionOptions>? configureOptions = null)
    {
        var server = host.App.GetTestServer();

        var hubConnection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, path), options => {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                configureOptions?.Invoke(options);
            })
            .Build();

        return new ApiHubConnection(hubConnection);
    }

    private static async Task<ApiHubConnection> ConnectAsync(TestWebHost host)
    {
        var connection = CreateConnection(host, "hub");
        await connection.StartAsync();
        return connection;
    }
}
