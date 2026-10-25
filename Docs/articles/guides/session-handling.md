<div class="article">

# Session Handling

This guide covers implementing sessions on the service: the token type, the session store, signing in and out, ending sessions, retrieving the session in endpoints, and the options that control validation and expiry.

### How sessions work

A session is represented by two things: an encrypted **session cookie** that travels with every request, and a **session record** in a store of your choosing (typically a database table). The cookie holds your **session token**, with the data your endpoints need on every request, next to the session's refresh info, which the library keeps for you: when the session was last refreshed, when it expires, its generation and whether it is persistent. The cookie is self-contained, so most requests are authenticated by decrypting it alone. The store is consulted only when the cookie is due for a periodic refresh, when a request forces validation, when signing in ends the request's previous session, when signing out, and when the session of an open SignalR connection is rechecked. [Session Token Model](../concepts/session-token-model.md) explains the lifecycle in depth; this guide focuses on what you implement.

### Types you provide

Session handling is generic over the token type and is backed by a store that you implement:

- A **token type** implementing <xref:Singulink.Net.Http.Api.Service.ISessionToken`1>. It is serialized to JSON, encrypted with ASP.NET Core data protection and stored in the cookie, next to the library's refresh info.
- A **session store** implementing <xref:Singulink.Net.Http.Api.Service.ISessionStore`1>. The store exchanges session values with the library through <xref:Singulink.Net.Http.Api.Service.SessionRecord> and <xref:Singulink.Net.Http.Api.Service.SessionLookupResult`1>, so your session entity does not need to implement anything.

## The Session Token

The token carries the user's identity plus whatever the service needs on every request without a lookup, such as the user's role. It is encrypted, so clients never see its contents; it is a server-side type, and anything the client needs to know about the session is returned explicitly by endpoints such as "get current session". Here the token is a record:

```csharp
public record SessionToken(long SessionId, int UserId, int TokenVersion, UserRole Role) : ISessionToken<SessionToken>
{
    string ISessionToken.UserId => UserId.ToString(CultureInfo.InvariantCulture);
}
```

The token holds your data only. <xref:Singulink.Net.Http.Api.Service.ISessionToken.UserId> is the one member the library needs: it is a string so that the library can compare it with the user ID precondition query parameter without knowing your ID type. The refresh info lives next to the token in the cookie, so the token type has no fields for it, and how often a cookie is refreshed is set with <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.RefreshAfter?displayProperty=nameWithType>.

`TokenVersion` is not part of the interface. It is a version number on the user record that changes whenever any of the data the token captures changes, such as the user's role. The store compares it with the version in the token and gives the session a token with the current data when they differ (see below). Changing it only refreshes the data in the token: the session continues, and the user stays signed in. To end sessions, see [Ending Sessions](#ending-sessions).

The cookie grows with the token, and browsers silently drop a cookie that is larger than 4,096 bytes, counting its name, value and attributes, so a token that takes the cookie over that size signs browser users out. A token with a list, such as the user's memberships, can take it over that size as the list grows. When session handling sends a session cookie over that size, it logs an error with the cookie's size and the token type. It still sends the cookie, since the toolkit's clients outside browsers have no such limit.

<xref:Singulink.Net.Http.Api.Service.ISessionToken`1>, implemented with the token type itself as shown, lets endpoints take the token as a parameter. It implements `IBindableFromHttpContext<T>` with <xref:Singulink.Net.Http.Api.Service.HttpContextExtensions.BindSessionTokenAsync*>, which reads the parameter's nullability and its <xref:Singulink.Net.Http.Api.Service.SessionAccessAttribute> to decide how to retrieve it.

## The Session Store

The session record is whatever entity your data layer uses; the library never sees it directly. It must hold the values the library updates on refresh (device, IP address, refresh time, expiry time and generation), whether the session is persistent, and the time the session was created, which maximum session lifetimes are measured from (see [Maximum session lifetime](#maximum-session-lifetime)). It usually links to the user, so that the token version can be compared in the same query that loads the record:

```csharp
public class Session
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public string Device { get; set; } = "";
    public IPAddress? IpAddress { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime RefreshedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public int Generation { get; set; }
    public bool IsPersistent { get; set; }
}
```

The store is the library's window onto your database. The library resolves it from a new service scope for each operation, such as the lookup at the start of a request or the refresh when the response starts, so the store can take a database context through dependency injection, and its work never mixes with the endpoint's own database context:

```csharp
public sealed class SessionStore(AppDbContext db) : ISessionStore<SessionToken>
{
    public async Task<SessionLookupResult<SessionToken>?> GetSessionAsync(SessionToken token, CancellationToken cancellationToken)
    {
        // One query loads the record and the user's current token version.
        var row = await db.Sessions
            .Where(s => s.Id == token.SessionId)
            .Select(s => new {
                Record = new SessionRecord {
                    Device = s.Device,
                    IpAddress = s.IpAddress,
                    RefreshedUtc = s.RefreshedUtc,
                    ExpiresUtc = s.ExpiresUtc,
                    Generation = s.Generation,
                    IsPersistent = s.IsPersistent,
                    CreatedUtc = s.CreatedUtc,
                },
                s.User.TokenVersion,
                s.User.Role,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
            return null;

        if (row.TokenVersion == token.TokenVersion)
            return new(row.Record);

        // The data in the token is out of date: the session continues with a token with the current data.
        return new(row.Record, token with { TokenVersion = row.TokenVersion, Role = row.Role });
    }

    public async Task<bool> TryRefreshSessionAsync(
        SessionToken token, SessionRecord refreshed, int expectedGeneration, CancellationToken cancellationToken)
    {
        // A single conditional update: the generation check and the write happen atomically.
        int rows = await db.Sessions
            .Where(s => s.Id == token.SessionId && s.Generation == expectedGeneration)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Device, refreshed.Device)
                .SetProperty(x => x.IpAddress, refreshed.IpAddress)
                .SetProperty(x => x.RefreshedUtc, refreshed.RefreshedUtc)
                .SetProperty(x => x.ExpiresUtc, refreshed.ExpiresUtc)
                .SetProperty(x => x.Generation, refreshed.Generation), cancellationToken);

        return rows == 1;
    }

    public async Task InvalidateSessionAsync(SessionToken token, CancellationToken cancellationToken)
    {
        await db.Sessions.Where(s => s.Id == token.SessionId).ExecuteDeleteAsync(cancellationToken);
    }
}
```

Each method has a specific job:

- <xref:Singulink.Net.Http.Api.Service.ISessionStore`1.GetSessionAsync*?displayProperty=nameWithType> loads the record and, if the data in the token is out of date, a replacement token, in one call. The session continues with the replacement: the request gets it, and the cookie is reissued with it. Return `null` to end the session; the library then clears the cookie, except that a browser's request keeps a cookie that was due (see [Ending Sessions](#ending-sessions)). Before it reissues a cookie at the start of the response, without a refresh, the library looks the session up again, and reissues nothing if the result is `null` or the store throws. Pass the creation time to the <xref:Singulink.Net.Http.Api.Service.SessionRecord> as shown.
- <xref:Singulink.Net.Http.Api.Service.ISessionStore`1.TryRefreshSessionAsync*?displayProperty=nameWithType> writes the refreshed values, but only if the stored generation still matches the one the library validated. The check and the write must be atomic, which a filtered `UPDATE` gives you for free. Return `false` when no row was updated: the library then skips issuing a cookie because a concurrent request already refreshed (or ended) the session. The creation time and the persistent flag never change, so they do not need to be written.
- <xref:Singulink.Net.Http.Api.Service.ISessionStore`1.InvalidateSessionAsync*?displayProperty=nameWithType> removes the record. It is called on sign-out, when validation rejects a session, when <xref:Singulink.Net.Http.Api.Service.ISessionStore`1.GetSessionAsync*> throws an <xref:Singulink.Net.Http.Api.UnauthorizedApiException>, and when a request that has a session signs in, for that previous session. The cancellation token of these calls is the request's, except when signing out, when ending the previous session at sign-in and after `GetSessionAsync` threw in a request, which are not cancelled, and in the rechecks of SignalR connections, which have a token of their own.

Register everything with <xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddHttpSessionHandling*>, optionally configuring <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions>. The store is registered as a scoped service:

```csharp
services.AddDbContext<AppDbContext>(options => options.UseSqlServer(connectionString));

services.AddHttpSessionHandling<SessionToken, SessionStore>(options => {
    options.PersistentSessionExpiry = TimeSpan.FromDays(90);
});
```

This overload serializes the token with reflection. Another overload takes the token type's `JsonTypeInfo<T>`, such as one from a source-generated serializer context, and serializes it with that instead (see [Trimming and Native AOT](trimming-and-aot.md#session-tokens)).

Session handling checks the origin of requests against the allowed origins that <xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddAllowedOrigins*> registers, and the app fails to start without them.

> [!NOTE]
> The cookie is encrypted with ASP.NET Core data protection, which session handling registers with `services.AddDataProtection()` unless an `IDataProtectionProvider` is already registered. A provider that the app registers before session handling is used as it is, without ASP.NET Core's default key ring. A cookie can only be read with the keys it was encrypted with. Unless the app chooses where the keys are kept, such as with `PersistKeysToFileSystem`, ASP.NET Core keeps them in a default location that depends on the host, and on some hosts that location doesn't survive a restart, which signs everyone out. Servers that read the same cookie share the key ring: the servers of one service (see [Running Multiple Servers](multiple-servers.md)), and services that share the cookie across subdomains through <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.CookieDomain?displayProperty=nameWithType>.

The cookie's data protection purpose includes the full name of the token type, so renaming the token type or moving it to another namespace signs everyone out, unless <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.DataProtectionPurpose?displayProperty=nameWithType> is set to the previous purpose, such as `Singulink/Session[MyApp.Sessions.SessionToken]`.

## Signing In and Out

Endpoints receive the session context as a <xref:Singulink.Net.Http.Api.Service.SessionContext`1> parameter. Sign in with <xref:Singulink.Net.Http.Api.Service.SessionContext`1.SignInAsync*?displayProperty=nameWithType>, which calls your function to verify credentials, create the session record and return the token, then issues the cookie:

```csharp
static async Task<CurrentSessionInfo> SignInAsync(SignInCommand command, SessionContext<SessionToken> sessionContext, AppDbContext db)
{
    var token = await sessionContext.SignInAsync(command.RememberMe, async info => {
        var user = await VerifyCredentialsAsync(db, command.Email, command.Password);   // throws UnauthorizedApiException on failure

        var session = new Session {
            UserId = user.Id,
            Device = info.Device,
            IpAddress = info.IpAddress,
            CreatedUtc = info.CreatedUtc,
            RefreshedUtc = info.RefreshedUtc,
            ExpiresUtc = info.ExpiresUtc,
            Generation = info.Generation,
            IsPersistent = info.IsPersistent,
        };

        db.Sessions.Add(session);
        await db.SaveChangesAsync();

        return new SessionToken(session.Id, user.Id, user.TokenVersion, user.Role);
    });

    return new CurrentSessionInfo(token.UserId);
}

static Task SignOutAsync(SessionContext<SessionToken> sessionContext) => sessionContext.SignOutAsync();
```

Signing in checks the request's `Origin` header against the allowed origins before it calls your function, like retrieving the session does (see [Allowing all origins](#allowing-all-origins)), so a page on another site cannot sign the browser in to a session of its choosing. <xref:Singulink.Net.Http.Api.Service.SessionContext`1.EnsureSignInOriginAllowed*?displayProperty=nameWithType> performs the same check without signing in. A session that the request already has is ended through <xref:Singulink.Net.Http.Api.Service.ISessionStore`1.InvalidateSessionAsync*> after the new session's cookie is issued, unless your function returned that session's token. The response has a `Session-Change: sign-in` header, and the response of <xref:Singulink.Net.Http.Api.Service.SessionContext`1.SignOutAsync*> a `Session-Change: sign-out` header. The toolkit's native clients take the session cookie that such a response sets or deletes whatever session the device has by then, since another response can arrive first and change it: for a sign-in, one of the replaced session, such as a refresh, a deletion or a `sign-in-required` error, and for a sign-out, a refresh of the session. They take only these two values, and a header with another value doesn't mark a response. A native client also counts a sign-in's response as the start of a session, which the hub connections that wait for one after theirs ended start again with (see [Waiting for a new session](signalr-hubs.md#waiting-for-a-new-session)). In the browser, which applies the cookie itself, the client doesn't read the header, and <xref:Singulink.Net.Http.Api.Service.WebApplicationExtensions.UseAllowedOrigins*> doesn't expose it to pages of other origins. Other clients can ignore it.

The <xref:Singulink.Net.Http.Api.Service.SignInInfo> passed to your function carries the device (parsed from the User-Agent header, or the header itself for an unknown browser, shortened to <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.MaxDeviceLength?displayProperty=nameWithType>), the IP address and the session's starting values: its creation and refresh time, its expiry time (the expiry selected by the persistent flag after the creation time, capped at the end of the maximum session lifetime if one is configured), generation 0 and the persistent flag. The library issues the cookie with the same values, so store them on the session record as shown. <xref:Singulink.Net.Http.Api.Service.SignInInfo> derives from <xref:Singulink.Net.Http.Api.Service.SessionRecord>, so code that maps a <xref:Singulink.Net.Http.Api.Service.SessionRecord> to your session entity maps it too. Your function returns only your token; the library adds its refresh info to the cookie. <xref:Singulink.Net.Http.Api.Service.SignInInfo> has a public constructor, so the function can be called with one in tests, and <xref:Singulink.Net.Http.Api.Service.Testing.TestSessionContext`1> in the `Singulink.Net.Http.Api.Service.Testing` package is a session context without a request or a session store: it has the token of a session or none, checks the origin and the user ID precondition like session handling, and signing in calls the function with a <xref:Singulink.Net.Http.Api.Service.SignInInfo> for the configured expiries.

The function can verify any kind of credential. For sign-ins that finish somewhere other than the request that creates the session, such as a passkey signed on another page or a device approved from a signed-in one, see [Sign-In Codes and Device Linking](sign-in-codes.md) and [Passkeys](passkeys.md).

<xref:Singulink.Net.Http.Api.Service.SessionContext`1.SignOutAsync*?displayProperty=nameWithType> invalidates the session in the store and clears the cookie. It does not validate or refresh the session first, so it works even when the session has expired or the store record is already gone. It checks the user ID precondition only when the request has one (see [Optional user ID precondition](#optional-user-id-precondition)), so a client that does not know who is signed in can sign out, and one acting for another user gets a `412 Precondition Failed`.

## Ending Sessions

A session ends when the user signs out, when it expires, when it reaches its maximum lifetime, or when your store says so. To end sessions from the service, return `null` from <xref:Singulink.Net.Http.Api.Service.ISessionStore`1.GetSessionAsync*> for them, or delete their records, which makes the lookup return `null`:

```csharp
await db.Sessions.Where(s => s.UserId == userId).ExecuteDeleteAsync();
```

A request that finds the session ended continues without a session and the cookie is cleared, so an endpoint that requires a session responds with `401 Unauthorized` (`sign-in-required`). A browser's request, which the service tells by its fetch metadata (`Sec-Fetch-Site`, which current browsers send), keeps a cookie that was due for the lookup: the request can have run while a sign-in on the device replaced its session, and its response would delete the new session's cookie. A browser that doesn't send fetch metadata gets the deletion. Such a cookie stays due, so the session is looked up whenever a request reads it, until a sign-in or sign-out replaces the cookie. A cookie that a forced validation found ended before it was due is cleared. For the same reason, a response reissues a cookie, such as with a replacement token, only if the session is still in the store when the response starts. To give the client a reason, such as a disabled account, throw an <xref:Singulink.Net.Http.Api.UnauthorizedApiException> from <xref:Singulink.Net.Http.Api.Service.ISessionStore`1.GetSessionAsync*> instead: the session is invalidated through <xref:Singulink.Net.Http.Api.Service.ISessionStore`1.InvalidateSessionAsync*>, the cookie is cleared, and the request fails with that exception. In a SignalR connection, or a streaming response that has already started, the cookie can no longer be cleared: the client receives the exception, and its requests find the session gone when they next validate it, as below. The recheck of an open SignalR connection that ends the session also tells the toolkit's client that the session ended, which ends it on the device (see [Rechecking the sessions of open connections](signalr-hubs.md#rechecking-the-sessions-of-open-connections)). Requests notice the change when they next validate the session against the store, which they do when the cookie is due for a refresh: within <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.RefreshAfter> (10 minutes by default) of the change, on any server. An endpoint that uses <xref:Singulink.Net.Http.Api.Service.SessionAccessOptions.ForceValidate?displayProperty=nameWithType> validates the session on every request and notices it right away. Open SignalR connections notice it at their next session check, unless their rechecks are turned off (see [Rechecking the sessions of open connections](signalr-hubs.md#rechecking-the-sessions-of-open-connections)).

Changing the data the token captures, and with it the user's token version, does not end sessions: the store only replaces their tokens.

## Retrieving the Session in Endpoints

The simplest way to require a signed-in user is a non-nullable token parameter. A nullable parameter makes sign-in optional:

```csharp
app.MapGet("/me", (SessionToken token) => new UserInfo(token.UserId));
app.MapGet("/public", (SessionToken? token) => token is null ? "Hello, guest" : $"Hello, user {token.UserId}");
```

Retrieval options are set with <xref:Singulink.Net.Http.Api.Service.SessionAccessAttribute> on the parameter, or passed to <xref:Singulink.Net.Http.Api.Service.SessionContext`1.GetTokenAsync*?displayProperty=nameWithType> and <xref:Singulink.Net.Http.Api.Service.SessionContext`1.GetRequiredTokenAsync*?displayProperty=nameWithType> when using the context directly. The <xref:Singulink.Net.Http.Api.Service.SessionAccessOptions> flags are:

#### Forced validation

<xref:Singulink.Net.Http.Api.Service.SessionAccessOptions.ForceValidate?displayProperty=nameWithType> validates the session against the store even if the cookie is not due for a refresh. As with a periodic refresh, a token that the store replaces is replaced before the endpoint runs. With it, a user whose access was revoked moments ago cannot act on a token that still lists it, as in this endpoint for permanent deletions:

```csharp
static async Task DeletePermanentlyAsync(long id, [SessionAccess(SessionAccessOptions.ForceValidate)] SessionToken token, AppDbContext db)
{
    // token reflects the user's current access
}
```

Forced validation never rotates the session's generation, so it does not affect concurrent requests; it costs one store lookup per retrieval. Each retrieval that forces validation validates the session again, also later in the same request or SignalR connection, except for a token that the request set itself by signing in or with <xref:Singulink.Net.Http.Api.Service.SessionContext`1.SetToken*?displayProperty=nameWithType>.

#### Optional user ID precondition

Retrieving the session token checks the user that the client is acting for, which a request identifies in the `if-userId` query parameter (name configurable via <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.UserIdPreconditionQueryName?displayProperty=nameWithType>). If the parameter is missing the request fails with <xref:Singulink.Net.Http.Api.UserRequiredApiException> (HTTP 428), and if it does not match the session user it fails with <xref:Singulink.Net.Http.Api.UserChangedApiException> (HTTP 412), whose <xref:Singulink.Net.Http.Api.UserChangedApiException.ExpectedUserId> is the parameter's value. The error that the client gets carries neither that value nor the session's user, and the toolkit's client sets the property from its own request. This catches clients that are still acting for a user who has since signed out or been replaced on the same device. <xref:Singulink.Net.Http.Api.Service.SessionAccessOptions.OptionalUserIdPrecondition?displayProperty=nameWithType> skips the check when the parameter is absent, for endpoints that are called before the client knows who is signed in, such as "get current session".

#### Allowing all origins

Requests are rejected with <xref:Singulink.Net.Http.Api.ForbiddenApiException> when they carry an `Origin` header that is not in the allowed origins, which blocks cross-site request forgery. <xref:Singulink.Net.Http.Api.Service.SessionAccessOptions.AllowAllOrigins?displayProperty=nameWithType> disables the check for an endpoint that is safe to call from anywhere.

Flags that should apply to every retrieval can be set once through <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.ForcedAccessOptions?displayProperty=nameWithType>.

### Using the session context directly

Endpoints can also take a <xref:Singulink.Net.Http.Api.Service.SessionContext`1> parameter to call <xref:Singulink.Net.Http.Api.Service.SessionContext`1.SetToken*?displayProperty=nameWithType> after changing something the token captures (so the user gets an updated token immediately; the session keeps its refresh info), <xref:Singulink.Net.Http.Api.Service.SessionContext`1.ClearToken*?displayProperty=nameWithType>, or to read the request's <xref:Singulink.Net.Http.Api.Service.SessionContext`1.Device?displayProperty=nameWithType> and <xref:Singulink.Net.Http.Api.Service.SessionContext`1.IpAddress?displayProperty=nameWithType>. <xref:Singulink.Net.Http.Api.Service.HttpContextExtensions.GetClientDevice*> and <xref:Singulink.Net.Http.Api.Service.HttpContextExtensions.GetClientIpAddress*> get the same values from an `HttpContext`, without a session context. The IP address of a client that connects over IPv4 to a socket that also accepts IPv6 is the IPv4 address, rather than the IPv4-mapped IPv6 address that the connection reports. Outside of endpoint parameters, <xref:Singulink.Net.Http.Api.Service.HttpContextExtensions.GetRequiredSessionTokenAsync*> and related methods on `HttpContext` provide the same access, and <xref:Singulink.Net.Http.Api.Service.HubCallerContextExtensions.GetRequiredSessionTokenAsync*> does the same for SignalR hubs.

The token is read once per request and cached, and a due cookie is validated once, so retrieving the token from several places in the same request costs nothing extra unless the retrievals force validation.

## Options

<xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions> controls the cookie and the validation policy:

| Option | Default | Purpose |
| --- | --- | --- |
| <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.SessionCookieName> | `__Host-session-token` | Cookie name, which the client's cookie name matches. |
| <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.CookieDomain> | host only | A parent domain shares the session with its subdomains. |
| <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.CookiePath> | `/` | Cookie path. |
| <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.DataProtectionPurpose> | from the token type | Data protection purpose of the cookie. The default includes the token type's full name. |
| <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.UserIdPreconditionQueryName> | `if-userId` | Query parameter that carries the user ID precondition. |
| <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.ForcedAccessOptions> | none | Access options applied to every retrieval. |
| <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.RefreshAfter> | 10 minutes | How long a cookie is used before the session is validated against the store and refreshed. |
| <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.PreviousGenerationGracePeriod> | 2 minutes | How long a cookie from the previous generation is still accepted from the same device. |
| <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.MaxDeviceLength> | 512 | Longest device description recorded for a session; longer ones are shortened. |
| <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.TempSessionExpiry> | 1 day | Sliding expiry for non-persistent sessions. |
| <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.PersistentSessionExpiry> | 30 days | Sliding expiry for persistent sessions. |
| <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.MaxTempSessionLifetime> | no limit | Longest a non-persistent session lasts after sign-in, even while in use. |
| <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.MaxPersistentSessionLifetime> | no limit | Longest a persistent session lasts after sign-in, even while in use. |
| <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.RevalidateHubConnections> | `true` | Whether the sessions of SignalR connections are rechecked (see [SignalR Hubs](signalr-hubs.md#rechecking-the-sessions-of-open-connections)). |

Browsers only accept a cookie whose name starts with `__Host-`, as the default name does, without a domain and with the path `/`. An app that sets <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.CookieDomain> or another <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.CookiePath> with such a name fails to start, so it chooses another name, and its clients use the same one.

Expiry is sliding: it is measured from the last refresh, so an active session never expires unless a maximum lifetime is set. The cookie of a persistent session expires when the session does. Both expiries must be longer than <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.RefreshAfter>, since sessions are only refreshed after it; the options are validated when the app starts. See [Session Token Model](../concepts/session-token-model.md) for how the grace period and generations interact.

### Maximum session lifetime

To end sessions a fixed time after sign-in no matter how actively they are used, set a maximum lifetime for either kind of session. For example, to sign users out of temporary sessions after two hours without use, and twelve hours after sign-in at the latest:

```csharp
services.AddHttpSessionHandling<SessionToken, SessionStore>(options => {
    options.TempSessionExpiry = TimeSpan.FromHours(2);
    options.MaxTempSessionLifetime = TimeSpan.FromHours(12);
});
```

The limits work as follows:

- They are measured from <xref:Singulink.Net.Http.Api.Service.SessionRecord.CreatedUtc?displayProperty=nameWithType>, which the store records when each session is created and returns from <xref:Singulink.Net.Http.Api.Service.ISessionStore`1.GetSessionAsync*?displayProperty=nameWithType>.
- Like the sliding expiry, they are enforced when the session is validated against the store: on a periodic refresh or a forced validation. A cookie's validity period never extends past the limit, and a cookie is due for a refresh when its validity period ends if that comes before <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.RefreshAfter>, so the first request after the limit ends the session.
- A session past its limit is signed out like an expired one: the record is removed through <xref:Singulink.Net.Http.Api.Service.ISessionStore`1.InvalidateSessionAsync*?displayProperty=nameWithType>, and the cookie is cleared as for an expired session (see [Ending Sessions](#ending-sessions)).
- The expiry time given at sign-in and on each refresh never extends past the limit. Near the end of a session the record's expiry time is therefore the limit rather than a full expiry period after the refresh, and a persistent session's cookie expires then too.

### Listing sessions

Validation ends a session once the time is past its <xref:Singulink.Net.Http.Api.Service.SessionRecord.ExpiresUtc?displayProperty=nameWithType>, so a list of a user's sessions can leave out the ones that have expired but have not been removed yet, and expired records can be removed, with a filter that the database runs:

```csharp
static Task<List<SessionInfo>> GetSessionsAsync(SessionToken token, AppDbContext db, TimeProvider time)
{
    var utcNow = time.GetUtcNow().UtcDateTime;

    return db.Sessions
        .Where(s => s.UserId == token.UserId && s.ExpiresUtc >= utcNow)
        .Select(s => new SessionInfo(s.Id, s.Device, s.CreatedUtc, s.ExpiresUtc))
        .ToListAsync();
}

static Task<int> RemoveExpiredSessionsAsync(AppDbContext db, TimeProvider time)
{
    var utcNow = time.GetUtcNow().UtcDateTime;
    return db.Sessions.Where(s => s.ExpiresUtc < utcNow).ExecuteDeleteAsync();
}
```

The expiry time of a record is capped at the end of the maximum lifetime that was configured when the record was written. Validation also applies the maximum lifetime that is configured now, so a session whose limit was shortened since its record was last written ends at the new limit, before its expiry time.

## Upgrading from 3.x

See [Upgrading from 3.x](upgrading.md) for the changes to session tokens, session stores and the session cookie.

## Next Steps

Continue with these related articles:

- [Session Token Model](../concepts/session-token-model.md) - Refresh, rotation, grace periods and what the store methods are called for.
- [Sign-In Codes and Device Linking](sign-in-codes.md) - Handing a sign-in from one place to another and approving new devices.
- [Passkeys](passkeys.md) - Passkey sign-in, verification and registration.
- [Building API Clients](api-clients.md) - Persisting the session on the client and sending the user ID precondition.
- [SignalR Hubs](signalr-hubs.md) - Authorizing hub connections with the session.

</div>
