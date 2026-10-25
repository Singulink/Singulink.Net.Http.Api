<div class="article">

# Architecture and Conventions

This article describes what each package contains, how a request flows through the service and client, and the reasoning behind the conventions the toolkit imposes.

### Packages

| Package | Referenced by | Contents |
| --- | --- | --- |
| `Singulink.Net.Http.Api` | Both sides, and data contract assemblies | <xref:Singulink.Net.Http.Api.ApiException> hierarchy, the error codes of the toolkit's own errors, <xref:Singulink.Net.Http.Api.StreamingResponse>, hub contract definitions, the JSON null handling (<xref:Singulink.Net.Http.Api.ApiNullHandlingExtensions>), and the data contracts that apps take from and return to their clients as-is, such as <xref:Singulink.Net.Http.Api.DataContracts.PasskeyCeremony>, <xref:Singulink.Net.Http.Api.DataContracts.SignInCode> and <xref:Singulink.Net.Http.Api.DataContracts.DeviceLinkStatus> (see [Data contracts](../guides/sign-in-codes.md#data-contracts)). |
| `Singulink.Net.Http.Api.Service` | ASP.NET Core services | Session handling (including the <xref:Singulink.Net.Http.Api.Service.ISessionToken> contract), <xref:Singulink.Net.Http.Api.Service.ApiExceptionMiddleware> and <xref:Singulink.Net.Http.Api.Service.IApiExceptionHandler>, streaming response conversion, origin validation, per-client rate limiting, sign-in codes and device linking, the short-lived store (<xref:Singulink.Net.Http.Api.Service.IShortLivedStore>) that keeps their short-lived state, chunked uploads and their store (<xref:Singulink.Net.Http.Api.Service.Uploads.IUploadStore>), hub filter and hub extensions. |
| `Singulink.Net.Http.Api.Service.Cloudflare` | ASP.NET Core services behind Cloudflare | Middleware that applies the client address and scheme forwarded by Cloudflare (see <xref:Singulink.Net.Http.Api.Service.CloudflareServiceCollectionExtensions.AddCloudflareForwarding*>), with Cloudflare's IP ranges kept up to date. |
| `Singulink.Net.Http.Api.Service.Testing` | Test projects of services | <xref:Singulink.Net.Http.Api.Service.Testing.TestSessionContext`1>, a session context without a request or a session store, <xref:Singulink.Net.Http.Api.Service.Testing.SoftwareAuthenticator>, which answers passkey ceremonies as a browser with a passkey would, <xref:Singulink.Net.Http.Api.Service.Testing.TestUploadedFile>, an uploaded file for calling upload handlers, and <xref:Singulink.Net.Http.Api.Service.Testing.FaultInjectionHandler>, which makes chosen requests fail. |
| `Singulink.Net.Http.Api.Service.Passkeys` | ASP.NET Core services with passkey sign-in | <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyService> for WebAuthn sign-in, verification and registration ceremonies over the app's own passkey storage (<xref:Singulink.Net.Http.Api.Service.Passkeys.IPasskeyStore>), and the passkey hand-off page for native apps. See [Passkeys](../guides/passkeys.md). |
| `Singulink.Net.Http.Api.Client` | Client apps | <xref:Singulink.Net.Http.Api.Client.ApiClientBase>, <xref:Singulink.Net.Http.Api.Client.Pkce>, and the passkey authenticator interface (<xref:Singulink.Net.Http.Api.Client.Passkeys.IPasskeyAuthenticator>) with its credential and error types. |
| `Singulink.Net.Http.Api.Client.SignalR` | Client apps using hubs | <xref:Singulink.Net.Http.Api.Client.SignalRApiClientBase> and <xref:Singulink.Net.Http.Api.Client.ApiHubConnection>. |
| `Singulink.Net.Http.Api.Client.WinUI` | WinUI 3 and Uno Platform client apps using passkeys | <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyAuthenticator>, and <xref:Singulink.Net.Http.Api.Client.LoopbackWebAuthenticationBrokerProvider> for Uno Platform desktop apps. |

The shared package has no ASP.NET Core dependency, so a data contracts assembly that declares hub contracts can be referenced by mobile and browser clients as well as the service. Session token and store types are server-side only (clients only ever hold the encrypted cookie), which is why their contracts live in the service package.

## Request Flow

A request made through <xref:Singulink.Net.Http.Api.Client.ApiClientBase> carries up to three things the service relies on: the session cookie, the user agent, and the user ID precondition query parameter, which requests from a client created for a user carry. On the service, with the middleware in the order of [Pipeline order](../guides/getting-started.md#pipeline-order):

1. <xref:Singulink.Net.Http.Api.Service.ApiExceptionMiddleware> wraps the rest of the pipeline.
2. <xref:Singulink.Net.Http.Api.Service.WebApplicationExtensions.UseAllowedOrigins*> applies CORS for browser clients.
3. Endpoint parameter binding retrieves the session token: the origin is checked, the cookie is decrypted, the precondition is compared, and if the cookie is due for a refresh or the endpoint forces validation, the session store is consulted.
4. The endpoint runs and returns a value, throws an <xref:Singulink.Net.Http.Api.ApiException>, or returns an <xref:System.Collections.Generic.IAsyncEnumerable`1> that the streaming conversion writes out record by record.
5. When the response starts, a deferred refresh may attach a new session cookie.

Back on the client, the response's session cookie (if any) replaces the current token, and its expiry tells whether the session is persistent; an error status is turned into the matching exception, and a successful body is deserialized.

## Conventions and Their Reasoning

#### Sessions in a self-contained cookie

The cookie holds the application's session token, with everything the service needs to authorize most requests, next to the session's refresh info, which the library maintains. Authentication therefore costs one decryption rather than a store lookup. The store is consulted periodically to refresh the session and detect changes, and immediately for operations that force validation. This trades a bounded staleness window for a large reduction in database traffic; [Session Token Model](session-token-model.md) explains how the window is kept safe.

The library owns the refresh bookkeeping, so the application's token type holds only the application's data, and the store only builds a token when the data in it is out of date. The store is resolved from a service scope of its own for each operation, so it can use a database context from dependency injection without sharing one with the endpoint.

Cookies were chosen over bearer tokens because browsers manage them safely (HttpOnly, Secure) and native clients can treat them as an opaque string. The cost is cross-site request forgery, which the toolkit addresses by rejecting requests whose `Origin` header is not an allowed origin.

#### Errors as exceptions

Endpoint code throws exceptions with user-facing messages and optional codes, and client code catches the same exception types. The wire format is deliberately plain: a text body and a small content type variation for error codes, readable in any HTTP tool. Unexpected exceptions are mapped by one handler for regular responses, streaming responses and hubs, so logging and reference IDs are consistent regardless of where a failure happens. See [Error Response Format](error-format.md).

#### The user ID precondition

Clients state which user they are acting for on every request. A mismatch means the client's state is stale, for example after a different user signed in on a shared device, and the request is rejected before it does anything. This is cheap insurance against a class of bugs that is otherwise very hard to reproduce.

#### Streams as JSON Lines

Minimal API route handlers that return <xref:System.Collections.Generic.IAsyncEnumerable`1> are written as newline-delimited records with in-band pings, errors and an explicit end marker, so clients get items as they are produced, connections stay alive, failures after the first item are still reported, and truncation is detectable. See [Streaming Response Format](streaming-format.md).

#### Hub contracts

SignalR identifies methods by string name. Shared definitions capture the name and argument types once, so both sides are checked at compile time, and the hub filter and connection wrapper carry the error conventions over to hub calls.

## Further Reading

These articles cover the related topics in more depth:

- [Getting Started](../guides/getting-started.md) - A minimal service and client.
- [Session Token Model](session-token-model.md) - The session lifecycle in detail.
- [Error Response Format](error-format.md) and [Streaming Response Format](streaming-format.md) - The wire formats.

</div>
