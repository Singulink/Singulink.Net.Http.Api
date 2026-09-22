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
- <xref:Singulink.Net.Http.Api.HubMethod`1> (and variants with up to three arguments plus the result) is invoked and returns a result, in either direction.
- <xref:Singulink.Net.Http.Api.HubStream`1> (and variants with up to three arguments plus the item type) streams items from the server to the client.

A definition does not record its direction. A message sent from a client is handled by a hub method with the same name; a message sent from the server is handled by a client handler registered for the definition. Pass an explicit name to the constructor when the member name must differ from the wire name, for example to keep compatibility with existing clients.

> [!TIP]
> Group definitions per hub and use `nameof` on the hub method side, so a renamed definition fails to compile on the server too.

## Using the Contract on the Server

Hub methods are named after the client-to-server definitions and take the same argument types. Server-to-client messages are sent with <xref:Singulink.Net.Http.Api.Service.HubClientProxyExtensions.SendAsync*>, and methods are invoked on a single client with <xref:Singulink.Net.Http.Api.Service.HubClientProxyExtensions.InvokeAsync*>:

```csharp
public sealed class DocumentEditHub : Hub
{
    public async Task UpdatePresence(Guid? itemId, bool hasUnsavedEdits)
    {
        var snapshot = PresenceTracker.Update(Context.ConnectionId, itemId, hasUnsavedEdits);
        await Clients.Group(GroupName).SendAsync(DocumentHub.PresenceUpdated, snapshot);
    }

    public Task<DocumentSnapshot> Subscribe(long documentId) => ...;

    public async IAsyncEnumerable<DocumentChange> Changes(long documentId, [EnumeratorCancellation] CancellationToken cancellationToken) { ... }

    public Task<string> GetDraftFromEditor(string editorConnectionId) =>
        Clients.Client(editorConnectionId).InvokeAsync(DocumentHub.RequestDraft, "latest", Context.ConnectionAborted);
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
    long documentId = long.Parse(Context.GetHttpContext()!.Request.RouteValues["documentId"]!.ToString()!);

    if (!await CanReadAsync(token, documentId))
        throw new ForbiddenApiException("You do not have access to this document.");

    await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(documentId));
}
```

With the hub filter registered, a connection rejected this way surfaces on the client as the typed exception through the connection's closed event.

> [!NOTE]
> The session token is validated when the connection is established. Hub methods called later on the same connection see the token as it was at that time, so long-lived connections should authorize at the granularity that makes sense for the hub, typically per document or resource in `OnConnectedAsync`.

## Using the Contract on the Client

A client deriving from <xref:Singulink.Net.Http.Api.Client.SignalRApiClientBase> creates connections with <xref:Singulink.Net.Http.Api.Client.SignalRApiClientBase.CreateHubConnection*>, which returns an <xref:Singulink.Net.Http.Api.Client.ApiHubConnection> that shares the client's session and default query parameters:

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
await connection.SendAsync(DocumentHub.UpdatePresence, selectedItemId, hasUnsavedEdits: false);

await foreach (var change in connection.StreamAsync(DocumentHub.Changes, documentId, cancellationToken))
    ApplyChange(change);
```

Errors reported by the hub filter are thrown as the matching <xref:Singulink.Net.Http.Api.ApiException> from invocations and stream enumeration, and delivered through <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.Closed?displayProperty=nameWithType> when a connection is rejected. Other hub errors remain `HubException` instances.

The connection automatically reconnects by default (override <xref:Singulink.Net.Http.Api.Client.SignalRApiClientBase.CreateHubConnectionBuilder*?displayProperty=nameWithType> to change the policy) and always presents the client's current session token when connecting or reconnecting, so a session refreshed by an HTTP request between reconnects does not invalidate the session.

<xref:Singulink.Net.Http.Api.Client.ApiHubConnection.UnderlyingConnection?displayProperty=nameWithType> exposes the SignalR `HubConnection` for anything the typed API does not cover. Errors from direct use of it are not translated.

## Next Steps

Continue with these related articles:

- [Exception Handling](exception-handling.md) - The handler that maps hub exceptions.
- [Session Handling](session-handling.md) - The session the hub authorizes against.
- [Error Response Format](../concepts/error-format.md) - How hub errors are encoded.

</div>
