<div class="article">

# Upgrading to 4.0

This guide covers upgrading an app from 3.x to 4.0. Session tokens and stores change the most; features new in 4.0, such as sign-in codes, passkeys, chunked uploads and rate limiting, have guides of their own.

## Before Upgrading

- The session cookie has a new format, so users sign in once after upgrading. Services that read the same session cookie are upgraded together.
- The default session cookie name is `__Host-session-token` (<xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.SessionCookieName>) rather than `session-token`, in the service and the client. While 3.x clients or services are in use, set the name to `session-token` on both sides. A `__Host-` name can't be used with <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.CookieDomain> or another <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.CookiePath>: an app that sets either chooses its own name.
- A 4.0 hub connection doesn't start against a 3.x service, since it waits for a confirmation that only 4.0 sends (see [Confirming connections](../guides/signalr-hubs.md#confirming-connections)).
- Session handling registers ASP.NET Core data protection unless an `IDataProtectionProvider` is registered first (see [Session Handling](../guides/session-handling.md#the-session-store)), and requires the allowed origins (<xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddAllowedOrigins*>) at startup.
- <xref:Singulink.Net.Http.Api.Service.WebApplicationExtensions.UseApiResponseHandling*> and `services.AddApiResponseHandling()` require each other, and an app that calls `UseEndpoints` after mapping endpoints on the app fails to start (see [Streaming Responses](../guides/streaming-responses.md#writing-a-streaming-endpoint)).
- The client package no longer depends on `Microsoft.Extensions.Http`: an app that used it through the toolkit references it itself.
- The shared and client packages also target .NET Standard 2.0, for .NET Framework 4.6.2 and later. Apps on other runtimes that load it, such as .NET 8 and 9, get a build warning: they are not supported.
- Assemblies built against 3.x are rebuilt: several signatures change in ways that keep calls compiling, such as the client's path parameters (see [Clients](#clients)).

## Session Tokens and Stores

- <xref:Singulink.Net.Http.Api.Service.ISessionToken> only has <xref:Singulink.Net.Http.Api.Service.ISessionToken.UserId>. Remove `RefreshedUtc`, `ValidFor`, `Generation`, `IsPersistent` and `RefreshAfter` from your token type; the refresh interval is <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.RefreshAfter?displayProperty=nameWithType> (see [The Session Token](../guides/session-handling.md#the-session-token)).
- `ISessionStoreContextFactory<T>` and `ISessionStoreContext<T>` are replaced by the scoped <xref:Singulink.Net.Http.Api.Service.ISessionStore`1>, the second type argument of <xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddHttpSessionHandling*>. Its methods take a cancellation token.
- `CreateTokenAsync` and `SessionLookupResult.IsTokenStale` are replaced by the replacement token of <xref:Singulink.Net.Http.Api.Service.SessionLookupResult`1>.
- <xref:Singulink.Net.Http.Api.Service.SignInInfo> derives from <xref:Singulink.Net.Http.Api.Service.SessionRecord>, and `SessionExpiry` (a time span) is replaced by <xref:Singulink.Net.Http.Api.Service.SessionRecord.ExpiresUtc> (a time). <xref:Singulink.Net.Http.Api.Service.SessionRecord> has required properties instead of a constructor, and a new <xref:Singulink.Net.Http.Api.Service.SessionRecord.CreatedUtc> that the store saves when a session is created and returns when it loads it (see [Signing In and Out](../guides/session-handling.md#signing-in-and-out)).
- The device that a session records is at most <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.MaxDeviceLength?displayProperty=nameWithType> characters, 512 by default.
- <xref:Singulink.Net.Http.Api.Service.SessionContext`1.SetToken*> throws for a request without a session.
- The <xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddHttpSessionHandling*> overload without JSON type info is not trimming- or AOT-safe; use the overload that takes a <xref:System.Text.Json.Serialization.Metadata.JsonTypeInfo`1>.

Existing sessions need values for the new columns, or can simply be deleted, since users sign in again anyway. In SQL Server, where the 3.x `SessionExpiry` column holds ticks (a `bigint`):

```sql
ALTER TABLE Sessions ADD ExpiresUtc datetime2 NULL;
UPDATE Sessions SET ExpiresUtc = DATEADD(second, SessionExpiry / 10000000, RefreshedUtc);
ALTER TABLE Sessions ALTER COLUMN ExpiresUtc datetime2 NOT NULL;
ALTER TABLE Sessions DROP COLUMN SessionExpiry;

-- Existing sessions count from the migration:
ALTER TABLE Sessions ADD CreatedUtc datetime2 NOT NULL DEFAULT SYSUTCDATETIME();

-- Or from their last refresh:
ALTER TABLE Sessions ADD CreatedUtc datetime2 NULL;
UPDATE Sessions SET CreatedUtc = RefreshedUtc;
ALTER TABLE Sessions ALTER COLUMN CreatedUtc datetime2 NOT NULL;
```

## Session Behavior

- <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions> are validated at startup: an expiry that is not longer than <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.RefreshAfter> fails it.
- <xref:Singulink.Net.Http.Api.Service.SessionContext`1.SignInAsync*> rejects requests from origins that are not allowed, as retrieving the session does.
- <xref:Singulink.Net.Http.Api.Service.SessionContext`1.SignInAsync*> ends the request's previous session, so an app that signs in on a request that has a session, such as to switch accounts, loses the previous session's record.
- A store can end a session by throwing an <xref:Singulink.Net.Http.Api.UnauthorizedApiException> from <xref:Singulink.Net.Http.Api.Service.ISessionStore`1.GetSessionAsync*> (see [Ending Sessions](../guides/session-handling.md#ending-sessions)).
- In an app that registers SignalR, hub connections whose sessions have ended are closed, unless <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.RevalidateHubConnections?displayProperty=nameWithType> is `false` (see [Rechecking the sessions of open connections](../guides/signalr-hubs.md#rechecking-the-sessions-of-open-connections)).

## Origins

- An allowed origin given as a host, such as `example.com`, matches `https` only, and `http` too for loopback hosts. List other `http` origins with their scheme, such as `http://intranet.example.com` (see <xref:Singulink.Net.Http.Api.Service.OriginValidator>). Origins with a scheme or a port now match as given.
- An allowed origin that is not a valid host pattern throws an <xref:System.ArgumentException> when <xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddAllowedOrigins*> is called.
- <xref:Singulink.Net.Http.Api.Service.WebApplicationExtensions.UseAllowedOrigins*> exposes the `Retry-After`, `Reference-Id` and `Location` headers. An app with its own CORS policy exposes the ones its clients read.

## Errors

- With <xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddApiExceptionHandler*>, requests that minimal API endpoints can't read are reported as API errors in every environment (see [Requests That Cannot Be Read](../guides/exception-handling.md#requests-that-cannot-be-read)).
- `HubOptionsExtensions` is renamed to <xref:Singulink.Net.Http.Api.Service.ApiHubOptionsExtensions>.
- The toolkit's own errors carry error codes (see [Error Codes](../guides/exception-handling.md#error-codes)). Clients that read the response body themselves see the code in brackets before the message.
- The JSON null handling also rejects a `null` in a property that doesn't allow one when writing: such a response fails with a `500`, and a client throws a <xref:System.Text.Json.JsonException> for such a command (see [JSON Serialization](../guides/json-serialization.md)).
- Route templates without a leading slash gain one in endpoints mapped after <xref:Singulink.Net.Http.Api.Service.WebApplicationExtensions.UseApiResponseHandling*>, which changes the `http.route` tag of metrics and traces.

## Clients

- Request paths are an <xref:Singulink.Net.Http.Api.Client.InternalServices.InterpolatedApiPathHandler> instead of a string in <xref:Singulink.Net.Http.Api.Client.ApiClientBase.CreateRequest*>, <xref:Singulink.Net.Http.Api.Client.ApiClientBase.GetApiUrl*> and <xref:Singulink.Net.Http.Api.Client.SignalRApiClientBase.CreateHubConnection*>. Calls compile as before; overrides change their parameter type.
- Values in interpolated paths are escaped, so a `/` in a value no longer starts a segment: use the `raw` format, as in `$"items?{queryString:raw}"`, for values that hold several segments or a query string. Paths that lead outside the base address throw (see [Paths](../guides/api-clients.md#paths)), and alignment, as in `{value,8}`, doesn't compile.
- A hub outside the client's base address is connected through a child client whose <xref:Singulink.Net.Http.Api.Client.ApiClientBase.GetBaseAddress*> returns the hubs' address:

```csharp
// Created with new HubsApiClient(this) by the client whose base address is https://api.example.com/business/v1/.
public sealed class HubsApiClient(ApiClientBase parent) : SignalRApiClientBase(parent)
{
    protected override Uri GetBaseAddress() => new("https://api.example.com/hubs/");

    public ApiHubConnection CreateChatConnection() => CreateHubConnection("chat");
}
```

- The query string parameters of `CreateRequest`, `GetApiUrl` and `CreateHubConnection` are one parameter, `query`, passed as a collection expression: `CreateRequest(HttpMethod.Get, "items", [("page", 2), ("filter", filter)])`.
- `GetDefaultQueryParams` and the `DefaultUserIdPreconditionQueryName` and `DefaultSessionCookieName` constants are gone: child clients take the user ID precondition as a constructor argument, and <xref:Singulink.Net.Http.Api.Client.ApiClientBase.UserIdPreconditionQueryName> and <xref:Singulink.Net.Http.Api.Client.ApiClientBase.SessionCookieName> hold the names.
- A base address whose path has no trailing slash is used as if it had one.
- <xref:Singulink.Net.Http.Api.Client.ApiClientBase.IsPersistentSession> is read-only: the app passes its "remember me" choice to its sign-in endpoint.
- The constructors that take a type info resolver apply the JSON null handling, their parameter is named `typeInfoResolver` rather than `serializerContext`, and their configuration callback runs after the resolver is set (see [JSON Serialization](../guides/json-serialization.md)).
- Redirects are not followed, except on browser platforms: a `3xx` response throws an <xref:Singulink.Net.Http.Api.ApiException>.
- The session cookie is only taken from responses from the base address's host.
- Hub connections skip the negotiate request, so <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.ConnectionId> is `null`. A service that needs negotiation, such as Azure SignalR Service, turns it back on in the `configureOptions` callback of `CreateHubConnection`.
- A `401` with the `sign-in-required` code ends the session on the device (see [Session Persistence](../guides/api-clients.md#session-persistence)).
- <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.StartAsync*?displayProperty=nameWithType> completes once the hub accepted the connection, so an error from the hub's `OnConnectedAsync` fails the start (see [Using the Contract on the Client](../guides/signalr-hubs.md#using-the-contract-on-the-client)).
- An error response that is not in the toolkit's format keeps its status instead of being reported as a `500`.
- <xref:Singulink.Net.Http.Api.Client.ApiClientBase.RequestTimeout> also covers reading the response body.
- An <xref:System.IO.IOException> while reading a JSON response is thrown as an <xref:System.Net.Http.HttpRequestException>.
- The new protected <xref:Singulink.Net.Http.Api.Client.ApiClientBase.UploadAsync*> and <xref:Singulink.Net.Http.Api.Client.ApiClientBase.UploadStreamingAsync*> can clash with methods of the same name in a derived client (see [Chunked Uploads in Client Apps](../guides/uploads-client.md)).
- `VoidApiResponse` is internal.
- New types such as <xref:Singulink.Net.Http.Api.ConflictApiException>, <xref:Singulink.Net.Http.Api.Client.UploadSource> and <xref:Singulink.Net.Http.Api.Client.UploadOptions> can clash with an app's own types of the same name.

## Next Steps

Continue with these related articles:

- [Session Handling](../guides/session-handling.md) - The session token, the session store and signing in.
- [Building API Clients](../guides/api-clients.md) - Paths, session persistence and child clients.
- [JSON Serialization](../guides/json-serialization.md) - The null handling on the service and in clients.

</div>
