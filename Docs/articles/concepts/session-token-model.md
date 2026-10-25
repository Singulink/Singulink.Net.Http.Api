<div class="article">

# Session Token Model

This article explains the lifecycle of a session: how it is validated without a store lookup, when it is refreshed and rotated, how concurrent requests and reconnecting connections are tolerated, and how stolen cookies are detected.

### Two representations of a session

A session exists in two places. The **session record** in the store is the source of truth for whether the session is alive; it holds the refresh time, expiry time, generation counter, and the device and IP address that last refreshed it. The library sees it as a <xref:Singulink.Net.Http.Api.Service.SessionRecord>. The **session cookie** is a snapshot. It holds the application's **session token**, with the user's identity and whatever else the service captured, and next to it the library's copy of the record's refresh info at the time the cookie was issued: the refresh time, the validity period, the generation and whether the session is persistent. The cookie is serialized to JSON and encrypted with ASP.NET Core data protection, so it cannot be read or forged by clients.

The library maintains the refresh info itself, so the token only holds the application's data, and the application only builds a token when a session starts or when the data in it is out of date.

## Validation Without a Lookup

On most requests the service only decrypts and deserializes the cookie. Three checks then happen without touching the store:

- The request's `Origin` header, if present, must be an allowed origin. This blocks cross-site request forgery, which cookie-based sessions are otherwise exposed to. Signing in checks it too.
- The user ID precondition query parameter must match the token's user, unless the endpoint made it optional.
- The cookie's refresh time plus <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.RefreshAfter?displayProperty=nameWithType>, or plus its validity period if that is shorter, determines whether a refresh is due.

If nothing is due and nothing is forced, the endpoint runs with the token as-is. The token is cached for the rest of the request, so retrieving it from several parameters or from a hub costs nothing extra, except that every retrieval that forces validation looks the session up again.

## Periodic Refresh

When a refresh is due, the service performs a single store lookup at the start of the request with <xref:Singulink.Net.Http.Api.Service.ISessionStore`1.GetSessionAsync*?displayProperty=nameWithType>, which answers two questions at once:

1. **Liveness.** No record means the session is over: the cookie is cleared, except that a browser's request keeps a cookie that was due (see [Ending Sessions](../guides/session-handling.md#ending-sessions)), and the request proceeds as anonymous. A record whose sliding expiry has passed, or one past its maximum lifetime (see below), is over too, and is also removed from the store.
2. **Current data.** The store returns a replacement token in the <xref:Singulink.Net.Http.Api.Service.SessionLookupResult`1> if the data captured in the token has changed since it was created, typically by comparing a token version. Stores usually fold this into the same query that loads the record, so the two questions cost one round trip.

A replacement token takes effect right away, with the record's existing refresh info, so the endpoint never runs on a token the store already knows is out of date. The endpoint then runs with the current token.

The refresh itself is deferred until the response starts. The service builds a <xref:Singulink.Net.Http.Api.Service.SessionRecord> holding the current device and IP address, a new refresh time and expiry time, and the next generation, and hands it to <xref:Singulink.Net.Http.Api.Service.ISessionStore`1.TryRefreshSessionAsync*?displayProperty=nameWithType>. The write is conditional on the stored generation still being the one that was validated at request start, so it is a single statement with no second lookup. If it succeeds, the new refresh info goes into the cookie next to the (now current) token, and the new cookie goes out with the response. If it fails, another request refreshed or ended the session in the meantime; nothing is written and no cookie is issued, since that request's response already delivered the current cookie to the client.

Deferring the store write and the cookie avoids rotating the session for a request that then fails, and keeps the endpoint on one consistent token for its whole duration. If an endpoint sets or clears the token itself (for example sign-out), the deferred refresh is skipped.

Each store operation, the lookup at request start and the write at response start, runs in a service scope of its own, so a store's database context is never shared with the endpoint's.

Expiry is sliding. Each refresh moves the expiry time to a full expiry period after that moment, so a session that is used at least once per expiry period never expires, while an abandoned one expires after <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.TempSessionExpiry?displayProperty=nameWithType> or <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.PersistentSessionExpiry?displayProperty=nameWithType>. Expiry is always judged by the record in the store, not by the cookie.

### Maximum lifetime

Sliding expiry alone lets a session in regular use last forever. <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.MaxTempSessionLifetime?displayProperty=nameWithType> and <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.MaxPersistentSessionLifetime?displayProperty=nameWithType> optionally cap how long a session lasts after sign-in, however active it is. Both are off by default. The cap is measured from <xref:Singulink.Net.Http.Api.Service.SessionRecord.CreatedUtc?displayProperty=nameWithType>, the creation time the store records for every session.

A session past its maximum lifetime ends exactly like an expired one: the same store lookup detects it, the record is removed and the request proceeds as anonymous. Like expiry, the limit is only checked when the session is validated, on a periodic refresh or a forced validation. A cookie whose validity period ends before its refresh interval does is due for a refresh when the validity period ends, so a session in active use ends at its first request after the limit.

A refresh never extends a session past its maximum lifetime. When less than a full expiry period remains, the refreshed record's expiry time is the limit, and the cookie of a persistent session expires then too, rather than a full expiry period later. The expiry time handed to the application at sign-in is capped the same way. If the limit passes while a request runs, between validation and the deferred refresh, the session is not refreshed and the next validation ends it. The creation time never changes: the refreshed <xref:Singulink.Net.Http.Api.Service.SessionRecord> carries over the value that was loaded.

Validation compares the time with the record's <xref:Singulink.Net.Http.Api.Service.SessionRecord.ExpiresUtc?displayProperty=nameWithType>, and with the end of the maximum lifetime as it is configured at the time, which ends a session at a limit that was shortened after its record was last written.

## Generations and Theft Detection

Every refresh increments the record's generation, and the new cookie carries the new value. A cookie whose generation no longer matches the record is therefore one that was issued before a later refresh. On the next validation such a cookie is rejected and the session is invalidated, unless one of the rules below accepts it, which is what limits the damage from a stolen cookie: once the legitimate client refreshes, the copy stops working, and if the thief refreshes first, the legitimate client's next validation ends the session for both.

Rotation is only meaningful because refreshes are infrequent; forced validation (below) deliberately does not rotate so that security-sensitive endpoints can be called freely.

### The grace period

Rotation would break legitimate clients if it were strict. Two situations produce a valid client holding the previous generation:

- **Concurrent requests.** Two requests are sent with the same cookie; the first response rotates the session, and the second was already in flight with the old cookie.
- **Reconnecting connections.** A long-lived connection such as a SignalR hub was established with a cookie that an HTTP request has since refreshed. The toolkit's client presents the current cookie when reconnecting, but a request can still race a refresh.

A cookie exactly one generation behind is accepted for <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.PreviousGenerationGracePeriod?displayProperty=nameWithType> after the rotation, provided it comes from the same device (as described by the User-Agent header). The IP address is deliberately not compared, since mobile devices change networks constantly. Within the grace window the cookie is brought in line with the record's refresh info before the endpoint runs, and the current generation is reissued to the client without a store write or another rotation.

A request or connection also accepts a cookie of any older generation when the record was refreshed after its cookie was last known to be current: when the cookie was read and not due for a refresh, or when the session was last validated. This covers a SignalR connection, which keeps the cookie of its handshake while the client's requests refresh the session, and a request whose session another request refreshes while it runs. A cookie that was already behind the record when it was read gets no such allowance.

Any other cookie that is behind the record, such as one two or more generations behind when it is read, or one from a different device, is rejected.

The conditional refresh write closes the other side of the race. If two requests validate the same generation and both reach their deferred refresh, only the first write succeeds; the second sees that the generation moved on and issues nothing, so the session is never rotated twice for one refresh interval and a client can never be pushed more than one generation behind by its own concurrent requests.

> [!NOTE]
> With a refresh interval well above the grace period, such as the default ten minute <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.RefreshAfter?displayProperty=nameWithType> and two minute grace period, a legitimate client can only fall behind by one generation, and a stolen cookie is detected within one refresh interval.

## Forced Validation

Endpoints declared with <xref:Singulink.Net.Http.Api.Service.SessionAccessOptions.ForceValidate> perform the liveness lookup even when no refresh is due; everything else is the same as a periodic refresh. A replacement token takes effect before the endpoint runs, and the cookie is reissued with it, but no rotation occurs and nothing is written to the store unless a periodic refresh also happens to be due, so losing the response is harmless.

An endpoint that forces validation, such as one for permanent deletions, sees an access change as soon as the store does. Forced validation never rotates, so concurrent requests are unaffected however many endpoints use it.

## What Changes Are Reflected When

The model implies a bounded staleness window that is worth being explicit about:

- **Session ended elsewhere** (sign-out from another device, a deleted record, a store that returns `null`): noticed at the next periodic refresh or forced validation, since only the store lookup sees it. Until then, requests with the session's cookie are accepted, on every server: for at most <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.RefreshAfter?displayProperty=nameWithType> after the session was last refreshed. SignalR connections with session revalidation are closed at their next check, within <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.RefreshAfter?displayProperty=nameWithType> plus about a minute after their session was last checked. Connections without session revalidation stay open.
- **Maximum lifetime reached**: likewise noticed at the next periodic refresh or forced validation.
- **Captured data changed** (roles, memberships, anything in the token): the store replaces the token before the endpoint runs on the next periodic refresh or forced validation, whichever comes first.
- **Data the token does not capture**: always current, because the endpoint queries it.

A token that captures what common requests need to be authorized, with a token version that changes whenever any of it changes, authorizes them without a lookup and is brought up to date within a refresh interval, while endpoints that force validation see changes right away. A token version only brings the data in the token up to date; ending sessions is the store's job (see [Ending Sessions](../guides/session-handling.md#ending-sessions)).

## Further Reading

These articles cover the related topics in more depth:

- [Session Handling](../guides/session-handling.md) - Implementing the token, record and store.
- [Building API Clients](../guides/api-clients.md) - How the client stores the cookie and sends the precondition.

</div>
