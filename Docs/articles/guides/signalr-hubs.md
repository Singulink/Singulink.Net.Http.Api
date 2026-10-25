<div class="article">

# SignalR Hubs

The toolkit adds strongly typed contracts, session integration and typed error reporting to SignalR hubs. This guide covers declaring a hub contract, using it on the server and the client, streaming, and authorizing connections.

### Packages

The contract definitions live in `Singulink.Net.Http.Api` so a data contracts assembly can declare them without depending on ASP.NET Core. The server extensions are in `Singulink.Net.Http.Api.Service` and the client connection is in `Singulink.Net.Http.Api.Client.SignalR`.

## Declaring a Hub Contract

A contract is a static class of definitions. Each definition captures the message name and the argument types, so both sides get compile-time checking of both. The name is taken from the declaring member automatically through <xref:System.Runtime.CompilerServices.CallerMemberNameAttribute>, so a definition is just `= new()`:

```csharp
public static class DocumentHub
{
    // Client to server
    public static HubMessage<Guid?, bool> UpdatePresence { get; } = new();
    public static HubMethod<long, DocumentSnapshot> Subscribe { get; } = new();
    public static HubStream<long, DocumentChange> Changes { get; } = new();

    // Server to client
    public static HubMessage<IReadOnlyList<PresenceInfo>> PresenceUpdated { get; } = new();
    public static HubMethod<string, string> RequestDraft { get; } = new();
}
```

There are three kinds:

- <xref:Singulink.Net.Http.Api.HubMessage> (and the generic variants up to four arguments) is fire-and-forget in either direction.
- <xref:Singulink.Net.Http.Api.HubMethod`1> (and variants with up to four arguments plus the result) is invoked and returns a result, in either direction.
- <xref:Singulink.Net.Http.Api.HubStream`1> (and variants with up to four arguments plus the item type) streams items from the server to the client.

A definition does not record its direction. A message sent from a client is handled by a hub method with the same name; a message sent from the server is handled by a client handler registered for the definition. Pass an explicit name to the constructor when the member name must differ from the wire name, for example to keep compatibility with existing clients.

> [!TIP]
> A hub method that takes its name from the definition, as with `[HubMethodName(nameof(DocumentHub.UpdatePresence))]`, fails to compile on the server too when the definition is renamed.

## Using the Contract on the Server

Hub methods are named after the client-to-server definitions and take the same argument types. Server-to-client messages are sent with <xref:Singulink.Net.Http.Api.Service.HubClientProxyExtensions.SendAsync*>, and methods are invoked on a single client with <xref:Singulink.Net.Http.Api.Service.HubClientProxyExtensions.InvokeAsync*>:

```csharp
public sealed class DocumentEditHub : Hub
{
    private long DocumentId => long.Parse((string)Context.GetHttpContext()!.Request.RouteValues["documentId"]!, CultureInfo.InvariantCulture);

    public async Task UpdatePresence(Guid? itemId, bool hasUnsavedEdits)
    {
        var snapshot = PresenceTracker.Update(Context.ConnectionId, itemId, hasUnsavedEdits);
        await Clients.Group(GroupName(DocumentId)).SendAsync(DocumentHub.PresenceUpdated, snapshot);
    }

    public Task<DocumentSnapshot> Subscribe(long documentId) => ...;

    public async IAsyncEnumerable<DocumentChange> Changes(long documentId, [EnumeratorCancellation] CancellationToken cancellationToken) { ... }

    public Task<string> GetDraftFromEditor(string editorConnectionId) =>
        Clients.Client(editorConnectionId).InvokeAsync(DocumentHub.RequestDraft, "latest", Context.ConnectionAborted);

    private static string GroupName(long documentId) => $"document-{documentId}";
}
```

A <xref:Singulink.Net.Http.Api.HubStream`1> is implemented by a hub method returning <xref:System.Collections.Generic.IAsyncEnumerable`1> of the item type. Client-to-server streaming needs no definition of its own: an <xref:System.Collections.Generic.IAsyncEnumerable`1> argument on a message or method is streamed to the server automatically by SignalR.

#### Reporting hub errors as API errors

Register <xref:Singulink.Net.Http.Api.Service.ApiExceptionHubFilter> so that exceptions thrown by hub methods, during streaming, and while connecting are reported to clients as the same typed errors as HTTP requests, mapped by the registered <xref:Singulink.Net.Http.Api.Service.IApiExceptionHandler>:

```csharp
services.AddSignalR(options => options.AddApiExceptionFilter());
```

Without the filter, SignalR masks exception details and the client receives a generic `HubException`. With it, an <xref:Singulink.Net.Http.Api.ApiException> thrown from a hub method arrives on the client as the same exception type, and unexpected exceptions are logged with a reference ID by the handler. See [Exception Handling](exception-handling.md).

#### Authorizing connections with the session

<xref:Singulink.Net.Http.Api.Service.HubCallerContextExtensions.GetRequiredSessionTokenAsync*> retrieves the session token for the connection. Call it in `OnConnectedAsync` to authorize the connection and join the appropriate groups:

```csharp
public override async Task OnConnectedAsync()
{
    var token = await Context.GetRequiredSessionTokenAsync<SessionToken>();

    if (!await CanReadAsync(token, DocumentId))
        throw new ForbiddenApiException("You do not have access to this document.");

    await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(DocumentId));
}
```

With the hub filter registered, a connection rejected this way surfaces on the client as the typed exception, which fails the connection's start (see [Using the Contract on the Client](#using-the-contract-on-the-client)). SignalR logs each connection that `OnConnectedAsync` refuses this way, as any exception thrown there, at Error level with the exception and its stack trace, under the category `Microsoft.AspNetCore.SignalR.HubConnectionHandler` ("Error when dispatching 'OnConnectedAsync' on hub."). The toolkit's API errors are logged there like other exceptions.

#### Confirming connections

SignalR completes its handshake before it runs the hub's `OnConnectedAsync`, which can still refuse the connection. <xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddApiResponseHandling*> and <xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddHttpSessionHandling*> each add a hub filter, once, that sends the connection a message named `Singulink.Net.Http.Api.Connected`, without arguments, once the hub's `OnConnectedAsync` has succeeded. The filter is added after the filters that the app adds to the hub options, so it runs inside them, and around the filters of a single hub and the hub itself. An app's filter of the hub options whose code after the hub's `OnConnectedAsync` fails then closes a connection that the client counted as connected already, which the client reports as a close. The toolkit's client counts a connection as connected once the message arrives. Other clients have no handler for it, which SignalR clients log as a warning, and otherwise ignore it.

> [!NOTE]
> Hub methods get the session token from the cookie that the connection was established with, which a connection cannot refresh. The session is looked up in the store when the cookie's refresh is due, as for a request, but only once on each connection, and on every call with <xref:Singulink.Net.Http.Api.Service.SessionAccessOptions.ForceValidate>, which uses the replacement token that the store returns. Session revalidation (below) validates a due cookie when the connection connects, and closes the connections of sessions that have ended.

#### Rechecking the sessions of open connections

In an app that registers SignalR, session handling rechecks the sessions of open connections against the session store, unless <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.RevalidateHubConnections?displayProperty=nameWithType> is `false`:

```csharp
services.AddHttpSessionHandling<SessionToken, SessionStore>(options => options.RevalidateHubConnections = false);
```

When a connection connects with a cookie that is due for a refresh, the session is validated against the store before the hub's `OnConnectedAsync`, as retrieving the session would, without the origin and user ID precondition checks. A session that has ended leaves the connection without one, and an <xref:Singulink.Net.Http.Api.UnauthorizedApiException> from the store fails the connection. The client receives the exception as the API exception filter reports exceptions, whether that filter is added or not.

Without revalidation, the connection reads its cookie when the session is first retrieved on it. A hub that first retrieves the session in a hub method, after the client's requests have refreshed the session, then presents a cookie of an older generation, which ends the session as a replayed cookie once <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.PreviousGenerationGracePeriod> has passed since the refresh.

The session token of each connection that has a session when it connects is kept; connections without one are not tracked. About once a minute, the sessions of the connections that were last checked at least <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.RefreshAfter?displayProperty=nameWithType> ago are looked up with <xref:Singulink.Net.Http.Api.Service.ISessionStore`1.GetSessionAsync*?displayProperty=nameWithType>:

- A connection whose session has ended (see [Ending Sessions](session-handling.md#ending-sessions)), has expired or has reached its maximum lifetime is closed, and so are the other connections with its session token, whether they are due or not. A connection is first sent a message named `Singulink.Net.Http.Api.SessionEnded` with one argument: the error that ended the session in the format of hub errors (see [Error Response Format](../concepts/error-format.md#signalr-hubs)), which is `401` with `sign-in-required`, also for a session that a sign-in replaced, which the check can't tell from one that ended otherwise, or the <xref:Singulink.Net.Http.Api.UnauthorizedApiException> that the store ended the session with. The toolkit's client closes the connection when it receives the message, and ends the session on the device, except that in the browser a connection with <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.StayConnected> first tries the browser's cookie (see [Waiting for a new session](#waiting-for-a-new-session)). A connection that is still open 5 seconds later is aborted, and its hub method calls fail with the error meanwhile. Session handling adds the type info of the message's argument to the payload options of SignalR's JSON protocol, after the app's resolvers, in the service and in the connections that <xref:Singulink.Net.Http.Api.Client.SignalRApiClientBase> creates, so the message doesn't depend on the app's type info.
- Other clients have no handler for the message, which SignalR clients log as a warning, and see the connection close without an error after the 5 seconds. When such a client reconnects, a session check that validates the session against the store rejects it with `401 Unauthorized` (`sign-in-required`). The check validates the session when the cookie is due for a refresh or the check forces validation; otherwise the new connection is rechecked once its cookie is due.
- An open connection counts as use of its session, so a live session does not expire while the connection is open: its expiry time is moved to a full expiry period after the check, capped at the end of its maximum lifetime, through <xref:Singulink.Net.Http.Api.Service.ISessionStore`1.TryRefreshSessionAsync*?displayProperty=nameWithType>. The session's refresh time and generation stay the same, since a connection cannot deliver a new cookie to the client. Requests judge the session by the store's record, so the client's cookie stays valid for its next request, which refreshes the session as usual. If a request refreshes the session while it is being checked, the session is looked up again.
- When the store returns a replacement token for a live session, the connection's hub methods get it from then on.

A session ends its connections at their next check however it ends: with a sign-out (<xref:Singulink.Net.Http.Api.Service.SessionContext`1.SignOutAsync*>), with a sign-in that replaces it, with a validation of a request that ends it, such as when the store throws an <xref:Singulink.Net.Http.Api.UnauthorizedApiException>, or when the app removes it from its store, such as another session of the user. Each server checks the connections that it has in the same way, so a connection is closed within about <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.RefreshAfter> and a minute of the end of its session, whichever server the request that ended it reached. The checks end connections, and don't revoke cookies: a client that connects again with the cookie of an ended session is accepted until the cookie is due for a refresh, as above, unless the hub forces validation.

## Using the Contract on the Client

A client deriving from <xref:Singulink.Net.Http.Api.Client.SignalRApiClientBase> creates connections with <xref:Singulink.Net.Http.Api.Client.SignalRApiClientBase.CreateHubConnection*>, which returns an <xref:Singulink.Net.Http.Api.Client.ApiHubConnection> that shares the client's session and has its user ID precondition in the URL, like the client's requests:

```csharp
public ApiHubConnection CreateDocumentEditConnection(long documentId) =>
    CreateHubConnection($"documents/{documentId}/edit-hub");
```

The connection exposes only the typed API. Handlers are registered with <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.On*>, messages are sent with <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.SendAsync*> (no wait) or <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.InvokeAsync*> (waits for the server, and returns the result for methods), and streams are read with <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.StreamAsync*>:

```csharp
await using var connection = client.CreateDocumentEditConnection(documentId);

connection.On(DocumentHub.PresenceUpdated, presence => UpdatePresenceList(presence));
connection.On(DocumentHub.RequestDraft, (string kind) => Editor.GetDraftText());
connection.Closed += error => { ShowDisconnected(error); return Task.CompletedTask; };

await connection.StartAsync();

var snapshot = await connection.InvokeAsync(DocumentHub.Subscribe, documentId);
await connection.SendAsync(DocumentHub.UpdatePresence, selectedItemId, false);

await foreach (var change in connection.StreamAsync(DocumentHub.Changes, documentId, cancellationToken))
    ApplyChange(change);
```

<xref:Singulink.Net.Http.Api.Client.ApiHubConnection.StartAsync*?displayProperty=nameWithType> completes once the service confirms that the hub accepted the connection (see [Confirming connections](#confirming-connections)), and <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.State> is `Connecting` until then. An exception that the hub's `OnConnectedAsync` throws, or the loss of the connection before the confirmation, fails the start: <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.StartAsync*> throws it, as for a start that fails before the handshake, and <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.Closed> doesn't occur. Cancelling the start stops a connection that the hub hasn't accepted yet, and a start whose connection the hub accepted before the cancellation completes, as SignalR's start does once it connected. A start that follows a stop waits until the close of the stopped connection is handled, which SignalR reports after its stop returns. The connection is made for the toolkit's services: with a service that doesn't send the confirmation, the start doesn't complete.

Errors reported by the hub filter are thrown as the matching <xref:Singulink.Net.Http.Api.ApiException> from invocations, stream enumeration and a start that the hub refuses, and delivered through <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.Closed?displayProperty=nameWithType> when the service closes a connection that it accepted. Other hub errors remain `HubException` instances. An error with the `sign-in-required` error code ends the session on the device, as for requests, unless the client's session token has changed since the connection's handshake, such as when a request refreshed the session; a later request then ends it when it validates the session against the store (see [Session Persistence](api-clients.md#session-persistence)). A session that the store ends by throwing an <xref:Singulink.Net.Http.Api.UnauthorizedApiException> (see [Ending Sessions](session-handling.md#ending-sessions)) is removed from the store, but a connection cannot clear the client's cookie, and the store's error code is its own: when a hub method or the check of a connecting connection ends it, the client keeps its session token until a request validates the session, which ends it with `sign-in-required`. When the service's recheck of an open connection ends it, the connection ends the session on the device whatever the error code (see [Rechecking the sessions of open connections](#rechecking-the-sessions-of-open-connections)). A request validates the session when the cookie is due for a refresh or the endpoint forces validation.

Connections use the WebSockets transport without a negotiate request (<xref:Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions.SkipNegotiation>). Other transports and Azure SignalR Service need the negotiate request, which the `configureOptions` callback of <xref:Singulink.Net.Http.Api.Client.SignalRApiClientBase.CreateHubConnection*> turns back on by setting `SkipNegotiation` to `false`. Without negotiation, the client does not receive a connection ID, so <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.ConnectionId?displayProperty=nameWithType> and the argument of <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.Reconnected> are `null`, and stateful reconnect is off.

The connection always presents the client's current session token when connecting or reconnecting, so a session refreshed by an HTTP request between reconnects does not invalidate the session.

#### Reconnecting and staying connected

A connection reconnects automatically with SignalR's default policy (override <xref:Singulink.Net.Http.Api.Client.SignalRApiClientBase.CreateHubConnectionBuilder*?displayProperty=nameWithType> to change it): after it loses its connection, it tries again immediately and after 2, 10 and 30 seconds, and then gives up and raises <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.Closed>. A reconnect counts once the service confirms that the hub accepted it, as for a start: <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.Reconnected> occurs then, and a reconnect that the hub refuses closes the connection without it. A <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.StartAsync*> that fails throws, and the connection doesn't try again.

With <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.StayConnected?displayProperty=nameWithType>, the connection keeps trying until the app stops or disposes it:

- A start that fails, such as when the service can't be reached or a proxy answers the WebSocket handshake with a `503` while the service restarts, doesn't throw: <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.StartAsync*> raises <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.Reconnecting> with the exception and returns. A start that fails with an API error that isn't transient, other than one that ends the session (see [Waiting for a new session](#waiting-for-a-new-session)), or with a `HubException` still throws.
- After the automatic reconnect gave up, or when the connection closes otherwise, it starts the connection again.
- The attempts are spaced like the automatic reconnect's: after a close, the first attempt is immediate, and the ones that follow are 2, 10 and 30 seconds after the attempt before, and then every 30 seconds. A failed start counts as the first attempt, and after the automatic reconnect gave up, the attempts are every 30 seconds. A connection that closes less than 30 seconds after it started, such as when the hub's `OnConnectedAsync` fails with a transient error, counts as a failed attempt, so the attempts after its close continue with the next delay instead of starting over. An attempt that follows a start or close with an API error that has a retry time (<xref:Singulink.Net.Http.Api.ApiException.RetryAfter>), such as a <xref:Singulink.Net.Http.Api.ServiceUnavailableApiException> thrown in `OnConnectedAsync`, waits at least that long. <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.State> is `Reconnecting` meanwhile, and <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.Reconnected> occurs once the hub accepts the connection. An attempt that the hub refuses after the handshake counts as a failed attempt, without the event.
- The connection stays closed when the service closes or refuses it with an error that isn't transient (see <xref:Singulink.Net.Http.Api.Client.ApiFailures.IsTransient*?displayProperty=nameWithType>), such as a <xref:Singulink.Net.Http.Api.ForbiddenApiException> thrown in `OnConnectedAsync`: <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.StartAsync*> throws an error of its own start, and <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.Closed> reports the others. An error that ends the session waits for a new session instead (see [Waiting for a new session](#waiting-for-a-new-session)).

Messages that the service sends while the connection isn't connected aren't delivered later. Here the app reloads its notifications when the connection reconnects:

```csharp
var connection = client.CreateNotificationsConnection();
connection.StayConnected = true;
connection.On(NotificationHub.Notified, notification => Notifications.Insert(0, notification));
connection.Reconnected += _ => ReloadNotificationsAsync();

await connection.StartAsync();
```

#### Waiting for a new session

With <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.StayConnected>, a connection that the service closes or refuses because its session ended, with an <xref:Singulink.Net.Http.Api.UnauthorizedApiException> with the `sign-in-required` error code, waits for a new session on the device, and then starts again. So does a connection whose session the service's recheck found ended (see [Rechecking the sessions of open connections](#rechecking-the-sessions-of-open-connections)), with the error that the service sent, without trying to start again before the new session. Meanwhile <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.State> is `Reconnecting`, <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.Reconnecting> reports the error unless the connection was already reconnecting, and the API client reports the end of the session with <xref:Singulink.Net.Http.Api.Client.ApiClientBase.OnSessionEnded*>, as for requests. In the browser, a connection that last connected before the last <xref:Singulink.Net.Http.Api.Client.ApiClientBase.NotifySessionStarted*> doesn't report its session's end, since that session was replaced, and starts again with the browser's current session without waiting for a new one (see [Session Persistence](api-clients.md#session-persistence)). Once the connection starts again, <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.Reconnected> occurs, so the handler that reloads the notifications above also runs after the wait. The attempt follows a session start at once, whatever delay the attempts before it had reached, and the attempts after it are spaced as after the close of a connection that was stable. An attempt that the end of a wait without a session start leads to, such as the browser's timed tries of its cookie below, also waits out its delay, while one that a request leads to doesn't.

In the browser, whose tabs share the session cookie, another tab can have signed in without the tab being told. A connection that the hub had accepted, and that closes because its session ended with `sign-in-required`, doesn't end the session in its tab yet: it tries again with the browser's cookie once a session starts in the tab, or 5 seconds after the close, whichever is first. With the same user's session it connects again, with another user's session a hub that checks its user ID precondition refuses it as below, and only a refusal with `sign-in-required` ends the session in the tab. After such a refusal, or a start or an automatic reconnect that the hub refuses with `sign-in-required`, the connection waits for a session start in its tab, and meanwhile tries the browser's cookie again every 5 minutes, without ending the session in the tab again.

During either wait, and during the wait of a connection that the hub refused with another user's session (see below), the tab's requests can lead to a try sooner. When a request of a client that shares the connection's session, which began during the wait, gets a successful response from the service, and has the connection's user ID precondition (see [User ID Preconditions](api-clients.md#user-id-preconditions)), the connection tries the browser's cookie at once, without waiting out the delay that its attempts have reached, which stay as they are. A connection without a precondition does so after a request with any precondition, since it connects with the browser's session whoever's it is, while a connection with a precondition is refused with another user's session. Requests without a precondition don't lead to a try. The response doesn't show that the browser has a session, since the service only checks the session of a request whose endpoint retrieves it, and a refused try that a response led to is handled like a refused timed try. A connection makes such a try at most once every 30 seconds: after a response that comes less than 30 seconds after its last such try, it tries once they have passed.

Each try of a tab without a session is a WebSocket handshake that the hub refuses when it retrieves the session in `OnConnectedAsync`, after a lookup in the session store when the browser still has the cookie of an ended session that is due for a refresh, or that the hub validates. SignalR logs each of these refusals at Error level (see [Authorizing connections with the session](#authorizing-connections-with-the-session)), so a tab that stays without a session leads to one entry for each of its connections with <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.StayConnected> 5 seconds after its check ends it, and then one every 5 minutes, 12 an hour, and up to one more every 30 seconds while the tab's requests with the connection's user ID precondition, or with any for a connection without one, succeed without a session, such as those of endpoints that don't retrieve it. A connection that the hub refused with another user's session leads to an entry at each session start in its tab, and up to one every 30 seconds while the tab's requests with its user ID precondition succeed, which they only do with another user's session at endpoints that don't retrieve the session.

A connection without a user ID precondition, which a hub that retrieves the session only accepts with <xref:Singulink.Net.Http.Api.Service.SessionAccessOptions.OptionalUserIdPrecondition?displayProperty=nameWithType>, connects after the end of its session with the browser's session, whoever's it is. When the browser has no session, a hub that accepts connections without one, such as with <xref:Singulink.Net.Http.Api.Service.HubCallerContextExtensions.GetSessionTokenAsync*>, accepts the connection without a session, also a connection with a precondition, which is only checked against a session. The session's end isn't reported in the tab then. The service doesn't track a connection without a session (see [Rechecking the sessions of open connections](#rechecking-the-sessions-of-open-connections)), so a later sign-in or sign-out doesn't affect it: it stays without a session until it connects again.

The API client reports a new session when a response sets the session cookie while it has none, and when a sign-in's response, which the service marks with a `Session-Change` header, sets it, and when the app calls <xref:Singulink.Net.Http.Api.Client.ApiClientBase.NotifySessionStarted*>. In the browser, which holds the session cookie, only the call reports one, as the sign-in methods in [Signing In and Restoring a Session](api-clients.md#signing-in-and-restoring-a-session) make it. A session that started after the connection last connected counts too.

The new session can be another user's. A connection of a child client with a user ID precondition (see [User ID Preconditions](api-clients.md#user-id-preconditions)) is then refused by a hub that retrieves the session in `OnConnectedAsync`, as in [Authorizing connections with the session](#authorizing-connections-with-the-session), with a <xref:Singulink.Net.Http.Api.UserChangedApiException> whose <xref:Singulink.Net.Http.Api.UserChangedApiException.ExpectedUserId> is the connection's precondition. With <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.StayConnected>, the connection then waits for the next session start, as after the end of its session, and tries again: it connects once the session is its user's again, and with another user's session it is refused once more and waits again, so each session start leads to at most one attempt. Without it, the connection closes. In the browser, the tab's requests with the connection's precondition that succeed also lead to an attempt (see above), so a sign-in of the connection's user in another tab brings the connection back at the tab's next such request, or at a session start in the tab. A connection without a precondition connects with the new user's session.

A connection that wraps a `HubConnection` of the app's own (see <xref:Singulink.Net.Http.Api.Client.ApiHubConnection>) has no API client to share a session with, and handles the error like the other errors that aren't transient instead.

#### Handlers on the app's thread

Handlers run on the thread that receives the message, as in SignalR. <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.SynchronizationContext?displayProperty=nameWithType> runs them, and the handlers of the connection's events, on a synchronization context, such as the one of an app's UI thread:

```csharp
connection.SynchronizationContext = SynchronizationContext.Current;
```

The connection waits for each handler, and for the task that an asynchronous handler returns, before it handles the next message, with or without a synchronization context.

#### Disposing

<xref:Singulink.Net.Http.Api.Client.ApiHubConnection.DisposeAsync*> stops the connection, and the attempts to start it again. A connection that fails to close, such as with a failed close handshake, is disposed without an exception.

<xref:Singulink.Net.Http.Api.Client.ApiHubConnection.UnderlyingConnection?displayProperty=nameWithType> exposes the SignalR `HubConnection` for anything the typed API does not cover. Errors from direct use of it are not translated.

## Next Steps

Continue with these related articles:

- [Exception Handling](exception-handling.md) - The handler that maps hub exceptions.
- [Session Handling](session-handling.md) - The session the hub authorizes against.
- [Error Response Format](../concepts/error-format.md) - How hub errors are encoded.

</div>
