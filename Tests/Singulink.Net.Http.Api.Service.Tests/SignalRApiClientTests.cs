using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Singulink.Net.Http.Api.Client;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Runs hub connections created by <see cref="SignalRApiClientBase"/> against a test server.
/// </summary>
[PrefixTestClass]
public sealed class SignalRApiClientTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // Another host on the same machine, which every OS has: macOS only assigns 127.0.0.1 of the IPv4 loopback addresses.
    private static readonly IPAddress OtherHostAddress = IPAddress.IPv6Loopback;

    public static class EndingHubContract
    {
        public static HubMethod<int> EndSession { get; } = new();
    }

    /// <summary>
    /// A hub that reads the session when a client connects, and whose method fails as if the session had ended.
    /// </summary>
    public sealed class EndingHub : Hub
    {
        public override async Task OnConnectedAsync() =>
            await Context.GetRequiredSessionTokenAsync<TestSessionToken>(SessionAccessOptions.OptionalUserIdPrecondition);

        public int EndSession() => throw new UnauthorizedApiException("Sign in again.") { ErrorCode = ApiErrorCodes.SignInRequired };
    }

    [TestMethod]
    public async Task SignInRequiredHubError_EndsSessionOnDevice()
    {
        var store = new InMemorySessionStore();
        await using var host = await StartSessionHostAsync(store);

        // A cookie that is due for a refresh, of a session that has ended.
        store.UserStamps[7] = 1;
        string cookie = TestSessionCookies.Protect(
            host.App.Services, new(new TestSessionToken(1, 7, 1), DateTime.UtcNow.AddHours(-1), TimeSpan.FromDays(30), 0, true));

        var changes = new List<string?>();
        var client = new HubClient(host, cookie, changes.Add);
        await using var connection = client.CreateSessionHubConnection();

        // The hub refuses the connection after the handshake, which fails the start.
        var error = await Should.ThrowAsync<UnauthorizedApiException>(() => connection.StartAsync().WaitAsync(Timeout));
        error.ErrorCode.ShouldBe(ApiErrorCodes.SignInRequired);
        client.CurrentSessionToken.ShouldBeNull();
        client.SessionEndedCount.ShouldBe(1);
        changes.ShouldBe([null]);
    }

    [TestMethod]
    public async Task SessionTokenCallbackException_PropagatesFromCall()
    {
        var store = new InMemorySessionStore();
        store.UserStamps[7] = 1;
        await using var host = await StartSessionHostAsync(store);

        string cookie = TestSessionCookies.Protect(
            host.App.Services, new(new TestSessionToken(1, 7, 1), DateTime.UtcNow, TimeSpan.FromDays(30), 0, true));

        var client = new HubClient(host, cookie, ThrowWhenDeleted);
        await using var connection = client.CreateSessionHubConnection("ending-hub");
        await connection.StartAsync().WaitAsync(Timeout);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => connection.InvokeAsync(EndingHubContract.EndSession).WaitAsync(Timeout));

        ex.Message.ShouldBe("Deleting the token failed.");
        client.CurrentSessionToken.ShouldBeNull();
    }

    [TestMethod]
    public async Task SignInRequiredOfAConnectionThatBeganBeforeASessionStarted_EndsTheSessionWhoseTokenItHas()
    {
        var store = new InMemorySessionStore();
        store.UserStamps[7] = 1;
        await using var host = await StartSessionHostAsync(store);

        string cookie = TestSessionCookies.Protect(
            host.App.Services, new(new TestSessionToken(1, 7, 1), DateTime.UtcNow, TimeSpan.FromDays(30), 0, true));

        var client = new HubClient(host, cookie, _ => { });
        await using var connection = client.CreateSessionHubConnection("ending-hub");
        await connection.StartAsync().WaitAsync(Timeout);

        // A session start is reported while the device still has the connection's token, which tells that the report concerns the current session.
        client.StartSession();

        await Should.ThrowAsync<UnauthorizedApiException>(() => connection.InvokeAsync(EndingHubContract.EndSession).WaitAsync(Timeout));

        client.SessionEndedCount.ShouldBe(1);
        client.CurrentSessionToken.ShouldBeNull();
    }

    [TestMethod]
    public async Task SignInOnTheDevice_LeavesTheReplacedSessionsConnectionOpen()
    {
        var store = new InMemorySessionStore();
        store.UserStamps[7] = 1;
        await using var host = await StartSessionHostAsync(store);

        string cookie = TestSessionCookies.Protect(
            host.App.Services, new(new TestSessionToken(1, 7, 1), DateTime.UtcNow, TimeSpan.FromDays(30), 0, true));

        var client = new HubClient(host, cookie, _ => { });
        await using var connection = client.CreateSessionHubConnection();

        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += error => {
            closed.TrySetResult(error);
            return Task.CompletedTask;
        };

        await connection.StartAsync().WaitAsync(Timeout);

        (await connection.InvokeAsync(ApiHubConnectionTests.SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);

        // The service tracks the connection after the hub's OnConnectedAsync, which the handshake doesn't wait for, so that the sign-in reaches it.
        var tracker = host.App.Services.GetRequiredService<SessionConnectionTracker<TestSessionToken>>();
        using (var tracked = new CancellationTokenSource(Timeout))
        {
            while (tracker.CountTracked(token => token.SessionId == 1) is 0)
                await Task.Delay(10, tracked.Token);
        }

        // A sign-in of the device replaces the session. The service doesn't tell the connection right away: the connections of the replaced session close
        // at their next check, on every server alike.
        using (var signIn = new HttpRequestMessage(HttpMethod.Post, "/sign-in"))
        {
            signIn.Headers.Add("Cookie", $"{TestSessionCookies.CookieName}={cookie}");
            signIn.Headers.Add("User-Agent", "TestClient/1.0");

            using var response = await host.Client.SendAsync(signIn);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await Task.Delay(200);
        closed.Task.IsCompleted.ShouldBeFalse();
        (await connection.InvokeAsync(ApiHubConnectionTests.SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);
        client.SessionEndedCount.ShouldBe(0);
    }

    [TestMethod]
    public async Task SessionTokenCallbackException_OfARefusedStart_PropagatesFromTheStart()
    {
        var store = new InMemorySessionStore();
        await using var host = await StartSessionHostAsync(store);

        // A cookie that is due for a refresh, of a session that has ended.
        store.UserStamps[7] = 1;
        string cookie = TestSessionCookies.Protect(
            host.App.Services, new(new TestSessionToken(1, 7, 1), DateTime.UtcNow.AddHours(-1), TimeSpan.FromDays(30), 0, true));

        var client = new HubClient(host, cookie, ThrowWhenDeleted);
        await using var connection = client.CreateSessionHubConnection();

        // The hub refuses the connection after the handshake, as when a refusal fails the start before it.
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => connection.StartAsync().WaitAsync(Timeout));

        ex.Message.ShouldBe("Deleting the token failed.");
        client.CurrentSessionToken.ShouldBeNull();
    }

    private static void ThrowWhenDeleted(string? token)
    {
        if (token is null)
            throw new InvalidOperationException("Deleting the token failed.");
    }

    private static Task<TestWebHost> StartSessionHostAsync(InMemorySessionStore store) => TestWebHost.StartAsync(
        app => {
            app.MapHub<ApiHubConnectionTests.SessionHub>("/session-hub");
            app.MapHub<EndingHub>("/ending-hub");
            app.MapPost("/sign-in", async (SessionContext<TestSessionToken> session) => {
                await session.SignInAsync(persistent: true, _ => Task.FromResult(new TestSessionToken(9, 7, 1)));
            });
        },
        configureServices: s => {
            s.AddSignalR(o => o.AddApiExceptionFilter());
            s.AddSingleton<IOriginValidator>(new OriginValidator("localhost"));
            s.AddSingleton(store);
            s.AddHttpSessionHandling<TestSessionToken, ScopedInMemorySessionStore>();
        });

    [TestMethod]
    public async Task HubConnection_SkipsNegotiation()
    {
        await using var host = await TestWebHost.StartAsync(
            app => app.MapHub<ApiHubConnectionTests.SessionHub>("/session-hub"),
            configureServices: s => {
                s.AddSignalR(o => o.AddApiExceptionFilter());
                s.AddSingleton<IOriginValidator>(new OriginValidator("localhost"));
                s.AddSingleton(new InMemorySessionStore());
                s.AddHttpSessionHandling<TestSessionToken, ScopedInMemorySessionStore>();
            });

        string cookie = TestSessionCookies.Protect(
            host.App.Services, new(new TestSessionToken(1, 7, 1), DateTime.UtcNow, TimeSpan.FromDays(30), 0, true));

        var client = new HubClient(host, cookie, _ => { });
        await using var connection = client.CreateSessionHubConnection();

        await connection.StartAsync().WaitAsync(Timeout);

        (await connection.InvokeAsync(ApiHubConnectionTests.SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);
        client.HttpRequests.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task CrossHostRedirect_DoesNotCarrySessionCookie()
    {
        using var otherHost = new RawHttpServer(OtherHostAddress, _ => "HTTP/1.1 404 Not Found\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
        var location = new Uri(otherHost.BaseAddress, "hub");

        using var host = new RawHttpServer(IPAddress.Loopback, _ =>
            $"HTTP/1.1 302 Found\r\nLocation: {location}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");

        var client = new RemoteHubClient(host.BaseAddress, "secret-token");
        await using var connection = client.CreateHubConnection();

        await Should.ThrowAsync<WebSocketException>(() => connection.StartAsync().WaitAsync(Timeout));

        host.Requests.ShouldHaveSingleItem().ShouldContain("session-token=secret-token");
        otherHost.Requests.ShouldBeEmpty();
        client.CurrentSessionToken.ShouldBe("secret-token");
    }

    [TestMethod]
    public async Task Handshake_AppliesConnectionOptions()
    {
        using var host = new RawHttpServer(IPAddress.Loopback, _ => "HTTP/1.1 404 Not Found\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");

        var client = new RemoteHubClient(host.BaseAddress, "secret-token");
        await using var connection = client.CreateHubConnection(options => {
            options.Headers["X-Test"] = "1";
            options.AccessTokenProvider = () => Task.FromResult<string?>("access-token");
            options.Cookies.Add(host.BaseAddress, new Cookie("app", "2"));
        });

        await Should.ThrowAsync<WebSocketException>(() => connection.StartAsync().WaitAsync(Timeout));

        string request = host.Requests.ShouldHaveSingleItem();
        request.ShouldStartWith("GET /hub HTTP/1.1\r\n");
        request.ShouldContain("\r\nUser-Agent: RemoteHubClient/1.0\r\n");
        request.ShouldContain("\r\nX-Requested-With: XMLHttpRequest\r\n");
        request.ShouldContain("\r\nX-Test: 1\r\n");
        request.ShouldContain("\r\nAuthorization: Bearer access-token\r\n");
        request.ShouldContain("\r\nCookie: __Host-session-token=secret-token; app=2\r\n");
    }

    [TestMethod]
    public async Task OwnWebSocketFactory_HandshakeCookiesKeepTheirScope()
    {
        var baseAddress = new Uri("https://api.example.com/");
        var client = new RemoteHubClient(baseAddress, "secret-token");
        CookieContainer? handshakeCookies = null;

        await using var connection = client.CreateHubConnection(options => {
            options.Cookies.Add(baseAddress, new Cookie("app", "1"));

            // Applies the options to the handshake as SignalR's own WebSocket handling does, and stops there.
            options.WebSocketFactory = (context, _) => {
                using var webSocket = new ClientWebSocket();
                webSocket.Options.Cookies = context.Options.Cookies;
                context.Options.WebSocketConfiguration?.Invoke(webSocket.Options);
                handshakeCookies = webSocket.Options.Cookies;

                throw new WebSocketException("Not connected.");
            };
        });

        await Should.ThrowAsync<WebSocketException>(() => connection.StartAsync().WaitAsync(Timeout));

        handshakeCookies.ShouldNotBeNull();

        string hubCookies = handshakeCookies.GetCookieHeader(new Uri("https://api.example.com/hub"));
        hubCookies.ShouldContain("app=1");
        hubCookies.ShouldContain("__Host-session-token=secret-token");
        handshakeCookies.GetCookieHeader(new Uri("https://other.api.example.com/hub")).ShouldBeEmpty();
    }

    [TestMethod]
    public async Task HubConnection_ConnectsOverNetwork()
    {
        var store = new InMemorySessionStore();
        store.UserStamps[7] = 1;
        var userAgents = new List<string>();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddSignalR(o => o.AddApiExceptionFilter());
        builder.Services.AddSingleton<IOriginValidator>(new OriginValidator("localhost"));
        builder.Services.AddSingleton(store);
        builder.Services.AddHttpSessionHandling<TestSessionToken, ScopedInMemorySessionStore>();

        await using var app = builder.Build();

        app.Use((context, next) => {
            lock (userAgents)
                userAgents.Add(context.Request.Headers.UserAgent.ToString());

            return next(context);
        });

        app.MapHub<ApiHubConnectionTests.SessionHub>("/hub");
        await app.StartAsync();

        string cookie = TestSessionCookies.Protect(app.Services, new(new TestSessionToken(1, 7, 1), DateTime.UtcNow, TimeSpan.FromDays(30), 0, true));
        var client = new RemoteHubClient(new Uri(app.Urls.First()), cookie);
        await using var connection = client.CreateHubConnection();

        await connection.StartAsync().WaitAsync(Timeout);

        (await connection.InvokeAsync(ApiHubConnectionTests.SessionHubContract.WhoAmI).WaitAsync(Timeout)).ShouldBe(7);
        userAgents.ShouldBe(["RemoteHubClient/1.0"]);

        await connection.StopAsync().WaitAsync(Timeout);
    }

    [TestMethod]
    public async Task NegotiateRedirectToOtherHost_DoesNotCarrySessionCookie()
    {
        using var otherHost = new RawHttpServer(OtherHostAddress, _ => "HTTP/1.1 404 Not Found\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
        string body = $$"""{"url":"{{new Uri(otherHost.BaseAddress, "client/?hub=chat")}}","accessToken":"service-token"}""";

        // The app's host answers the negotiate request by sending the connection to another host, as Azure SignalR Service does.
        using var host = new RawHttpServer(IPAddress.Loopback, _ =>
            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: {body.Length}\r\n\r\n{body}");

        var client = new RemoteHubClient(host.BaseAddress, "secret-token");
        await using var connection = client.CreateHubConnection(options => options.SkipNegotiation = false);

        await Should.ThrowAsync<Exception>(() => connection.StartAsync().WaitAsync(Timeout));

        host.Requests.ShouldHaveSingleItem().ShouldContain("session-token=secret-token");
        otherHost.Requests.ShouldNotBeEmpty();
        otherHost.Requests.ShouldAllBe(request => !request.Contains("secret-token"));
    }

    private sealed class HubClient(TestWebHost host, string sessionToken, Action<string?> sessionTokenChanged)
        : SignalRApiClientBase(sessionToken, sessionTokenChanged)
    {
        private readonly TestServer _server = host.App.GetTestServer();
        private readonly List<string> _httpRequests = [];

        public string? CurrentSessionToken => SessionToken;

        public int SessionEndedCount { get; private set; }

        /// <summary>
        /// Gets the HTTP requests that hub connections sent through the test server's handler, such as negotiate requests.
        /// </summary>
        public IReadOnlyList<string> HttpRequests
        {
            get {
                lock (_httpRequests)
                    return [.. _httpRequests];
            }
        }

        protected override string UserAgent => "TestClient/1.0";

        protected override string SessionCookieName => host.App.Services.GetRequiredService<IOptions<SessionHandlingOptions>>().Value.SessionCookieName;

        public ApiHubConnection CreateSessionHubConnection(string path = "session-hub") => CreateHubConnection(path, options => {
            options.HttpMessageHandlerFactory = _ => new RecordingHandler(_server.CreateHandler(), _httpRequests);

            // The test server's in-memory WebSockets take the place of the client's own. The client's configuration still runs, and the headers it sets
            // are set on the test server's request.
            options.WebSocketFactory = async (context, cancellationToken) => {
                using (var webSocket = new ClientWebSocket())
                    context.Options.WebSocketConfiguration?.Invoke(webSocket.Options);

                var client = _server.CreateWebSocketClient();

                client.ConfigureRequest = request => {
                    request.Headers.UserAgent = UserAgent;
                    request.Headers.Cookie = $"{SessionCookieName}={SessionToken}";
                };

                return await client.ConnectAsync(context.Uri, cancellationToken);
            };
        });

        protected override Uri GetBaseAddress() => _server.BaseAddress;

        public void StartSession() => NotifySessionStarted();

        protected override void OnSessionEnded() => SessionEndedCount++;
    }

    private sealed class RecordingHandler(HttpMessageHandler innerHandler, List<string> requests) : DelegatingHandler(innerHandler)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (requests)
                requests.Add($"{request.Method} {request.RequestUri}");

            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// A client whose hub connections use the client's own transports, without a test server.
    /// </summary>
    private sealed class RemoteHubClient(Uri baseAddress, string sessionToken) : SignalRApiClientBase(sessionToken, null)
    {
        public string? CurrentSessionToken => SessionToken;

        protected override string UserAgent => "RemoteHubClient/1.0";

        public ApiHubConnection CreateHubConnection(Action<HttpConnectionOptions>? configureOptions = null) => CreateHubConnection("hub", configureOptions);

        protected override Uri GetBaseAddress() => baseAddress;
    }
}
