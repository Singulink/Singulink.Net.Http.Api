<div class="article">

# Building API Clients

<xref:Singulink.Net.Http.Api.Client.ApiClientBase> is the base class for clients of services built with the toolkit. This guide covers constructing a client, defining request methods, request paths, session persistence, child clients with user ID preconditions, signing in and restoring a session, and platform behavior.

### What the base class handles

Every request created through the base class carries the user ID precondition if the client has one, and on non-browser platforms the session cookie and the user agent; browsers send their own. Every response is checked for a refreshed session cookie and for error status codes, which are rethrown as <xref:Singulink.Net.Http.Api.ApiException> types. Derived classes only write the request methods.

## Constructing a Client

Derive from <xref:Singulink.Net.Http.Api.Client.ApiClientBase>, implement <xref:Singulink.Net.Http.Api.Client.ApiClientBase.GetBaseAddress*>, and choose a constructor based on how JSON should be serialized:

```csharp
public sealed class MyApiClient : ApiClientBase
{
    public static Uri BaseAddress { get; } = new("https://api.example.com/v1/");

    public MyApiClient(string? sessionToken = null, Action<string?>? sessionTokenChanged = null)
        : base(AppSerializerContext.Default, sessionToken, sessionTokenChanged) { }

    protected override Uri GetBaseAddress() => BaseAddress;
}
```

Request paths are resolved against the base address. A base address whose path has no trailing slash, such as `https://api.example.com/v1`, is used as if it had one.

The constructors that take an <xref:System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver> (typically a source-generated <xref:System.Text.Json.Serialization.JsonSerializerContext>) are compatible with trimming and native AOT. The constructors that take no resolver or a plain <xref:System.Text.Json.JsonSerializerOptions> use reflection-based serialization and are marked as incompatible with trimming.

The constructors that take a resolver create options with web defaults (camel case, case-insensitive) and the toolkit's null handling, which <xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddApiJson*> applies on the service: a response with a missing or `null` value where the contract does not allow one throws a <xref:System.Text.Json.JsonException>, and so does a command with a `null` in a property that does not allow one, when the request is sent (see [JSON Serialization](json-serialization.md)). Their configuration callback runs after the null handling is applied, so the settings it makes take precedence. The constructors without options use web defaults without the null handling, and the constructors that take <xref:System.Text.Json.JsonSerializerOptions> use the options as they are.

<xref:Singulink.Net.Http.Api.Client.ApiClientBase.UserAgent> is sent with every request and hub connection handshake on non-browser platforms; the service uses it to describe the device a session belongs to. By default it is the name and version of the app's entry assembly followed by the operating system, such as `MyApp/1.2.0.0 (Microsoft Windows 10.0.26100)`, or the name and version of the client library in an app without an entry assembly, such as a .NET for Android app. <xref:Singulink.Net.Http.Api.Client.ApiClientBase.CreateUserAgent*> creates a user agent of the same form with another product name and version, such as `MyApp/2.1.0 (Android 15)` for `CreateUserAgent("MyApp", "2.1.0")`. <xref:Singulink.Net.Http.Api.Client.ApiClientBase.SessionCookieName> (`__Host-session-token` by default) and <xref:Singulink.Net.Http.Api.Client.ApiClientBase.UserIdPreconditionQueryName> (`if-userId` by default) must match the service's <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions>, and can be overridden if the service changed them.

<xref:Singulink.Net.Http.Api.Client.ApiClientBase.RequestTimeout> limits how long a request can take, 100 seconds by default: until the response is read, or until it starts for a streaming response or a request that returns the <xref:System.Net.Http.HttpResponseMessage>. A request that times out throws a <xref:System.Threading.Tasks.TaskCanceledException> whose inner exception is a <xref:System.TimeoutException>. <xref:System.Threading.Timeout.InfiniteTimeSpan?displayProperty=nameWithType> and values longer than about 24.8 days (<xref:System.Int32.MaxValue?displayProperty=nameWithType> milliseconds) mean no timeout, and requests throw an <xref:System.InvalidOperationException> for zero, and for negative values other than <xref:System.Threading.Timeout.InfiniteTimeSpan?displayProperty=nameWithType>. A request can have a timeout of its own (see [Request Methods](#request-methods)).

## Request Methods

The request helpers send a request with one call. <xref:Singulink.Net.Http.Api.Client.ApiClientBase.GetAsync*>, <xref:Singulink.Net.Http.Api.Client.ApiClientBase.PostAsync*>, <xref:Singulink.Net.Http.Api.Client.ApiClientBase.PutAsync*>, <xref:Singulink.Net.Http.Api.Client.ApiClientBase.PatchAsync*> and <xref:Singulink.Net.Http.Api.Client.ApiClientBase.DeleteAsync*> take a path relative to the base address, content for the methods that send it, query string parameters and a cancellation token. Each has a form that returns the response as a type argument, and one that returns nothing:

```csharp
public Task<DocumentInfo> GetDocumentAsync(long id) => GetAsync<DocumentInfo>($"documents/{id}");

public Task<List<DocumentInfo>> GetDocumentsAsync(long folderId, bool includeDeleted, CancellationToken cancellationToken) =>
    GetAsync<List<DocumentInfo>>("documents", [("folderId", folderId), ("includeDeleted", includeDeleted)], cancellationToken);

public Task<DocumentInfo> CreateDocumentAsync(CreateDocumentCommand command) => PostAsync<DocumentInfo>("documents", command);

public Task<DocumentInfo> RenameDocumentAsync(long id, RenameDocumentCommand command) => PutAsync<DocumentInfo>($"documents/{id}", command);

public Task ArchiveDocumentAsync(long id, CancellationToken cancellationToken) => PostAsync($"documents/{id}/archive", cancellationToken);

public Task DeleteDocumentAsync(long id) => DeleteAsync($"documents/{id}");
```

Query string parameters are name/value tuples in a collection expression, as in `[("folderId", folderId), ("includeDeleted", includeDeleted)]` above. The same parameter takes them in <xref:Singulink.Net.Http.Api.Client.ApiClientBase.CreateRequest*>, <xref:Singulink.Net.Http.Api.Client.ApiClientBase.GetApiUrl*> and <xref:Singulink.Net.Http.Api.Client.SignalRApiClientBase.CreateHubConnection*>. Values are formatted with the invariant culture; dates use the round-trip format, byte arrays are base64 encoded, and parameters with a `null` value are left out.

The content argument is serialized as JSON, unless it is an <xref:System.Net.Http.HttpContent>, which is sent as it is, or a string, which is sent as text (`text/plain`). Minimal API endpoints read a `[FromBody] string` parameter as JSON, so they answer a string with a `415` (<xref:Singulink.Net.Http.Api.UnsupportedMediaTypeApiException>). <xref:System.Net.Http.Json.JsonContent.Create*?displayProperty=nameWithType> sends a string as JSON, with the type info of <xref:System.String> from a serializer context that lists it, as a trimmed app needs:

```csharp
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(DocumentInfo))]
internal sealed partial class AppSerializerContext : JsonSerializerContext;

public Task SetDocumentNoteAsync(long id, string note) => PutAsync($"documents/{id}/note", JsonContent.Create(note, AppSerializerContext.Default.String));
```

The response type argument selects how the response is read:

- Any JSON-serializable type deserializes the body.
- <xref:System.String> returns the body as text.
- <xref:System.Net.Http.HttpResponseMessage> returns the response itself once its headers are read, without reading the body. The caller disposes it.
- The forms without a response type ignore the body.

<xref:Singulink.Net.Http.Api.Client.ApiClientBase.CreateRequest*> and <xref:Singulink.Net.Http.Api.Client.ApiClientBase.SendAsync*> are the two steps the helpers combine. Creating the request separately gives access to it before it is sent, for example to add headers:

```csharp
public Task<ReportInfo> GetReportAsync(long id, string language)
{
    var request = CreateRequest(HttpMethod.Get, $"reports/{id}");
    request.Headers.AcceptLanguage.ParseAdd(language);

    return SendAsync<ReportInfo>(request);
}
```

<xref:Singulink.Net.Http.Api.Client.ApiClientBase.SetRequestTimeout*> gives a request a timeout of its own, which <xref:Singulink.Net.Http.Api.Client.ApiClientBase.SendAsync*> and <xref:Singulink.Net.Http.Api.Client.ApiClientBase.SendStreamingAsync*> use instead of <xref:Singulink.Net.Http.Api.Client.ApiClientBase.RequestTimeout>, with the same values. Here an upload has no timeout, so it takes as long as the connection needs, and the caller's token cancels it:

```csharp
public Task<FileInfo> UploadFileAsync(Stream file, string name, CancellationToken cancellationToken)
{
    var request = CreateRequest(HttpMethod.Post, "files", [("name", name)]);
    SetRequestTimeout(request, Timeout.InfiniteTimeSpan);

    return SendAsync<FileInfo>(request, new StreamContent(file), cancellationToken);
}
```

<xref:Singulink.Net.Http.Api.Client.ApiClientBase.UploadAsync*> and <xref:Singulink.Net.Http.Api.Client.ApiClientBase.UploadStreamingAsync*> upload a file to an upload endpoint in chunks instead, each in a request of its own, and send again the chunks whose requests fail (see [Chunked Uploads in Client Apps](uploads-client.md)).

<xref:Singulink.Net.Http.Api.Client.ApiClientBase.CreateRequest*> is virtual, so a client can also change every request it creates. <xref:Singulink.Net.Http.Api.Client.ApiClientBase.GetStreamingAsync*> and <xref:Singulink.Net.Http.Api.Client.ApiClientBase.PostStreamingAsync*> send a request to a streaming endpoint and return its items, and <xref:Singulink.Net.Http.Api.Client.ApiClientBase.SendStreamingAsync*> sends one created with <xref:Singulink.Net.Http.Api.Client.ApiClientBase.CreateRequest*>; see [Streaming Responses](streaming-responses.md).

### Paths

The compiler builds interpolated paths with <xref:Singulink.Net.Http.Api.Client.InternalServices.InterpolatedApiPathHandler>: the literal parts are used as written and each value is formatted with the invariant culture, like a query string value, and then escaped. A value such as `a/b c` becomes `a%2Fb%20c`, so characters such as `/`, `?` and `#` in a value don't start a new segment, a query or a fragment. Byte arrays are base64url encoded without padding, unlike in query strings, since ASP.NET Core unescapes route values except for `%2F`: a `/` in a value reaches the endpoint as `%2F`. The `raw` format skips the escaping:

```csharp
public Task<FileInfo> GetFileAsync(long folderId, string name) => GetAsync<FileInfo>($"folders/{folderId}/files/{name}");

public Task<FileInfo> GetFileByPathAsync(string relativePath) => GetAsync<FileInfo>($"files/{relativePath:raw}");
```

A string converts to a path as is, without escaping.

Escaping leaves dots as they are, so a value other than a `raw` one that is `.` or `..` throws an <xref:System.ArgumentException>. So does a path that leads outside the base address, such as `../x`, `//host/x`, an absolute URI of another address, or `/x` when the base address has a path. A path whose first segment has a colon, such as `items:batch` or `a:b`, is read as an absolute URI, so it throws one too; `./items:batch` is relative.

## Session Persistence

The session lives in a cookie that the base class stores as a string in <xref:Singulink.Net.Http.Api.Client.ApiClientBase.SessionToken> and sends with each request. The service refreshes it periodically, and the base class picks the new value up from the response. Only responses from the host of the base address can set or clear it.

To keep a user signed in across app restarts, pass the previously saved token to the constructor and supply a callback that saves changes. In WinUI 3 and Uno Platform apps, <xref:Singulink.Net.Http.Api.Client.SessionTokenStore> in `Singulink.Net.Http.Api.Client.WinUI` keeps the token: on Android in the app's password vault, on iOS in a keychain item that only the device can read, on Uno Platform desktop in the app's local settings, encrypted with DPAPI on Windows, and in Windows App SDK apps in a file encrypted with DPAPI, by default in a folder named after the app's entry assembly in the user's local application data folder (see <xref:Singulink.Net.Http.Api.Client.SessionTokenStore.FolderPath>). WebAssembly apps, whose browser holds the session cookie, keep none:

```csharp
var tokens = new SessionTokenStore(errorHandler: ex => logger.LogWarning(ex, "The session token could not be read or stored."));
var client = new MyApiClient(tokens.Read(), tokens.Write);
```

Its reads and writes are best effort: a failure, such as a keychain that can't be read or written while the device is locked, goes to the error handler instead of failing the request that changed the token, and the session goes on in memory. A stored token that can't be decrypted, such as one that a backup carried to another device, reads as `null`.

Whether a session is persistent is decided by the service when it signs the user in: it issues the cookie of a persistent session with an expiry (`Max-Age`), and the cookie of any other session without one. <xref:Singulink.Net.Http.Api.Client.ApiClientBase.IsPersistentSession> tells which applies. The callback is called with the token when a persistent session starts or its token changes, and with `null` when the stored token should be deleted: when the session ends, or when a session that isn't persistent replaces a persistent one. A token passed to the constructor is a persistent session's. The callback runs as part of the request or call that changed the token, such as the request whose response set the cookie, and an exception that it throws propagates to it.

The service ends the session on the device by clearing the cookie. Hub connections and streaming responses that have started can't clear it, so a <xref:Singulink.Net.Http.Api.UnauthorizedApiException> with the `sign-in-required` error code (<xref:Singulink.Net.Http.Api.ApiErrorCodes.SignInRequired>) ends the session on the device as well, unless the token has changed since the request or connection used it. When the error that closes a hub connection or makes it reconnect ends the session, an exception that the callback throws doesn't keep the connection's <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.Closed> or <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.Reconnecting> handlers from running: it is thrown after them, to the event of the underlying `HubConnection`. A start that fails with the error, also one that the hub refuses after the handshake, throws the callback's exception instead.

<xref:Singulink.Net.Http.Api.Client.ApiClientBase.OnSessionEnded*> is called when the session ends on the device: when a response clears the cookie, when a request, streaming response or hub connection that used the current token fails with `sign-in-required`, and when <xref:Singulink.Net.Http.Api.Client.ApiClientBase.ClearSessionToken*> drops a token. The client takes the session cookie that a response sets or clears only if its request sent the current token, so the late response of a request whose session a sign-in replaced while it ran changes nothing. The responses of a sign-in and a sign-out are the exception: the service marks them with a `Session-Change` header (`sign-in` or `sign-out`), and the client takes the cookie that they set or delete in any order, also after a response of the replaced session ended the session on the device, which calls <xref:Singulink.Net.Http.Api.Client.ApiClientBase.OnSessionEnded*> first, and after a refresh of the session that a sign-out ends. Between the two, the last response wins: a sign-in sent while a sign-out still runs can lose its new session on the device if the sign-out's response arrives last, and the new session then stays in the store until it expires. Waiting for the sign-out to finish before signing in again avoids it. A header with another value doesn't mark a response. It is called on the client without a parent, whose child clients share its session, after the session token changed callback:

```csharp
public event EventHandler? SessionEnded;

protected override void OnSessionEnded()
{
    UserClient = null;
    SessionEnded?.Invoke(this, EventArgs.Empty);
}
```

<xref:Singulink.Net.Http.Api.Client.ApiClientBase.ClearSessionToken*> drops the token and calls the callback with `null` without a request to the service, so a sign-out can end the session on the device whether or not its request succeeded. The service checks the user ID precondition of a sign-out only when the request has one, so the root client, which has none, can sign out:

```csharp
public async Task SignOutAsync()
{
    try
    {
        await PostAsync("sessions/sign-out");
    }
    finally
    {
        ClearSessionToken();
    }
}
```

> [!NOTE]
> On browser platforms (WebAssembly) the browser's cookie jar holds the session and the token is never visible to the client: <xref:Singulink.Net.Http.Api.Client.ApiClientBase.SessionToken> is `null`, <xref:Singulink.Net.Http.Api.Client.ApiClientBase.IsPersistentSession> is `false` and the session token changed callback is never called. Requests are sent with credentials included, and <xref:Singulink.Net.Http.Api.Client.ApiClientBase.ClearSessionToken*> does nothing. Since the client can't tell which session an error ended, <xref:Singulink.Net.Http.Api.Client.ApiClientBase.OnSessionEnded*> is called for every `sign-in-required` error and session-ended report of a request that began, or a hub connection that last connected, after the last <xref:Singulink.Net.Http.Api.Client.ApiClientBase.NotifySessionStarted*>: the others concern the session that the new one replaced. A request that begins, or a connection that connects, between the response that starts a session and that call counts as one of the session before it, and an error of the session before it that is handled before the call, such as of a request of that session that fails while the sign-in runs, still ends the session on the device. The browser applies the cookies of responses in the order they arrive. The service keeps the cookie of a browser's request that finds its session ended when the cookie is due, and reissues a cookie only while its session exists, so that the late response of a request of the session before it doesn't delete or replace the new session's cookie in most cases: a forced validation of a cookie that isn't due still deletes it, and so does a browser that doesn't send fetch metadata (see [Ending Sessions](session-handling.md#ending-sessions)). Only <xref:Singulink.Net.Http.Api.Client.ApiClientBase.NotifySessionStarted*> reports a new session (see [Signing In and Restoring a Session](#signing-in-and-restoring-a-session)).
>
> The tabs of a browser share its session cookie, so a sign-in in one tab replaces the session of all of them, and a sign-out in one tab ends it in all of them. The hub connections of the ended session close at their next session check, on every server alike, up to <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.RefreshAfter> and a minute later, with `sign-in-required`, since the check can't tell a replaced session from one that ended otherwise (see [Rechecking the sessions of open connections](signalr-hubs.md#rechecking-the-sessions-of-open-connections)). A connection with <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.StayConnected> that the hub had accepted doesn't end the session in its tab then: it tries again with the browser's cookie once a session starts in its tab, or 5 seconds after the close. With the same user's session it connects again. With another user's session, a hub that checks the connection's user ID precondition refuses it with a <xref:Singulink.Net.Http.Api.UserChangedApiException>, and the connection waits for the next session start in the tab, or for a request of the tab with its precondition that succeeds (see [Waiting for a new session](signalr-hubs.md#waiting-for-a-new-session)), while a connection without a precondition connects with that session. Only a refusal with `sign-in-required`, when the browser has no session, ends the session in the tab, where <xref:Singulink.Net.Http.Api.Client.ApiClientBase.OnSessionEnded*> is called. The connection then waits for a session start in its tab, and meanwhile tries the browser's cookie again every 5 minutes, which a sign-in in another tab can have set, and at once when a request of the tab with the connection's user ID precondition succeeds, at most once every 30 seconds (see [Waiting for a new session](signalr-hubs.md#waiting-for-a-new-session)). A tab without such hub connections isn't told, and its requests carry the browser's cookie.

## Child Clients

A child client is created with the constructor that takes a parent. It shares the parent's session and serializer options, and by default its base address, request timeout, user agent, session cookie name, user ID precondition query name, user ID precondition and HTTP handler (see [Platform Behavior](#platform-behavior)). A child can add a path to the parent's base address, and values in it are escaped like values in request paths:

```csharp
public sealed class RepositoryClient : ApiClientBase
{
    internal RepositoryClient(ApiClientBase parent, long repositoryId) : base(parent, $"repos/{repositoryId}/") { }

    public Task<List<IssueInfo>> GetIssuesAsync() => GetAsync<List<IssueInfo>>("issues");
}
```

A trailing slash is added to the path if it has none. Child clients can be nested, and each adds its path to its parent's base address. A path that leads outside the parent's base address makes the child throw an <xref:System.InvalidOperationException> when it builds a URL.

### User ID Preconditions

Services expect each request to state which user the client believes is signed in, and reject the request with <xref:Singulink.Net.Http.Api.UserChangedApiException> or <xref:Singulink.Net.Http.Api.UserRequiredApiException> when that does not match the session. This protects against a client acting for the wrong user after the session changed underneath it, for example on a shared device where a different user signed in.

A child client created with a user ID adds the precondition to the query string of every request and hub connection URL, and so do its own children:

```csharp
public sealed class UserApiClient : ApiClientBase
{
    internal UserApiClient(MyApiClient parent, int userId) : base(parent, userIdPrecondition: userId) { }

    public Task<List<DocumentInfo>> GetDocumentsAsync() => GetAsync<List<DocumentInfo>>("documents");

    public RepositoryClient GetRepository(long repositoryId) => new(this, repositoryId);
}
```

The user ID is formatted like a query string value, with the invariant culture, and the service compares it with the string form of its <xref:Singulink.Net.Http.Api.Service.ISessionToken.UserId?displayProperty=nameWithType>. A string is used as it is. A query string parameter of the same name in a request replaces the precondition, and one with a `null` value leaves it out.

Requests from a client without a user ID carry no precondition. The service accepts them on endpoints declared with <xref:Singulink.Net.Http.Api.Service.SessionAccessOptions.OptionalUserIdPrecondition?displayProperty=nameWithType>, such as sign-in or "get current session", which are called before the user is known.

A request, streaming response, upload or hub connection that the service refuses because the session is another user's fails with a <xref:Singulink.Net.Http.Api.UserChangedApiException> whose <xref:Singulink.Net.Http.Api.UserChangedApiException.ExpectedUserId> is its precondition, as the client sent it, or `null` if it had none. The error that the service sends doesn't carry it, and doesn't say whose session the service found.

## Signing In and Restoring a Session

A client signs the user in through the app's sign-in endpoint, and creates the child client for the user with the user ID from its response. At startup, a client that was constructed with a stored session token, or one in the browser, which holds the session cookie, asks the service for the current session through an endpoint declared with <xref:Singulink.Net.Http.Api.Service.SessionAccessOptions.OptionalUserIdPrecondition?displayProperty=nameWithType>, and creates the child client the same way:

```csharp
public sealed class MyApiClient : ApiClientBase
{
    // ...

    public UserApiClient? UserClient { get; private set; }

    public async Task SignInAsync(SignInCommand command)
    {
        var session = await PostAsync<CurrentSessionInfo>("sessions", command);
        UserClient = new UserApiClient(this, session.UserId);
        NotifySessionStarted();
    }

    public async Task RedeemSignInCodeAsync(RedeemSignInCodeCommand command, CancellationToken cancellationToken = default)
    {
        var session = await PostAsync<CurrentSessionInfo>("sessions/sign-in-code", command, cancellationToken);
        UserClient = new UserApiClient(this, session.UserId);
        NotifySessionStarted();
    }

    public async Task<bool> RestoreSessionAsync()
    {
        if (SessionToken is null && !OperatingSystem.IsBrowser())
            return false;

        try
        {
            var session = await GetAsync<CurrentSessionInfo>("sessions/current");
            UserClient = new UserApiClient(this, session.UserId);

            return true;
        }
        catch (UnauthorizedApiException ex) when (ex.ErrorCode == ApiErrorCodes.SignInRequired)
        {
            return false;
        }
    }

    protected override void OnSessionEnded() => UserClient = null;
}
```

Only an <xref:Singulink.Net.Http.Api.UnauthorizedApiException> with the `sign-in-required` error code (<xref:Singulink.Net.Http.Api.ApiErrorCodes.SignInRequired>) says that the request has no session. Other `401` errors, such as a rejected sign-in credential, leave the session as it is (see [Catching Errors on the Client](exception-handling.md#catching-errors-on-the-client)). A sign-in that ends with a sign-in code, such as a passkey sign-in (see [Passkeys in Client Apps](passkeys-client.md#signing-in)) or a device link (see [Device Linking](sign-in-codes.md#device-linking)), redeems it with the standard <xref:Singulink.Net.Http.Api.DataContracts.RedeemSignInCodeCommand>, and creates the child client from the response, as `RedeemSignInCodeAsync` above does.

<xref:Singulink.Net.Http.Api.Client.ApiClientBase.NotifySessionStarted*> reports the new session to the hub connections that wait for one after theirs ended (see [Waiting for a new session](signalr-hubs.md#waiting-for-a-new-session)). On non-browser platforms the client also reports it itself, when the response sets the session cookie while the client has none, and when the response of a sign-in, which the service marks with a `Session-Change` header, sets it; in the browser, which holds the cookie, only the call reports it.

## Platform Behavior

The API client library supports .NET 10 and later, and .NET Framework 4.6.2 and later (through its .NET Standard 2.0 build). The same client class runs in desktop, mobile and browser apps on .NET 10, and in .NET Framework apps. Browser (WebAssembly) apps must target .NET 10 or later. Other runtimes that can load .NET Standard 2.0 libraries, such as .NET 8 and 9, get a build warning: they are not supported or tested. The SignalR client library supports .NET 10 and later only.

All requests share one <xref:System.Net.Http.HttpClient> whose connections are periodically replaced so that DNS changes are picked up. The client reads the body of a response to the end before it disposes the response, also for a request without a result and for a streaming response after its end record or an error record, for up to 5 seconds: in the browser, disposing a response whose body wasn't read aborts its request, which the browser reports as a failed request. In the browser, it also reads the rest of a streaming response whose enumeration ends at an item, in the background (see [Streaming Responses](streaming-responses.md)). Chrome reports a response without a body, such as a `204`, as cancelled (`net::ERR_ABORTED`) in its network log unless the body is read in the task that receives the response, which the client can't do: such a request succeeds, and Chrome writes no error for it to the console. On non-browser platforms the base class applies the cookie and user agent headers itself, and redirects are not followed: a `3xx` response throws an <xref:Singulink.Net.Http.Api.ApiException> (see [Catching Errors on the Client](exception-handling.md#catching-errors-on-the-client)). On browser platforms the base class relies on the browser, which follows redirects, and sends credentials with each request.

<xref:Singulink.Net.Http.Api.Client.ApiClientBase.HttpHandler> sends a client's requests through a handler of its own instead of the shared one. A derived client sets it in a constructor, and its child clients use it too. The handler receives each request with the session cookie and user agent headers that the client set, and its response is handled like the shared handler's, which doesn't follow redirects or handle cookies. Hub connections don't use it. <xref:Singulink.Net.Http.Api.Client.ApiErrorResponse.Create*?displayProperty=nameWithType> creates a response in the toolkit's error format, which the client throws as the exception it was created from:

```csharp
public sealed class MyApiClient : ApiClientBase
{
    // ...

    internal MyApiClient(HttpMessageHandler handler) : base(AppSerializerContext.Default) => HttpHandler = handler;
}
```

```csharp
// A handler that answers every request with a 404 in the toolkit's format: the client's requests throw NotFoundApiException.
var client = new MyApiClient(new DelegateHandler(request => ApiErrorResponse.Create(new NotFoundApiException("The document does not exist."))));
```

A few details depend on the runtime or the compiler:

- Interpolated paths are built with <xref:Singulink.Net.Http.Api.Client.InternalServices.InterpolatedApiPathHandler>, which escapes their values, in C# 10 and later. Older compilers format them as strings first, with the current culture and without escaping.
- `DateOnly` and `TimeOnly` values use the round-trip format. .NET Framework does not have these types.
- A streaming response that ends without its terminal record throws <xref:System.Net.Http.HttpIOException> on .NET 10 and later, and <xref:System.IO.IOException> (its base class) on .NET Framework. Catch <xref:System.IO.IOException> to handle both.

## Upgrading from 3.x

See [Upgrading from 3.x](upgrading.md#clients) for the changes to API clients.

## Next Steps

Continue with these related articles:

- [Exception Handling](exception-handling.md) - Catching the typed exceptions thrown by requests.
- [JSON Serialization](json-serialization.md) - The null handling of the client's serializer options.
- [Streaming Responses](streaming-responses.md) - Consuming streamed results.
- [Chunked Uploads in Client Apps](uploads-client.md) - Uploading files in chunks.
- [SignalR Hubs](signalr-hubs.md) - Adding hub connections to a client.

</div>
