<div class="article">

# Building API Clients

<xref:Singulink.Net.Http.Api.Client.ApiClientBase> is the base class for clients of services built with the toolkit. This guide covers constructing a client, defining request methods, session persistence, user ID preconditions, child clients and platform behavior.

### What the base class handles

Every request created through the base class carries the session cookie and the user agent, and every response is checked for a refreshed session cookie and for error status codes, which are rethrown as <xref:Singulink.Net.Http.Api.ApiException> types. Derived classes only write the request methods.

## Constructing a Client

Derive from <xref:Singulink.Net.Http.Api.Client.ApiClientBase>, implement <xref:Singulink.Net.Http.Api.Client.ApiClientBase.GetBaseAddress*>, and choose a constructor based on how JSON should be serialized:

```csharp
public sealed class MyApiClient : ApiClientBase
{
    public MyApiClient(string? sessionToken = null, Action<string?>? sessionTokenChanged = null)
        : base(AppSerializerContext.Default, sessionToken, sessionTokenChanged) { }

    protected override Uri GetBaseAddress() => new("https://api.example.com/v1/");

    protected override string UserAgent => $"MyApp/{AppVersion} ({RuntimeInformation.OSDescription})";
}
```

The constructors that take an <xref:System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver> (typically a source-generated <xref:System.Text.Json.Serialization.JsonSerializerContext>) are compatible with trimming and native AOT and are the recommended choice. The constructors that take no resolver or a plain <xref:System.Text.Json.JsonSerializerOptions> use reflection-based serialization and are marked as incompatible with trimming. All of them apply web defaults (camel case, case-insensitive) unless overridden through the configuration callback.

Override <xref:Singulink.Net.Http.Api.Client.ApiClientBase.UserAgent> to identify the app; the service uses it to describe the device a session belongs to.

## Request Methods

<xref:Singulink.Net.Http.Api.Client.ApiClientBase.CreateRequest*> builds an <xref:System.Net.Http.HttpRequestMessage> for a path relative to the base address, with optional query string parameters passed as name/value tuples. Values are formatted invariantly; dates use the round-trip format and byte arrays are base64 encoded. <xref:Singulink.Net.Http.Api.Client.ApiClientBase.SendAsync*> sends it:

```csharp
public Task<List<DocumentInfo>> GetDocumentsAsync(long folderId, bool includeDeleted = false)
{
    var request = CreateRequest(HttpMethod.Get, "documents", ("folderId", folderId), ("includeDeleted", includeDeleted));
    return SendAsync<List<DocumentInfo>>(request);
}

public Task<DocumentInfo> CreateDocumentAsync(CreateDocumentCommand command)
{
    return SendAsync<DocumentInfo>(CreateRequest(HttpMethod.Post, "documents"), command);
}

public Task DeleteDocumentAsync(long id)
{
    return SendAsync(CreateRequest(HttpMethod.Delete, $"documents/{id}"));
}
```

The content argument is serialized as JSON, unless it is already an <xref:System.Net.Http.HttpContent> (for file uploads) or a string. The response type argument selects how the response is read:

- Any JSON-serializable type deserializes the body.
- <xref:System.String> returns the body as text.
- <xref:System.Net.Http.HttpResponseMessage> returns the response itself without reading the body, for callers that need headers or want to stream the content. The caller disposes it.
- The overload without a response type ignores the body.

<xref:Singulink.Net.Http.Api.Client.ApiClientBase.SendStreamingAsync*> consumes streaming endpoints; see [Streaming Responses](streaming-responses.md).

## Session Persistence

The session lives in a cookie that the base class stores as a string in <xref:Singulink.Net.Http.Api.Client.ApiClientBase.SessionToken> and sends with each request. The service refreshes it periodically, and the base class picks the new value up from the response.

To keep a user signed in across app restarts, pass the previously saved token to the constructor and supply a callback that saves changes:

```csharp
var client = new MyApiClient(settings.SessionToken, token => settings.SessionToken = token);
```

The callback is only invoked while <xref:Singulink.Net.Http.Api.Client.ApiClientBase.IsPersistentSession> is true. Set it from the user's "remember me" choice before signing in, and the sign-in response's cookie is persisted; leave it false and the session stays in memory only. Constructing the client with a saved token sets it to true automatically. The callback receives null when the session ends, so the saved value is cleared.

> [!NOTE]
> On browser platforms (Blazor WebAssembly) the browser's cookie jar holds the session and the token is never visible to the client. Requests are sent with credentials included, and everything else works the same.

## User ID Preconditions

Services expect each request to state which user the client believes is signed in, and reject the request with <xref:Singulink.Net.Http.Api.UserChangedApiException> or <xref:Singulink.Net.Http.Api.UserRequiredApiException> when that does not match the session. This protects against a client acting for the wrong user after the session changed underneath it, for example a shared device where a different user signed in.

Override <xref:Singulink.Net.Http.Api.Client.ApiClientBase.GetDefaultQueryParams*> to add the precondition to every request. The parameter name is <xref:Singulink.Net.Http.Api.Client.ApiClientBase.DefaultUserIdPreconditionQueryName> unless the service configured a different one:

```csharp
protected override ReadOnlySpan<(string Name, object? Value)> GetDefaultQueryParams(string path)
{
    return [(DefaultUserIdPreconditionQueryName, UserId)];
}
```

Default parameters are merged into every request and hub connection URL; per-call parameters with the same name take precedence.

### Child clients

A convenient structure is a root client that handles sign-in and exposes a per-user child client once the user is known. The child client is constructed with the parent-client constructor, shares its session state and serializer options, and is the one that adds the precondition:

```csharp
public sealed class UserApiClient : ApiClientBase
{
    public int UserId { get; }

    internal UserApiClient(int userId, MyApiClient parent) : base(parent)
    {
        UserId = userId;
    }

    protected override Uri GetBaseAddress() => MyApiClient.BaseAddress;

    protected override ReadOnlySpan<(string Name, object? Value)> GetDefaultQueryParams(string path) =>
        [(DefaultUserIdPreconditionQueryName, UserId)];
}
```

Endpoints called before the user is known, such as sign-in or "get current session", live on the root client and are declared with <xref:Singulink.Net.Http.Api.Service.SessionAccessOptions.OptionalUserIdPrecondition?displayProperty=nameWithType> on the service.

## Platform Behavior

All requests share one <xref:System.Net.Http.HttpClient> with a connection pool that refreshes DNS periodically. On non-browser platforms the base class applies the cookie and user agent headers itself; on browser platforms it relies on the browser and sends credentials with each request. Nothing else differs between platforms, and the same client class runs in desktop, mobile and browser apps.

## Next Steps

Continue with these related articles:

- [Exception Handling](exception-handling.md) - Catching the typed exceptions thrown by requests.
- [Streaming Responses](streaming-responses.md) - Consuming streamed results.
- [SignalR Hubs](signalr-hubs.md) - Adding hub connections to a client.

</div>
