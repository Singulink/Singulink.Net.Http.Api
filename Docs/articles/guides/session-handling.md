<div class="article">

# Session Handling

This guide covers implementing sessions on the service: the token type, the session store, signing in and out, retrieving the session in endpoints, and the options that control validation and expiry.

### How sessions work

A session is represented by two things: a **session token** that lives in an encrypted cookie and travels with every request, and a **session record** in a store of your choosing (typically a database table). The token is self-contained, so most requests are authenticated by decrypting the cookie alone. The store is consulted only when the token is due for a periodic refresh, when a request forces validation, or when signing out. [Session Token Model](../concepts/session-token-model.md) explains the lifecycle in depth; this guide focuses on what you implement.

### Types you provide

Session handling is generic over the token type and is backed by a store that you implement:

- A **token type** implementing <xref:Singulink.Net.Http.Api.Service.ISessionToken>. It is serialized to JSON, encrypted with ASP.NET Core data protection and stored in the cookie.
- A **store context** implementing <xref:Singulink.Net.Http.Api.Service.ISessionStoreContext`1>, created by an <xref:Singulink.Net.Http.Api.Service.ISessionStoreContextFactory`1>. The store exchanges session values with the library through <xref:Singulink.Net.Http.Api.Service.SessionRecord> and <xref:Singulink.Net.Http.Api.Service.SessionLookupResult>, so your session entity does not need to implement anything.

## The Session Token

The token carries the user's identity plus whatever the service needs on every request without a lookup, such as roles or a list of accessible resources. It is encrypted, so clients never see its contents; it is a server-side type, and anything the client needs to know about the session is returned explicitly by endpoints such as "get current session". A record works well:

```csharp
public record SessionToken(
    long SessionId,
    int UserId,
    int SecurityStamp,
    List<RoleSummary> Roles,
    DateTime RefreshedUtc,
    TimeSpan ValidFor,
    int Generation,
    bool IsPersistent) : ISessionToken, IBindableFromHttpContext<SessionToken>
{
    [JsonIgnore]
    public TimeSpan RefreshAfter => TimeSpan.FromMinutes(10);

    string ISessionToken.UserId => UserId.ToString(CultureInfo.InvariantCulture);

    static ValueTask<SessionToken?> IBindableFromHttpContext<SessionToken>.BindAsync(HttpContext context, ParameterInfo parameter)
    {
        return context.BindSessionTokenAsync<SessionToken>(parameter);
    }
}
```

The interface members have specific roles:

- <xref:Singulink.Net.Http.Api.Service.ISessionToken.RefreshedUtc>, <xref:Singulink.Net.Http.Api.Service.ISessionToken.ValidFor> and <xref:Singulink.Net.Http.Api.Service.ISessionToken.Generation> are refresh info copied from the session record when the token is created. Your code never sets them; the store's token creation method applies the values from the <xref:Singulink.Net.Http.Api.Service.SessionRecord> it is given.
- <xref:Singulink.Net.Http.Api.Service.ISessionToken.RefreshAfter> is how long a token is used before the service validates it against the store and issues a fresh one. It is a property so that it can vary by user type. Ten minutes is a reasonable default; longer values reduce store traffic at the cost of slower reaction to changes in the user's data.
- <xref:Singulink.Net.Http.Api.Service.ISessionToken.UserId> is a string so that the library can compare it with the user ID precondition query parameter without knowing your ID type.
- <xref:Singulink.Net.Http.Api.Service.ISessionToken.IsPersistent> controls whether the cookie survives browser and app restarts.

The `SecurityStamp` above is not part of the interface, but it is the recommended way to detect when a token's contents are out of date: bump a stamp on the user record whenever anything captured in the token changes, and compare it in the store (see below).

Implementing `IBindableFromHttpContext<T>` as shown lets endpoints take the token as a parameter. <xref:Singulink.Net.Http.Api.Service.HttpContextExtensions.BindSessionTokenAsync*> reads the parameter's nullability and its <xref:Singulink.Net.Http.Api.Service.SessionAccessAttribute> to decide how to retrieve it.

## The Session Store

The session record is whatever entity your data layer uses; the library never sees it directly. It must hold the values the service updates on refresh (device, IP address, refresh time, validity and generation), and usually a link to the user so that the staleness check can be folded into the lookup:

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
    public TimeSpan ValidFor { get; set; }
    public int Generation { get; set; }
    public bool IsPersistent { get; set; }
}
```

The store context is the library's window onto your database. It is created per operation through the factory and disposed afterwards, so a context that owns a database connection is the natural shape:

```csharp
public sealed class SessionStoreContextFactory(IDbContextFactory<AppDbContext> dbFactory) : ISessionStoreContextFactory<SessionToken>
{
    public ISessionStoreContext<SessionToken> Create() => new SessionStoreContext(dbFactory.CreateDbContext());
}

public sealed class SessionStoreContext(AppDbContext db) : ISessionStoreContext<SessionToken>
{
    public ValueTask DisposeAsync() => db.DisposeAsync();

    public Task<SessionLookupResult?> GetSessionAsync(SessionToken token)
    {
        // One query loads the record and compares the user's current security stamp to the one captured in the token.
        return db.Sessions
            .Where(s => s.Id == token.SessionId)
            .Select(s => new SessionLookupResult(
                new SessionRecord(s.Device, s.IpAddress, s.RefreshedUtc, s.ValidFor, s.Generation, s.IsPersistent),
                s.User.SecurityStamp != token.SecurityStamp))
            .FirstOrDefaultAsync();
    }

    public async Task<bool> TryRefreshSessionAsync(SessionToken token, SessionRecord refreshed, int expectedGeneration)
    {
        // A single conditional update: the generation check and the write happen atomically.
        int rows = await db.Sessions
            .Where(s => s.Id == token.SessionId && s.Generation == expectedGeneration)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Device, refreshed.Device)
                .SetProperty(x => x.IpAddress, refreshed.IpAddress)
                .SetProperty(x => x.RefreshedUtc, refreshed.RefreshedUtc)
                .SetProperty(x => x.ValidFor, refreshed.ValidFor)
                .SetProperty(x => x.Generation, refreshed.Generation));

        return rows == 1;
    }

    public Task InvalidateSessionAsync(SessionToken token) =>
        db.Sessions.Where(s => s.Id == token.SessionId).ExecuteDeleteAsync();

    public async ValueTask<SessionToken> CreateTokenAsync(SessionToken previous, SessionRecord session, bool isStale)
    {
        if (!isStale)
        {
            return previous with {
                RefreshedUtc = session.RefreshedUtc,
                ValidFor = session.ValidFor,
                Generation = session.Generation,
            };
        }

        var user = await db.Users.Include(u => u.Roles).SingleAsync(u => u.Id == previous.UserId);
        var roles = user.Roles.Select(r => new RoleSummary(r.Id, r.Name)).ToList();

        return new SessionToken(
            previous.SessionId, user.Id, user.SecurityStamp, roles,
            session.RefreshedUtc, session.ValidFor, session.Generation, previous.IsPersistent);
    }
}
```

Each method has a specific job:

- <xref:Singulink.Net.Http.Api.Service.ISessionStoreContext`1.GetSessionAsync*?displayProperty=nameWithType> loads the record and reports whether the token is stale in one call. Return `null` when the session does not exist. The staleness check should be cheap; comparing a security stamp inside the same query, as above, costs nothing extra.
- <xref:Singulink.Net.Http.Api.Service.ISessionStoreContext`1.TryRefreshSessionAsync*?displayProperty=nameWithType> writes the refreshed values, but only if the stored generation still matches the one the service validated at request start. The check and the write must be atomic, which a filtered `UPDATE` gives you for free. Return `false` when no row was updated: the service then skips issuing a cookie because a concurrent request already refreshed (or ended) the session.
- <xref:Singulink.Net.Http.Api.Service.ISessionStoreContext`1.InvalidateSessionAsync*?displayProperty=nameWithType> removes the record. It is called on sign-out and when validation rejects a token.
- <xref:Singulink.Net.Http.Api.Service.ISessionStoreContext`1.CreateTokenAsync*?displayProperty=nameWithType> produces a new token with the refresh info from the given <xref:Singulink.Net.Http.Api.Service.SessionRecord> applied. When the previous token is current the implementation can copy it and apply the refresh info synchronously, which is why the method returns a <xref:System.Threading.Tasks.ValueTask`1>. When it is stale the token must be rebuilt from the latest data. Rebuilding regardless of the flag is always correct, just slower.

Register everything with <xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddHttpSessionHandling*>, optionally configuring <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions>:

```csharp
services.AddHttpSessionHandling<SessionToken, SessionStoreContextFactory>(options => {
    options.PersistentSessionExpiry = TimeSpan.FromDays(90);
});
```

> [!NOTE]
> The cookie is encrypted with ASP.NET Core data protection. Configure the data protection key ring to persist across restarts and deployments (for example with `PersistKeysToFileSystem`), otherwise every restart signs all users out. Services sharing a session cookie across subdomains must also share the key ring and set <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.CookieDomain?displayProperty=nameWithType>.

## Signing In and Out

Endpoints receive the session context as a <xref:Singulink.Net.Http.Api.Service.SessionContext`1> parameter. Sign in with <xref:Singulink.Net.Http.Api.Service.SessionContext`1.SignInAsync*?displayProperty=nameWithType>, which calls your function to verify credentials and create the session record, then issues the cookie:

```csharp
static async Task<CurrentSessionInfo> SignInAsync(SignInCommand command, SessionContext<SessionToken> sessionContext, AppDbContext db)
{
    var token = await sessionContext.SignInAsync(command.RememberMe, async info => {
        var user = await VerifyCredentialsAsync(db, command.Email, command.Password);   // throws UnauthorizedApiException on failure

        var session = new Session {
            UserId = user.Id,
            Device = info.Device,
            IpAddress = info.IpAddress,
            CreatedUtc = DateTime.UtcNow,
            RefreshedUtc = DateTime.UtcNow,
            ValidFor = info.SessionExpiry,
            IsPersistent = info.IsPersistent,
        };

        db.Sessions.Add(session);
        await db.SaveChangesAsync();

        return new SessionToken(session.Id, user.Id, user.SecurityStamp, roles, session.RefreshedUtc, session.ValidFor, Generation: 0, session.IsPersistent);
    });

    return new CurrentSessionInfo(token.UserId);
}

static Task SignOutAsync(SessionContext<SessionToken> sessionContext) => sessionContext.SignOutAsync();
```

The <xref:Singulink.Net.Http.Api.Service.SignInInfo> passed to your function carries the device (parsed from the User-Agent header), the IP address, the expiry duration selected by the persistent flag, and the flag itself. Copy them onto the session record as shown.

<xref:Singulink.Net.Http.Api.Service.SessionContext`1.SignOutAsync*?displayProperty=nameWithType> invalidates the session in the store and clears the cookie. It does not validate or refresh the session first, so it works even when the token is expired or the store record is already gone.

## Retrieving the Session in Endpoints

The simplest way to require a signed-in user is a non-nullable token parameter. A nullable parameter makes sign-in optional:

```csharp
app.MapGet("/me", (SessionToken token) => new UserInfo(token.UserId));
app.MapGet("/public", (SessionToken? token) => token is null ? "Hello, guest" : $"Hello, user {token.UserId}");
```

Retrieval options are set with <xref:Singulink.Net.Http.Api.Service.SessionAccessAttribute> on the parameter, or passed to <xref:Singulink.Net.Http.Api.Service.SessionContext`1.GetTokenAsync*?displayProperty=nameWithType> and <xref:Singulink.Net.Http.Api.Service.SessionContext`1.GetRequiredTokenAsync*?displayProperty=nameWithType> when using the context directly. The <xref:Singulink.Net.Http.Api.Service.SessionAccessOptions> flags are:

#### Forced validation

<xref:Singulink.Net.Http.Api.Service.SessionAccessOptions.ForceValidate?displayProperty=nameWithType> validates the session against the store even if the token is not due for a refresh. As with a periodic refresh, a token whose contents are out of date is rebuilt before the endpoint runs. Use it for security-sensitive operations such as permanent deletions, so that a user whose access was revoked moments ago cannot act on a token that still lists it:

```csharp
static async Task DeletePermanentlyAsync(long id, [SessionAccess(SessionAccessOptions.ForceValidate)] SessionToken token, AppDbContext db)
{
    // token reflects the user's current access
}
```

Forced validation never rotates the token's generation, so it is safe to use liberally; the cost is one store lookup per request.

#### Optional user ID precondition

Every request is expected to identify the user the client believes it is acting for, in the `if-userId` query parameter (name configurable via <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.UserIdPreconditionQueryName?displayProperty=nameWithType>). If the parameter is missing the request fails with <xref:Singulink.Net.Http.Api.UserRequiredApiException> (HTTP 428), and if it does not match the session user it fails with <xref:Singulink.Net.Http.Api.UserChangedApiException> (HTTP 412). This catches clients that are still acting for a user who has since signed out or been replaced on the same device. <xref:Singulink.Net.Http.Api.Service.SessionAccessOptions.OptionalUserIdPrecondition?displayProperty=nameWithType> skips the check when the parameter is absent, for endpoints that are called before the client knows who is signed in, such as "get current session".

#### Allowing all origins

Requests are rejected with <xref:Singulink.Net.Http.Api.ForbiddenApiException> when they carry an `Origin` header that is not in the allowed origins, which blocks cross-site request forgery. <xref:Singulink.Net.Http.Api.Service.SessionAccessOptions.AllowAllOrigins?displayProperty=nameWithType> disables the check for an endpoint that is safe to call from anywhere.

Flags that should apply to every retrieval can be set once through <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.ForcedAccessOptions?displayProperty=nameWithType>.

### Using the session context directly

Endpoints can also take a <xref:Singulink.Net.Http.Api.Service.SessionContext`1> parameter to call <xref:Singulink.Net.Http.Api.Service.SessionContext`1.SetToken*?displayProperty=nameWithType> after changing something the token captures (so the user gets an updated token immediately), <xref:Singulink.Net.Http.Api.Service.SessionContext`1.ClearToken*?displayProperty=nameWithType>, or to read the request's <xref:Singulink.Net.Http.Api.Service.SessionContext`1.Device?displayProperty=nameWithType> and <xref:Singulink.Net.Http.Api.Service.SessionContext`1.IpAddress?displayProperty=nameWithType>. Outside of endpoint parameters, <xref:Singulink.Net.Http.Api.Service.HttpContextExtensions.GetRequiredSessionTokenAsync*> and related methods on `HttpContext` provide the same access, and <xref:Singulink.Net.Http.Api.Service.HubCallerContextExtensions.GetRequiredSessionTokenAsync*> does the same for SignalR hubs.

The token is read and validated at most once per request and cached, so retrieving it from several places in the same request costs nothing extra.

## Options

<xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions> controls the cookie and the validation policy:

| Option | Default | Purpose |
| --- | --- | --- |
| <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.SessionCookieName> | `session-token` | Cookie name. Must match the client's cookie name. |
| <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.CookieDomain> | host only | Set to a parent domain to share the session across subdomains. |
| <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.CookiePath> | `/` | Cookie path. |
| <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.UserIdPreconditionQueryName> | `if-userId` | Query parameter that carries the user ID precondition. |
| <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.ForcedAccessOptions> | none | Access options applied to every retrieval. |
| <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.MultipleRefreshGracePeriod> | 2 minutes | How long a token from the previous generation is still accepted from the same device. |
| <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.TempSessionExpiry> | 1 day | Sliding expiry for non-persistent sessions. |
| <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.PersistentSessionExpiry> | 30 days | Sliding expiry for persistent sessions. |

Expiry is sliding: it is measured from the last refresh, so an active session never expires. See [Session Token Model](../concepts/session-token-model.md) for how the grace period and generations interact.

## Next Steps

Continue with these related articles:

- [Session Token Model](../concepts/session-token-model.md) - Refresh, rotation, grace periods and what the store methods are called for.
- [Building API Clients](api-clients.md) - Persisting the session on the client and sending the user ID precondition.
- [SignalR Hubs](signalr-hubs.md) - Authorizing hub connections with the session.

</div>
