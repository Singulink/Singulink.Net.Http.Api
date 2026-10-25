using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
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

        public static HubMethod<int, int, int, int, string> Join { get; } = new();

        public static HubMethod<string> RelayFourArguments { get; } = new();

        public static HubMethod<IAsyncEnumerable<int>, int> Sum { get; } = new();

        public static HubMessage<string> FailWith { get; } = new();

        public static HubMethod<string, int> FailMethodWith { get; } = new();

        public static HubMethod<string, int> FailWithHubError { get; } = new();

        // Server to client:
        public static HubMessage Pinged { get; } = new();

        public static HubMessage<string, int> Echoed { get; } = new();

        public static HubMethod<string, string> AskClient { get; } = new();

        public static HubMethod<int, int, int, int, int> AddOnClient { get; } = new();

        // Server to client streams:
        public static HubStream<int, int> Count { get; } = new();

        public static HubStream<int, string, int> CountThenFail { get; } = new();

        public static HubStream<int, int, int, int, int> Range { get; } = new();

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

        public string Join(int a, int b, int c, int d) => $"{a}{b}{c}{d}";

        public async Task<string> RelayFourArguments() =>
            (await Clients.Caller.InvokeAsync(TestHubContract.AddOnClient, 1, 2, 3, 4, Context.ConnectionAborted)).ToString(CultureInfo.InvariantCulture);

        public async IAsyncEnumerable<int> Range(int from, int count, int step, int skip)
        {
            for (int i = skip; i < count; i++)
            {
                await Task.Yield();
                yield return from + (i * step);
            }
        }

        public async Task<int> Sum(IAsyncEnumerable<int> values)
        {
            int sum = 0;

            await foreach (int value in values)
                sum += value;

            return sum;
        }

        public Task FailWith(string kind) => throw CreateException(kind);

        public int FailMethodWith(string kind) => throw CreateException(kind);

        public int FailWithHubError(string message) => throw new HubException(message);

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

        public static Guid ReferenceId { get; } = Guid.CreateVersion7();

        private static Exception CreateException(string kind) => kind switch {
            "forbidden" => new ForbiddenApiException("no access") { ErrorCode = "denied" },
            "notfound" => new NotFoundApiException("missing"),
            "reference" => new ConflictApiException("[ref:not-a-reference] conflict") { ErrorCode = "the-code", ReferenceId = ReferenceId },
            "retry" => new ServiceUnavailableApiException("[retry:7] busy") {
                ErrorCode = "busy", ReferenceId = ReferenceId, RetryAfter = TimeSpan.FromSeconds(1.2),
            },
            "retry-only" => new TooManyRequestsApiException("slow down") { RetryAfter = TimeSpan.FromMinutes(5) },
            "old-format" => new HubException($"503 [busy] [ref:{ReferenceId:D}] busy"),
            "bad-retry" => new HubException("429 [] [retry:soon] slow down"),
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
    public async Task FourArguments_MethodsAndStreams()
    {
        await using var host = await StartHubHostAsync();
        await using var connection = await ConnectAsync(host);

        (await connection.InvokeAsync(TestHubContract.Join, 1, 2, 3, 4)).ShouldBe("1234");

        using var handler = connection.On(TestHubContract.AddOnClient, (int a, int b, int c, int d) => a + b + c + d);
        (await connection.InvokeAsync(TestHubContract.RelayFourArguments)).ShouldBe("10");

        var items = new List<int>();

        await foreach (int item in connection.StreamAsync(TestHubContract.Range, 10, 4, 5, 1))
            items.Add(item);

        items.ShouldBe([15, 20, 25]);
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
        notFound.ReferenceId.ShouldBeNull();

        var withReference = await Should.ThrowAsync<ConflictApiException>(() => connection.InvokeAsync(TestHubContract.FailMethodWith, "reference"));
        withReference.Message.ShouldBe("[ref:not-a-reference] conflict");
        withReference.ErrorCode.ShouldBe("the-code");
        withReference.ReferenceId.ShouldBe(TestHub.ReferenceId);
        withReference.RetryAfter.ShouldBeNull();

        // The connection is still usable afterwards.
        (await connection.InvokeAsync(TestHubContract.Double, 1)).ShouldBe(2);
    }

    [TestMethod]
    public async Task ApiException_WithRetryAfter_IsReportedWithIt()
    {
        await using var host = await StartHubHostAsync();
        await using var connection = await ConnectAsync(host);

        var withReference = await Should.ThrowAsync<ServiceUnavailableApiException>(() => connection.InvokeAsync(TestHubContract.FailMethodWith, "retry"));
        withReference.Message.ShouldBe("[retry:7] busy");
        withReference.ErrorCode.ShouldBe("busy");
        withReference.ReferenceId.ShouldBe(TestHub.ReferenceId);
        withReference.RetryAfter.ShouldBe(TimeSpan.FromSeconds(2));

        var retryOnly = await Should.ThrowAsync<TooManyRequestsApiException>(() => connection.InvokeAsync(TestHubContract.FailMethodWith, "retry-only"));
        retryOnly.Message.ShouldBe("slow down");
        retryOnly.ErrorCode.ShouldBeNull();
        retryOnly.ReferenceId.ShouldBeNull();
        retryOnly.RetryAfter.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [TestMethod]
    public async Task HubError_WithoutRetryMarkerOrWithAnInvalidOne_IsReportedWithoutRetryAfter()
    {
        await using var host = await StartHubHostAsync();
        await using var connection = await ConnectAsync(host);

        var oldFormat = await Should.ThrowAsync<ServiceUnavailableApiException>(() => connection.InvokeAsync(TestHubContract.FailMethodWith, "old-format"));
        oldFormat.Message.ShouldBe("busy");
        oldFormat.ErrorCode.ShouldBe("busy");
        oldFormat.ReferenceId.ShouldBe(TestHub.ReferenceId);
        oldFormat.RetryAfter.ShouldBeNull();

        var badRetry = await Should.ThrowAsync<TooManyRequestsApiException>(() => connection.InvokeAsync(TestHubContract.FailMethodWith, "bad-retry"));
        badRetry.Message.ShouldBe("[retry:soon] slow down");
        badRetry.RetryAfter.ShouldBeNull();
    }

    [TestMethod]
    [DataRow("404 [] missing", null, "missing")]
    [DataRow("404 [gone] missing", "gone", "missing")]
    [DataRow("404 [] [draft] missing", null, "[draft] missing")]
    [DataRow("404 [] [ref:{ref}] [retry:3] missing", null, "missing")]
    [DataRow("404 [gone] [ref:{ref}] [retry:3] missing", "gone", "missing")]
    public async Task HubError_WithBracketedErrorCode_IsTranslated(string hubError, string? errorCode, string message)
    {
        await using var host = await StartHubHostAsync();
        await using var connection = await ConnectAsync(host);
        bool hasMarkers = hubError.Contains("[ref:", StringComparison.Ordinal);
        hubError = hubError.Replace("{ref}", TestHub.ReferenceId.ToString("D"), StringComparison.Ordinal);

        var ex = await Should.ThrowAsync<NotFoundApiException>(() => connection.InvokeAsync(TestHubContract.FailWithHubError, hubError));

        ex.ErrorCode.ShouldBe(errorCode);
        ex.Message.ShouldBe(message);
        ex.ReferenceId.ShouldBe(hasMarkers ? TestHub.ReferenceId : null);
        ex.RetryAfter.ShouldBe(hasMarkers ? TimeSpan.FromSeconds(3) : null);
    }

    [TestMethod]
    [DataRow("404 missing")]
    [DataRow("100 items exceed the limit")]
    [DataRow("404 [not a code] missing")]
    [DataRow("404 [ref:{ref}] missing")]
    public async Task HubError_WithoutValidBracketedErrorCode_StaysHubException(string hubError)
    {
        await using var host = await StartHubHostAsync();
        await using var connection = await ConnectAsync(host);
        hubError = hubError.Replace("{ref}", TestHub.ReferenceId.ToString("D"), StringComparison.Ordinal);

        var ex = await Should.ThrowAsync<HubException>(() => connection.InvokeAsync(TestHubContract.FailWithHubError, hubError));

        ex.Message.ShouldEndWith("HubException: " + hubError);

        // The connection is still usable afterwards.
        (await connection.InvokeAsync(TestHubContract.Double, 1)).ShouldBe(2);
    }

    [TestMethod]
    public async Task UnexpectedException_WithHandler_IsLoggedAndReportedAsServerError()
    {
        await using var host = await StartHubHostAsync(s => s.AddApiExceptionHandler());
        await using var connection = await ConnectAsync(host);

        var ex = await Should.ThrowAsync<ServerErrorApiException>(() => connection.InvokeAsync(TestHubContract.FailMethodWith, "unexpected"));
        ex.Message.ShouldBe($"An unexpected error has occurred and has been logged. Reference ID: {ex.ReferenceId.ShouldNotBeNull()}.");

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
    public async Task ApiException_WhileConnecting_FailsTheStart()
    {
        await using var host = await StartHubHostAsync();
        await using var connection = CreateConnection(host, "rejecting-hub");
        int closedCount = 0;

        connection.Closed += _ => {
            Interlocked.Increment(ref closedCount);
            return Task.CompletedTask;
        };

        // The handshake completes before OnConnectedAsync runs, and the start waits for the service to confirm that the hub accepted the connection, so the
        // failure surfaces as a failed start, without a Closed event, as for a start that fails before the handshake.
        var ex = await Should.ThrowAsync<ForbiddenApiException>(() => connection.StartAsync().WaitAsync(Timeout));

        ex.Message.ShouldBe("not allowed here");
        ex.ErrorCode.ShouldBe("denied");
        connection.State.ShouldBe(HubConnectionState.Disconnected);

        await Task.Delay(100);
        closedCount.ShouldBe(0);
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
            s.AddHttpSessionHandling<TestSessionToken, ScopedInMemorySessionStore>();
        });

        var utcNow = DateTime.UtcNow;
        store.UserStamps[7] = 1;
        store.Sessions[1] = new TestSessionData {
            Id = 1, UserId = 7, Device = "TestClient/1.0", RefreshedUtc = utcNow, ExpiresUtc = utcNow.AddDays(30), IsPersistent = true, CreatedUtc = utcNow,
        };
        string cookie = TestSessionCookies.Protect(host.App.Services, new(new TestSessionToken(1, 7, 1), utcNow, TimeSpan.FromDays(30), 0, true));

        await using var connection = CreateConnection(host, "session-hub", options => {
            options.Headers["Cookie"] = $"{TestSessionCookies.CookieName}={cookie}";
            options.Headers["User-Agent"] = "TestClient/1.0";
        });

        await connection.StartAsync();
        (await connection.InvokeAsync(SessionHubContract.WhoAmI)).ShouldBe(7);
    }

    [TestMethod]
    public async Task SessionToken_FromHubCallerContext_NotSignedIn_FailsTheStartWithUnauthorized()
    {
        var store = new InMemorySessionStore();
        await using var host = await StartHubHostAsync(s => {
            s.AddSingleton<IOriginValidator>(new OriginValidator("localhost"));
            s.AddSingleton(store);
            s.AddHttpSessionHandling<TestSessionToken, ScopedInMemorySessionStore>();
        });

        await using var connection = CreateConnection(host, "session-hub", options => options.Headers["User-Agent"] = "TestClient/1.0");

        await Should.ThrowAsync<UnauthorizedApiException>(() => connection.StartAsync().WaitAsync(Timeout));
    }

    [TestMethod]
    public async Task SessionToken_FromHubCallerContext_UnreadableToken_FailsTheStartWithUnauthorized()
    {
        // Over WebSockets the response has already started (101 upgrade) when the hub reads the token, so clearing the unreadable token must not attempt
        // to delete the cookie - that would fail with "response has already started" and surface as a server error instead of unauthorized.

        var store = new InMemorySessionStore();
        await using var host = await StartHubHostAsync(s => {
            s.AddSingleton<IOriginValidator>(new OriginValidator("localhost"));
            s.AddSingleton(store);
            s.AddHttpSessionHandling<TestSessionToken, ScopedInMemorySessionStore>();
        });

        await using var connection = CreateWebSocketConnection(host, "session-hub", options => {
            options.Headers["Cookie"] = $"{TestSessionCookies.CookieName}=not-a-valid-token";
            options.Headers["User-Agent"] = "TestClient/1.0";
        });

        await Should.ThrowAsync<UnauthorizedApiException>(() => connection.StartAsync().WaitAsync(Timeout));
    }

    [TestMethod]
    public async Task SessionToken_FromHubCallerContext_InvalidSession_FailsTheStartWithUnauthorized()
    {
        // A refresh-due token whose session no longer exists in the store is cleared during validation - same "response already started" concern as above.

        var store = new InMemorySessionStore();
        await using var host = await StartHubHostAsync(s => {
            s.AddSingleton<IOriginValidator>(new OriginValidator("localhost"));
            s.AddSingleton(store);
            s.AddHttpSessionHandling<TestSessionToken, ScopedInMemorySessionStore>();
        });

        store.UserStamps[7] = 1;
        string cookie = TestSessionCookies.Protect(
            host.App.Services, new(new TestSessionToken(1, 7, 1), DateTime.UtcNow.AddHours(-1), TimeSpan.FromDays(30), 0, true));

        await using var connection = CreateWebSocketConnection(host, "session-hub", options => {
            options.Headers["Cookie"] = $"{TestSessionCookies.CookieName}={cookie}";
            options.Headers["User-Agent"] = "TestClient/1.0";
        });

        await Should.ThrowAsync<UnauthorizedApiException>(() => connection.StartAsync().WaitAsync(Timeout));
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

    /// <summary>
    /// Creates a connection over WebSockets (via the test server's in-memory WebSocket client) for tests that depend on the response having already started
    /// when the hub's <c>OnConnectedAsync</c> runs, which is not the case with long polling.
    /// </summary>
    private static ApiHubConnection CreateWebSocketConnection(TestWebHost host, string path, Action<HttpConnectionOptions>? configureOptions = null)
    {
        var server = host.App.GetTestServer();

        var hubConnection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, path), options => {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.WebSockets;
                options.WebSocketFactory = async (context, cancellationToken) => {
                    var client = server.CreateWebSocketClient();

                    client.ConfigureRequest = request => {
                        foreach (var (name, value) in context.Options.Headers)
                            request.Headers[name] = value;
                    };

                    return await client.ConnectAsync(context.Uri, cancellationToken);
                };

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
