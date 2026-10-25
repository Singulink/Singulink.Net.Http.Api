<div class="article">

# Cloudflare

This guide covers running a service behind Cloudflare with the `Singulink.Net.Http.Api.Service.Cloudflare` package: restoring the client's address and scheme, trusting Cloudflare's headers, and keeping Cloudflare's IP ranges up to date. It ends with the limits of Cloudflare's proxy that a service behind it meets.

### Why a service needs it

Behind Cloudflare, connections come from Cloudflare's edge servers rather than from clients. Without help, every request appears to come from one of a handful of Cloudflare addresses, which breaks anything that depends on the client's address, such as [rate limiting](rate-limiting.md), logging and session records. Cloudflare reports the client's real address in the `CF-Connecting-IP` header, but that header can only be trusted on connections that really come from Cloudflare: anyone who reaches the service directly can send it.

The package trusts Cloudflare's headers only on connections from Cloudflare's published IP ranges, so a service can be reached through Cloudflare and directly: each request gets the client address that Cloudflare forwarded, or the address of its own connection.

## Setting Up

Register the services and add the middleware first in the pipeline (see [Pipeline order](getting-started.md#pipeline-order)):

```csharp
var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;

services.AddCloudflareForwarding(options => {
    // Required: Cloudflare's IP ranges are saved here for when they cannot be downloaded at startup. Relative paths start
    // at the content root, so this file is in a "data" folder next to the app's folder and survives redeployments.
    options.SavedRangesPath = "../data/cloudflare-ip-ranges.txt";
});

var app = builder.Build();

// Must run before anything that uses the client's address or the request scheme.
app.UseCloudflareForwarding();

app.UseApiResponseHandling();
app.UseHttpsRedirection();
app.UseAllowedOrigins();
app.UseRateLimiter();
```

<xref:Singulink.Net.Http.Api.Service.CloudflareServiceCollectionExtensions.AddCloudflareForwarding*> registers the options and a hosted service that keeps Cloudflare's IP ranges up to date. <xref:Singulink.Net.Http.Api.Service.CloudflareWebApplicationExtensions.UseCloudflareForwarding*> adds the middleware.

<xref:Singulink.Net.Http.Api.Service.CloudflareForwardingOptions.SavedRangesPath> is required: the app fails to start with an <xref:Microsoft.Extensions.Options.OptionsValidationException> if it is not set. See [Saving the ranges](#saving-the-ranges) for where to put the file.

> [!IMPORTANT]
> Call <xref:Singulink.Net.Http.Api.Service.CloudflareWebApplicationExtensions.UseCloudflareForwarding*> before any other middleware. Middleware that runs earlier, such as HTTPS redirection, rate limiting or request logging, sees Cloudflare's address and scheme instead of the client's. [Pipeline order](getting-started.md#pipeline-order) lists where the other middleware goes.

## What the Middleware Does

Each request is handled according to where its connection comes from. IPv4-mapped IPv6 addresses are treated as the IPv4 address they map, and so are IPv4-mapped networks in <xref:Singulink.Net.Http.Api.Service.CloudflareForwardingOptions.TrustedProxyNetworks>, such as `::ffff:10.0.0.0/104`.

#### Connections from Cloudflare

When the connection's address is in one of Cloudflare's IP ranges:

- <xref:Microsoft.AspNetCore.Http.ConnectionInfo.RemoteIpAddress?displayProperty=nameWithType> is set to the address in the `CF-Connecting-IP` header.
- <xref:Microsoft.AspNetCore.Http.HttpRequest.Scheme?displayProperty=nameWithType> is set from the `X-Forwarded-Proto` header, so a request that reached Cloudflare over HTTPS is seen as an HTTPS request even if Cloudflare connects to the service over HTTP.
- Cloudflare's headers are kept.

A missing or invalid header leaves the address or scheme unchanged. Only `http` and `https` are accepted as schemes.

#### Direct connections

All other connections are direct: their address and scheme are left unchanged, and every request header whose name starts with `CF-` is removed. As a result, a Cloudflare header that is present can be trusted. For example, Cloudflare adds a `CF-Ray` header to every request it forwards, so checking for it tells you whether a request came through Cloudflare:

```csharp
bool throughCloudflare = context.Request.Headers.ContainsKey("CF-Ray");
```

#### Visitor location

<xref:Singulink.Net.Http.Api.Service.CloudflareHttpContextExtensions.GetClientLocation*> returns the visitor location that Cloudflare sends with a request, as Cloudflare provides it: the country code, which Cloudflare sends while IP geolocation is turned on for the zone, and the continent, region, city, postal code, metro code, latitude, longitude and time zone, which it sends while its "Add visitor location headers" managed transform is turned on:

```csharp
if (context.GetClientLocation() is { } location)
    logger.LogInformation("Request from {City}, {Region}, {Country}.", location.City, location.Region, location.CountryCode);
```

It returns `null` unless the middleware verified that the request came through Cloudflare, so direct connections, and every request when the middleware is not in the pipeline (such as in development), have no location. It also returns `null` when Cloudflare sent no location headers.

<xref:Singulink.Net.Http.Api.Service.CloudflareClientLocation.ToString*?displayProperty=nameWithType> gives the city, the region and the country code as text, such as `Toronto, Ontario, CA`, without the ones that Cloudflare did not send and without the country code `XX`, which Cloudflare sends for an unknown country. It is empty if none of them is known:

```csharp
string? location = context.GetClientLocation()?.ToString();
```

#### Cloudflare Tunnel

With Cloudflare Tunnel, requests reach the service through the `cloudflared` process rather than from Cloudflare's IP ranges. Its connections come from its own address, such as a loopback address when it runs on the same machine, and are direct connections by default: the visitor's address is lost and Cloudflare's headers are removed. Every visitor then shares the loopback address, so per-client rate limits apply to all of them together, since loopback addresses are not exempt from rate limits by default (see [Exempting Clients](rate-limiting.md#exempting-clients)).

Add the addresses or networks that `cloudflared` connects from to <xref:Singulink.Net.Http.Api.Service.CloudflareForwardingOptions.TrustedProxyNetworks> to handle its connections like connections from Cloudflare:

```csharp
services.AddCloudflareForwarding(options => {
    options.SavedRangesPath = "../data/cloudflare-ip-ranges.txt";
    options.TrustedProxyNetworks.Add("127.0.0.1");
    options.TrustedProxyNetworks.Add("::1");
});
```

Any client that connects from those addresses can then send requests with a client address and Cloudflare headers of its choosing. A direct connection with a `CF-Connecting-IP` header that has another address than the connection's is logged as a warning, once an hour for each connection address and for up to 100 addresses an hour: it comes from a proxy that is not trusted, or from a client that sent the header itself.

## Cloudflare's IP Ranges

Cloudflare publishes its IP ranges at `https://www.cloudflare.com/ips-v4/` and `https://www.cloudflare.com/ips-v6/`. The middleware needs them to tell requests through Cloudflare from direct connections, so the package downloads them, keeps a saved copy at <xref:Singulink.Net.Http.Api.Service.CloudflareForwardingOptions.SavedRangesPath> and refreshes them periodically.

#### At startup

The ranges are downloaded at startup, or read from the saved copy if [downloads are turned off](#turning-off-downloads). In apps built with `WebApplication`, the service only starts accepting requests once this is done, so the first requests are already handled correctly. Startup never fails because of the ranges and never waits longer than <xref:Singulink.Net.Http.Api.Service.CloudflareForwardingOptions.DownloadTimeout> for a download, which keeps it well within limits such as the 120 second startup limit of IIS's ASP.NET Core Module:

1. The ranges are downloaded and saved to <xref:Singulink.Net.Http.Api.Service.CloudflareForwardingOptions.SavedRangesPath>.
2. If the download fails, the saved copy is used and a warning with the copy's date is logged.
3. If there is no saved copy, for example on the first start on a new server, or it cannot be read, the service starts anyway and refuses every request until a download succeeds (see below). The download is retried every <xref:Singulink.Net.Http.Api.Service.CloudflareForwardingOptions.InitialRetryInterval>.

#### Requests before the ranges are available

Without the ranges, requests through Cloudflare cannot be told apart from direct connections. Treating them all as direct connections would make every client appear to come from a few Cloudflare servers: unrelated users would share per-client rate limits, and anything else that relies on the client's address would misbehave. So until the ranges are available, the middleware answers every request with a 503 response in the toolkit's [error format](../concepts/error-format.md), with the error code `cloudflare-ranges-unavailable` (<xref:Singulink.Net.Http.Api.ApiErrorCodes.CloudflareRangesUnavailable>) and a `Retry-After` header set to <xref:Singulink.Net.Http.Api.Service.CloudflareForwardingOptions.InitialRetryInterval> in whole seconds:

```text
HTTP/1.1 503 Service Unavailable
Content-Type: text/plain; charset=utf-8; format=error-code
Retry-After: 10
Cache-Control: no-store

[cloudflare-ranges-unavailable] The service is starting. Please try again shortly.
```

The toolkit's client throws a <xref:Singulink.Net.Http.Api.ServiceUnavailableApiException> for it, with the message and error code from the response. As soon as the ranges are available, requests are handled normally. New servers and containers with a seeded saved copy (see below) start with ranges.

#### Refreshing

After startup the ranges are downloaded again every <xref:Singulink.Net.Http.Api.Service.CloudflareForwardingOptions.RefreshInterval>. The new ranges replace the old ones for all requests at once and are saved. When a download fails, the current ranges stay in use and the download is retried after <xref:Singulink.Net.Http.Api.Service.CloudflareForwardingOptions.RetryInterval>. A response that is not a valid list of ranges (such as an error page) counts as a failed download.

Each server keeps its own ranges by design: it downloads, saves and refreshes them itself and only trusts the ranges it has loaded, so services that run on several servers share nothing for them (see [Running Multiple Servers](multiple-servers.md)).

#### Saving the ranges

<xref:Singulink.Net.Http.Api.Service.CloudflareForwardingOptions.SavedRangesPath> is required. A file outside the folder that a deployment replaces survives restarts and redeployments, and the app needs permission to write it. Relative paths are resolved against the app's content root, so `../data/cloudflare-ip-ranges.txt` puts the file in a `data` folder next to the app's folder.

Every successful download replaces the file in a single step: neither a failed write nor a power loss leaves a partly written copy behind, and other processes or servers that read the file never see one. If the file cannot be written, for example on a read-only deployment, a warning is logged and the downloaded ranges are used from memory.

#### Seeding the saved copy

A copy at the saved path, such as one baked into a container image or mounted, lets containers, new servers and read-only deployments serve requests even if Cloudflare's lists cannot be downloaded when they first start. If the location is read-only, the service runs on the seeded copy and keeps the ranges it downloads later in memory, logging a warning each time it cannot save them. To run on the seeded copy alone, [turn off downloads](#turning-off-downloads).

The file has one range per line in CIDR notation, like Cloudflare's IPv4 and IPv6 lists concatenated, and must end with a line that contains only `# end`. The end line shows that the file is complete: a file that is read while it is still being written has no end line yet, so it is not used. Blank lines and other lines that start with `#` are ignored. Any other line that is not a valid range makes the whole file unusable, and the service then behaves as if there were no saved copy.

To create or update the file with `curl` (Cloudflare's lists do not end with a line break, hence the `echo` commands). It writes a new file and only moves it into place if both downloads succeed, so a failure leaves an existing file as it was:

```sh
{ curl -fsS https://www.cloudflare.com/ips-v4/ && echo && curl -fsS https://www.cloudflare.com/ips-v6/ && echo && echo '# end'; } \
    > cloudflare-ip-ranges.txt.new && mv cloudflare-ip-ranges.txt.new cloudflare-ip-ranges.txt
```

Or with PowerShell, which writes nothing if either download fails:

```powershell
(Invoke-RestMethod https://www.cloudflare.com/ips-v4/), (Invoke-RestMethod https://www.cloudflare.com/ips-v6/), '# end' |
    Set-Content cloudflare-ip-ranges.txt
```

The date of a saved copy, which warnings report, is the file's last write time.

#### Turning off downloads

With <xref:Singulink.Net.Http.Api.Service.CloudflareForwardingOptions.DownloadRanges> set to `false`, the service reads the ranges from a file that is provided to it instead of downloading them, and makes no outbound requests for them:

```csharp
services.AddCloudflareForwarding(options => {
    options.SavedRangesPath = "cloudflare-ip-ranges.txt";
    options.DownloadRanges = false;

    // Changes to the file are picked up within 5 minutes.
    options.RefreshInterval = TimeSpan.FromMinutes(5);
});
```

The ranges are then read from <xref:Singulink.Net.Http.Api.Service.CloudflareForwardingOptions.SavedRangesPath> at startup and again every <xref:Singulink.Net.Http.Api.Service.CloudflareForwardingOptions.RefreshInterval>, and are never downloaded or saved. When the file changes, the new ranges replace the old ones for all requests at once. Reading the small file is cheap, so the interval can be much shorter than the default of a day in this mode.

If the file cannot be read, for example while a network share is unavailable, or it is not a complete list, the ranges in use are kept, a warning is logged and the file is read again after <xref:Singulink.Net.Http.Api.Service.CloudflareForwardingOptions.RetryInterval>. Without any ranges, for example when the file is missing at startup, the service refuses every request as described in [Requests before the ranges are available](#requests-before-the-ranges-are-available) and reads the file again every <xref:Singulink.Net.Http.Api.Service.CloudflareForwardingOptions.InitialRetryInterval>. <xref:Singulink.Net.Http.Api.Service.CloudflareForwardingOptions.DownloadTimeout> and <xref:Singulink.Net.Http.Api.Service.CloudflareForwardingOptions.StaleWarningAge> have no effect, so no out of date warnings are logged.

The file is kept current outside the service, for example by recreating it with the commands above whenever the container image is built, or by one server that downloads the ranges with downloads turned on and saves them to a shared location that the other servers read.

#### Logging

The package logs these events:

| Event | Level | Details |
| --- | --- | --- |
| A download fails | Warning | Logged on every failed attempt, with the exception and when the next attempt is. While no ranges are available, the message also says that requests are refused. |
| The saved copy cannot be read with downloads turned off | Warning | Logged on every failed attempt, with the exception and when the next attempt is. The ranges in use are kept. While there are none, the message also says that requests are refused. |
| The saved copy is used at startup | Warning | Includes the number of ranges, the file and the date of the copy. |
| There is no saved copy at startup | Warning | Requests are refused until a download succeeds. |
| The saved copy is put in use with downloads turned off | Information | Logged at startup and whenever the file changes, with the number of ranges, the file and the date of the copy. |
| The saved copy cannot be read or is not a valid list when a download at startup fails | Error | Treated as if there were no saved copy. |
| The ranges in use are out of date | Warning | Logged with every failed download once the ranges are older than <xref:Singulink.Net.Http.Api.Service.CloudflareForwardingOptions.StaleWarningAge>, with their date. Requests through Cloudflare servers in newer ranges are treated as direct connections. |
| The saved copy cannot be written | Warning | The downloaded ranges are still used. |
| A direct connection forwards another address in `CF-Connecting-IP` | Warning | Logged once an hour for each connection address, for up to 100 addresses an hour, with both addresses. The connection is not from Cloudflare's ranges or a trusted proxy network. |
| A download succeeds | Information | Includes the number of ranges. |

Cloudflare rarely changes its ranges, so a few failed downloads are harmless. Ranges that stay out of date usually mean that the service cannot reach `www.cloudflare.com`, for example because of an outbound firewall or proxy.

#### Options

<xref:Singulink.Net.Http.Api.Service.CloudflareForwardingOptions> has these settings:

| Option | Default | Purpose |
| --- | --- | --- |
| <xref:Singulink.Net.Http.Api.Service.CloudflareForwardingOptions.SavedRangesPath> | none | File the ranges are saved to after each download and read from when a download at startup fails. With downloads turned off, the only source of the ranges. Required. |
| <xref:Singulink.Net.Http.Api.Service.CloudflareForwardingOptions.DownloadRanges> | `true` | Whether the ranges are downloaded. When `false`, they are read from the saved copy instead. |
| <xref:Singulink.Net.Http.Api.Service.CloudflareForwardingOptions.TrustedProxyNetworks> | none | Addresses and networks whose connections are handled like connections from Cloudflare (see [Cloudflare Tunnel](#cloudflare-tunnel)). |
| <xref:Singulink.Net.Http.Api.Service.CloudflareForwardingOptions.RefreshInterval> | 1 day | How often the ranges are downloaded again, or read again from the saved copy when downloads are turned off. |
| <xref:Singulink.Net.Http.Api.Service.CloudflareForwardingOptions.RetryInterval> | 5 minutes | How long to wait after a failed download, or a failed read with downloads turned off, before trying again (no longer than the refresh interval). |
| <xref:Singulink.Net.Http.Api.Service.CloudflareForwardingOptions.InitialRetryInterval> | 10 seconds | How long to wait between attempts while no ranges are available and requests are refused. Also sent to clients in the `Retry-After` header. |
| <xref:Singulink.Net.Http.Api.Service.CloudflareForwardingOptions.DownloadTimeout> | 10 seconds | How long a download may take, which also bounds how long startup waits. |
| <xref:Singulink.Net.Http.Api.Service.CloudflareForwardingOptions.StaleWarningAge> | 7 days | How old the ranges in use can get before failed downloads also log that they are out of date. Not used with downloads turned off. |

The options can also be bound from configuration, including the required path, with `services.Configure<CloudflareForwardingOptions>(configuration.GetSection("Cloudflare"))`.

#### Customizing downloads

Downloads use the <xref:System.Net.Http.HttpClient> named <xref:Singulink.Net.Http.Api.Service.CloudflareForwardingOptions.HttpClientName>, so it can be configured through <xref:System.Net.Http.IHttpClientFactory>, for example to use an outbound proxy:

```csharp
services.AddHttpClient(CloudflareForwardingOptions.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { Proxy = new WebProxy("http://proxy.internal:3128") });
```

### Things to know

- ASP.NET Core's forwarded headers middleware, applied to Cloudflare's connections as well, applies the client's address a second time.
- Connections through Cloudflare Tunnel come from the `cloudflared` process rather than from Cloudflare's IP ranges, so they are direct connections unless its address is in <xref:Singulink.Net.Http.Api.Service.CloudflareForwardingOptions.TrustedProxyNetworks> (see [Cloudflare Tunnel](#cloudflare-tunnel)).
- The 503 response that refuses requests while no ranges are available is written before any middleware that comes after this one, such as CORS, so it has no CORS headers: browser pages on other origins cannot read it and see a network error.
- Health checks also receive the 503 response while no ranges are available, so load balancers and orchestrators see the service as not ready yet.

## Limits of Cloudflare's Proxy

Requests through Cloudflare's proxy meet limits of Cloudflare's own, which apply to the toolkit's requests as to any others.

#### Request bodies

Cloudflare answers a request whose body is larger than its plan allows with a `413` error page of its own ([limits](https://developers.cloudflare.com/workers/platform/limits/)): 100 MB on the Free and Pro plans, 200 MB on the Business plan, and up to 5 GB on the Enterprise plan, as set on the zone. The toolkit's client throws a <xref:Singulink.Net.Http.Api.ContentTooLargeApiException> for it, with the message "The service returned an error (413 Content Too Large).". [Chunked uploads](uploads.md) send files in requests of at most 64 MiB, about 67.1 MB.

#### Slow responses

Cloudflare's Proxy Read Timeout, 125 seconds, is the longest time between two reads of a response from the service ([connection limits](https://developers.cloudflare.com/fundamentals/reference/connection-limits/)), and only Enterprise zones can raise it, up to 6,000 seconds. A response that has not started 125 seconds after the request arrived ends with a `524`, and so does a streaming response that sends nothing for 125 seconds, which reaches the client as a truncated stream: an <xref:System.Net.Http.HttpIOException>. A response that sends something more often never times out, however long it runs, such as a streaming response with a <xref:Singulink.Net.Http.Api.Service.KeepAlivePingAttribute> of a shorter interval, or the completion of a chunked upload, which pings every 10 seconds by default. Cloudflare does not compress `application/jsonl` responses by default ([compression](https://developers.cloudflare.com/speed/optimization/content/compression/)), so their records reach the client as the service writes them.

The Proxy Write Timeout, 30 seconds for sending data to the service, cannot be changed. Both timeouts end the request with a `524`.

<xref:Singulink.Net.Http.Api.Client.ApiFailures.IsTransient*?displayProperty=nameWithType> counts a `520` to `524` without an error code as transient, so a client that makes a request again after one of them can start a long operation a second time while the first still runs on the service.

#### WebSockets

WebSockets work through Cloudflare on all plans ([WebSockets](https://developers.cloudflare.com/network/websockets/)). Cloudflare closes idle WebSocket connections, and restarts its servers when it releases new code, which drops connections. SignalR's keep-alive messages, every 15 seconds by default, keep a hub connection busy, and a connection whose <xref:Singulink.Net.Http.Api.Client.ApiHubConnection.StayConnected?displayProperty=nameWithType> is set reconnects after a drop. SignalR's long polling, with polls of 90 seconds, stays within the read timeout.

#### Caching

Cloudflare does not cache JSON or HTML responses, but by default it caches responses to `GET` requests whose path ends in a static file extension, such as `.pdf`, `.jpg`, `.zip`, `.mp4` or `.docx` ([default cache behavior](https://developers.cloudflare.com/cache/concepts/default-cache-behavior/)). It does not cache a response with a `Set-Cookie` header, or with `Cache-Control: private`, `no-store`, `no-cache` or `max-age=0`, so an API route with a file name at the end of its path is cached unless its response has one of them.

#### Bot protection

Bot Fight Mode can challenge requests of API clients and mobile apps, which clients other than browsers cannot answer, and WAF skip rules do not exempt requests from it: IP Access rules do ([Bot Fight Mode](https://developers.cloudflare.com/bots/get-started/bot-fight-mode/)). Super Bot Fight Mode can be skipped for requests with WAF skip rules.

## Further Reading

Continue with these related articles:

- [Rate Limiting](rate-limiting.md) - Per-client limits, which rely on the client's real address.
- [Error Response Format](../concepts/error-format.md) - How errors, including the 503 response, are encoded on the wire.
- [Architecture and Conventions](../concepts/architecture.md) - What each package contains.

</div>
