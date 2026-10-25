<div class="article">

# Rate Limiting

This guide covers limiting how often each client can call a service: adding per-client limits to endpoints or the whole service, exempting clients by address, and reporting rejected requests in the toolkit's error format.

### How it works

The service package adds helpers on top of ASP.NET Core rate limiting (`Microsoft.AspNetCore.RateLimiting`). Each limit applies to every client address separately, so one client that makes too many requests does not affect others. Clients can be exempted by address (see [Exempting Clients](#exempting-clients)). Rejected requests receive a `429 Too Many Requests` error that clients built with the toolkit report like any other API error.

Requests are counted in the registered short-lived store, which is the same store the sign-in features use, or in one memory store that all the limits share if the service registers none. With a store that all of a service's servers share, a client's limit holds across all of them (see [Several Servers](#several-servers)).

> [!IMPORTANT]
> The client address is <xref:Microsoft.AspNetCore.Http.ConnectionInfo.RemoteIpAddress?displayProperty=nameWithType>, the address the connection comes from. Behind a proxy, such as a reverse proxy, a load balancer or Cloudflare, every request comes from the proxy, so all clients share one limit and quickly lock each other out. The forwarded client address must be applied before rate limiting runs: behind Cloudflare with the `Singulink.Net.Http.Api.Service.Cloudflare` package (see [Cloudflare](cloudflare.md)), and behind other proxies with ASP.NET Core's forwarded headers middleware, trusting only the proxies' addresses:
>
> ```csharp
> services.Configure<ForwardedHeadersOptions>(options => {
>     options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
>     options.KnownIPNetworks.Add(IPNetwork.Parse("10.0.0.0/24")); // The proxies' network.
> });
>
> var app = builder.Build();
>
> app.UseForwardedHeaders(); // Before rate limiting and anything else that uses the client's address.
> app.UseApiResponseHandling();
> app.UseAllowedOrigins();
> app.UseRateLimiter();
> ```
>
> A request from a private or loopback address with an `X-Forwarded-For`, `Forwarded`, `X-Real-IP` or `CF-Connecting-IP` header shows that a forwarded address was not applied, so the first one is logged as a warning, once for each limit.

## Adding Limits

Add policies with <xref:Singulink.Net.Http.Api.Service.RateLimiterOptionsExtensions.AddClientFixedWindowLimiter*> and apply them to endpoints with `RequireRateLimiting`. Each policy allows each client a number of requests per window, which one overload takes as arguments and another sets with a callback that can set the other options too:

```csharp
services.AddRateLimiter(options => options
    .AddClientFixedWindowLimiter("sign-in", permitLimit: 10, TimeSpan.FromMinutes(1))
    .AddClientFixedWindowLimiter("setup-emails", o => {
        o.PermitLimit = 20;
        o.Window = TimeSpan.FromHours(1);
        o.RejectionMessage = "Too many emails were sent. Please try again in an hour.";
    })
    .SetApiRejectionResponse("Too many attempts. Please wait a few minutes and try again."));

var app = builder.Build();

app.UseCloudflareForwarding(); // Only for services behind Cloudflare.
app.UseApiResponseHandling();
app.UseAllowedOrigins();
app.UseRateLimiter();

app.MapPost("/sessions", SignInAsync).RequireRateLimiting("sign-in");
app.MapPost("/setup/registration", RequestRegistrationAsync).RequireRateLimiting("setup-emails");
```

The rate limiter comes after the middleware that applies the client's address and after <xref:Singulink.Net.Http.Api.Service.WebApplicationExtensions.UseAllowedOrigins*>, which lets browser pages on other origins read its rejections (see [Pipeline order](getting-started.md#pipeline-order)). Endpoint policies need routing to run before the rate limiter. `WebApplication` adds routing at the start of the pipeline automatically; if you call `UseRouting` yourself, call `UseRateLimiter` after it.

#### Limiting the whole service

<xref:Singulink.Net.Http.Api.Service.RateLimiterOptionsExtensions.SetClientFixedWindowGlobalLimiter*> sets a limit that applies to every request, with the same two kinds of overloads. Endpoint policies apply in addition to it:

```csharp
services.AddRateLimiter(options => options.SetClientFixedWindowGlobalLimiter(permitLimit: 600, TimeSpan.FromMinutes(1)));
```

#### Options

<xref:Singulink.Net.Http.Api.Service.ClientRateLimitOptions> has these settings:

| Option | Default | Purpose |
| --- | --- | --- |
| <xref:Singulink.Net.Http.Api.Service.ClientRateLimitOptions.PermitLimit> | none | Requests each client can make per window. Must be set. |
| <xref:Singulink.Net.Http.Api.Service.ClientRateLimitOptions.Window> | 1 minute | Length of the window. |
| <xref:Singulink.Net.Http.Api.Service.ClientRateLimitOptions.QueueLimit> | 0 | Requests over the limit that wait for the next window instead of being rejected, on each server. Waiting requests try again in no particular order when each window starts. |
| <xref:Singulink.Net.Http.Api.Service.ClientRateLimitOptions.RejectionMessage> | none | The message of the error for requests that the limit rejects, instead of the rejection response's (see [Rejection Responses](#rejection-responses)). |
| <xref:Singulink.Net.Http.Api.Service.ClientRateLimitOptions.Enabled> | `true` | Whether requests are limited. When `false`, no request is limited. |
| <xref:Singulink.Net.Http.Api.Service.ClientRateLimitOptions.ExemptPrivateNetworks> | `false` | Whether private addresses and connections without an address are exempt (see below). |
| <xref:Singulink.Net.Http.Api.Service.ClientRateLimitOptions.ExemptNetworks> | empty | Additional addresses and networks that are exempt. |

#### How clients are identified

IPv4-mapped IPv6 addresses are treated as the IPv4 address they map. IPv6 clients are limited per /64 network rather than per address, since a single client usually controls a whole /64 and could otherwise use a new address for every request.

<xref:Singulink.Net.Http.Api.Service.ClientRateLimitOptions.GetClientKey*> returns the key that the limits count a client address under: the IPv4 address, the network address of the IPv6 address's /64, or `0.0.0.0` for a connection without a remote IP address. It does not apply exemptions.

## Exempting Clients

No client is exempt by default. With <xref:Singulink.Net.Http.Api.Service.ClientRateLimitOptions.ExemptPrivateNetworks> set to `true`, these clients are never limited:

- Private IPv4 addresses (`10.0.0.0/8`, `172.16.0.0/12` and `192.168.0.0/16`).
- Loopback addresses (`127.0.0.0/8` and `::1`).
- Link-local addresses (`169.254.0.0/16` and `fe80::/10`).
- The shared address space used by carrier-grade NAT (`100.64.0.0/10`).
- Unique local IPv6 addresses (`fc00::/7`).
- Connections without a remote IP address, such as Unix domain sockets.

```csharp
options.SetClientFixedWindowGlobalLimiter(o => {
    o.PermitLimit = 600;
    o.ExemptPrivateNetworks = true;
});
```

Behind a proxy whose forwarded address is not applied, every request comes from the proxy's address, which is usually private, so every request is then exempt.

Add other clients to <xref:Singulink.Net.Http.Api.Service.ClientRateLimitOptions.ExemptNetworks>, as IP addresses or networks in CIDR notation. A typical case is an office's public IP address, used by internal services that reach the service through Cloudflare:

```csharp
options.AddClientFixedWindowLimiter("sign-in", o => {
    o.PermitLimit = 10;
    o.ExemptNetworks.Add("203.0.113.7");
    o.ExemptNetworks.Add("2001:db8:1234::/48");
});
```

An invalid entry throws a <xref:System.FormatException> when the limit is added.

#### Settings from configuration

<xref:Singulink.Net.Http.Api.Service.ClientRateLimitOptions> can be bound from a configuration section, which keeps addresses out of code and lets a deployment turn limits off. For example, with this in `appsettings.json`:

```json
{
  "RateLimits": {
    "ExemptNetworks": [ "203.0.113.7/32", "2001:db8:1234::/48" ]
  }
}
```

bind the section for all the limits with <xref:Singulink.Net.Http.Api.Service.RateLimiterOptionsExtensions.ConfigureClientLimits*>, then add the limits with the values that differ per limit:

```csharp
var limits = builder.Configuration.GetSection("RateLimits");

services.AddRateLimiter(options => options
    .ConfigureClientLimits(o => limits.Bind(o))
    .AddClientFixedWindowLimiter("sign-in", permitLimit: 10, TimeSpan.FromMinutes(1))
    .AddClientFixedWindowLimiter("codes", permitLimit: 10, TimeSpan.FromMinutes(1))
    .AddClientFixedWindowLimiter("setup-emails", permitLimit: 20, TimeSpan.FromHours(1))
    .SetApiRejectionResponse("Too many attempts. Please wait a few minutes and try again."));
```

<xref:Singulink.Net.Http.Api.Service.RateLimiterOptionsExtensions.ConfigureClientLimits*> applies to the limits added after it, before their own values, so a limit's own values take precedence over configuration. To turn the limits off for automated tests that make many requests from one address, set `RateLimits:Enabled` to `false`, for example with the environment variable `RateLimits__Enabled=false`.

## Rejection Responses

Rejected requests receive a 429 response in the toolkit's [error format](../concepts/error-format.md), with the error code <xref:Singulink.Net.Http.Api.ApiErrorCodes.RateLimited> (`rate-limited`) and a `Retry-After` header giving the number of seconds the client should wait before trying again:

```text
HTTP/1.1 429 Too Many Requests
Content-Type: text/plain; charset=utf-8; format=error-code
Retry-After: 60

[rate-limited] Too many attempts. Please wait a few minutes and try again.
```

The `Retry-After` value counts down to the start of the next window. <xref:Singulink.Net.Http.Api.Service.RateLimiterOptionsExtensions.AddClientFixedWindowLimiter*> and <xref:Singulink.Net.Http.Api.Service.RateLimiterOptionsExtensions.SetClientFixedWindowGlobalLimiter*> set this response for all of the service's limits, unless <xref:Microsoft.AspNetCore.RateLimiting.RateLimiterOptions.OnRejected?displayProperty=nameWithType> is already set; ASP.NET Core would otherwise reject requests with an empty `503 Service Unavailable` response. <xref:Singulink.Net.Http.Api.Service.RateLimiterOptionsExtensions.SetApiRejectionResponse*> sets it with a message of your own, before or after the limits are added, and a handler of your own set with `OnRejected` replaces it. A request that a limit with a <xref:Singulink.Net.Http.Api.Service.ClientRateLimitOptions.RejectionMessage?displayProperty=nameWithType> rejects receives that message instead. Another overload takes a function that returns the message for each rejected request, given a <xref:Singulink.Net.Http.Api.Service.RateLimitRejectionContext> with the request's <xref:Microsoft.AspNetCore.Http.HttpContext>, the rejection message of the limit, if it has one, and how long the client should wait (<xref:Singulink.Net.Http.Api.Service.RateLimitRejectionContext.RetryAfter>), which the `Retry-After` header has in whole seconds, rounded up:

```csharp
options.SetApiRejectionResponse(rejection =>
    rejection.RejectionMessage ?? GetTooManyRequestsMessage(rejection.HttpContext, rejection.RetryAfter));
```

The toolkit's client throws a <xref:Singulink.Net.Http.Api.TooManyRequestsApiException> for a 429, with the message from the response and the `Retry-After` value in <xref:Singulink.Net.Http.Api.ApiException.RetryAfter>:

```csharp
try
{
    await client.SignInAsync(command);
}
catch (TooManyRequestsApiException ex)
{
    ShowMessage(ex.Message);
}
```

## Other Limits

The per-client limits count requests by client address. <xref:Singulink.Net.Http.Api.Service.ShortLivedStoreExtensions.AcquirePermitAsync*> counts attempts by any key, such as an email address, in the same kind of fixed windows and in the same store, so those limits hold across servers too (see [Counting attempts](sign-in-codes.md#counting-attempts)).

## Several Servers

Services that run on several servers register a short-lived store that all of them share, such as a Redis store (see [Running Multiple Servers](multiple-servers.md)). The limits then count each client's requests in that store, so a client gets the same number of requests per window however the load balancer spreads them. Windows start at fixed times on each server's clock (a one-minute window starts at every full minute), so servers whose clocks differ start them at different times. <xref:Singulink.Net.Http.Api.Service.ClientRateLimitOptions.QueueLimit> applies on each server separately.

With a shared store, combine these policies only with <xref:Singulink.Net.Http.Api.Service.RateLimiterOptionsExtensions.SetClientFixedWindowGlobalLimiter*> or a concurrency limiter as the global limiter. ASP.NET Core would count each request twice against a global fixed window, sliding window or token bucket limiter of its own. The memory store has no such limitation.

Requests that wait in the queue are not served in the order they arrived: when a window starts, each of them tries again and is counted in that window, along with new requests.

## Further Reading

Continue with these related articles:

- [Cloudflare](cloudflare.md) - Restoring the client's address for services behind Cloudflare.
- [Running Multiple Servers](multiple-servers.md) - Sharing rate limit counts between servers.
- [Error Response Format](../concepts/error-format.md) - How errors are encoded on the wire.
- [Rate limiting middleware in ASP.NET Core](https://learn.microsoft.com/aspnet/core/performance/rate-limit) - The underlying framework feature.

</div>
