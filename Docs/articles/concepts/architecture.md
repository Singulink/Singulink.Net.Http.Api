<div class="article">

# Architecture and Conventions

This article describes what each package contains, how a request flows through the service and client, and the reasoning behind the conventions the toolkit imposes.

### Packages

| Package | Referenced by | Contents |
| --- | --- | --- |
| `Singulink.Net.Http.Api` | Both sides, and data contract assemblies | <xref:Singulink.Net.Http.Api.ApiException> hierarchy, <xref:Singulink.Net.Http.Api.StreamingResponse>, hub contract definitions. |
| `Singulink.Net.Http.Api.Service` | ASP.NET Core services | Session handling (including the <xref:Singulink.Net.Http.Api.Service.ISessionToken> and <xref:Singulink.Net.Http.Api.Service.ISessionData> contracts), <xref:Singulink.Net.Http.Api.Service.ApiExceptionMiddleware> and <xref:Singulink.Net.Http.Api.Service.IApiExceptionHandler>, streaming response conversion, origin validation, hub filter and hub extensions. |
| `Singulink.Net.Http.Api.Client` | Client apps | <xref:Singulink.Net.Http.Api.Client.ApiClientBase>. |
| `Singulink.Net.Http.Api.Client.SignalR` | Client apps using hubs | <xref:Singulink.Net.Http.Api.Client.SignalRApiClientBase> and <xref:Singulink.Net.Http.Api.Client.ApiHubConnection>. |

The shared package has no ASP.NET Core dependency, so a data contracts assembly that declares hub contracts can be referenced by mobile and browser clients as well as the service. Session token types are server-side only (clients only ever hold the encrypted cookie), which is why their contracts live in the service package.

## Request Flow

A request made through <xref:Singulink.Net.Http.Api.Client.ApiClientBase.SendAsync*> carries three things the service relies on: the session cookie, the user agent, and the user ID precondition query parameter. On the service:

1. <xref:Singulink.Net.Http.Api.Service.WebApplicationExtensions.UseAllowedOrigins*> applies CORS for browser clients.
2. <xref:Singulink.Net.Http.Api.Service.ApiExceptionMiddleware> wraps the rest of the pipeline.
3. Endpoint parameter binding retrieves the session token: the origin is checked, the cookie is decrypted, the precondition is compared, and if the token is due for a refresh or the endpoint forces validation, the session store is consulted.
4. The endpoint runs and returns a value, throws an <xref:Singulink.Net.Http.Api.ApiException>, or returns an <xref:System.Collections.Generic.IAsyncEnumerable`1> that the streaming conversion writes out record by record.
5. When the response starts, a deferred refresh may attach a new session cookie.

Back on the client, the response's session cookie (if any) replaces the stored token, an error status is turned into the matching exception, and a successful body is deserialized.

## Conventions and Their Reasoning

#### Sessions in a self-contained cookie

The token contains everything the service needs to authorize most requests, so authentication costs one decryption rather than a store lookup. The store is consulted periodically to refresh the token and detect changes, and immediately for operations that force validation. This trades a bounded staleness window for a large reduction in database traffic; [Session Token Model](session-token-model.md) explains how the window is kept safe.

Cookies were chosen over bearer tokens because browsers manage them safely (HttpOnly, Secure) and native clients can treat them as an opaque string. The cost is cross-site request forgery, which the toolkit addresses by rejecting requests whose `Origin` header is not an allowed origin.

#### Errors as exceptions

Endpoint code throws exceptions with user-facing messages and optional codes, and client code catches the same exception types. The wire format is deliberately plain: a text body and a small content type variation for error codes, readable in any HTTP tool. Unexpected exceptions are mapped by one handler for regular responses, streaming responses and hubs, so logging and reference IDs are consistent regardless of where a failure happens. See [Error Response Format](error-format.md).

#### The user ID precondition

Clients state which user they are acting for on every request. A mismatch means the client's state is stale, for example after a different user signed in on a shared device, and the request is rejected before it does anything. This is cheap insurance against a class of bugs that is otherwise very hard to reproduce.

#### Streams as JSON Lines

Endpoints returning <xref:System.Collections.Generic.IAsyncEnumerable`1> are written as newline-delimited records with in-band pings, errors and an explicit end marker, so clients get items as they are produced, connections stay alive, failures after the first item are still reported, and truncation is detectable. See [Streaming Response Format](streaming-format.md).

#### Hub contracts

SignalR identifies methods by string name. Shared definitions capture the name and argument types once, so both sides are checked at compile time, and the hub filter and connection wrapper carry the error conventions over to hub calls.

## Further Reading

These articles cover the related topics in more depth:

- [Getting Started](../guides/getting-started.md) - A minimal service and client.
- [Session Token Model](session-token-model.md) - The session lifecycle in detail.
- [Error Response Format](error-format.md) and [Streaming Response Format](streaming-format.md) - The wire formats.

</div>
