<div class="article">

# Running Multiple Servers

This guide covers running a service on several servers behind a load balancer, where each request can reach a different server: the session key ring that all servers must share, the short-lived store that keeps sign-in state, rate limit counts and the counts of open uploads, the storage of chunked uploads, and the state that stays on each server.

### What servers share

A service built with the toolkit keeps very little state between requests, and all of it in three places:

- **The Data Protection key ring.** Session tokens are encrypted and signed with ASP.NET Core Data Protection, so every server must use the same keys to read the tokens the others issue.
- **The short-lived stores.** Sign-in codes, device links, passkey ceremonies, rate limit counts and the counts of open chunked uploads live in an <xref:Singulink.Net.Http.Api.Service.IShortLivedStore> between requests, which each feature resolves with a key of its own. With stores that all servers share, any server can finish what another started.
- **The upload store.** Services with [chunked uploads](uploads.md) keep the uploads in a directory or a store of their own. With a directory or store that all servers share, any server can take any request of an upload (see [Uploads](#uploads)).

Sessions themselves live in your session store (see [Session Handling](session-handling.md)), which is usually your database and therefore shared already.

The toolkit relies on the servers' clocks being synchronized: a server judges whether a session cookie is due for validation by the refresh time that another server wrote into it, and the rate limit windows start at the same times on every server. A server whose clock lags behind accepts a cookie that is already due, which can be a stale cookie that another server would reject, for up to the time its clock lags after the cookie became due.

## Sharing the Key Ring

This is standard ASP.NET Core configuration. Give every server the same application name and a key ring in shared storage:

```csharp
builder.Services.AddDataProtection()
    .SetApplicationName("MyService")
    .PersistKeysToFileSystem(new DirectoryInfo(@"\\fileserver\keys\my-service"));
```

`PersistKeysToStackExchangeRedis` (in `Microsoft.AspNetCore.DataProtection.StackExchangeRedis`) and `PersistKeysToDbContext` (in `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore`) keep the keys in Redis or your database instead. `ProtectKeysWithCertificate` and similar methods protect the keys at rest. See [Configure ASP.NET Core Data Protection](https://learn.microsoft.com/aspnet/core/security/data-protection/configuration/overview) for all the options.

Without a shared key ring, a server cannot read the session cookie that another issued, so users appear signed out whenever the load balancer switches servers.

## The Short-Lived Store

The store's types are in the `Singulink.Net.Http.Api.Service` namespace. The default store, <xref:Singulink.Net.Http.Api.Service.MemoryShortLivedStore>, keeps everything in the memory of one server, up to a size you can set (see [The memory store's size](sign-in-codes.md#the-memory-stores-size)). Services that run on several servers register a store backed by storage they all share, such as Redis or a database table. Register it as a singleton, before or after the toolkit's registration methods:

```csharp
builder.Services.AddSingleton<IShortLivedStore, RedisShortLivedStore>();
builder.Services.AddDeviceLinks();
builder.Services.AddPasskeys<PasskeyStore>(options => builder.Configuration.GetSection("Passkeys").Bind(options));
```

Each feature of the toolkit resolves its store as a keyed service, with a key of <xref:Singulink.Net.Http.Api.Service.ShortLivedStoreKeys>: <xref:Singulink.Net.Http.Api.Service.ShortLivedStoreKeys.SignIn> for sign-in codes, device links and passkey ceremonies, <xref:Singulink.Net.Http.Api.Service.ShortLivedStoreKeys.RateLimiting> for the per-client rate limits, and <xref:Singulink.Net.Http.Api.Service.ShortLivedStoreKeys.Uploads> for chunked uploads. A store registered without a key, as above, is every feature's store, and the store of the app's own values. With the default memory store, each key has a memory store of its own instead, so that one feature can't take the memory of another (see [The memory store's size](sign-in-codes.md#the-memory-stores-size)). A store registered for a key takes the place of that key's store, and one registered for `KeyedService.AnyKey` takes the place of every key's:

```csharp
builder.Services.AddKeyedSingleton<IShortLivedStore, RedisShortLivedStore>(ShortLivedStoreKeys.Uploads);
```

With only that key's store shared, the store of sign-in stays in the memory of each server, which is warned about when the app starts if the app has sign-in codes (see [Mixing shared and process-local stores](#mixing-shared-and-process-local-stores)).

The toolkit serializes its own state, so a store only ever handles keys and string values, and never the toolkit's types. During a rolling deployment, state that one version stored and another cannot read is treated as missing and logged as a warning: the user sees an expired step and starts it again.

### Mixing shared and process-local stores

Each key's store can be shared or process-local (see <xref:Singulink.Net.Http.Api.Service.IShortLivedStore.SharingMode>):

| Store | Process-local | Shared |
| --- | --- | --- |
| Sign-in | One server. A warning is logged when the app starts if another store, or the upload store, is shared: sign-in codes, device links and passkey ceremonies started on one server are not found on another. | Any server finishes what another started. |
| Rate limiting | Each server counts its own requests, so a client gets the limit on each server. | A client's limit holds across all servers. |
| Uploads | Each server counts its own open uploads. The app fails to start if the upload store is shared (see [Uploads](#uploads)). | The counts and cancellations of uploads are seen by all servers. |

A shared store doesn't prove that there are several servers, such as for one server whose stores move to Redis one at a time, so a process-local store of sign-in next to shared ones is only warned about. The app's own store, registered without a key, isn't checked.

### The operations

A store has six operations, and reports its usage. Values expire after a lifetime, and an expired value behaves as if it did not exist. Each operation must be atomic across all servers:

| Operation | What it does |
| --- | --- |
| <xref:Singulink.Net.Http.Api.Service.IShortLivedStore.TryAddAsync*> | Adds a value under a key, unless the key already has one. Returns whether it was added, so only one of several servers adding the same key succeeds. |
| <xref:Singulink.Net.Http.Api.Service.IShortLivedStore.SetAsync*> | Sets the value of a key, replacing a value or a counter it holds, with the lifetime counted from now. The key has a value throughout. |
| <xref:Singulink.Net.Http.Api.Service.IShortLivedStore.GetAsync*> | Gets the value of a key. |
| <xref:Singulink.Net.Http.Api.Service.IShortLivedStore.TakeAsync*> | Gets and removes the value of a key. A value is returned at most once, even when servers take it at the same moment. |
| <xref:Singulink.Net.Http.Api.Service.IShortLivedStore.RemoveAsync*> | Removes the value of a key. |
| <xref:Singulink.Net.Http.Api.Service.IShortLivedStore.IncrementAsync*> | Adds an amount to a counter and returns the new count. A key without a value counts from 0, and every increment sets the counter to expire after the lifetime. |

<xref:Singulink.Net.Http.Api.Service.IShortLivedStore.GetUsageAsync*> reports how much the store holds and can hold, for the health check of the store (see [Health Checks and Metrics](diagnostics.md)). A store reports the values it can tell, and <xref:Singulink.Net.Http.Api.Service.ShortLivedStoreUsage.Unknown> if it can tell none.

A store also says with <xref:Singulink.Net.Http.Api.Service.IShortLivedStore.SharingMode> whether its values are visible only to the process that stores them (<xref:Singulink.Net.Http.Api.Service.StoreSharingMode.ProcessLocal>), as those of the memory store are, or to every server that uses the same storage (<xref:Singulink.Net.Http.Api.Service.StoreSharingMode.Shared>). [Chunked uploads](#uploads) read it when the app starts.

A counter is stored as its count in decimal text, so <xref:Singulink.Net.Http.Api.Service.IShortLivedStore.GetAsync*> reads it like any other value. The toolkit's keys all start with `httpapi:`. Services that share one Redis server or table keep their keys apart with a prefix of each service's own inside the store, as the Redis store below does.

### A Redis store

Each operation is a single Redis command, or a transaction for an increment and its expiry. `GETDEL` needs Redis 6.2 or later:

| Operation | Redis |
| --- | --- |
| `TryAddAsync` | `SET key value PX lifetime NX` |
| `SetAsync` | `SET key value PX lifetime` |
| `GetAsync` | `GET key` |
| `TakeAsync` | `GETDEL key` |
| `RemoveAsync` | `DEL key` |
| `IncrementAsync` | `MULTI`, `INCRBY key amount`, `PEXPIRE key lifetime`, `EXEC` |
| `GetUsageAsync` | `INFO memory` (`used_memory`, `maxmemory`) and `DBSIZE` |

With [StackExchange.Redis](https://stackexchange.github.io/StackExchange.Redis/):

```csharp
public sealed class RedisShortLivedStore(IConnectionMultiplexer redis) : IShortLivedStore
{
    // Keeps this service's keys apart from those of other services on the same Redis server.
    private const string Prefix = "my-service:";

    private IDatabase Database => redis.GetDatabase();

    public StoreSharingMode SharingMode => StoreSharingMode.Shared;

    // The server's memory and keys, also those of other services on the same server. A maxmemory of 0 means no limit. These are the first server's
    // values: a cluster reports those of each node.
    public async ValueTask<ShortLivedStoreUsage> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        var server = redis.GetServers()[0];
        var memory = (await server.InfoAsync("memory")).SelectMany(g => g).ToDictionary(p => p.Key, p => p.Value);
        long maxMemory = long.Parse(memory["maxmemory"], CultureInfo.InvariantCulture);

        return new ShortLivedStoreUsage {
            EntryCount = await server.DatabaseSizeAsync(),
            UsedBytes = long.Parse(memory["used_memory"], CultureInfo.InvariantCulture),
            MaxBytes = maxMemory is 0 ? null : maxMemory,
        };
    }

    public async ValueTask<bool> TryAddAsync(string key, string value, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        return await Database.StringSetAsync(Prefix + key, value, lifetime, when: When.NotExists);
    }

    public async ValueTask SetAsync(string key, string value, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        await Database.StringSetAsync(Prefix + key, value, lifetime);
    }

    public async ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        return (string?)await Database.StringGetAsync(Prefix + key);
    }

    public async ValueTask<string?> TakeAsync(string key, CancellationToken cancellationToken = default)
    {
        return (string?)await Database.StringGetDeleteAsync(Prefix + key);
    }

    public async ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        await Database.KeyDeleteAsync(Prefix + key);
    }

    public async ValueTask<long> IncrementAsync(string key, long amount, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        var transaction = Database.CreateTransaction();
        var count = transaction.StringIncrementAsync(Prefix + key, amount);
        _ = transaction.KeyExpireAsync(Prefix + key, lifetime);

        await transaction.ExecuteAsync();
        return await count;
    }
}
```

Redis removes expired keys by itself. Redis evicts keys or refuses writes when it is full, by its `maxmemory-policy`, which the usage leaves to Redis's own monitoring, so it reports no refused writes.

### A database store

A table with a key, a value and an expiry time holds everything:

```sql
CREATE TABLE short_lived_store (
    key text PRIMARY KEY,
    value text NOT NULL,
    expires_at timestamptz NOT NULL
);

CREATE INDEX short_lived_store_expires_at ON short_lived_store (expires_at);
```

Each operation is one statement. In PostgreSQL, using the database's clock so that the servers' clocks do not matter:

| Operation | PostgreSQL |
| --- | --- |
| `TryAddAsync` | `INSERT INTO short_lived_store VALUES (@key, @value, now() + @lifetime) ON CONFLICT (key) DO UPDATE SET value = excluded.value, expires_at = excluded.expires_at WHERE short_lived_store.expires_at <= now()`, which added the value if it affected a row. |
| `SetAsync` | `INSERT INTO short_lived_store VALUES (@key, @value, now() + @lifetime) ON CONFLICT (key) DO UPDATE SET value = excluded.value, expires_at = excluded.expires_at` |
| `GetAsync` | `SELECT value FROM short_lived_store WHERE key = @key AND expires_at > now()` |
| `TakeAsync` | `DELETE FROM short_lived_store WHERE key = @key AND expires_at > now() RETURNING value` |
| `RemoveAsync` | `DELETE FROM short_lived_store WHERE key = @key` |
| `IncrementAsync` | `INSERT INTO short_lived_store VALUES (@key, @amount::text, now() + @lifetime) ON CONFLICT (key) DO UPDATE SET value = CASE WHEN short_lived_store.expires_at > now() THEN (short_lived_store.value::bigint + @amount)::text ELSE excluded.value END, expires_at = excluded.expires_at RETURNING value::bigint` |
| `GetUsageAsync` | `SELECT count(*) FROM short_lived_store`, as the entry count, and nothing else |

In SQL Server, `DELETE ... OUTPUT deleted.value` takes a value, and `MERGE ... WITH (HOLDLOCK)` with `OUTPUT inserted.value` adds or sets a value or increments a counter in one statement.

Rows of expired values stay in the table until they are deleted, so delete them from time to time, for example every few minutes from a background service:

```sql
DELETE FROM short_lived_store WHERE expires_at <= now();
```

The store is a singleton that every request uses, so open a connection per operation (for example from an `NpgsqlDataSource` or a pooled `DbContext` factory) rather than holding a scoped one.

## Uploads

[Chunked uploads](uploads.md) keep everything in their store, such as the directory store, so servers share uploads by sharing it: a directory on a network share, such as an SMB or NFS share mounted at the same path on every server, registered with <xref:Singulink.Net.Http.Api.Service.Uploads.DirectoryUploadStoreOptions.SharingMode?displayProperty=nameWithType> set to <xref:Singulink.Net.Http.Api.Service.StoreSharingMode.Shared>, or a shared store of the app's own that all servers reach (see [Stores of your own](uploads.md#stores-of-your-own)):

- Any server takes any chunk, since each chunk is written at its own place and marked as received on its own.
- A direct upload is stored and run as an upload in chunks, so a client whose response broke off completes it on any server, and a request made again with its ID on another server attaches to its run or gets its outcome.
- A completion runs the handler on the server it reaches, which claims the run in the store. The claim succeeds on one server only, and that server renews it every 15 seconds while the handler runs. A completion on another server waits for the run's outcome, with ping records meanwhile.
- A claim that is not renewed for a minute has lapsed, because its server stopped, and the next request for the upload, or the next cleanup on any server, ends the upload as interrupted. Claims are renewed every 15 seconds, so a server whose clock runs more than about 45 seconds ahead of another's takes that server's running claims for lapsed ones. The markers of chunk writes, which are taken for stopped a minute after they last showed that they are alive, every 15 seconds, compare the clocks of servers too. A claim or the outcome of an upload in its directory that the directory store finds empty or partly written is taken as being written for a minute after its last write, and as broken after that, by the file system's clock, which is the file server's on a share, so the file server's clock doesn't have to agree with the servers'.
- A chunk that a server is writing keeps the run from being claimed on any server, and a server that writes a chunk of a claimed run leaves the file as it is.
- Uploads are created atomically in the store, so of creations with the same ID on several servers, one creates the upload, and the others get it.
- A claim that its server can't release, such as while another process has it open, is kept renewed and released again by that server, which answers the upload's chunks and completions with `503` (`upload-store-unavailable`) meanwhile. Other servers take it for a running run.
- The outcome of a finished upload is kept in the store, so any server answers a completion made again or a creation made again with the upload's ID, and any server's cleanup removes the outcome once it is no longer kept, by the time of the server that stored it.
- The open uploads of each user, and of all users, are counted in the short-lived store (see [Limits of open uploads](uploads.md#limits-of-open-uploads)), and the cancellations of uploads are written there, so servers that share uploads must share the store of <xref:Singulink.Net.Http.Api.Service.ShortLivedStoreKeys.Uploads> too: an app whose upload store is shared and whose store of uploads is process-local fails to start. The limits then hold across all of them, an upload that ends on any server stops counting at once, and creations fail on every server while the short-lived store can't be reached.
- A cancellation on one server reaches the handler that runs on another at that server's next check, every <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.CancelCheckInterval?displayProperty=nameWithType> (1 second by default), and the server that runs the handler ends the upload (see [Cancelling an upload](uploads.md#cancelling-an-upload)).

Without a shared directory or store, each server has uploads of its own, in a process-local directory (<xref:Singulink.Net.Http.Api.Service.Uploads.DirectoryUploadStoreOptions.SharingMode?displayProperty=nameWithType> set to <xref:Singulink.Net.Http.Api.Service.StoreSharingMode.ProcessLocal>) or store, and every request of an upload has to reach the server that created it, such as through a load balancer that keeps a client on one server. A cancellation then reaches the server that runs the handler, which cancels the handler's token at once.

Such servers still share the store of sign-in. When they share the store of uploads too, the counts of open uploads are kept for each machine: their keys include the machine's name, and <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxUploadsPerUser>, <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxTotalSizePerUser> and <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxTotalSize> apply to the open uploads of each machine. Processes on one machine with process-local directories of their own share the machine's counts, which the cleanups of each correct to the uploads of its own directory, so run one such process on each machine.

How the stores combine:

| Upload store | Store of uploads | Uploads |
| --- | --- | --- |
| Shared | Shared | Any server takes any request of an upload. The counts are kept for all servers, and a cancellation reaches the handler at its server's next check. |
| Shared | Process-local | The app fails to start. |
| Process-local | Shared | Every request of an upload must reach the server that keeps it. The counts are kept for each machine, and a cancellation reaches the handler at once. |
| Process-local | Process-local | One server. The counts are its own, and a cancellation reaches the handler at once. |

A process-local directory is locked while the app runs, so the app fails to start while another process uses it, such as the previous worker process while IIS recycles an application pool with overlapping (see [The directory store](uploads.md#the-directory-store)).

A store that servers share makes its creations and claims atomic across all of them, writes chunks idempotently and durably, and stores only the first outcome of an upload, durably. The directory store creates the files that only one server may create, such as a claim or the outcome of an upload in its directory, in one step that fails if the file exists (an exclusive create, `O_CREAT | O_EXCL` on Linux and macOS), which the file server decides for all of its clients over SMB and over NFS from version 3. A server finds out whether another server has created a file by opening it, and the times that tell whether a claim or a write of a chunk is alive are in the content of its file, so each server gets the latest renewal, whatever file attributes its client caches.

The clients of shares also cache that a file was not there, and the contents of directories, which the directory store lists to find the chunks and the writes of chunks of an upload, and the outcome of a finished upload among the user's outcomes. A server that misses a file that another server has just created can claim a run while a chunk is written on the other server, write a chunk of a run that the other server has claimed, or answer a request for an upload that has just finished on the other server as for one that doesn't exist. The directory store relies on clients that ask the file server instead:

| Client | Settings |
| --- | --- |
| Windows (SMB) | `FileNotFoundCacheLifetime` and `DirectoryCacheLifetime` of 0, which `Set-SmbClientConfiguration -FileNotFoundCacheLifetime 0 -DirectoryCacheLifetime 0` sets. They are 5 and 10 seconds by default. |
| Linux (NFS) | The mount option `lookupcache=positive`, and close-to-open consistency, which is the default (`cto`). By default, a missing file is cached for up to a minute. |
| Linux (SMB) | `0` in `/proc/fs/cifs/LookupCacheEnabled`, for all mounts. Otherwise, a missing file is cached for a second. |
| macOS (NFS) | The mount option `nonegnamecache`, which turns off the cache of missing files. |
| macOS (SMB) | Directories are cached for 30 to 60 seconds (`dir_cache_min` and `dir_cache_max` in `/etc/nsmb.conf`), and `nsmb.conf` documents no setting that turns the cache off. |

## Rate Limits

The per-client rate limits count requests in the store of <xref:Singulink.Net.Http.Api.Service.ShortLivedStoreKeys.RateLimiting>, so with a shared store a client's limit holds across all servers, and requests to limited endpoints fail if the store cannot be reached. With a process-local store, each server limits on its own. See [Several Servers](rate-limiting.md#several-servers) for how the limits behave on several servers.

## What Stays on Each Server

Some state is kept per server by design and needs nothing shared:

- **Cloudflare's IP ranges.** With the `Singulink.Net.Http.Api.Service.Cloudflare` package, each server downloads, saves and refreshes Cloudflare's ranges itself, and only trusts the ranges it has loaded. Servers can read one shared copy of the ranges file instead when downloads are turned off; see [Cloudflare](cloudflare.md).
- **SignalR connections.** A hub connection stays on the server it connected to. Sending to clients connected to other servers takes a SignalR backplane, such as the Redis backplane. Each server checks the sessions of its own connections, and a session that ends in any way, such as with a sign-out or a sign-in that replaces it on any server, or the app removing it from its store, ends its connections at their next check on each server alike, up to <xref:Singulink.Net.Http.Api.Service.SessionHandlingOptions.RefreshAfter> and a minute later (see [Rechecking the sessions of open connections](signalr-hubs.md#rechecking-the-sessions-of-open-connections)).
- **Your own caches.** Anything your service keeps in memory, such as cached lookups, is per server. Flows that span requests can keep their state in the short-lived store instead (see [The short-lived store](sign-in-codes.md#the-short-lived-store)).

## Next Steps

Continue with these related articles:

- [Sign-In Codes and Device Linking](sign-in-codes.md) - The short-lived store and the features that use it.
- [Passkeys](passkeys.md) - Passkey ceremonies, which can finish on any server.
- [Rate Limiting](rate-limiting.md) - Per-client limits.
- [Chunked Uploads](uploads.md) - Upload endpoints and their storage.
- [Session Handling](session-handling.md) - Session tokens and the key ring.

</div>
