<div class="article">

# Session Token Model

This article explains the lifecycle of a session token: how it is validated without a store lookup, when it is refreshed and rotated, how concurrent requests and reconnecting connections are tolerated, and how stolen tokens are detected.

### Two representations of a session

A session exists in two places. The **session record** in the store is the source of truth for whether the session is alive; it holds the refresh time, validity period, generation counter, and the device and IP address that last refreshed it. The **session token** in the cookie is a snapshot: the user's identity and whatever else the service captured, plus a copy of the record's refresh info at the time the token was issued. The token is serialized to JSON and encrypted with ASP.NET Core data protection, so it cannot be read or forged by clients.

## Validation Without a Lookup

On most requests the service only decrypts and deserializes the cookie. Three checks then happen without touching the store:

- The request's `Origin` header, if present, must be an allowed origin. This blocks cross-site request forgery, which cookie-based sessions are otherwise exposed to.
- The user ID precondition query parameter must match the token's user, unless the endpoint made it optional.
- The token's refresh time plus its <xref:Singulink.Net.Http.Api.Service.ISessionToken.RefreshAfter> interval determines whether a refresh is due.

If nothing is due and nothing is forced, the endpoint runs with the token as-is. The token is cached for the rest of the request, so retrieving it from several parameters or from a hub costs nothing extra.

## Periodic Refresh

When a refresh is due, the service performs two store operations at the start of the request:

1. **Liveness.** The session record is loaded with <xref:Singulink.Net.Http.Api.Service.ISessionStoreContext`2.GetSessionDataAsync*>. A missing record, or one whose sliding expiry has passed, means the session is over: the record is removed and the cookie is cleared, and the request proceeds as anonymous.
2. **Staleness.** <xref:Singulink.Net.Http.Api.Service.ISessionStoreContext`2.IsTokenStaleAsync*> asks whether the data captured in the token has changed since it was created, typically by comparing a security stamp.

If the token is stale, <xref:Singulink.Net.Http.Api.Service.ISessionStoreContext`2.CreateTokenAsync*> rebuilds it from current data right away, using the record's existing refresh info, so the endpoint never runs on a token the service already knows is out of date. The endpoint then runs with the current token.

The refresh itself is deferred until the response starts: the record's refresh time, validity, device and IP address are updated, its generation is incremented, and <xref:Singulink.Net.Http.Api.Service.ISessionStoreContext`2.CreateTokenAsync*> applies the new refresh info to the (now current) token. The new cookie goes out with the response. Deferring the store write and the cookie avoids rotating the session for a request that then fails, and keeps the endpoint on one consistent token for its whole duration. If an endpoint sets or clears the token itself (for example sign-out), the deferred refresh is skipped.

Expiry is sliding. Each refresh restarts the validity period from that moment, so a session that is used at least once per validity period never expires, while an abandoned one expires after <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.TempSessionExpiry> or <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.PersistentSessionExpiry>.

## Generations and Theft Detection

Every refresh increments the record's generation, and the new token carries the new value. A token whose generation no longer matches the record is therefore one that was issued before a later refresh. On the next validation such a token is rejected and the session is invalidated, which is what limits the damage from a stolen cookie: once the legitimate client refreshes, the copy stops working, and if the thief refreshes first, the legitimate client's next validation ends the session for both.

Rotation is only meaningful because refreshes are infrequent; forced validation (below) deliberately does not rotate so that security-sensitive endpoints can be called freely.

### The grace period

Rotation would break legitimate clients if it were strict. Two situations produce a valid client holding the previous generation:

- **Concurrent requests.** Two requests are sent with the same token; the first response rotates the session, and the second was already in flight with the old token.
- **Reconnecting connections.** A long-lived connection such as a SignalR hub was established with a token that an HTTP request has since refreshed. The toolkit's client presents the current token when reconnecting, but a request can still race a refresh.

A token exactly one generation behind is accepted for <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.MultipleRefreshGracePeriod> after the rotation, provided it comes from the same device (as described by the User-Agent header). The IP address is deliberately not compared, since mobile devices change networks constantly. Within the grace window such a request is re-issued the current generation without another rotation. A token two or more generations behind, or from a different device, is never accepted.

> [!NOTE]
> The refresh interval should comfortably exceed the grace period. With a ten minute <xref:Singulink.Net.Http.Api.Service.ISessionToken.RefreshAfter> and a two minute grace period, a legitimate client can only fall behind by one generation, and a stolen token is detected within one refresh interval.

## Forced Validation

Endpoints declared with <xref:Singulink.Net.Http.Api.Service.SessionAccessOptions.ForceValidate> perform the liveness and staleness checks even when no refresh is due; everything else is the same as a periodic refresh. A stale token is rebuilt before the endpoint runs, and the rebuilt token is re-issued to the client, but no rotation occurs and nothing is written to the store unless a periodic refresh also happens to be due, so losing the response is harmless.

This is the tool for operations where acting on stale information is unacceptable, such as permanent deletions after an access change. Because it never rotates, it can be applied to as many endpoints as needed without affecting concurrency.

## What Changes Are Reflected When

The model implies a bounded staleness window that is worth being explicit about:

- **Session ended elsewhere** (sign out from another device, administrative invalidation): noticed at the next periodic refresh or forced validation, since only the liveness check sees the store.
- **Captured data changed** (roles, memberships, anything in the token): the token is rebuilt before the endpoint runs on the next periodic refresh or forced validation, whichever comes first.
- **Data the token does not capture**: always current, because the endpoint queries it.

Design tokens accordingly. Capture what is needed to authorize the common request cheaply, keep a security stamp that changes whenever any of it changes, and force validation on the few endpoints that must see changes instantly.

## Further Reading

These articles cover the related topics in more depth:

- [Session Handling](../guides/session-handling.md) - Implementing the token, record and store.
- [Building API Clients](../guides/api-clients.md) - How the client stores the token and sends the precondition.

</div>
