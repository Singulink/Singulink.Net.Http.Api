<div class="article">

# Trimming and Native AOT

The packages are annotated for trimming and Native AOT. APIs that use reflection-based serialization or runtime code generation carry `RequiresUnreferencedCode` and `RequiresDynamicCode`, so in an app that is trimmed or published with Native AOT, the analyzers warn where the app calls them. The rest of the packages give no trim or AOT warnings. The app provides JSON type info for its own types, such as a source-generated serializer context.

## Clients

The <xref:Singulink.Net.Http.Api.Client.ApiClientBase> constructors that take an <xref:System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver> serialize with it. The others use reflection-based serialization and are annotated (see [Constructing a Client](api-clients.md#constructing-a-client)).

Without reflection-based serialization, the resolver needs type info for every type that the client writes or reads as JSON, and a type without it throws a <xref:System.NotSupportedException> when it is written or read:

- The content of requests, other than an <xref:System.Net.Http.HttpContent> or a string, and the responses that are read as a type.
- The items of streaming responses.
- The arguments, results and stream items of hub calls and handlers, which hub connections write and read with the client's serializer options.
- The toolkit's data contracts that the client sends and reads, such as <xref:Singulink.Net.Http.Api.DataContracts.BeginPasskeySignInCommand>, <xref:Singulink.Net.Http.Api.DataContracts.PasskeyCeremony> and <xref:Singulink.Net.Http.Api.DataContracts.DeviceLinkStatus>, which have no type info of their own:

```csharp
[JsonSerializable(typeof(DocumentInfo))]
[JsonSerializable(typeof(SearchResultGroup))]
[JsonSerializable(typeof(BeginPasskeySignInCommand))]
[JsonSerializable(typeof(PasskeyCeremony))]
internal sealed partial class AppSerializerContext : JsonSerializerContext;
```

## Services

#### Session tokens

<xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddHttpSessionHandling*> has two overloads. One takes the `JsonTypeInfo<T>` of the session token type, for example from a source-generated serializer context, and serializes tokens with it. The library's own part of the cookie uses source-generated metadata of its own:

```csharp
[JsonSerializable(typeof(SessionToken))]
internal sealed partial class SessionTokenJsonContext : JsonSerializerContext;

services.AddHttpSessionHandling<SessionToken, SessionStore>(SessionTokenJsonContext.Default.SessionToken);
```

The other serializes tokens with reflection and <xref:System.Text.Json.JsonSerializerOptions.Default?displayProperty=nameWithType>, and is annotated. Both read session cookies with the same JSON settings they write them with. A source-generated context without <xref:System.Text.Json.Serialization.JsonSourceGenerationOptionsAttribute> settings, like the one above, has the same settings as <xref:System.Text.Json.JsonSerializerOptions.Default?displayProperty=nameWithType>, so it reads the cookies that the reflection-based overload writes.

#### Streaming responses

Streamed items are serialized with the app's HTTP JSON options (`Microsoft.AspNetCore.Http.Json.JsonOptions`, configured with <xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddApiJson*> or `ConfigureHttpJsonOptions`), using the type info of the endpoint's declared item type, as minimal APIs do for regular results. Minimal APIs also look up the type info of each endpoint's declared result type when the endpoints are built, so without reflection-based serialization, the options need type info for `IAsyncEnumerable<T>` of the item type as well as for the item type:

```csharp
[JsonSerializable(typeof(SearchResultGroup))]
[JsonSerializable(typeof(IAsyncEnumerable<SearchResultGroup>))]
internal sealed partial class AppJsonContext : JsonSerializerContext;

services.AddApiJson(AppJsonContext.Default);
```

#### Chunked uploads

<xref:Singulink.Net.Http.Api.Service.ChunkedUploadEndpointRouteBuilderExtensions.MapChunkedUpload*> builds the validator and the handler of an upload endpoint with `RequestDelegateFactory`, as `MapPost(string, Delegate)` builds an endpoint's handler, which the request delegate generator cannot replace for calls inside the toolkit. It is annotated, and upload endpoints are not supported in trimmed and Native AOT apps. <xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddChunkedUploads*> and the upload stores are not annotated.

#### Passkeys

The passkey service runs its ceremonies with Fido2NetLib 4.3.1, which is marked as trimmable but not as AOT compatible. In a Native AOT publish of an app that registers passkeys and signs in with them, Fido2NetLib gives no trim or AOT warnings, and the ceremonies work. Fido2NetLib depends on NSec, whose native library (libsodium) is published next to the app's executable.

#### SignalR hubs

SignalR supports Native AOT with its JSON protocol, and so do <xref:Singulink.Net.Http.Api.Service.ApiHubOptionsExtensions.AddApiExceptionFilter*> and the session revalidation of hub connections (<xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.RevalidateHubConnections?displayProperty=nameWithType>). Without reflection-based serialization, the JSON protocol needs type info for the types of the hub's arguments, results and stream items. <xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddApiJson*> adds the resolver to the hub payload options as well as to the HTTP JSON options (see [JSON Serialization](json-serialization.md)):

```csharp
services.AddApiJson(AppJsonContext.Default);

services.AddSignalR(options => options.AddApiExceptionFilter());
```

Without the toolkit's null handling, `AddJsonProtocol(options => options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default))` adds the resolver to the hub payload options alone.

In apps without dynamic code, such as Native AOT apps, exceptions thrown while streaming value type items (for example from a hub method that returns `IAsyncEnumerable<int>`) are not reported as API errors: the client gets SignalR's general error for a failed stream. Streams of reference type items, and exceptions thrown by hub methods and connection handlers, are reported as API errors as usual. See [ASP.NET Core support for Native AOT](https://learn.microsoft.com/aspnet/core/fundamentals/native-aot) for what SignalR itself supports.

## Next Steps

Continue with these related articles:

- [Session Handling](session-handling.md) - Implementing the token and the session store, and retrieving the session in endpoints.
- [Streaming Responses](streaming-responses.md) - Returning `IAsyncEnumerable<T>` from endpoints and consuming it on the client.
- [Building API Clients](api-clients.md) - Serializer options, session persistence and platform behavior.

</div>
