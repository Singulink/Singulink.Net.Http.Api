<div class="article">

# Health Checks and Metrics

This guide covers what a service built with the toolkit reports about its own state: ASP.NET Core health checks of the toolkit's features, which a status page, a load balancer or an orchestrator reads, and metrics through `System.Diagnostics.Metrics`, which OpenTelemetry, Prometheus, `dotnet-counters` and Application Insights collect.

## Health Checks

The toolkit adds no health check unless the app adds it. Add the checks of every toolkit feature that the app registered with <xref:Singulink.Net.Http.Api.Service.HttpApiHealthChecksBuilderExtensions.AddHttpApiChecks*>, after the features' registration methods, and map them to an endpoint of their own by their tag:

```csharp
services.AddChunkedUploads<SessionToken>();
services.AddCloudflareForwarding(options => ...);

services.AddHealthChecks().AddHttpApiChecks();

// After the app is built:
app.MapHealthChecks("/health/httpapi", new HealthCheckOptions { Predicate = check => check.Tags.Contains("httpapi") });
```

The toolkit's checks report the state of its features for a status page and monitoring. They are not meant as a load balancer's check, unless an app chooses that check by check (see [Behind a load balancer](#behind-a-load-balancer)).

Or add the checks one at a time, each with an optional name, failure status and tags:

| Method | Checks | Default name |
| --- | --- | --- |
| <xref:Singulink.Net.Http.Api.Service.HttpApiHealthChecksBuilderExtensions.AddShortLivedStoreCheck*> | The short-lived store of the app's own values, or of a key of <xref:Singulink.Net.Http.Api.Service.ShortLivedStoreKeys> | `httpapi-short-lived-store`, or `httpapi-short-lived-store-sign-in` and so on |
| <xref:Singulink.Net.Http.Api.Service.HttpApiHealthChecksBuilderExtensions.AddChunkedUploadsCheck*> | Chunked uploads: the upload store, the counts of open uploads, the last cleanup | `httpapi-uploads` |
| <xref:Singulink.Net.Http.Api.Service.HttpApiHealthChecksBuilderExtensions.AddClientRateLimitsCheck*> | The per-client rate limits | `httpapi-rate-limiting` |
| <xref:Singulink.Net.Http.Api.Service.HttpApiHealthChecksBuilderExtensions.AddHubSessionsCheck*> | Hub connections with a session | `httpapi-hub-sessions` |
| <xref:Singulink.Net.Http.Api.Service.CloudflareHealthChecksBuilderExtensions.AddCloudflareForwardingCheck*> | Cloudflare's IP ranges | `httpapi-cloudflare` |

<xref:Singulink.Net.Http.Api.Service.HttpApiHealthChecksBuilderExtensions.AddHttpApiChecks*> adds the store of the app's own values when a short-lived store is registered, the store of `SignIn` with sign-in codes, device links or passkeys, chunked uploads and the store of `Uploads`, hub sessions with session handling, the rate limits with ASP.NET Core's rate limiting (and the store of `RateLimiting` when a short-lived store is registered), and Cloudflare forwarding. Two checks of the same name make the health check service fail when it starts, so a check that the app adds as well needs a name of its own.

Each check is tagged `httpapi` and with its feature, such as `uploads`, unless the app passes tags of its own. Each reports this server's state.

#### Statuses

| Check | Fails (the failure status, `Unhealthy` by default) | `Degraded` |
| --- | --- | --- |
| Short-lived store | The store can't be read (<xref:Singulink.Net.Http.Api.Service.IShortLivedStore.GetUsageAsync*> throws) | The store refused a write because it was full within the last minute, or it is more than 90% full |
| Chunked uploads | The upload store can't be read (<xref:Singulink.Net.Http.Api.Service.Uploads.IUploadStore.GetUsageAsync*> throws), or the total length of the open uploads can't be read from the short-lived store | The last cleanup failed, the open uploads have taken up <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxTotalSize>, or the store has less space available than the open uploads can still take up under <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxTotalSize>, or, without it, than the largest upload that the service accepts |
| Rate limits | Never | The short-lived store couldn't count a request of a limit within the last 5 minutes |
| Hub sessions | Never | A recheck of the sessions in the last round failed, such as when the session store couldn't be reached |
| Cloudflare | No ranges are loaded, so every request is refused | The downloaded ranges are older than <xref:Singulink.Net.Http.Api.Service.CloudflareForwardingOptions.StaleWarningAge> |

A store that refuses writes because it is full is `Degraded` whatever the failure status: it protects itself, the writes it refuses fail and everything else works.

The uploads check is `Degraded` while the open uploads have taken up <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxTotalSize>, when the service refuses new uploads with `503` (`upload-storage-full`) until uploads end. The limits of each user are not part of the rules.

The largest upload that the service accepts is the largest <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadEndpointOptions.MaxSize> of the mapped upload endpoints, with <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxSize> for those without one. The directory store reserves an upload's whole length when it creates it, where the file system can, so it refuses creations (`503`, `upload-storage-full`) once it has less room than an upload's length, and the check is `Degraded` once it has less room than the largest of them. With <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxTotalSize> set above what the store's volume can hold, as a limit of the service rather than of the disk, the check is `Degraded` for as long as the volume has less space available than the open uploads can still take up under it. A store that can't tell its available space has no space rule.

#### Behind a load balancer

ASP.NET Core's health check endpoint answers `503` for `Unhealthy` and `200` for `Degraded` and `Healthy` by default, and a load balancer that reads it takes a server whose check fails out of rotation. A check of something that all servers use, such as a shared upload directory or a Redis store, fails on every server at once while it can't be reached, which would take every server out of rotation. An app that puts one of the toolkit's checks on its load balancer's endpoint chooses it by its own tag, and passes a failure status of `Degraded` for a check that shouldn't take the server out of rotation:

```csharp
// The Cloudflare check fails while no ranges are loaded, when the server refuses every request.
services.AddHealthChecks().AddCloudflareForwardingCheck("ready-cloudflare", tags: ["ready"]);

// The uploads check fails while the shared upload directory can't be read, on every server at once.
services.AddHealthChecks().AddChunkedUploadsCheck("ready-uploads", HealthStatus.Degraded, ["ready"]);

app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
```

A check that fails has the exception in <xref:Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Exception> and its message in the description and under <xref:Singulink.Net.Http.Api.Service.HttpApiHealthCheckData.Error>, so a writer that serializes the report to JSON doesn't need the exception.

#### A status page

The values of each check are in <xref:Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Data>, under the keys of <xref:Singulink.Net.Http.Api.Service.HttpApiHealthCheckData> and <xref:Singulink.Net.Http.Api.Service.CloudflareHealthCheckData>: times are `DateTimeOffset` values in UTC, durations and windows are seconds as `double` values, counts, sizes (in bytes) and limits are `long` values, and a value that a check can't tell is left out. An admin page runs the checks and shows the report:

```csharp
app.MapGet("/admin/status", async (HealthCheckService health) => {
    var report = await health.CheckHealthAsync(check => check.Tags.Contains("httpapi"));

    return report.Entries.Select(e => new {
        e.Key,
        Status = e.Value.Status.ToString(),
        e.Value.Description,
        e.Value.Data,
    });
}).RequireAuthorization("Admin");
```

A run of the checks that <xref:Singulink.Net.Http.Api.Service.HttpApiHealthChecksBuilderExtensions.AddHttpApiChecks*> adds reads each short-lived store's usage once (no round trip for a memory store, about two Redis commands for the Redis store of [Running Multiple Servers](multiple-servers.md#a-redis-store)): the check of a key whose store is the app's store reports `Healthy` with the store's type and sharing mode, and leaves the read to the check of the app's store. It also reads the upload store's usage (the free space of the directory store's volume) and one counter of the short-lived store. Nothing lists uploads or scans keys.

The values of the stores come from <xref:Singulink.Net.Http.Api.Service.IShortLivedStore.GetUsageAsync*> and <xref:Singulink.Net.Http.Api.Service.Uploads.IUploadStore.GetUsageAsync*>, which a store of the app's own implements: a store that can tell nothing returns <xref:Singulink.Net.Http.Api.Service.ShortLivedStoreUsage.Unknown> or <xref:Singulink.Net.Http.Api.Service.Uploads.UploadStoreUsage.Unknown>, and one that can't be read throws, which fails the check.

## Metrics

The toolkit reports to one meter, `Singulink.Net.Http.Api.Service`, through the app's `IMeterFactory`. Counters cost next to nothing while no listener is attached, and the other instruments read fields only when they are collected:

```csharp
builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddMeter("Singulink.Net.Http.Api.Service"));
```

| Instrument | Kind | Unit | Tags |
| --- | --- | --- | --- |
| `httpapi.short_lived_store.entry.count` | Observable up-down counter | `{entry}` | `httpapi.short_lived_store.key` |
| `httpapi.short_lived_store.usage` | Observable up-down counter | `By` | `httpapi.short_lived_store.key` |
| `httpapi.short_lived_store.limit` | Observable up-down counter | `By` | `httpapi.short_lived_store.key` |
| `httpapi.short_lived_store.refused_writes` | Observable counter | `{write}` | `httpapi.short_lived_store.key` |
| `httpapi.upload.running_handlers` | Observable up-down counter | `{upload}` | |
| `httpapi.upload.pending_claim_releases` | Observable up-down counter | `{claim}` | |
| `httpapi.upload.open.count` | Gauge | `{upload}` | |
| `httpapi.upload.open.size` | Gauge | `By` | |
| `httpapi.upload.store.available` | Gauge | `By` | |
| `httpapi.upload.created` | Counter | `{upload}` | `httpapi.upload.kind`: `chunked`, `direct` |
| `httpapi.upload.ended` | Counter | `{upload}` | `httpapi.upload.end`: `succeeded`, `failed`, `cancelled`, `interrupted`, `expired`, `deleted` |
| `httpapi.upload.rejected` | Counter | `{upload}` | `httpapi.upload.rejection`: `uploads_per_user`, `total_size_per_user`, `total_size`, `too_large`, `no_room` |
| `httpapi.upload.cancellations` | Counter | `{request}` | `httpapi.upload.cancellation`: `deleted`, `signalled`, `finished`, `not_found` |
| `httpapi.upload.store.failures` | Counter | `{operation}` | `httpapi.upload.store.operation`, such as `write_chunk` |
| `httpapi.upload.cleanup.duration` | Histogram | `s` | `httpapi.upload.cleanup.result`: `completed`, `failed` |
| `httpapi.rate_limiting.queued_requests` | Observable up-down counter | `{request}` | `httpapi.rate_limiting.policy` |
| `httpapi.rate_limiting.rejections` | Counter | `{request}` | `httpapi.rate_limiting.policy`, `httpapi.rate_limiting.reason`: `limit`, `store_unavailable` |
| `httpapi.session.hub_connections` | Observable up-down counter | `{connection}` | `httpapi.session.token_type` |
| `httpapi.session.hub_connections_ended` | Counter | `{connection}` | `httpapi.session.token_type` |
| `httpapi.cloudflare.range.count` | Gauge | `{range}` | |
| `httpapi.cloudflare.range.age` | Gauge | `s` | |
| `httpapi.cloudflare.refresh.failures` | Counter | `{refresh}` | |
| `httpapi.cloudflare.refused_requests` | Counter | `{request}` | |

The names follow OpenTelemetry's naming conventions: the unit is the instrument's unit, not part of its name. The kinds follow them too: the values of the memory stores are up-down counters, whose sum across stores and servers is meaningful, and the upload and Cloudflare values are gauges, because servers that share an upload store report the same upload values, and every server the same ranges, which a sum across servers would count once per server. `httpapi.short_lived_store.key` is a key of <xref:Singulink.Net.Http.Api.Service.ShortLivedStoreKeys>, or `default` for the store of the app's own values. Only memory stores that the toolkit creates have store instruments: a store of the app's own, such as one over Redis, is reported by its own exporters, and a <xref:Singulink.Net.Http.Api.Service.MemoryShortLivedStore> that the app creates with `new` isn't observed. `httpapi.upload.open.count` and `open.size` are those of the last cleanup, every 5 minutes, and `httpapi.upload.store.available` is reported for the directory store only, and not for a few minutes after a read of it failed. The global limiter has no policy, so its measurements have no `httpapi.rate_limiting.policy` tag, as in ASP.NET Core's rate limiting metrics.

ASP.NET Core's own meters report requests by route and status code, rate limiting's leases and queues, and SignalR's connections, which the toolkit doesn't repeat.

## Rate Limiter Statistics

The per-client limiters answer `RateLimiter.GetStatistics()` without a call to the store: the requests waiting for the next window on this server, the leases granted and refused since the client's limiter was created, and the permits left in the current window as this server last counted them. With a shared store, requests on other servers since then are not included.

## See Also

- [Running Multiple Servers](multiple-servers.md) - The store samples, with their usage.
- [Health checks in ASP.NET Core](https://learn.microsoft.com/aspnet/core/host-and-deploy/health-checks) - The underlying framework feature.
- [ASP.NET Core metrics](https://learn.microsoft.com/aspnet/core/log-mon/metrics/metrics) - Collecting metrics.

</div>
