using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.SignalR;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Keeps the session token of each hub connection that has a session, and rechecks the sessions against the session store about once a minute. A
/// connection is checked when it was last checked (or its session last validated) at least <see cref="SessionHandlingOptions.RefreshAfter"/> ago. A
/// session that has ended (including one the store ends by throwing an <see cref="UnauthorizedApiException"/>) ends its connection (see
/// <see cref="EndConnectionAsync"/>); a live session's validity is extended, without changing its refresh time or generation, since the connection cannot
/// deliver a new cookie to the client. A replacement token from the session store becomes the token of the connection's session context. A session that
/// ends, however it ends, such as with a sign-out or a sign-in that replaces it on any server, or by the app removing it from its store, ends its
/// connections at their next check.
/// </summary>
internal sealed class SessionConnectionTracker<TSessionToken> : ISessionConnectionTracker, IDisposable, IAsyncDisposable
    where TSessionToken : class, ISessionToken
{
    private const int MaxCheckAttempts = 3;
    private const int MaxConcurrentChecks = 4;

    private const string HubContextJustification =
        "The hub context type is made for a hub type, a reference type, that SignalR serves. If it can't be made, the connection is aborted without the " +
        "session-ended message.";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    // How long a connection whose session ended stays open after it was sent the session-ended message, for the client to close it.
    private static readonly TimeSpan SessionEndedCloseDelay = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, TrackedConnection> _connections = [];

    // The errors of the connections whose sessions ended, as hub errors, until they close.
    private readonly ConcurrentDictionary<string, string> _endedConnections = [];

    // The clients of each hub type, or null if they can't be resolved.
    private readonly ConcurrentDictionary<Type, IHubClients?> _hubClients = [];

    private readonly IServiceProvider _services;
    private readonly SessionHandler<TSessionToken> _handler;
    private readonly SessionHandlingOptions _options;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _disposed = new();
    private readonly Lock _timerLock = new();
    private readonly Lock _checkLock = new();

    private ITimer? _timer;
    private Task _checkTask = Task.CompletedTask;
    private bool _checking;
    private bool _checkPending;

    public SessionConnectionTracker(IServiceProvider services, SessionHandler<TSessionToken> handler, ILoggerFactory loggerFactory)
    {
        _services = services;
        _handler = handler;
        _options = handler.Options;
        _logger = loggerFactory.CreateLogger("Singulink.Net.Http.Api.Service.SessionHandling");
    }

    public ValueTask ValidateIfDueAsync(HubCallerContext connection)
    {
        if (connection.GetHttpContext()?.GetSessionContext<TSessionToken>() is not HttpSessionContext<TSessionToken> sessionContext)
            return ValueTask.CompletedTask;

        return sessionContext.ValidateIfDueAsync();
    }

    public void Track(HubCallerContext connection, Type hubType)
    {
        if (connection.GetHttpContext()?.GetSessionContext<TSessionToken>() is not HttpSessionContext<TSessionToken> sessionContext)
            return;

        if (sessionContext.GetSessionToTrack() is not { } session)
            return;

        _connections[connection.ConnectionId] = new TrackedConnection(connection, hubType, sessionContext, session.Token, session.CheckedUtc);
        EnsureTimerStarted();
    }

    /// <summary>
    /// Gets the number of tracked connections whose session token matches the predicate, for tests, which wait for the tracking that follows the hub's
    /// <c>OnConnectedAsync</c>.
    /// </summary>
    internal int CountTracked(Func<TSessionToken, bool> predicate) => _connections.Values.Count(c => predicate(c.Token));

    public void Untrack(HubCallerContext connection)
    {
        _connections.TryRemove(connection.ConnectionId, out _);
        _endedConnections.TryRemove(connection.ConnectionId, out _);
    }

    public string? GetSessionEndedError(HubCallerContext connection) => _endedConnections.GetValueOrDefault(connection.ConnectionId);

    public void Dispose()
    {
        lock (_timerLock)
        {
            _timer?.Dispose();
            _timer = null;
        }

        _disposed.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _checkTask;
    }

    private void EnsureTimerStarted()
    {
        if (Volatile.Read(ref _timer) is not null)
            return;

        lock (_timerLock)
        {
            if (_timer is null && !_disposed.IsCancellationRequested)
                _timer = _handler.TimeProvider.CreateTimer(_ => OnTimer(), null, CheckInterval, CheckInterval);
        }
    }

    private void OnTimer()
    {
        lock (_checkLock)
        {
            // If the previous check is still running, another one runs when it is done.
            if (_checking)
            {
                _checkPending = true;
                return;
            }

            _checking = true;
        }

        _checkTask = RunChecksAsync();
    }

    private async Task RunChecksAsync()
    {
        await Task.Yield();

        while (true)
        {
            await CheckConnectionsAsync();

            lock (_checkLock)
            {
                if (!_checkPending || _disposed.IsCancellationRequested)
                {
                    _checking = false;
                    return;
                }

                _checkPending = false;
            }
        }
    }

    private async Task CheckConnectionsAsync()
    {
        try
        {
            var utcNow = _handler.UtcNow;
            var dueConnections = _connections.Values.Where(c => utcNow - c.CheckedUtc >= _options.RefreshAfter).ToList();

            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentChecks, CancellationToken = _disposed.Token };
            await Parallel.ForEachAsync(dueConnections, parallelOptions, CheckConnectionAsync);
        }
        catch (OperationCanceledException) when (_disposed.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Checks the session of a connection, and ends the connection, and the other connections with its token, if the session has ended. A live session's
    /// validity is extended (see <see cref="CheckSessionAsync"/>).
    /// </summary>
    private async ValueTask CheckConnectionAsync(TrackedConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            for (int attempt = 1; attempt <= MaxCheckAttempts; attempt++)
            {
                var (result, storeError) = await CheckSessionAsync(connection, cancellationToken);

                switch (result)
                {
                    case CheckResult.Live:
                        return;
                    case CheckResult.Ended:
                    {
                        // The session's other connections, which can have been checked more recently, end with it.
                        var error = storeError ?? SessionErrors.SignInRequired();
                        var token = connection.Token;

                        await Task.WhenAll(_connections.Values.Where(c => Equals(c.Token, token)).Select(c => EndConnectionAsync(c, error, cancellationToken)));
                        return;
                    }
                }
            }

            // The session kept changing under the check: it is checked again in the next round.
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "The session of hub connection {ConnectionId} could not be checked.", connection.Context.ConnectionId);
        }
    }

    /// <summary>
    /// Ends the connection of a session that ended. The connection is sent the session-ended message (<see cref="ToolkitHubMessages.SessionEnded"/>) with
    /// the error, which the toolkit's client handles by closing the connection itself, and is aborted if it is still open
    /// <see cref="SessionEndedCloseDelay"/> later. Meanwhile its hub method calls fail with the error. A connection that can't be sent the message is
    /// aborted right away.
    /// </summary>
    private async Task EndConnectionAsync(TrackedConnection connection, ApiException error, CancellationToken cancellationToken)
    {
        var context = connection.Context;

        // The connection closed, or another check ended it.
        if (!_connections.TryRemove(context.ConnectionId, out _))
            return;

        if (GetHubClients(connection.HubType) is not { } clients)
        {
            context.Abort();
            return;
        }

        string hubError = ResponseExceptionInfo.FromApiException(error).ToHubErrorString();
        _endedConnections[context.ConnectionId] = hubError;
        _ = CloseAfterDelayAsync(context);

        try
        {
            await clients.Client(context.ConnectionId).SendCoreAsync(ToolkitHubMessages.SessionEnded, [hubError], cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Hub connection {ConnectionId} could not be sent the end of its session.", context.ConnectionId);
            context.Abort();
        }
    }

    /// <summary>
    /// Aborts a connection whose session ended if it is still open <see cref="SessionEndedCloseDelay"/> from now.
    /// </summary>
    private async Task CloseAfterDelayAsync(HubCallerContext connection)
    {
        try
        {
            await Task.Delay(SessionEndedCloseDelay, _handler.TimeProvider, connection.ConnectionAborted);
            connection.Abort();
        }
        catch (OperationCanceledException)
        {
            // The connection closed.
        }
        finally
        {
            _endedConnections.TryRemove(connection.ConnectionId, out _);
        }
    }

    /// <summary>
    /// Gets the clients of a hub type from its <see cref="IHubContext{THub}"/>, or <see langword="null"/> if they can't be resolved.
    /// </summary>
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = HubContextJustification)]
    private IHubClients? GetHubClients(Type hubType)
    {
        return _hubClients.GetOrAdd(hubType, static (hubType, tracker) => {
            try
            {
                var hubContext = tracker._services.GetService(typeof(IHubContext<>).MakeGenericType(hubType));

                // IHubContext<THub> is covariant.
                return (hubContext as IHubContext<Hub>)?.Clients;
            }
            catch (Exception ex)
            {
                tracker._logger.LogWarning(ex, "The hub context of {HubType} could not be resolved.", hubType);
                return null;
            }
        }, this);
    }

    /// <summary>
    /// Looks up the session of a connection and extends its validity to a full expiry period from now (capped at its maximum lifetime), keeping its refresh
    /// time and generation. HTTP validation judges the session by the store's record, so the client's cookie stays valid while the connection is open. The
    /// refresh time is kept so that the grace period for cookies of the previous generation is still measured from the last rotation. Returns the exception
    /// of a store that ended the session by throwing one.
    /// </summary>
    private async Task<(CheckResult Result, UnauthorizedApiException? StoreError)> CheckSessionAsync(
        TrackedConnection connection, CancellationToken cancellationToken)
    {
        await using var scope = _handler.CreateStoreScope();
        var store = SessionHandler<TSessionToken>.GetStore(scope);

        // The connection's hub methods can have replaced the token by validating the session themselves.
        if (connection.Session.CurrentToken is { } currentToken)
            connection.Token = currentToken;

        var checkedToken = connection.Token;
        SessionLookupResult<TSessionToken>? lookup;

        try
        {
            lookup = await store.GetSessionAsync(checkedToken, cancellationToken);
        }
        catch (UnauthorizedApiException ex)
        {
            // The store ended the session.
            await store.InvalidateSessionAsync(checkedToken, cancellationToken);
            return (CheckResult.Ended, ex);
        }

        if (lookup is null)
            return (CheckResult.Ended, null);

        var session = lookup.Session;
        var utcNow = _handler.UtcNow;

        if (session.IsExpired(_options, utcNow))
        {
            await store.InvalidateSessionAsync(connection.Token, cancellationToken);
            return (CheckResult.Ended, null);
        }

        if (lookup.ReplacementToken is { } replacementToken)
        {
            connection.Token = replacementToken;
            await connection.Session.ApplyReplacementTokenAsync(checkedToken, replacementToken);
        }

        var expiresUtc = SessionTime.Add(utcNow, _options.GetSessionExpiry(session.IsPersistent));

        if (_options.GetLifetimeEndUtc(session.IsPersistent, session.CreatedUtc) is { } lifetimeEndUtc && lifetimeEndUtc < expiresUtc)
            expiresUtc = lifetimeEndUtc;

        if (expiresUtc > session.GetExpiresUtc(_options))
        {
            var extendedSession = new SessionRecord {
                Device = session.Device,
                IpAddress = session.IpAddress,
                RefreshedUtc = session.RefreshedUtc,
                ExpiresUtc = expiresUtc,
                Generation = session.Generation,
                IsPersistent = session.IsPersistent,
                CreatedUtc = session.CreatedUtc,
            };

            // A request that refreshed the session in the meantime changed its generation: read it again.
            if (!await store.TryRefreshSessionAsync(connection.Token, extendedSession, expectedGeneration: session.Generation, cancellationToken))
                return (CheckResult.Changed, null);
        }

        connection.CheckedUtc = utcNow;
        return (CheckResult.Live, null);
    }

    private enum CheckResult
    {
        Live,
        Ended,
        Changed,
    }

    private sealed class TrackedConnection(
        HubCallerContext context, Type hubType, HttpSessionContext<TSessionToken> session, TSessionToken token, DateTime checkedUtc)
    {
        public HubCallerContext Context { get; } = context;

        public Type HubType { get; } = hubType;

        public HttpSessionContext<TSessionToken> Session { get; } = session;

        public TSessionToken Token { get; set; } = token;

        public DateTime CheckedUtc { get; set; } = checkedUtc;
    }
}
