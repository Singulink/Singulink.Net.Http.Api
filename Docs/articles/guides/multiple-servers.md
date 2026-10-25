<div class="article">

# Running Multiple Servers

This guide covers running a service on several servers behind a load balancer, where each request can reach a different server: the session key ring that all servers must share, the short-lived store that keeps sign-in state and rate limit counts, the storage of chunked uploads, and the state that stays on each server.

### What servers share

A service built with the toolkit keeps very little state between requests, and all of it in three places:

- **The Data Protection key ring.** Session tokens are encrypted and signed with ASP.NET Core Data Protection, so every server must use the same keys to read the tokens the others issue.
- **The short-lived store.** Sign-in codes, device links, passkey ceremonies and rate limit counts live in an <xref:Singulink.Net.Http.Api.Service.IShortLivedStore> between requests. With a store that all servers share, any server can finish what another started.
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

Sign-in codes, device links, passkey ceremonies and the per-client rate limits all use the registered store. The toolkit serializes its own state, so a store only ever handles keys and string values, and never the toolkit's types. During a rolling deployment, state that one version stored and another cannot read is treated as missing and logged as a warning: the user sees an expired step and starts it again.

### The operations

A store has five operations. Values expire after a lifetime, and an expired value behaves as if it did not exist. Each operation must be atomic across all servers:

| Operation | What it does |
| --- | --- |
| <xref:Singulink.Net.Http.Api.Service.IShortLivedStore.TryAddAsync*> | Adds a value under a key, unless the key already has one. Returns whether it was added, so only one of several servers adding the same key succeeds. |
| <xref:Singulink.Net.Http.Api.Service.IShortLivedStore.GetAsync*> | Gets the value of a key. |
| <xref:Singulink.Net.Http.Api.Service.IShortLivedStore.TakeAsync*> | Gets and removes the value of a key. A value is returned at most once, even when servers take it at the same moment. |
| <xref:Singulink.Net.Http.Api.Service.IShortLivedStore.RemoveAsync*> | Removes the value of a key. |
| <xref:Singulink.Net.Http.Api.Service.IShortLivedStore.IncrementAsync*> | Adds an amount to a counter and returns the new count. A key without a value counts from 0, and every increment sets the counter to expire after the lifetime. |

A counter is stored as its count in decimal text, so <xref:Singulink.Net.Http.Api.Service.IShortLivedStore.GetAsync*> reads it like any other value. The toolkit's keys all start with `httpapi:`. Services that share one Redis server or table keep their keys apart with a prefix of each service's own inside the store, as the Redis store below does.

### A Redis store

Each operation is a single Redis command, or a transaction for an increment and its expiry. `GETDEL` needs Redis 6.2 or later:

| Operation | Redis |
| --- | --- |
| `TryAddAsync` | `SET key value PX lifetime NX` |
| `GetAsync` | `GET key` |
| `TakeAsync` | `GETDEL key` |
| `RemoveAsync` | `DEL key` |
| `IncrementAsync` | `MULTI`, `INCRBY key amount`, `PEXPIRE key lifetime`, `EXEC` |

With [StackExchange.Redis](https://stackexchange.github.io/StackExchange.Redis/):

```csharp
public sealed class RedisShortLivedStore(IConnectionMultiplexer redis) : IShortLivedStore
{
    // Keeps this service's keys apart from those of other services on the same Redis server.
    private const string Prefix = "my-service:";

    private IDatabase Database => redis.GetDatabase();

    public async ValueTask<bool> TryAddAsync(string key, string value, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        return await Database.StringSetAsync(Prefix + key, value, lifetime, when: When.NotExists);
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

Redis removes expired keys by itself.

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
| `GetAsync` | `SELECT value FROM short_lived_store WHERE key = @key AND expires_at > now()` |
| `TakeAsync` | `DELETE FROM short_lived_store WHERE key = @key AND expires_at > now() RETURNING value` |
| `RemoveAsync` | `DELETE FROM short_lived_store WHERE key = @key` |
| `IncrementAsync` | `INSERT INTO short_lived_store VALUES (@key, @amount::text, now() + @lifetime) ON CONFLICT (key) DO UPDATE SET value = CASE WHEN short_lived_store.expires_at > now() THEN (short_lived_store.value::bigint + @amount)::text ELSE excluded.value END, expires_at = excluded.expires_at RETURNING value::bigint` |

In SQL Server, `DELETE ... OUTPUT deleted.value` takes a value, and `MERGE ... WITH (HOLDLOCK)` with `OUTPUT inserted.value` adds a value or increments a counter in one statement.

Rows of expired values stay in the table until they are deleted, so delete them from time to time, for example every few minutes from a background service:

```sql
DELETE FROM short_lived_store WHERE expires_at <= now();
```

The store is a singleton that every request uses, so open a connection per operation (for example from an `NpgsqlDataSource` or a pooled `DbContext` factory) rather than holding a scoped one.

## Uploads

[Chunked uploads](uploads.md) keep everything in their store, the directory store by default, so servers share uploads by sharing it: a directory on a network share, such as an SMB or NFS share mounted at the same path on every server, set as <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.Directory?displayProperty=nameWithType>, or a store of the app's own that all servers reach (see [Stores of your own](uploads.md#stores-of-your-own)):

- Any server takes any chunk, since each chunk is written at its own place and marked as received on its own.
- A completion runs the handler on the server it reaches, which claims the run in the store. The claim succeeds on one server only, and that server renews it every 15 seconds while the handler runs. A completion on another server waits for the run's outcome, with ping records meanwhile.
- A claim that is not renewed for a minute has lapsed, because its server stopped, and the next request for the upload, or the next cleanup on any server, ends the upload as interrupted. Claims are renewed every 15 seconds, so a server whose clock runs more than about 45 seconds ahead of another's takes that server's running claims for lapsed ones. The markers of chunk writes, which are taken for stopped a minute after they last showed that they are alive, every 15 seconds, and key reservations, which are taken over 2 minutes after they were made, compare the clocks of servers too. A claim, an outcome or a key reservation that the directory store finds empty or partly written is taken as being written for a minute after its last write, and as broken after that, by the file system's clock, which is the file server's on a share, so the file server's clock doesn't have to agree with the servers'.
- A chunk that a server is writing keeps the run from being claimed on any server, and a server that writes a chunk of a claimed run leaves the file as it is.
- The idempotency keys of creations are reserved in the store, so of creations with the same key on several servers, one creates the upload, unless a server stalls for more than a minute while it reserves the key, as [the directory store](uploads.md#the-directory-store) describes.
- A claim that its server can't release, such as while another process has it open, is kept renewed and released again by that server, which answers the upload's chunks and completions with `503` (`upload-store-unavailable`) meanwhile. Other servers take it for a running run.
- Each server counts a user's open uploads in the store for each creation, so uploads that several servers create at the same moment can exceed <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxUploadsPerUser> and <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxTotalSizePerUser>. The total length of the open uploads is computed from the store every five minutes, and each server adds only the uploads it creates to it in between, so uploads that several servers create between two counts can exceed <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxTotalSize>.
- An abort on one server stops the handler that runs on another when that server next renews the claim.

Without a shared directory or store, each server has uploads of its own, and every request of an upload has to reach the server that created it, such as through a load balancer that keeps a client on one server. Each server applies <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxUploadsPerUser>, <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxTotalSizePerUser> and <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxTotalSize> to its own uploads.

A store that servers share makes its claims and key reservations atomic across all of them, writes chunks idempotently and durably, and stores only the first outcome of an upload, durably. The directory store creates the files that only one server may create, such as a claim, an outcome or a reservation, in one step that fails if the file exists (an exclusive create, `O_CREAT | O_EXCL` on Linux and macOS), which the file server decides for all of its clients over SMB and over NFS from version 3. A server finds out whether another server has created a file by opening it, and the times that tell whether a claim or a write of a chunk is alive are in the content of its file, so each server gets the latest renewal, whatever file attributes its client caches.

The clients of shares also cache that a file was not there, and the contents of directories, which the directory store lists to find the chunks and the writes of chunks of an upload. A server that misses a file that another server has just created can claim a run while a chunk is written on the other server, or write a chunk of a run that the other server has claimed. The directory store relies on clients that ask the file server instead:

| Client | Settings |
| --- | --- |
| Windows (SMB) | `FileNotFoundCacheLifetime` and `DirectoryCacheLifetime` of 0, which `Set-SmbClientConfiguration -FileNotFoundCacheLifetime 0 -DirectoryCacheLifetime 0` sets. They are 5 and 10 seconds by default. |
| Linux (NFS) | The mount option `lookupcache=positive`, and close-to-open consistency, which is the default (`cto`). By default, a missing file is cached for up to a minute. |
| Linux (SMB) | `0` in `/proc/fs/cifs/LookupCacheEnabled`, for all mounts. Otherwise, a missing file is cached for a second. |
| macOS (NFS) | The mount option `nonegnamecache`, which turns off the cache of missing files. |
| macOS (SMB) | Directories are cached for 30 to 60 seconds (`dir_cache_min` and `dir_cache_max` in `/etc/nsmb.conf`), and `nsmb.conf` documents no setting that turns the cache off. |

## Rate Limits

The per-client rate limits count requests in the registered short-lived store, so a client's limit holds across all servers, and requests to limited endpoints fail along with the sign-in features if the store cannot be reached. See [Several Servers](rate-limiting.md#several-servers) for how the limits behave on several servers.

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
