using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// A hub connection that exposes a strongly typed API based on <see cref="HubMessage"/>, <see cref="HubMethod{TResult}"/> and <see cref="HubStream{TItem}"/>
/// definitions, and reports errors from the server as <see cref="ApiException"/> instances (matching the behavior of regular API requests).
/// </summary>
/// <remarks>
/// <para>
/// The connection counts as connected once the service confirms that the hub accepted it. SignalR completes its handshake before the hub runs its
/// <c>OnConnectedAsync</c>, which can still refuse the connection, and the toolkit's service sends the connection a message once the hub's
/// <c>OnConnectedAsync</c> succeeded (the service library's <c>AddApiResponseHandling</c> and <c>AddHttpSessionHandling</c> add the hub filter that
/// sends it). <see cref="StartAsync(CancellationToken)"/> completes, and <see cref="Reconnected"/> occurs, only then, and <see cref="State"/> is
/// <see cref="HubConnectionState.Connected"/> from then on. An error that the hub's <c>OnConnectedAsync</c> throws, or a loss of the connection before the
/// message, fails the start or the reconnect rather than closing a connection that counted as connected. The connection is made for the toolkit's
/// services: with a service that doesn't send the message, a start doesn't complete.
/// </para>
/// <para>
/// Errors are reported as <see cref="ApiException"/> instances when the server uses the API exception hub filter; other hub errors are reported as
/// <see cref="HubException"/> instances as usual. Errors that close a connection that counted as connected are reported via <see cref="Closed"/>.
/// </para>
/// <para>
/// When the service's session handling finds that the session of an open connection has ended, it sends the connection the error that ended the session,
/// an <see cref="UnauthorizedApiException"/> with the <see cref="ApiErrorCodes.SignInRequired"/> error code or the one that the service's session store
/// ended it with, and the connection closes with that error. A connection that <see cref="SignalRApiClientBase"/> created also ends the session on the
/// device, whatever the error code, as a cleared session cookie does for requests. In the browser, a connection with <see cref="StayConnected"/> whose
/// session ended with the <see cref="ApiErrorCodes.SignInRequired"/> error code first tries again with the browser's session cookie (see
/// <see cref="StayConnected"/>).
/// </para>
/// </remarks>
public sealed class ApiHubConnection : IAsyncDisposable
{
    // The delay before an attempt to start the connection again, by the number of attempts that failed: SignalR's default reconnect policy, followed by
    // the interval it ends with.
    private static readonly TimeSpan[] RestartDelays = [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    // The longest delay that Task.Delay takes on every target framework.
    private static readonly TimeSpan MaxRestartDelay = TimeSpan.FromMilliseconds(int.MaxValue);

    // How long the connection must stay connected for the attempts that follow its close to begin again with the first one. A connection that closes
    // sooner, such as one whose hub fails its OnConnectedAsync, counts as a failed attempt.
    private static readonly TimeSpan StableConnectionTime = TimeSpan.FromSeconds(30);

    // In the browser, how long a connection whose session ended while it was connected waits for a session start in its tab before it tries again with the
    // browser's session cookie anyway: another tab may have signed in.
    private static readonly TimeSpan BrowserSessionWait = TimeSpan.FromSeconds(5);

    // In the browser, how long a connection whose attempt with the browser's session cookie was refused because the browser has no session waits for a
    // session start in its tab before it tries again with the cookie anyway.
    private static readonly TimeSpan SignedOutRetryInterval = TimeSpan.FromMinutes(5);

    // In the browser, how long after an attempt that a request cued (see WaitForSessionStartAsync) the next attempt that a request cues is made at the
    // earliest.
    private static readonly TimeSpan CuedAttemptInterval = TimeSpan.FromSeconds(30);

    private readonly HubConnection _connection;
    private readonly Action<ApiException, Task>? _apiErrorReceived;
    private readonly Func<Task>? _nextSessionStart;
    private readonly Action<Task>? _sessionEnded;
    private readonly bool _usesBrowserSession;
    private readonly Func<CancellationToken, Task>? _requestCue;
    private readonly string? _userIdPrecondition;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _lock = new();

    // Set while the connection tries to start again after a failed start or a close (see StayConnected).
    private CancellationTokenSource? _restartCancellation;
    private Task _restartTask = Task.CompletedTask;

    // When the connection last started or reconnected, and how many attempts to start it again failed before then.
    private long _connectedTimestamp;
    private int _failedAttemptsBeforeConnected;

    // Completes when a session starts on the device after the connection last connected: the task of the next session start at its last handshake (see
    // RecordHandshake), or when it last began to connect if no handshake was recorded. Null if no API client created the connection.
    private Task? _sessionStart;

    // Whether the service confirmed the connection since it last began to connect (see OnConfirmed), after which the handshakes that RecordHandshake
    // reports are ignored. Cleared when the connection closes.
    private bool _connected;

    // Completes when the service confirms that the hub accepted the connection that the connection last began to connect with (see BeginConnecting), with
    // null, or when that connection closes before, with the error that it closed with.
    private TaskCompletionSource<Exception?> _confirmation = CreateConfirmation();

    // Whether a start of the app's or an attempt to start again awaits the confirmation (see WaitForConfirmationAsync), and so handles a close before it, and
    // the error that the underlying connection was lost with while the confirmation was awaited, which stops it instead of reconnecting.
    private ConfirmationWait _confirmationWait;
    private Exception? _lostBeforeConfirmation;

    // How many underlying connections the starts and the attempts to start again connected, and how many closes of the underlying connection were
    // handled, and a task that completes when the next close is handled. SignalR raises the close event of a stopped connection after its stop returns, so
    // that a start can follow the stop before the close is handled (see WaitForPendingCloseAsync).
    private int _startedConnections;
    private int _handledCloses;
    private TaskCompletionSource _nextCloseHandled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Whether the connection that an attempt to start again started closed before the attempt ended, the error it closed with, whether it closed because
    // its session ended, and whether it closed after the hub accepted it with an end of its session that it tries again after (see DefersSessionEnd).
    private bool _closedWhileRestarting;
    private Exception? _restartCloseError;
    private bool _restartCloseEndedSession;
    private bool _restartCloseDefersEnd;

    // Whether the current attempt to start again reports the end of its session, which it doesn't once the end was reported and no session started since
    // (see RestartAsync).
    private bool _attemptReportsEnd = true;

    // The error that the service reported the end of the connection's session with, until the connection closes.
    private Exception? _sessionEndedError;

    // Whether the app stopped or disposed the connection since it last started it.
    private bool _stopRequested;

    // Whether the app was told that the connection is reconnecting, and not yet that it reconnected or closed.
    private bool _reconnecting;

    // When the last attempt to start again that a request cued began (see WaitForSessionStartAsync), if any.
    private long? _lastCuedAttemptTimestamp;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiHubConnection"/> class that wraps the specified connection.
    /// </summary>
    /// <remarks>
    /// The errors that the service reports are translated to <see cref="ApiException"/> instances, but an error that ends the session, such as a 401 with
    /// the <see cref="ApiErrorCodes.SignInRequired"/> error code or the error that the service reports the end of the connection's session with, doesn't
    /// end it on the device, as it does for the connections that <see cref="SignalRApiClientBase"/> creates, which share the session of their API client.
    /// With <see cref="StayConnected"/>, such an error leaves the connection closed, where those connections wait for a new session.
    /// </remarks>
    public ApiHubConnection(HubConnection connection) : this(connection, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiHubConnection"/> class that wraps the specified connection and passes the API errors it receives to
    /// the specified callback before they are thrown or reported. With <see cref="StayConnected"/>, a connection whose session ended waits for the task
    /// that <paramref name="nextSessionStart"/> returns, which completes when a session next starts on the device, before it starts again; without it, the
    /// connection stays closed. When the service reports that the connection's session ended, <paramref name="sessionEnded"/> is called. Both callbacks
    /// get the task of <paramref name="nextSessionStart"/> from when the connection last connected (see <see cref="RecordHandshake"/>), which has
    /// completed if a session started since, and the wait for a new session waits for the same task. A connection whose session is the browser's
    /// (<paramref name="usesBrowserSession"/>), which another tab can replace, also tries again with it after the end of its session without a session start
    /// (see <see cref="StayConnected"/>), and sooner when the task that <paramref name="requestCue"/> returns completes, which a successful response of a
    /// request with the connection's user ID precondition completes; the wait ends with the token, which cancels the task. The attempts to start the
    /// connection again are timed with the specified time provider, or <see cref="TimeProvider.System"/> if it is <see langword="null"/>. The
    /// <see cref="UserChangedApiException"/> errors that the connection reports carry the specified user ID precondition of the connection's URL (see
    /// <see cref="UserChangedApiException.ExpectedUserId"/>).
    /// </summary>
    internal ApiHubConnection(
        HubConnection connection,
        Action<ApiException, Task>? apiErrorReceived,
        Func<Task>? nextSessionStart = null,
        TimeProvider? timeProvider = null,
        Action<Task>? sessionEnded = null,
        bool usesBrowserSession = false,
        Func<CancellationToken, Task>? requestCue = null,
        string? userIdPrecondition = null)
    {
        _connection = connection;
        _apiErrorReceived = apiErrorReceived;
        _nextSessionStart = nextSessionStart;
        _sessionEnded = sessionEnded;
        _usesBrowserSession = usesBrowserSession && nextSessionStart is not null;
        _requestCue = _usesBrowserSession ? requestCue : null;
        _userIdPrecondition = userIdPrecondition;
        _sessionStart = nextSessionStart?.Invoke();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _connection.Closed += OnClosedAsync;
        _connection.Reconnecting += OnReconnectingAsync;
        _connection.Reconnected += OnReconnectedAsync;
        _connection.On<string>(ToolkitHubMessages.SessionEnded, OnSessionEnded);
        _connection.On(ToolkitHubMessages.Connected, OnConfirmed);
    }

    /// <summary>
    /// Occurs when the connection is closed. The exception (if any) that caused the connection to close is provided as the argument.
    /// </summary>
    /// <remarks>
    /// A start that fails, also one whose connection the hub refuses after the handshake (see the remarks of <see cref="ApiHubConnection"/>), throws instead.
    /// With <see cref="StayConnected"/>, it only occurs when the app stops or disposes the connection, or when the service closes or refuses the connection
    /// with an error that isn't transient, other than one that ends the session of a connection that waits for a new session (see
    /// <see cref="StayConnected"/>).
    /// </remarks>
    public event Func<Exception?, Task>? Closed;

    /// <summary>
    /// Occurs when the connection starts reconnecting after losing its underlying connection. The exception (if any) that caused the connection to be lost is
    /// provided as the argument.
    /// </summary>
    /// <remarks>
    /// With <see cref="StayConnected"/>, it also occurs when the connection starts trying again after a start that failed, with the exception that the start
    /// failed with, and after the underlying connection closed, unless it was already reconnecting.
    /// </remarks>
    public event Func<Exception?, Task>? Reconnecting;

    /// <summary>
    /// Occurs when the connection successfully reconnects. The new connection ID is provided as the argument, which is <see langword="null"/> for a
    /// connection without a negotiate request (see <see cref="ConnectionId"/>).
    /// </summary>
    /// <remarks>
    /// It occurs once the service confirms that the hub accepted the reconnected connection (see the remarks of <see cref="ApiHubConnection"/>). A reconnect
    /// that the hub refuses after the handshake doesn't raise it, and is handled as a close. With <see cref="StayConnected"/>, it also occurs when a
    /// connection that is trying again (see <see cref="Reconnecting"/>) starts, including one whose first start failed and one that waited for a new
    /// session.
    /// </remarks>
    public event Func<string?, Task>? Reconnected;

    /// <summary>
    /// Gets or sets a value indicating whether the connection keeps trying to connect until the app stops or disposes it. Default is
    /// <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When it is <see langword="true"/>:
    /// </para>
    /// <list type="bullet">
    ///   <item>A start that fails doesn't throw, unless it failed with an API error that isn't transient (see <see cref="ApiFailures.IsTransient"/>) and
    ///   that the connection doesn't wait for a new session after (see below), a <see cref="HubException"/>, or an <see cref="ArgumentException"/>,
    ///   <see cref="InvalidOperationException"/> or <see cref="NotSupportedException"/>. <see cref="StartAsync(CancellationToken)"/> then raises
    ///   <see cref="Reconnecting"/> with the exception and returns, and the connection tries again.</item>
    ///   <item>When the underlying connection closes, the connection starts again, also after the automatic reconnect of the underlying connection gave up.
    ///   It stays closed when the app stops or disposes it, and when the service closes it with an error that isn't transient and that it doesn't wait for
    ///   a new session after, such as a <see cref="ForbiddenApiException"/> thrown in the hub's <c>OnConnectedAsync</c>, or with a
    ///   <see cref="HubException"/> that isn't an API error.</item>
    ///   <item>A connection that <see cref="SignalRApiClientBase"/> created, which fails to start or closes because its session ended, with an
    ///   <see cref="UnauthorizedApiException"/> with the <see cref="ApiErrorCodes.SignInRequired"/> error code, or because the service reported that its
    ///   session ended (see the remarks of <see cref="ApiHubConnection"/>), whatever the error, waits for a new session on the device before it starts
    ///   again. The API client reports a new session when a response sets the session cookie while it has none or a sign-in's response sets it, and when
    ///   the app calls <see cref="ApiClientBase.NotifySessionStarted"/>, which is the only report on browser platforms, where the browser holds the
    ///   session cookie. A session that started after the connection last connected counts too. The new session can be another user's: a hub that checks
    ///   the user ID precondition of the connection's client then refuses the connection with a <see cref="UserChangedApiException"/>, while a connection
    ///   without a precondition connects with the new session. A connection that fails to start or closes with a <see cref="UserChangedApiException"/>
    ///   waits for a new session in the same way, and then tries again: it connects once the session is its user's again, and with another user's
    ///   session it is refused once more and waits again, so each session start leads to at most one attempt. In the browser, a request can also lead to
    ///   one (see below). A connection created with <see cref="ApiHubConnection(HubConnection)"/> handles such errors like the other errors that aren't
    ///   transient instead.</item>
    ///   <item>In the browser, whose tabs share the session cookie, another tab can have signed in. A connection that closes because its session ended
    ///   with the <see cref="ApiErrorCodes.SignInRequired"/> error code, while the hub had accepted it, doesn't end the session in its tab yet: it tries
    ///   again with the browser's cookie once a session starts in its tab or 5 seconds after the close, whichever is first. With a session of the same
    ///   user it connects, with another user's session it is refused as above, and a connection without a user ID precondition connects with the
    ///   session, whoever's it is. A hub that accepts connections without a session accepts the connection without one if the browser has none, also one
    ///   with a user ID precondition, which is only checked against a session, and the service doesn't track such a connection, so later session changes
    ///   don't close it. Only a refusal with <see cref="ApiErrorCodes.SignInRequired"/> ends the session in the tab. After such a refusal, a start that
    ///   the service refuses with it, or an automatic reconnect that the hub refuses with it, the connection waits for a session start in its tab, and
    ///   meanwhile tries again with the browser's cookie every 5 minutes, without ending the session in the tab again. During either wait, and during the
    ///   wait after a refusal with a <see cref="UserChangedApiException"/>, a successful response to a request of a client that shares the connection's
    ///   session, which began during the wait and has the connection's user ID precondition, or any user ID precondition for a connection without one,
    ///   makes the connection try again at once, at most once every 30 seconds:
    ///   after a response that comes less than 30 seconds after its last such try, it tries again once they have passed. The response doesn't show that
    ///   the browser has a session, since the service only checks the session of a request whose endpoint retrieves it, and a try that is refused is
    ///   handled like the other tries.</item>
    /// </list>
    /// <para>
    /// The attempts are spaced like the ones of SignalR's default reconnect policy, which it continues: after a close, the first attempt is immediate,
    /// and the ones that follow are 2, 10 and 30 seconds after the attempt before, and then every 30 seconds. A failed start counts as the first attempt,
    /// and after the automatic reconnect of the underlying connection gave up, the attempts are every 30 seconds. A connection that closes less than 30
    /// seconds after it started, such as when the hub's <c>OnConnectedAsync</c> fails, counts as a failed attempt, so the attempts after its close
    /// continue with the next delay instead of starting over. An attempt that follows a start or close with an API error that has a retry time
    /// (<see cref="ApiException.RetryAfter"/>) waits at least that long. An attempt that waits for a new session is made as soon as the session starts,
    /// and the attempts after it are spaced as after a close of a connection that was stable. An attempt that the end of a wait without a session start
    /// leads to, as the browser's timed tries of its cookie, also waits out its delay, counted from the start or close before it, while one that a request
    /// cues doesn't. Meanwhile <see cref="State"/> is <see cref="HubConnectionState.Reconnecting"/>, and once the hub accepts the connection,
    /// <see cref="Reconnected"/> occurs. Setting it to <see langword="false"/> while the connection tries again stops it after its next attempt, which
    /// then raises <see cref="Closed"/>.
    /// </para>
    /// </remarks>
    public bool StayConnected { get; set; }

    /// <summary>
    /// Gets or sets the synchronization context that the handlers registered with <c>On</c> and the handlers of <see cref="Closed"/>,
    /// <see cref="Reconnecting"/> and <see cref="Reconnected"/> run on, such as the one of an app's UI thread. Default is <see langword="null"/>, which runs
    /// them on the thread that receives the message, as SignalR does.
    /// </summary>
    /// <remarks>
    /// The connection waits for each handler, and for the task that an asynchronous handler returns, as it does without a synchronization context, and a
    /// handler's exception is handled as it is without one. A handler that is invoked on the context already runs directly.
    /// </remarks>
    public SynchronizationContext? SynchronizationContext { get; set; }

    /// <summary>
    /// Gets the underlying <see cref="HubConnection"/>. Errors from direct use of it are not translated to <see cref="ApiException"/> instances.
    /// </summary>
    public HubConnection UnderlyingConnection => _connection;

    /// <summary>
    /// Gets the current state of the connection: <see cref="HubConnectionState.Reconnecting"/> while it tries to start again, including while it waits for
    /// a new session (see <see cref="StayConnected"/>), and otherwise the state of the underlying connection, except that a connection whose hub hasn't
    /// accepted it yet (see the remarks of <see cref="ApiHubConnection"/>) is <see cref="HubConnectionState.Connecting"/>, or
    /// <see cref="HubConnectionState.Reconnecting"/> while it reconnects.
    /// </summary>
    public HubConnectionState State
    {
        get {
            lock (_lock)
            {
                if (_restartCancellation is not null)
                    return HubConnectionState.Reconnecting;

                var state = _connection.State;

                if (state is HubConnectionState.Connected && !_connected)
                    return _reconnecting ? HubConnectionState.Reconnecting : HubConnectionState.Connecting;

                return state;
            }
        }
    }

    /// <summary>
    /// Gets the connection ID that the negotiate request returned, or <see langword="null"/> if the connection is not established or was established
    /// without a negotiate request (<see cref="Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions.SkipNegotiation"/>), as the connections
    /// that <see cref="SignalRApiClientBase"/> creates are by default.
    /// </summary>
    public string? ConnectionId => _connection.ConnectionId;

    /// <summary>
    /// Starts the connection, and completes once the service confirms that the hub accepted it (see the remarks of <see cref="ApiHubConnection"/>). With
    /// <see cref="StayConnected"/>, most failed starts don't throw, and the connection keeps trying (see <see cref="StayConnected"/>).
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the start, which stops a connection that the hub hasn't accepted yet. It doesn't cancel the attempts
    /// that follow a failed start. A start whose connection the hub accepted before the cancellation completes, as SignalR's start does once it
    /// connected.</param>
    /// <remarks>
    /// A start while the connection tries to start again (see <see cref="StayConnected"/>) takes the place of the attempts, and reports how it ends as
    /// they do: <see cref="Reconnected"/> occurs if it succeeds, and <see cref="Closed"/> if it fails and the connection doesn't try again. An attempt's
    /// connection that is open by then is stopped first.
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        // The app was told that the connection is reconnecting, so it is told how the start ends instead of the attempts.
        bool wasRestarting = await StopRestartingAsync(keepReconnecting: true).ConfigureAwait(false);

        // The close of a connection that was stopped just before, such as with StopAsync, is handled first, so that it isn't taken for this start's.
        await WaitForPendingCloseAsync().ConfigureAwait(false);
        var sessionStart = _nextSessionStart?.Invoke();
        Task<Exception?> confirmation;

        lock (_lock)
        {
            _stopRequested = false;
            confirmation = BeginConnecting(sessionStart, awaitsConfirmation: true);
            MarkConnected(failedAttempts: 0);
        }

        try
        {
            try
            {
                await TranslateAsync(_connection.StartAsync(cancellationToken)).ConfigureAwait(false);
            }
            catch
            {
                EndAwaitingConfirmation();
                throw;
            }

            CountStartedConnection();

            // A connection that closes before the hub accepts it fails the start with the error that it closed with, already translated.
            if (await WaitForConfirmationAsync(confirmation, cancellationToken).ConfigureAwait(false) is { } closeError)
                ExceptionDispatchInfo.Throw(closeError);
        }
        catch (Exception ex)
        {
            var sessionWait = GetSessionWait(ex, sessionEnded: false, defersEnd: false);

            if (StayConnected && !cancellationToken.IsCancellationRequested && (sessionWait is not SessionWait.None || IsRetryableStartFailure(ex)))
            {
                BeginRestarting(ex, failedAttempts: 1, sessionWait is SessionWait.None ? null : SessionStart, sessionWait);
                return;
            }

            if (wasRestarting && EndReconnecting())
                _ = RunHandlersAsync(Closed, ex);

            throw;
        }

        if (wasRestarting && EndReconnecting())
            _ = RunHandlersAsync(Reconnected, _connection.ConnectionId);
    }

    /// <summary>
    /// Stops the connection, and the attempts to start it again (see <see cref="StayConnected"/>).
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
            _stopRequested = true;

        bool raiseClosed = await StopRestartingAsync().ConfigureAwait(false) && _connection.State is HubConnectionState.Disconnected;
        await _connection.StopAsync(cancellationToken).ConfigureAwait(false);

        // A connection that started raises the event itself.
        if (raiseClosed)
            _ = RunHandlersAsync(Closed, null);
    }

    /// <summary>
    /// Stops and disposes the connection. A connection that fails to close, such as with a failed close handshake, is disposed without an exception.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        lock (_lock)
            _stopRequested = true;

        bool raiseClosed = await StopRestartingAsync().ConfigureAwait(false) && _connection.State is HubConnectionState.Disconnected;

        try
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or HttpRequestException or TimeoutException or OperationCanceledException or
            InvalidOperationException)
        {
            // Nothing can be done about a connection that failed to close, and it is gone either way.
        }

        if (raiseClosed)
            _ = RunHandlersAsync(Closed, null);
    }

    // Handlers for messages sent by the server:

    /// <summary>
    /// Registers a handler that is invoked when the server sends the message, on <see cref="SynchronizationContext"/> if it is set.
    /// </summary>
    public IDisposable On(HubMessage message, Action handler) => _connection.On(message.Name, () => Run(handler));

    /// <inheritdoc cref="On(HubMessage, Action)"/>
    public IDisposable On(HubMessage message, Func<Task> handler) => _connection.On(message.Name, () => RunAsync(handler));

    /// <inheritdoc cref="On(HubMessage, Action)"/>
    public IDisposable On<T1>(HubMessage<T1> message, Action<T1> handler) => _connection.On(message.Name, (T1 arg1) => Run(() => handler(arg1)));

    /// <inheritdoc cref="On(HubMessage, Action)"/>
    public IDisposable On<T1>(HubMessage<T1> message, Func<T1, Task> handler) => _connection.On(message.Name, (T1 arg1) => RunAsync(() => handler(arg1)));

    /// <inheritdoc cref="On(HubMessage, Action)"/>
    public IDisposable On<T1, T2>(HubMessage<T1, T2> message, Action<T1, T2> handler) =>
        _connection.On(message.Name, (T1 arg1, T2 arg2) => Run(() => handler(arg1, arg2)));

    /// <inheritdoc cref="On(HubMessage, Action)"/>
    public IDisposable On<T1, T2>(HubMessage<T1, T2> message, Func<T1, T2, Task> handler) =>
        _connection.On(message.Name, (T1 arg1, T2 arg2) => RunAsync(() => handler(arg1, arg2)));

    /// <inheritdoc cref="On(HubMessage, Action)"/>
    public IDisposable On<T1, T2, T3>(HubMessage<T1, T2, T3> message, Action<T1, T2, T3> handler) =>
        _connection.On(message.Name, (T1 arg1, T2 arg2, T3 arg3) => Run(() => handler(arg1, arg2, arg3)));

    /// <inheritdoc cref="On(HubMessage, Action)"/>
    public IDisposable On<T1, T2, T3>(HubMessage<T1, T2, T3> message, Func<T1, T2, T3, Task> handler) =>
        _connection.On(message.Name, (T1 arg1, T2 arg2, T3 arg3) => RunAsync(() => handler(arg1, arg2, arg3)));

    /// <inheritdoc cref="On(HubMessage, Action)"/>
    public IDisposable On<T1, T2, T3, T4>(HubMessage<T1, T2, T3, T4> message, Action<T1, T2, T3, T4> handler) =>
        _connection.On(message.Name, (T1 arg1, T2 arg2, T3 arg3, T4 arg4) => Run(() => handler(arg1, arg2, arg3, arg4)));

    /// <inheritdoc cref="On(HubMessage, Action)"/>
    public IDisposable On<T1, T2, T3, T4>(HubMessage<T1, T2, T3, T4> message, Func<T1, T2, T3, T4, Task> handler) =>
        _connection.On(message.Name, (T1 arg1, T2 arg2, T3 arg3, T4 arg4) => RunAsync(() => handler(arg1, arg2, arg3, arg4)));

    // Handlers for methods invoked by the server (client results):

    /// <summary>
    /// Registers a handler that is invoked when the server invokes the method, on <see cref="SynchronizationContext"/> if it is set, returning the result
    /// to the server.
    /// </summary>
    public IDisposable On<TResult>(HubMethod<TResult> method, Func<TResult> handler) =>
        _connection.On(method.Name, (Func<Task<TResult>>)(() => Run(handler)));

    /// <inheritdoc cref="On{TResult}(HubMethod{TResult}, Func{TResult})"/>
    public IDisposable On<TResult>(HubMethod<TResult> method, Func<Task<TResult>> handler) =>
        _connection.On(method.Name, (Func<Task<TResult>>)(() => RunAsync(handler)));

    /// <inheritdoc cref="On{TResult}(HubMethod{TResult}, Func{TResult})"/>
    public IDisposable On<T1, TResult>(HubMethod<T1, TResult> method, Func<T1, TResult> handler) =>
        _connection.On(method.Name, (Func<T1, Task<TResult>>)(arg1 => Run(() => handler(arg1))));

    /// <inheritdoc cref="On{TResult}(HubMethod{TResult}, Func{TResult})"/>
    public IDisposable On<T1, TResult>(HubMethod<T1, TResult> method, Func<T1, Task<TResult>> handler) =>
        _connection.On(method.Name, (Func<T1, Task<TResult>>)(arg1 => RunAsync(() => handler(arg1))));

    /// <inheritdoc cref="On{TResult}(HubMethod{TResult}, Func{TResult})"/>
    public IDisposable On<T1, T2, TResult>(HubMethod<T1, T2, TResult> method, Func<T1, T2, TResult> handler) =>
        _connection.On(method.Name, (Func<T1, T2, Task<TResult>>)((arg1, arg2) => Run(() => handler(arg1, arg2))));

    /// <inheritdoc cref="On{TResult}(HubMethod{TResult}, Func{TResult})"/>
    public IDisposable On<T1, T2, TResult>(HubMethod<T1, T2, TResult> method, Func<T1, T2, Task<TResult>> handler) =>
        _connection.On(method.Name, (Func<T1, T2, Task<TResult>>)((arg1, arg2) => RunAsync(() => handler(arg1, arg2))));

    /// <inheritdoc cref="On{TResult}(HubMethod{TResult}, Func{TResult})"/>
    public IDisposable On<T1, T2, T3, TResult>(HubMethod<T1, T2, T3, TResult> method, Func<T1, T2, T3, TResult> handler) =>
        _connection.On(method.Name, (Func<T1, T2, T3, Task<TResult>>)((arg1, arg2, arg3) => Run(() => handler(arg1, arg2, arg3))));

    /// <inheritdoc cref="On{TResult}(HubMethod{TResult}, Func{TResult})"/>
    public IDisposable On<T1, T2, T3, TResult>(HubMethod<T1, T2, T3, TResult> method, Func<T1, T2, T3, Task<TResult>> handler) =>
        _connection.On(method.Name, (Func<T1, T2, T3, Task<TResult>>)((arg1, arg2, arg3) => RunAsync(() => handler(arg1, arg2, arg3))));

    /// <inheritdoc cref="On{TResult}(HubMethod{TResult}, Func{TResult})"/>
    public IDisposable On<T1, T2, T3, T4, TResult>(HubMethod<T1, T2, T3, T4, TResult> method, Func<T1, T2, T3, T4, TResult> handler) =>
        _connection.On(method.Name, (Func<T1, T2, T3, T4, Task<TResult>>)((arg1, arg2, arg3, arg4) => Run(() => handler(arg1, arg2, arg3, arg4))));

    /// <inheritdoc cref="On{TResult}(HubMethod{TResult}, Func{TResult})"/>
    public IDisposable On<T1, T2, T3, T4, TResult>(HubMethod<T1, T2, T3, T4, TResult> method, Func<T1, T2, T3, T4, Task<TResult>> handler) =>
        _connection.On(method.Name, (Func<T1, T2, T3, T4, Task<TResult>>)((arg1, arg2, arg3, arg4) => RunAsync(() => handler(arg1, arg2, arg3, arg4))));

    // Sending messages to the server (without waiting for the server to process them):

    /// <summary>
    /// Sends a message to the server without waiting for the server to process it.
    /// </summary>
    public Task SendAsync(HubMessage message, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.SendCoreAsync(message.Name, [], cancellationToken));

    /// <inheritdoc cref="SendAsync(HubMessage, CancellationToken)"/>
    public Task SendAsync<T1>(HubMessage<T1> message, T1 arg1, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.SendCoreAsync(message.Name, [arg1], cancellationToken));

    /// <inheritdoc cref="SendAsync(HubMessage, CancellationToken)"/>
    public Task SendAsync<T1, T2>(HubMessage<T1, T2> message, T1 arg1, T2 arg2, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.SendCoreAsync(message.Name, [arg1, arg2], cancellationToken));

    /// <inheritdoc cref="SendAsync(HubMessage, CancellationToken)"/>
    public Task SendAsync<T1, T2, T3>(HubMessage<T1, T2, T3> message, T1 arg1, T2 arg2, T3 arg3, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.SendCoreAsync(message.Name, [arg1, arg2, arg3], cancellationToken));

    /// <inheritdoc cref="SendAsync(HubMessage, CancellationToken)"/>
    public Task SendAsync<T1, T2, T3, T4>(HubMessage<T1, T2, T3, T4> message, T1 arg1, T2 arg2, T3 arg3, T4 arg4, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.SendCoreAsync(message.Name, [arg1, arg2, arg3, arg4], cancellationToken));

    // Invoking messages on the server (waiting for the server to finish processing them):

    /// <summary>
    /// Sends a message to the server and waits for the server to finish processing it.
    /// </summary>
    public Task InvokeAsync(HubMessage message, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.InvokeCoreAsync(message.Name, [], cancellationToken));

    /// <inheritdoc cref="InvokeAsync(HubMessage, CancellationToken)"/>
    public Task InvokeAsync<T1>(HubMessage<T1> message, T1 arg1, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.InvokeCoreAsync(message.Name, [arg1], cancellationToken));

    /// <inheritdoc cref="InvokeAsync(HubMessage, CancellationToken)"/>
    public Task InvokeAsync<T1, T2>(HubMessage<T1, T2> message, T1 arg1, T2 arg2, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.InvokeCoreAsync(message.Name, [arg1, arg2], cancellationToken));

    /// <inheritdoc cref="InvokeAsync(HubMessage, CancellationToken)"/>
    public Task InvokeAsync<T1, T2, T3>(HubMessage<T1, T2, T3> message, T1 arg1, T2 arg2, T3 arg3, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.InvokeCoreAsync(message.Name, [arg1, arg2, arg3], cancellationToken));

    /// <inheritdoc cref="InvokeAsync(HubMessage, CancellationToken)"/>
    public Task InvokeAsync<T1, T2, T3, T4>(HubMessage<T1, T2, T3, T4> message, T1 arg1, T2 arg2, T3 arg3, T4 arg4, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.InvokeCoreAsync(message.Name, [arg1, arg2, arg3, arg4], cancellationToken));

    // Invoking methods on the server:

    /// <summary>
    /// Invokes a method on the server and returns its result.
    /// </summary>
    public Task<TResult> InvokeAsync<TResult>(HubMethod<TResult> method, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.InvokeCoreAsync<TResult>(method.Name, [], cancellationToken));

    /// <inheritdoc cref="InvokeAsync{TResult}(HubMethod{TResult}, CancellationToken)"/>
    public Task<TResult> InvokeAsync<T1, TResult>(HubMethod<T1, TResult> method, T1 arg1, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.InvokeCoreAsync<TResult>(method.Name, [arg1], cancellationToken));

    /// <inheritdoc cref="InvokeAsync{TResult}(HubMethod{TResult}, CancellationToken)"/>
    public Task<TResult> InvokeAsync<T1, T2, TResult>(HubMethod<T1, T2, TResult> method, T1 arg1, T2 arg2, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.InvokeCoreAsync<TResult>(method.Name, [arg1, arg2], cancellationToken));

    /// <inheritdoc cref="InvokeAsync{TResult}(HubMethod{TResult}, CancellationToken)"/>
    public Task<TResult> InvokeAsync<T1, T2, T3, TResult>(
        HubMethod<T1, T2, T3, TResult> method, T1 arg1, T2 arg2, T3 arg3, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.InvokeCoreAsync<TResult>(method.Name, [arg1, arg2, arg3], cancellationToken));

    /// <inheritdoc cref="InvokeAsync{TResult}(HubMethod{TResult}, CancellationToken)"/>
    public Task<TResult> InvokeAsync<T1, T2, T3, T4, TResult>(
        HubMethod<T1, T2, T3, T4, TResult> method, T1 arg1, T2 arg2, T3 arg3, T4 arg4, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.InvokeCoreAsync<TResult>(method.Name, [arg1, arg2, arg3, arg4], cancellationToken));

    // Streaming from the server:

    /// <summary>
    /// Invokes a streaming method on the server and returns the streamed items. The stream is started when enumeration begins and is cancelled when the
    /// enumeration is disposed or the cancellation token is cancelled.
    /// </summary>
    public IAsyncEnumerable<TItem> StreamAsync<TItem>(HubStream<TItem> stream, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.StreamAsyncCore<TItem>(stream.Name, [], cancellationToken), cancellationToken);

    /// <inheritdoc cref="StreamAsync{TItem}(HubStream{TItem}, CancellationToken)"/>
    public IAsyncEnumerable<TItem> StreamAsync<T1, TItem>(HubStream<T1, TItem> stream, T1 arg1, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.StreamAsyncCore<TItem>(stream.Name, [arg1], cancellationToken), cancellationToken);

    /// <inheritdoc cref="StreamAsync{TItem}(HubStream{TItem}, CancellationToken)"/>
    public IAsyncEnumerable<TItem> StreamAsync<T1, T2, TItem>(HubStream<T1, T2, TItem> stream, T1 arg1, T2 arg2, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.StreamAsyncCore<TItem>(stream.Name, [arg1, arg2], cancellationToken), cancellationToken);

    /// <inheritdoc cref="StreamAsync{TItem}(HubStream{TItem}, CancellationToken)"/>
    public IAsyncEnumerable<TItem> StreamAsync<T1, T2, T3, TItem>(
        HubStream<T1, T2, T3, TItem> stream, T1 arg1, T2 arg2, T3 arg3, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.StreamAsyncCore<TItem>(stream.Name, [arg1, arg2, arg3], cancellationToken), cancellationToken);

    /// <inheritdoc cref="StreamAsync{TItem}(HubStream{TItem}, CancellationToken)"/>
    public IAsyncEnumerable<TItem> StreamAsync<T1, T2, T3, T4, TItem>(
        HubStream<T1, T2, T3, T4, TItem> stream, T1 arg1, T2 arg2, T3 arg3, T4 arg4, CancellationToken cancellationToken = default)
        => TranslateAsync(_connection.StreamAsyncCore<TItem>(stream.Name, [arg1, arg2, arg3, arg4], cancellationToken), cancellationToken);

    /// <summary>
    /// Gets a value indicating whether a start that failed with the specified exception is tried again with <see cref="StayConnected"/>: unless the
    /// service refused it with an error that isn't transient, or the exception reports a mistake of the caller.
    /// </summary>
    private static bool IsRetryableStartFailure(Exception exception) => exception switch {
        ApiException => ApiFailures.IsTransient(exception),
        HubException or ArgumentException or InvalidOperationException or NotSupportedException => false,
        _ => true,
    };

    /// <summary>
    /// Gets a value indicating whether a connection that closed with the specified error, translated like the errors of calls, starts again with
    /// <see cref="StayConnected"/>: unless the service closed it with an error that isn't transient.
    /// </summary>
    private static bool IsRetryableClose(Exception? error) => error switch {
        ApiException => ApiFailures.IsTransient(error),
        HubException => false,
        _ => true,
    };

    /// <summary>
    /// Gets the delay before an attempt to start the connection again: the delay for the number of attempts that failed, or the retry time of the API error
    /// that the connection last failed with, if it is longer.
    /// </summary>
    private static TimeSpan GetRestartDelay(int failedAttempts, Exception? lastError)
    {
        var delay = RestartDelays[Math.Min(failedAttempts, RestartDelays.Length - 1)];

        if (lastError is ApiException { RetryAfter: { } retryAfter } && retryAfter > delay)
            delay = retryAfter < MaxRestartDelay ? retryAfter : MaxRestartDelay;

        return delay;
    }

    /// <summary>
    /// Gets a value indicating whether a connection that failed to start or closed with the specified error, translated like the errors of calls, waits for
    /// a new session before it starts again with <see cref="StayConnected"/>: if an API client created it, and the service reported that its session ended
    /// (<paramref name="sessionEnded"/>) or the error says so, or that the session is another user's than the user ID precondition's.
    /// </summary>
    private bool WaitsForSession(Exception? error, bool sessionEnded) => _nextSessionStart is not null &&
        (sessionEnded || IsSignInRequired(error) || error is UserChangedApiException);

    private static bool IsSignInRequired(Exception? error) => error is UnauthorizedApiException { ErrorCode: ApiErrorCodes.SignInRequired };

    /// <summary>
    /// Gets how the attempt that follows a failed start or a close with the specified error, translated like the errors of calls, waits for a new session
    /// (see <see cref="WaitsForSession"/>). In the browser, after a <see cref="ApiErrorCodes.SignInRequired"/> error, the attempt also tries the browser's
    /// session cookie without a session start, sooner if the close defers the end of the session (see <see cref="DefersSessionEnd"/>), and after a
    /// <see cref="UserChangedApiException"/>, a request with the connection's user ID precondition can cue it.
    /// </summary>
    private SessionWait GetSessionWait(Exception? error, bool sessionEnded, bool defersEnd)
    {
        if (!WaitsForSession(error, sessionEnded))
            return SessionWait.None;

        if (!_usesBrowserSession)
            return SessionWait.NextStart;

        if (IsSignInRequired(error))
            return defersEnd ? SessionWait.BrowserSession : SessionWait.SignedOut;

        return error is UserChangedApiException ? SessionWait.UserChanged : SessionWait.NextStart;
    }

    /// <summary>
    /// Gets a value indicating whether a connection that closes with the specified error, translated like the errors of calls, tries again with the
    /// browser's session cookie before it ends the session in its tab: in the browser, with <see cref="StayConnected"/>, if the hub had accepted the
    /// connection and its session ended with a <see cref="ApiErrorCodes.SignInRequired"/> error, since another tab may have signed in. Called under the
    /// lock.
    /// </summary>
    private bool DefersSessionEnd(Exception? error, bool wasConnected) =>
        _usesBrowserSession && wasConnected && StayConnected && !_stopRequested && IsSignInRequired(error);

    /// <summary>
    /// Handles the message that the service sends before it closes a connection whose session ended: stops the underlying connection, whose close then
    /// ends the session on the device and is reported with the error from the message (see <see cref="OnClosedAsync"/>).
    /// </summary>
    private void OnSessionEnded(string hubError)
    {
        lock (_lock)
        {
            if (_sessionEndedError is not null || _connection.State is not HubConnectionState.Connected)
                return;

            _sessionEndedError = ResponseExceptionInfo.TryCreateFromHubError(hubError, _userIdPrecondition) ?? (Exception)new HubException(hubError);
        }

        // Not awaited: the stop waits for the handlers of the messages that the connection received, this one included.
        _ = Task.Run(() => _connection.StopAsync());
    }

    /// <summary>
    /// Ends the session on the device, after the service reported that the connection's session ended. An exception that the callback throws is returned
    /// to be thrown after the handlers of the event that reports the close ran.
    /// </summary>
    private ExceptionDispatchInfo? EndSession()
    {
        try
        {
            _sessionEnded?.Invoke(SessionStart);
            return null;
        }
        catch (Exception ex)
        {
            return ExceptionDispatchInfo.Capture(ex);
        }
    }

    private Task OnReconnectingAsync(Exception? error)
    {
        // The underlying connection presents the session token of the moment to each attempt to reconnect, and each handshake records its session start.
        var sessionStart = _nextSessionStart?.Invoke();

        lock (_lock)
        {
            // A connection that is lost before the hub accepted it fails the start or attempt that awaits the confirmation, which then decides how to go on:
            // it is stopped rather than reconnected, and its close reports the error that it was lost with (see OnClosedAsync).
            if (_confirmationWait is not ConfirmationWait.None)
            {
                if (_confirmationWait is ConfirmationWait.Awaiting)
                {
                    _lostBeforeConfirmation = error ?? new IOException("The connection was lost before the hub accepted it.");
                    _ = Task.Run(() => _connection.StopAsync());
                }

                return Task.CompletedTask;
            }

            _reconnecting = true;
            BeginConnecting(sessionStart, awaitsConfirmation: false);
        }

        return InvokeErrorHandlersAsync(Reconnecting, error);
    }

    private Task OnReconnectedAsync(string? connectionId)
    {
        Task<Exception?> confirmation;

        lock (_lock)
        {
            // Being stopped by the start or attempt that awaits its confirmation (see OnReconnectingAsync).
            if (_confirmationWait is not ConfirmationWait.None)
                return Task.CompletedTask;

            confirmation = _confirmation.Task;
        }

        // Not awaited: the confirmation follows the handshake, which the underlying connection reports with this event.
        _ = ReportReconnectedAsync(confirmation, connectionId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Raises <see cref="Reconnected"/> for an automatic reconnect of the underlying connection once the service confirms that the hub accepted it. A
    /// connection that closes before reports nothing here: its close is handled as the close of a connection that reconnects (see
    /// <see cref="OnClosedAsync"/>).
    /// </summary>
    private async Task ReportReconnectedAsync(Task<Exception?> confirmation, string? connectionId)
    {
        if (await confirmation.ConfigureAwait(false) is not null)
            return;

        lock (_lock)
        {
            // The connection closed since, or the app stopped or started it again.
            if (!_connected || !_reconnecting || _restartCancellation is not null)
                return;

            _reconnecting = false;
            MarkConnected(failedAttempts: 0);
        }

        await RunHandlersAsync(Reconnected, connectionId).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles the message with which the service confirms that the hub accepted the connection (see <see cref="ToolkitHubMessages.Connected"/>): the
    /// connection counts as connected from then on.
    /// </summary>
    private void OnConfirmed()
    {
        TaskCompletionSource<Exception?> confirmation;

        lock (_lock)
        {
            // The wait for the confirmation was cancelled, and the connection is being stopped.
            if (_confirmationWait is ConfirmationWait.Cancelling)
                return;

            _confirmationWait = ConfirmationWait.None;
            _connected = true;
            confirmation = _confirmation;
        }

        confirmation.TrySetResult(null);
    }

    private async Task OnClosedAsync(Exception? error)
    {
        Exception? sessionEndedError;

        lock (_lock)
        {
            sessionEndedError = _sessionEndedError;
            _sessionEndedError = null;
        }

        // A connection whose session ended closes with the error that the service reported it with.
        bool sessionEnded = sessionEndedError is not null;
        var translatedError = sessionEnded ? sessionEndedError : TranslateConnectionError(error, report: false).Error;
        bool defersEnd;
        bool reportsEnd;

        lock (_lock)
        {
            // An attempt to start again after the end of the session was reported doesn't report it again (see RestartAsync).
            defersEnd = DefersSessionEnd(translatedError, wasConnected: _connected);
            reportsEnd = !defersEnd && (_restartCancellation is null || _attemptReportsEnd);
        }

        // The session ends on the device as it does for a close with an error that says so.
        var callbackError = !reportsEnd ? null : sessionEnded ? EndSession() : translatedError is ApiException apiError ? ReportApiError(apiError) : null;
        bool restart = false;
        bool handledByAttempt = false;
        bool handledByStart = false;
        int failedAttempts = 0;
        Task? sessionStart = null;
        var sessionWait = SessionWait.None;
        TaskCompletionSource<Exception?> confirmation;
        TaskCompletionSource closeHandled;
        Exception closeError;

        lock (_lock)
        {
            // A connection that was lost while its confirmation was awaited closes with the error that it was lost with (see OnReconnectingAsync).
            var closeReason = _lostBeforeConfirmation ?? translatedError;
            closeError = closeReason ?? (_stopRequested ? new OperationCanceledException("The connection was stopped before the hub accepted it.") :
                new IOException("The connection closed before the hub accepted it."));

            _lostBeforeConfirmation = null;
            _connected = false;
            confirmation = _confirmation;

            // Whoever handles the close is decided under this lock, and a start that waits for it (see WaitForPendingCloseAsync) goes on once it is.
            _handledCloses++;
            closeHandled = _nextCloseHandled;
            _nextCloseHandled = new(TaskCreationOptions.RunContinuationsAsynchronously);

            // The connection that an attempt to start again started closed before the attempt ended, which handles the close.
            if (_restartCancellation is not null)
            {
                _closedWhileRestarting = handledByAttempt = true;
                _restartCloseError = closeReason;
                _restartCloseEndedSession = sessionEnded;
                _restartCloseDefersEnd = defersEnd;
            }
            else if (_confirmationWait is not ConfirmationWait.None)
            {
                // The connection that a start of the app's started closed before the hub accepted it: the start fails with the error, or with the exception of
                // the API error callback, as when the start itself fails. Or a stopped attempt stopped its connection (see StopStoppedAttemptAsync).
                handledByStart = true;

                if (callbackError is not null)
                {
                    closeError = callbackError.SourceException;
                    callbackError = null;
                }
            }
            else
            {
                sessionWait = GetSessionWait(translatedError, sessionEnded, defersEnd);
                restart = StayConnected && !_stopRequested && (sessionWait is not SessionWait.None || IsRetryableClose(translatedError));

                if (sessionWait is not SessionWait.None)
                    sessionStart = _sessionStart;

                // The automatic reconnect of the underlying connection made the attempts that the restarts begin with. Otherwise the attempts begin with
                // the first one if the connection stayed connected long enough, and a connection that closed sooner counts as a failed attempt.
                if (_reconnecting)
                    failedAttempts = RestartDelays.Length - 1;
                else if (_timeProvider.GetElapsedTime(_connectedTimestamp) >= StableConnectionTime)
                    failedAttempts = 0;
                else
                    failedAttempts = _failedAttemptsBeforeConnected + 1;

                if (!restart)
                    _reconnecting = false;
            }

            _confirmationWait = ConfirmationWait.None;
        }

        // Ends the wait of an attempt, a start or an automatic reconnect for the confirmation, if any.
        confirmation.TrySetResult(closeError);
        closeHandled.TrySetResult();

        if (restart)
            BeginRestarting(translatedError, failedAttempts, sessionStart, sessionWait);
        else if (!handledByAttempt && !handledByStart)
            await RunHandlersAsync(Closed, translatedError).ConfigureAwait(false);

        callbackError?.Throw();
    }

    /// <summary>
    /// Starts trying to start the connection again in the background, after raising <see cref="Reconnecting"/> unless it was raised already. The first
    /// attempt also waits for the specified session start, if any, as the session wait says. Does nothing if the connection was stopped or is trying
    /// already.
    /// </summary>
    private void BeginRestarting(Exception? error, int failedAttempts, Task? sessionStart, SessionWait sessionWait)
    {
        CancellationTokenSource cancellation;
        bool raiseReconnecting;

        lock (_lock)
        {
            if (_stopRequested || _restartCancellation is not null)
                return;

            raiseReconnecting = !_reconnecting;
            _reconnecting = true;
            _restartCancellation = cancellation = new CancellationTokenSource();
        }

        var reconnectingRaised = raiseReconnecting ? RunHandlersAsync(Reconnecting, error) : Task.CompletedTask;

        lock (_lock)
        {
            if (_restartCancellation == cancellation)
                _restartTask = RestartAsync(failedAttempts, error, sessionStart, sessionWait, reconnectingRaised, cancellation);
        }
    }

    /// <summary>
    /// Tries to start the connection until it starts, the attempts are stopped, or an attempt fails with an error that ends them. An attempt that follows
    /// the end of the session waits for a new one (<paramref name="sessionStart"/> for the first attempt) as well as for its delay, and in the browser for at
    /// most the time that the session wait gives (<paramref name="sessionWait"/> for the first attempt, see <see cref="StayConnected"/>).
    /// </summary>
    private async Task RestartAsync(
        int failedAttempts, Exception? lastError, Task? sessionStart, SessionWait sessionWait, Task reconnectingRaised, CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;

        // In the browser, whether the end of the session was reported, after which an attempt that no session start led to doesn't report it again.
        bool endReported = sessionWait is SessionWait.SignedOut;

        // Started under the lock, which the attempts don't hold.
        await Task.Yield();

        // The handlers run before the first attempt, so that they never follow the event of its result. Their exceptions are left unobserved, as SignalR
        // leaves them.
        await Task.WhenAny(reconnectingRaised).ConfigureAwait(false);

        while (true)
        {
            if (!StayConnected)
            {
                if (EndRestarting(cancellation))
                    _ = RunHandlersAsync(Closed, lastError);

                return;
            }

            bool reportsEnd = !endReported;

            // If the attempt fails because its session ended, the next attempt waits for the session start that the attempt's handshake recorded, so that
            // a session that started during the attempt counts.
            try
            {
                var delay = GetRestartDelay(failedAttempts, lastError);
                using var delayCancellation = delay > TimeSpan.Zero && sessionStart is not null ?
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;

                var delayed = delay > TimeSpan.Zero ?
                    Task.Delay(delay, _timeProvider, delayCancellation?.Token ?? cancellationToken) : Task.CompletedTask;

                bool sessionStarted = false;
                bool cued = false;

                // The delay runs while the attempt waits for a new session, so that the attempts stay as far apart as without the wait, unless a session
                // starts or a request cues the attempt, which then follows at once. A cue, which is no proof of a session, leaves the delays of the
                // attempts after it as they are.
                if (sessionStart is not null)
                {
                    var waitEnd = await WaitForSessionStartAsync(sessionStart, sessionWait, cancellationToken).ConfigureAwait(false);

                    if (waitEnd is SessionWaitEnd.SessionStarted)
                    {
                        // The attempt concerns the session that started, and the attempts after it are spaced as after a close of a stable connection.
                        sessionStarted = true;
                        endReported = false;
                        reportsEnd = true;
                        failedAttempts = 0;
                    }

                    cued = waitEnd is SessionWaitEnd.RequestCue;
                }

                if (sessionStarted || cued)
                    delayCancellation?.Cancel();
                else
                    await delayed.ConfigureAwait(false);

                var attemptSessionStart = _nextSessionStart?.Invoke();
                Task<Exception?> confirmation;

                lock (_lock)
                {
                    if (_restartCancellation != cancellation)
                        return;

                    _closedWhileRestarting = false;
                    _restartCloseError = null;
                    _restartCloseEndedSession = false;
                    _restartCloseDefersEnd = false;
                    _attemptReportsEnd = reportsEnd;
                    confirmation = BeginConnecting(attemptSessionStart, awaitsConfirmation: true);
                    MarkConnected(failedAttempts);

                    if (cued)
                        _lastCuedAttemptTimestamp = _timeProvider.GetTimestamp();
                }

                try
                {
                    await _connection.StartAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    EndAwaitingConfirmation();
                    throw;
                }

                CountStartedConnection();

                // A connection that closes before the hub accepts it is recorded as closed before the attempt ended (see OnClosedAsync).
                await WaitForConfirmationAsync(confirmation, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Stopped, which raises the events.
                return;
            }
            catch (Exception ex)
            {
                // Translated like the errors of the app's starts and of closes, so that an API error decides whether to try again, and reaches the API
                // error callback, which ends the session on the device if the error ends it, unless the attempt doesn't report the end. An exception that
                // the callback throws is dropped, since no call of the app's waits for the attempts. (SignalR likewise only logs the exceptions of the
                // Closed handlers.)
                var error = TranslateConnectionError(ex, reportsEnd).Error!;
                lastError = error;
                sessionWait = GetSessionWait(error, sessionEnded: false, defersEnd: false);

                if (sessionWait is not SessionWait.None || IsRetryableStartFailure(error))
                {
                    failedAttempts++;
                    sessionStart = sessionWait is SessionWait.None ? null : SessionStart;
                    endReported |= sessionWait is SessionWait.SignedOut;
                    continue;
                }

                if (EndRestarting(cancellation))
                    _ = RunHandlersAsync(Closed, lastError);

                return;
            }

            bool stopped;
            bool closed = false;
            bool closedSessionEnded = false;
            bool closedDefersEnd = false;

            lock (_lock)
            {
                // Stopped, or a start of the app's took the place of the attempts, which raise the events, as the hub accepted the attempt's connection.
                stopped = _restartCancellation != cancellation;

                if (!stopped)
                {
                    // The connection can close before the attempt ends, such as when the hub's OnConnectedAsync fails, before the hub accepted it. It then
                    // counts as a failed attempt, as a connection that closes after the attempt ended and before it was stable does.
                    closed = _closedWhileRestarting;
                    lastError = _restartCloseError;
                    closedSessionEnded = _restartCloseEndedSession;
                    closedDefersEnd = _restartCloseDefersEnd;

                    if (!closed)
                        EndRestartingCore();
                }
            }

            if (stopped)
            {
                await StopStoppedAttemptAsync().ConfigureAwait(false);
                return;
            }

            if (!closed)
            {
                cancellation.Dispose();
                _ = RunHandlersAsync(Reconnected, _connection.ConnectionId);

                return;
            }

            sessionWait = GetSessionWait(lastError, closedSessionEnded, closedDefersEnd);

            if (sessionWait is not SessionWait.None || IsRetryableClose(lastError))
            {
                // A close that defers the end of the session leaves it to the next attempt to report; a refusal for want of a session reported it now,
                // or already.
                failedAttempts++;
                sessionStart = sessionWait is SessionWait.None ? null : SessionStart;
                endReported = sessionWait switch {
                    SessionWait.SignedOut => true,
                    SessionWait.BrowserSession => false,
                    _ => endReported,
                };

                continue;
            }

            if (EndRestarting(cancellation))
                _ = RunHandlersAsync(Closed, lastError);

            return;
        }
    }

    /// <summary>
    /// Records the session start of a handshake of the connection: the task of the next session start then, which has completed by the time of a report of
    /// the connection if a session started since, so that the report concerns the session that the new one replaced. Ignored once the connection
    /// connected, until it begins to connect again, so that the connection keeps the session start of the handshake that connected it, also when the hook
    /// that reports handshakes runs for other requests of the connection.
    /// </summary>
    internal void RecordHandshake()
    {
        if (_nextSessionStart is null)
            return;

        var sessionStart = _nextSessionStart();

        lock (_lock)
        {
            if (!_connected)
                _sessionStart = sessionStart;
        }
    }

    /// <summary>
    /// Gets the session start of the connection (see <see cref="RecordHandshake"/>). A connection that no API client created has none, and its API error
    /// callback, if any, gets a completed task.
    /// </summary>
    private Task SessionStart => Volatile.Read(ref _sessionStart) ?? Task.CompletedTask;

    /// <summary>
    /// Records that the connection begins to connect, with the specified session start until a handshake records one, and returns the task of its
    /// confirmation (see <see cref="_confirmation"/>). If <paramref name="awaitsConfirmation"/> is <see langword="true"/>, the caller awaits the
    /// confirmation (see <see cref="WaitForConfirmationAsync"/>) and handles a close before it. Called under the lock.
    /// </summary>
    private Task<Exception?> BeginConnecting(Task? sessionStart, bool awaitsConfirmation)
    {
        _sessionStart = sessionStart;
        _connected = false;
        _confirmationWait = awaitsConfirmation ? ConfirmationWait.Awaiting : ConfirmationWait.None;
        _lostBeforeConfirmation = null;
        _confirmation = CreateConfirmation();

        return _confirmation.Task;
    }

    private static TaskCompletionSource<Exception?> CreateConfirmation() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Records that a start or an attempt to start again no longer awaits the confirmation, since the underlying connection failed to start, which no close
    /// follows.
    /// </summary>
    private void EndAwaitingConfirmation()
    {
        lock (_lock)
            _confirmationWait = ConfirmationWait.None;
    }

    /// <summary>
    /// Records that a start or an attempt to start again connected the underlying connection, whose close is then still to be handled.
    /// </summary>
    private void CountStartedConnection()
    {
        lock (_lock)
            _startedConnections++;
    }

    /// <summary>
    /// Returns a task that completes once the close of a disconnected underlying connection that a start or an attempt connected is handled, up to the
    /// point where it is told apart from the close of a later connection (see <see cref="OnClosedAsync"/>), or a completed task if no close is to be handled.
    /// A connection that the app started through <see cref="UnderlyingConnection"/> isn't counted, and its close not waited for.
    /// </summary>
    private Task WaitForPendingCloseAsync()
    {
        lock (_lock)
        {
            if (_startedConnections <= _handledCloses || _connection.State is not HubConnectionState.Disconnected)
                return Task.CompletedTask;

            return _nextCloseHandled.Task;
        }
    }

    /// <summary>
    /// Stops the underlying connection of an attempt to start again whose hub accepted it as the attempts were stopped, or a start of the app's took their
    /// place, so that it isn't left open. Its close is the attempt's: it doesn't lead to another attempt or raise <see cref="Closed"/>, which the stop or
    /// start reports instead. Returns once the close is handled.
    /// </summary>
    private async Task StopStoppedAttemptAsync()
    {
        lock (_lock)
        {
            // Closed already, and handled.
            if (_startedConnections <= _handledCloses)
                return;

            _confirmationWait = ConfirmationWait.Cancelling;
        }

        await _connection.StopAsync(CancellationToken.None).ConfigureAwait(false);
        await WaitForPendingCloseAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for the service to confirm that the hub accepted the connection that a start or an attempt to start again started, and returns
    /// <see langword="null"/> once it did, or the error that the connection closed with before (see <see cref="OnClosedAsync"/>). If the token is cancelled
    /// first, the connection is stopped, and the wait throws once its close ended it.
    /// </summary>
    private async Task<Exception?> WaitForConfirmationAsync(Task<Exception?> confirmation, CancellationToken cancellationToken)
    {
        try
        {
            return await confirmation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            bool stop;

            lock (_lock)
            {
                // Otherwise the connection was confirmed or closed just before.
                stop = _confirmationWait is ConfirmationWait.Awaiting;

                if (stop)
                    _confirmationWait = ConfirmationWait.Cancelling;
            }

            if (!stop)
                return await confirmation.ConfigureAwait(false);

            await _connection.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await confirmation.ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Waits for a new session before an attempt to start the connection again, as the session wait says. In the browser, the wait also ends without a
    /// session start after the time that the session wait gives, if any (see <see cref="GetSessionWaitLimit"/>), and, unless it only waits for the next
    /// session start (<see cref="SessionWait.NextStart"/>), when a request cues the attempt (see <see cref="_requestCue"/>). The attempt follows a cue at most
    /// once every <see cref="CuedAttemptInterval"/>: after a cue that comes sooner after the last cued attempt, the wait ends once the interval has passed. A
    /// session start that comes first ends the wait as a session start.
    /// </summary>
    private async Task<SessionWaitEnd> WaitForSessionStartAsync(Task sessionStart, SessionWait sessionWait, CancellationToken cancellationToken)
    {
        var limit = GetSessionWaitLimit(sessionWait);
        var requestCue = sessionWait is SessionWait.NextStart ? null : _requestCue;

        if (limit is null && requestCue is null)
        {
            await sessionStart.WaitAsync(cancellationToken).ConfigureAwait(false);
            return SessionWaitEnd.SessionStarted;
        }

        // Ends the timers and the wait for a cue when the wait ends. A wait without a limit ends with a session start or a cue.
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var waitToken = waitCancellation.Token;
        var limitReached = limit is { } timeout ? Task.Delay(timeout, _timeProvider, waitToken) : Task.Delay(Timeout.InfiniteTimeSpan, waitToken);

        try
        {
            var cue = requestCue?.Invoke(waitToken) ?? limitReached;
            var ended = await Task.WhenAny(sessionStart, limitReached, cue).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (sessionStart.IsCompleted)
                return SessionWaitEnd.SessionStarted;

            if (ended == limitReached)
                return SessionWaitEnd.LimitReached;

            var throttle = GetCuedAttemptWait();

            if (throttle > TimeSpan.Zero)
            {
                ended = await Task.WhenAny(sessionStart, limitReached, Task.Delay(throttle, _timeProvider, waitToken)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                if (sessionStart.IsCompleted)
                    return SessionWaitEnd.SessionStarted;

                if (ended == limitReached)
                    return SessionWaitEnd.LimitReached;
            }

            return SessionWaitEnd.RequestCue;
        }
        finally
        {
            waitCancellation.Cancel();
        }
    }

    /// <summary>
    /// Gets how long an attempt that a request cued waits so that it follows the last such attempt by at least <see cref="CuedAttemptInterval"/>.
    /// </summary>
    private TimeSpan GetCuedAttemptWait()
    {
        lock (_lock)
        {
            if (_lastCuedAttemptTimestamp is not { } lastCuedAttempt)
                return TimeSpan.Zero;

            var elapsed = _timeProvider.GetElapsedTime(lastCuedAttempt);
            return elapsed < CuedAttemptInterval ? CuedAttemptInterval - elapsed : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Gets the longest wait for a session start of an attempt to start the connection again, or <see langword="null"/> if it waits for one however long it
    /// takes: in the browser, the attempt tries the browser's session cookie after that wait (see <see cref="StayConnected"/>).
    /// </summary>
    private static TimeSpan? GetSessionWaitLimit(SessionWait sessionWait) => sessionWait switch {
        SessionWait.BrowserSession => BrowserSessionWait,
        SessionWait.SignedOut => SignedOutRetryInterval,
        _ => null,
    };

    /// <summary>
    /// Records when the connection starts, and how many attempts to start it again failed before then. Called under the lock.
    /// </summary>
    private void MarkConnected(int failedAttempts)
    {
        _connectedTimestamp = _timeProvider.GetTimestamp();
        _failedAttemptsBeforeConnected = Math.Min(failedAttempts, RestartDelays.Length - 1);
    }

    /// <summary>
    /// Ends the attempts to start the connection again if they are the specified ones. Returns <see langword="false"/> if they were stopped.
    /// </summary>
    private bool EndRestarting(CancellationTokenSource cancellation)
    {
        lock (_lock)
        {
            if (_restartCancellation != cancellation)
                return false;

            EndRestartingCore();
        }

        cancellation.Dispose();
        return true;
    }

    /// <summary>
    /// Clears the state of the attempts to start the connection again. Called under the lock.
    /// </summary>
    private void EndRestartingCore()
    {
        _restartCancellation = null;
        _restartTask = Task.CompletedTask;
        _closedWhileRestarting = false;
        _restartCloseError = null;
        _restartCloseEndedSession = false;
        _reconnecting = false;
    }

    /// <summary>
    /// Records that the app is told that the connection no longer reconnects. Returns <see langword="false"/> if it was told already.
    /// </summary>
    private bool EndReconnecting()
    {
        lock (_lock)
        {
            if (!_reconnecting)
                return false;

            _reconnecting = false;
            return true;
        }
    }

    /// <summary>
    /// Stops the attempts to start the connection again, if there are any, and waits for the current attempt. Returns <see langword="true"/> if there were.
    /// Unless <paramref name="keepReconnecting"/> is <see langword="true"/>, the app counts as told that the connection no longer reconnects.
    /// </summary>
    private async Task<bool> StopRestartingAsync(bool keepReconnecting = false)
    {
        CancellationTokenSource? cancellation;
        Task restartTask;

        lock (_lock)
        {
            cancellation = _restartCancellation;
            restartTask = _restartTask;

            if (cancellation is null)
                return false;

            _restartCancellation = null;
            _restartTask = Task.CompletedTask;
            _closedWhileRestarting = false;
            _restartCloseError = null;
            _restartCloseEndedSession = false;
            _reconnecting &= keepReconnecting;
        }

        cancellation.Cancel();
        await restartTask.ConfigureAwait(false);
        cancellation.Dispose();

        return true;
    }

    /// <summary>
    /// Returns the <see cref="ApiException"/> that a hub error represents, after passing it to the API error callback, or <see langword="null"/> if it
    /// represents none. An exception that the callback throws propagates.
    /// </summary>
    private ApiException? TryTranslate(HubException exception)
    {
        if (ResponseExceptionInfo.TryCreateFromHubError(exception.Message, _userIdPrecondition) is not { } apiException)
            return null;

        _apiErrorReceived?.Invoke(apiException, SessionStart);
        return apiException;
    }

    /// <summary>
    /// Translates the error of the underlying connection like the errors of calls, and passes an API error to the API error callback unless
    /// <paramref name="report"/> is <see langword="false"/>. An exception that the callback throws is returned to be thrown after the handlers of the event
    /// that reports the error ran.
    /// </summary>
    private (Exception? Error, ExceptionDispatchInfo? CallbackError) TranslateConnectionError(Exception? error, bool report = true)
    {
        if (error is not HubException hubException ||
            ResponseExceptionInfo.TryCreateFromHubError(hubException.Message, _userIdPrecondition) is not { } apiException)
        {
            return (error, null);
        }

        return (apiException, report ? ReportApiError(apiException) : null);
    }

    /// <summary>
    /// Passes an API error of the underlying connection to the API error callback. An exception that the callback throws is returned.
    /// </summary>
    private ExceptionDispatchInfo? ReportApiError(ApiException exception)
    {
        try
        {
            _apiErrorReceived?.Invoke(exception, SessionStart);
            return null;
        }
        catch (Exception ex)
        {
            return ExceptionDispatchInfo.Capture(ex);
        }
    }

    // The translation runs in the catch blocks rather than in exception filters, which would swallow an exception thrown by the API error callback.

    private async Task TranslateAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (HubException ex)
        {
            if (TryTranslate(ex) is { } apiException)
                throw apiException;

            throw;
        }
    }

    private async Task<T> TranslateAsync<T>(Task<T> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (HubException ex)
        {
            if (TryTranslate(ex) is { } apiException)
                throw apiException;

            throw;
        }
    }

    private async IAsyncEnumerable<T> TranslateAsync<T>(IAsyncEnumerable<T> source, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var enumerator = source.GetAsyncEnumerator(cancellationToken);

        await using (enumerator.ConfigureAwait(false))
        {
            while (true)
            {
                bool hasNext;

                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (HubException ex)
                {
                    if (TryTranslate(ex) is { } apiException)
                        throw apiException;

                    ExceptionDispatchInfo.Throw(ex);
                    throw; // Unreachable
                }

                if (!hasNext)
                    yield break;

                yield return enumerator.Current;
            }
        }
    }

    /// <summary>
    /// Invokes the handlers of an event that reports the error of the underlying connection, translated like the errors of calls. The handlers run even if
    /// the API error callback throws, and its exception is thrown afterward.
    /// </summary>
    private async Task InvokeErrorHandlersAsync(Func<Exception?, Task>? handlers, Exception? error)
    {
        var (translatedError, callbackError) = TranslateConnectionError(error);

        await RunHandlersAsync(handlers, translatedError).ConfigureAwait(false);
        callbackError?.Throw();
    }

    /// <summary>
    /// Invokes the handlers of an event, each on <see cref="SynchronizationContext"/> if it is set.
    /// </summary>
    private Task RunHandlersAsync<T>(Func<T, Task>? handlers, T arg)
    {
        if (handlers is null)
            return Task.CompletedTask;

        var invocationList = handlers.GetInvocationList();

        if (invocationList.Length is 1)
            return RunAsync(() => handlers(arg));

        var tasks = new Task[invocationList.Length];

        for (int i = 0; i < invocationList.Length; i++)
        {
            var handler = (Func<T, Task>)invocationList[i];
            tasks[i] = RunAsync(() => handler(arg));
        }

        return Task.WhenAll(tasks);
    }

    // The handlers run on SynchronizationContext if it is set and is not the current one, and otherwise directly.

    private Task Run(Action action)
    {
        if (GetHandlerContext() is { } context)
            return Post(context, () => {
                action();
                return Task.FromResult(true);
            });

        action();
        return Task.CompletedTask;
    }

    private Task<TResult> Run<TResult>(Func<TResult> func) =>
        GetHandlerContext() is { } context ? Post(context, () => Task.FromResult(func())) : Task.FromResult(func());

    private Task RunAsync(Func<Task> action)
    {
        if (GetHandlerContext() is { } context)
            return Post(context, async () => {
                await action().ConfigureAwait(false);
                return true;
            });

        return action();
    }

    private Task<TResult> RunAsync<TResult>(Func<Task<TResult>> func) => GetHandlerContext() is { } context ? Post(context, func) : func();

    private SynchronizationContext? GetHandlerContext() => SynchronizationContext is { } context && context != SynchronizationContext.Current ? context : null;

    private static Task<TResult> Post<TResult>(SynchronizationContext context, Func<Task<TResult>> func)
    {
        var completion = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Post(static state => {
            var (func, completion) = ((Func<Task<TResult>>, TaskCompletionSource<TResult>))state!;
            _ = CompleteAsync(func, completion);
        }, (func, completion));

        return completion.Task;

        static async Task CompleteAsync(Func<Task<TResult>> func, TaskCompletionSource<TResult> completion)
        {
            try
            {
                completion.TrySetResult(await func().ConfigureAwait(false));
            }
            catch (OperationCanceledException ex)
            {
                completion.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }
    }

    /// <summary>
    /// How an attempt to start the connection again waits for a new session (see <see cref="StayConnected"/>).
    /// </summary>
    private enum SessionWait
    {
        // The attempt doesn't wait for a new session.
        None,

        // The attempt waits for a session start on the device.
        NextStart,

        // In the browser, after the session of a connection that the hub had accepted ended: the attempt waits for a session start in the tab for at most
        // BrowserSessionWait, and then tries the browser's session cookie, which a sign-in in another tab may have replaced.
        BrowserSession,

        // In the browser, after an attempt was refused for want of a session: the attempt waits for a session start in the tab for at most
        // SignedOutRetryInterval, and then tries the browser's session cookie, which a sign-in in another tab may have set.
        SignedOut,

        // In the browser, after the hub refused the connection with another user's session: the attempt waits for a session start in the tab, or for a
        // request with the connection's user ID precondition (see WaitForSessionStartAsync), which suggests that a sign-in in another tab brought the
        // connection's user back.
        UserChanged,
    }

    private enum SessionWaitEnd
    {
        // A session started on the device.
        SessionStarted,

        // In the browser, the time that the session wait gives passed.
        LimitReached,

        // In the browser, a request cued the attempt (see WaitForSessionStartAsync).
        RequestCue,
    }

    private enum ConfirmationWait
    {
        None,
        Awaiting,

        // The wait was cancelled, and the connection is being stopped: a confirmation that arrives meanwhile is ignored.
        Cancelling,
    }
}
