<div class="article">

# Chunked Uploads

This guide covers receiving files in chunks on the service: mapping an upload endpoint with a validator and a handler, how the handler runs and reports its result, the limits and expiry of uploads, and where uploads are kept.

### How chunked uploads work

A client creates an upload, sends the file in chunks of a few megabytes, and then completes the upload, which runs the app's handler with the complete file. Each chunk is a request of its own that can be sent again, in any order and in parallel, so a dropped connection costs at most a chunk, and no request carries more than 64 MiB, which keeps every request well under the request body limits of proxies such as Cloudflare. The completion streams the handler's result, or the items it reports its progress with, in the toolkit's [streaming format](../concepts/streaming-format.md), with ping records while the handler works.

The handler runs at most once for an upload, and keeps running when the client disconnects. A client that comes back with the completion again gets the run's items, or its outcome once it has ended, so a long run of the handler is never lost to a dropped connection.

The toolkit's client sends uploads with <xref:Singulink.Net.Http.Api.Client.ApiClientBase.UploadAsync*> and <xref:Singulink.Net.Http.Api.Client.ApiClientBase.UploadStreamingAsync*> (see [Chunked Uploads in Client Apps](uploads-client.md)). [Upload Format](../concepts/upload-format.md) specifies the requests for clients built without the toolkit.

## Setting Up

The types of uploads are in the `Singulink.Net.Http.Api.Service.Uploads` namespace, and their registration and mapping methods, `AddChunkedUploads` and `MapChunkedUpload`, in `Singulink.Net.Http.Api.Service`, like the toolkit's other registration methods. Register the services with your session token type and the directory that uploads are kept in:

```csharp
services.AddChunkedUploads<SessionToken>(options => {
    // Required. Relative paths start at the content root. Servers that share uploads use the same directory.
    options.Directory = "../data/uploads";
});
```

<xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddChunkedUploads*> registers the services and a hosted service that removes expired uploads. Uploads belong to users, so they require session handling for the token type (see [Session Handling](session-handling.md)), and the app fails to start without it. It also fails to start if <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.Directory> is not set, unless the app registers an upload store of its own (see [Stores of Your Own](#stores-of-your-own)). The options are validated when the app starts.

Map an upload endpoint with <xref:Singulink.Net.Http.Api.Service.ChunkedUploadEndpointRouteBuilderExtensions.MapChunkedUpload*>, an optional validator that runs when an upload is created, and the handler that runs with the complete file:

```csharp
group.MapChunkedUpload("/{folderId}/files", ValidateFileAsync, UploadFileAsync, upload => upload.MaxSize = 1000 << 20);
```

The endpoint's uploads are under its pattern:

| Request | What it does |
| --- | --- |
| `POST {pattern}/uploads` | Creates an upload and runs the validator. |
| `PUT {pattern}/uploads/{id}` | Sends a chunk. |
| `GET {pattern}/uploads/{id}` | Gets the upload's status. |
| `DELETE {pattern}/uploads/{id}` | Aborts the upload. |
| `POST {pattern}/uploads/{id}/complete` | Runs the handler, or attaches to its run, and streams its result. |

Every request needs a session of the user who created the upload, and comes to the endpoint and route values that the upload was created with: any other request finds no upload. As for any endpoint that takes a session token, a request without a session fails with `401` (`sign-in-required`), and one without the user ID precondition or for another user with `428` or `412`.

<xref:Singulink.Net.Http.Api.Service.ChunkedUploadEndpointRouteBuilderExtensions.MapChunkedUpload*> returns the <xref:Microsoft.AspNetCore.Routing.RouteGroupBuilder> of the uploads, whose conventions, such as `WithTags`, `RequireRateLimiting` or `ExcludeFromDescription`, apply to all five endpoints. Endpoint filters of the group (`AddEndpointFilter`) run around the requests of the five endpoints, with only the `HttpContext` as an argument, rather than around the validator and the handler, and the validation of `AddValidation` doesn't apply to the parameters of the validator and the handler. <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadEndpointOptions.MaxSize?displayProperty=nameWithType> sets the largest upload of the endpoint.

> [!NOTE]
> The validator and the handler are built with `RequestDelegateFactory`, as for `MapPost(string, Delegate)`, so <xref:Singulink.Net.Http.Api.Service.ChunkedUploadEndpointRouteBuilderExtensions.MapChunkedUpload*> is annotated with `RequiresUnreferencedCode` and `RequiresDynamicCode` and is not supported in trimmed and Native AOT apps.

#### The validator

The validator runs when an upload is created, before any chunk is sent, with the file's name, length and content type in an <xref:Singulink.Net.Http.Api.Service.Uploads.UploadRequest> parameter. It binds route values, the query string, services and the session as any endpoint does, returns nothing, and rejects an upload by throwing an <xref:Singulink.Net.Http.Api.ApiException>, which is the creation's error:

```csharp
static async Task ValidateFileAsync(long folderId, UploadRequest upload, SessionToken token, AppDbContext db)
{
    if (!AllowedTypes.Contains(Path.GetExtension(upload.FileName)))
        throw new ValidationApiException("This type of file is not supported.");

    await EnsureCanWriteAsync(db, folderId, token.UserId);
}
```

The completion of an upload must have the query string of its creation, other than the user ID precondition, so that the handler binds the values that the validator bound: a completion with another query string fails with `409` (`upload-query-mismatch`). The toolkit's client sends the same query string with both. Neither the validator nor the handler takes a request body. <xref:Singulink.Net.Http.Api.Service.Uploads.UploadRequest> has a public constructor, so a validator can be called in a test with one.

#### The handler

The handler runs once all chunks have arrived, with the complete file in an <xref:Singulink.Net.Http.Api.Service.Uploads.UploadedFile> parameter, and binds its other parameters as any endpoint does. It returns its result, or an <xref:System.Collections.Generic.IAsyncEnumerable`1> of items that report its progress and end with its result, of a reference type, also wrapped in a <xref:System.Threading.Tasks.Task`1> or <xref:System.Threading.Tasks.ValueTask`1>:

```csharp
static async IAsyncEnumerable<FileUploadResult> UploadFileAsync(
    long folderId, UploadedFile file, SessionToken token, AppDbContext db, FileStore store, [EnumeratorCancellation] CancellationToken cancellationToken)
{
    await EnsureCanWriteAsync(db, folderId, token.UserId);

    await using (var stream = file.OpenReadStream())
    {
        await foreach (double fraction in store.ConvertAsync(stream, cancellationToken))
            yield return new FileUploadProgress(fraction);
    }

    yield return new FileUploadSuccess(await store.AddAsync(folderId, file, cancellationToken));
}
```

The result and the items are written with the app's JSON options, for the declared result or item type, as for [streaming responses](streaming-responses.md). A creation fails with a `500` if the app's JSON options have no type info for that type, before any chunk is sent. Handlers that return no result, a value type or an `IResult` fail when they are mapped, as do validators that return a result, a handler that takes an <xref:Singulink.Net.Http.Api.Service.Uploads.UploadRequest> and a validator that takes an <xref:Singulink.Net.Http.Api.Service.Uploads.UploadedFile>.

The handler runs well after the validator, possibly hours later, and what the validator checked, such as the user's permissions, can have changed in between.

<xref:Singulink.Net.Http.Api.Service.Uploads.UploadedFile> has the members of `IFormFile` that apply to it:

| Member | What it is |
| --- | --- |
| <xref:Singulink.Net.Http.Api.Service.Uploads.UploadedFile.Id> | The upload ID. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.UploadedFile.FileName> | The file's name as the client sent it. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.UploadedFile.Length> | The file's length, which is the length the upload was created with. The handler gets exactly that many bytes. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.UploadedFile.ContentType> | The content type as the client sent it, if it sent one. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.UploadedFile.OpenReadStream*> | Opens a read-only stream of the file, which is seekable with the directory store. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.UploadedFile.FilePath> | The path of the file with the directory store, or `null` with a store that keeps no file, such as one over an object store. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.UploadedFile.MoveToAsync*> | Moves the file to a path of the app's. With the directory store, a move on the same volume copies nothing and a move to another volume copies the file's content, as with stores that keep no file. |

The file is removed when the handler finishes, unless it was moved or the handler failed with a `503`, which leaves the file for the next run. A handler that moves the file and then fails with a `503` leaves no file for the next run, which then fails. Without its file, the upload also counts as one that received no chunk, so a completion made more than <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.UnstartedExpiry> after the upload's creation finds it expired, with a `404` (`upload-not-found`). A handler that reads the file through its stream works with every store.

#### Commands

The client can send a command of the app's with the creation, such as the ID of the document that the file is for. An <xref:Singulink.Net.Http.Api.Service.Uploads.UploadRequest`1> or <xref:Singulink.Net.Http.Api.Service.Uploads.UploadedFile`1> parameter has it in its `Command` property, read with the app's JSON options:

```csharp
static Task ValidateAttachmentAsync(UploadRequest<AttachmentCommand> upload, SessionToken token, AppDbContext db) => ...;

static Task<FileInfo> UploadAttachmentAsync(UploadedFile<AttachmentCommand> file, SessionToken token, AppDbContext db) => ...;
```

The command is read when the upload is created, so a creation whose command cannot be read, or that has none where the parameter does not allow `null`, fails right away with `400` (`upload-invalid`). The validator and the handler take the same command type. The body of a creation, command included, can be up to 64 KiB long, and a longer one fails with `413`.

## Running the Handler

The first completion request runs the handler, unless the application is stopping, when it fails with `503` (`upload-server-stopping`) and a `Retry-After` of a second, so that the client completes the upload on another server or once the application has restarted. The handler's `CancellationToken` parameter and the request's `HttpContext.RequestAborted` are the run's token, which is not cancelled when the client disconnects: the completion request stays open until the handler has finished, so the handler's scoped services stay alive and its outcome is kept. The run's token is cancelled when the upload is aborted with `DELETE` (on another server, at that server's next renewal of the claim, up to 15 seconds later), when the handler has run for <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.IdleExpiry?displayProperty=nameWithType>, when the server loses the run's claim, such as when the store fails to renew it before its lease runs out, and when the application stops. A handler that is still running a minute after its time limit is left to finish on its own, and its upload ends as interrupted: the completion requests get that outcome, and the request that runs the handler stays open until the handler has finished, so the handler's scoped services and its `HttpContext` stay alive.

#### Outcomes

The handler's outcome is its last item, or its single result, or its error. It is kept for <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.OutcomeRetention> after the handler has finished, and the file is removed. Completion requests get the run as follows:

- A request while the handler runs on the same server gets the latest item, then the items that follow. A request whose client reads more than 16 items behind the handler skips the oldest of them.
- A request while the handler runs on another server that shares the uploads sends ping records until the run has ended, and then gets its outcome. If its own server stops meanwhile, it fails with `503` (`upload-server-stopping`) and a `Retry-After` of a second.
- A request after the run has ended gets its outcome: the last item and the end record, or the error.

The handler runs at most once. A run that stops before it finishes, because the application stopped, the server failed, or the handler ran past <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.IdleExpiry>, ends the upload with a `500` with the error code `upload-interrupted` (<xref:Singulink.Net.Http.Api.UploadErrorCodes.Interrupted?displayProperty=nameWithType>), which is its outcome: the handler does not run again, and the client uploads the file again to retry. A handler that fails with a `503`, by throwing a <xref:Singulink.Net.Http.Api.ServiceUnavailableApiException> or an exception that the app's <xref:Singulink.Net.Http.Api.Service.IApiExceptionHandler> maps to a `503`, leaves the upload with all its chunks instead, so the client's next completion request runs the handler again.

#### Errors

The handler reports errors by throwing an <xref:Singulink.Net.Http.Api.ApiException>, and they follow the streaming rules: an error before the stream's first record, such as a handler that throws within the first ping interval, is a regular error response, and one after it is an error record. Either way the client throws the same exception type. Other exceptions are mapped by the registered <xref:Singulink.Net.Http.Api.Service.IApiExceptionHandler> once, as for any endpoint. An exception that it does not map, or fails to map, is logged once with a new reference ID and becomes a `500` with it, which every completion request for the upload gets. A request whose parameters cannot be bound, such as a header value of the wrong type, fails without running the handler and leaves the upload with all its chunks.

#### Pings

While the handler produces nothing, the completion stream sends a ping record every <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.CompletionPingInterval> (10 seconds by default), or at the interval of a <xref:Singulink.Net.Http.Api.Service.KeepAlivePingAttribute> on the handler, and the first one starts the response. Proxies that end a response that sends nothing for a while, such as Cloudflare after 125 seconds, therefore leave a completion open for as long as the handler runs. The toolkit's client repeats a completion that sends nothing for its completion idle timeout, 60 seconds by default, so a longer ping interval makes it repeat completions while the handler runs.

## Limits and Expiry

<xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions> has these settings:

| Option | Default | Purpose |
| --- | --- | --- |
| <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.Directory> | none | The directory of the directory store. Required unless the app registers a store of its own. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxSize> | 1 GiB | Largest upload of the endpoints whose <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadEndpointOptions.MaxSize?displayProperty=nameWithType> is not set. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.DefaultChunkSize> | 5 MiB | Chunk size of uploads whose client asks for none. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MinChunkSize> | 5 MiB | Smallest chunk size a client can ask for. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxChunkSize> | 64 MiB | Largest chunk size a client can ask for. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxChunkCount> | 10,000 | Largest number of chunks of an upload. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxUploadsPerUser> | 20 | Open uploads of a user, across all upload endpoints. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxTotalSizePerUser> | no limit | Total length of the open uploads of a user, across all upload endpoints. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxTotalSize> | no limit | Total length of all open uploads. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.IdleExpiry> | 1 day | How long an upload is kept after its last chunk, and how long its handler can run. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.UnstartedExpiry> | 15 minutes | How long an upload that has received no chunk is kept after its creation, up to the idle expiry. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.OutcomeRetention> | 1 hour | How long an upload's outcome is kept. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.CompletionPingInterval> | 10 seconds | Ping interval of completion streams. |

Open uploads are the uploads that are receiving chunks and those whose handler is running. <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.IdleExpiry>, <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.UnstartedExpiry>, <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.OutcomeRetention> and <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.CompletionPingInterval> can be up to 24 days.

#### Chunk sizes

Chunk sizes are always from 5 MiB, the smallest part size that object stores such as Amazon S3 accept for all but the last part, to 64 MiB, about 67.1 MB, a third under the smallest request body limit of Cloudflare's plans. A file smaller than a chunk is sent as one chunk, so small files take one request. The service keeps the chunk size that a client asks for within <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MinChunkSize> and <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxChunkSize>, and within the bounds of the store (see <xref:Singulink.Net.Http.Api.Service.Uploads.IUploadStore.MinChunkSize?displayProperty=nameWithType>), and raises it if the upload would otherwise have more than <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxChunkCount> chunks. An upload that would need larger chunks than the largest size fails with `413` (`upload-too-large`). The created upload has the chunk size the service chose.

#### Limits of open uploads

A creation over <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxUploadsPerUser> fails with `429` (`upload-limit-reached`), one over <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxTotalSizePerUser> with `429` (`upload-size-limit-reached`), and one over <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxTotalSize> with `503` (`upload-storage-full`) and a `Retry-After` of a minute. A server counts a user's open uploads in the store for one creation of the user at a time, so only uploads that several servers create at the same moment can exceed <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxUploadsPerUser> and <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxTotalSizePerUser>. An upload that is longer than <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxTotalSizePerUser> can never be created, and fails with `413` (`upload-exceeds-size-limit`), and one over the endpoint's largest upload with `413` (`upload-too-large`): an upload over both fails with the error of the smaller limit. The total is computed every five minutes, when expired uploads are removed, and each server adds the uploads it creates to it in between, reserving an upload's length before it creates the upload, so a server stays within <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxTotalSize>, but servers that share uploads can exceed it with the uploads they created since the total was computed. An upload that ends, with an outcome or a deletion, stops counting right away on the server where it ends, and on other servers at their next count. The application logs a warning when it starts if <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxTotalSize> is not set.

#### Repeated creations

A client can send an idempotency key with a creation (<xref:Singulink.Net.Http.Api.DataContracts.CreateUploadCommand.IdempotencyKey?displayProperty=nameWithType>), so that a creation it makes again, such as after its response was lost, gets the same upload. The key is reserved for the new upload before the validator runs, atomically, so of creations with the same key one creates the upload:

- A creation whose key's upload exists, for the same endpoint, route values, query string, file and command, gets that upload with `200 OK` rather than `201 Created`. The validator does not run again.
- A creation whose key's upload is for another endpoint, other route values, another query string or another file or command fails with `409` (`upload-key-conflict`).
- A creation whose key an earlier creation reserved and has not created its upload with yet waits up to 5 seconds for it, and then fails with `503` (`upload-creation-pending`) and a `Retry-After` of a second.

A creation that fails releases its key, and deleting an upload, such as when it is aborted or expires, releases the key too. A key that was reserved 2 minutes ago for an upload that does not exist, such as by a server that failed while it created the upload, is taken over by the next creation with it. That also happens to the key of a creation whose validator still runs after 2 minutes: a creation made again with the key then creates another upload. A reservation that the store can't remove yet, such as while another process has its file open, makes the creations with the key fail with `503` (`upload-creation-pending`) until it is removed: the directory store removes it again in the background for up to 10 minutes, and after that the key is free again once the cleanup removes the release's marker, an hour after it was made.

#### Expiry

An upload that has received no chunk is removed <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.UnstartedExpiry> after its creation, and stops counting toward the limits of open uploads then, also before the cleanup removes it. An upload that has received a chunk is removed <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.IdleExpiry> after its last chunk. Its outcome is removed <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.OutcomeRetention> after the handler finished. Requests treat an expired upload as gone right away, and a hosted service removes expired uploads when the app starts and every five minutes. The status of an upload (<xref:Singulink.Net.Http.Api.DataContracts.UploadStatus.ExpiresUtc?displayProperty=nameWithType>) says when it expires.

#### Request size limits

The chunk endpoint sets the request size limit of each chunk request to the chunk's length, whatever the server's limit is, through `IHttpMaxRequestBodySizeFeature`. IIS checks its own `maxAllowedContentLength` (30,000,000 bytes by default) before ASP.NET Core runs, which admits chunks of up to about 28 MiB, and code cannot raise it for an endpoint. The creation sets the limit of its request to 64 KiB, and the completion has no body.

Kestrel answers a request body that arrives slower than its minimum data rate (240 bytes a second by default) with a `408`, which the toolkit's client retries for chunks. The chunk and completion endpoints have no request timeout of the request timeouts middleware (`UseRequestTimeouts`), whatever its default policy or the group's conventions: a chunk takes as long as the client's connection needs, and a completion as long as the handler runs.

#### Rate limits

The conventions of the uploads' group, such as `RequireRateLimiting`, apply to all five endpoints, and a global limiter counts each chunk as a request: a 1 GiB upload in 5 MiB chunks is 205 chunk requests. Rejections use the app's rejection response (see [Rate Limiting](rate-limiting.md)), and the toolkit's client waits for their `Retry-After`.

#### Messages of the service's errors

The errors that the service reports itself, such as `upload-too-large` or `upload-limit-reached` (see [Errors](../concepts/upload-format.md#errors)), go through the registered <xref:Singulink.Net.Http.Api.Service.IApiExceptionHandler> as any exception of a request does, also when a completion writes one as an error record after its first record, and when it reports an interrupted upload. Each has an error code, and <xref:Singulink.Net.Http.Api.Service.Uploads.UploadErrorData> gets the values that their messages name, such as the largest length of an upload in an `upload-too-large` error. A handler derived from <xref:Singulink.Net.Http.Api.Service.ApiExceptionHandler> gets them in <xref:Singulink.Net.Http.Api.Service.ApiExceptionHandler.MapApiExceptionAsync*> (see [Mapping Exceptions with a Handler](exception-handling.md#mapping-exceptions-with-a-handler)), whose override can return them with other messages:

```csharp
protected override ValueTask<ApiException> MapApiExceptionAsync(HttpContext httpContext, ApiException exception)
{
    if (exception.ErrorCode is UploadErrorCodes.TooLarge && UploadErrorData.TryGetMaxSize(exception, out long maxSize))
        return new(exception.WithMessage($"Files can be up to {maxSize >> 20} MiB."));

    return new(exception);
}
```

## Storage

#### The directory store

By default, uploads are kept in <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.Directory>, on a local disk or a share that several servers mount at the same path (see [Running Multiple Servers](multiple-servers.md#uploads)). Each upload has a directory, `{Directory}/{userKey}/{uploadId}`, where `userKey` is a hash of the user ID:

| File | What it holds |
| --- | --- |
| `upload.json` | The upload's record: its owner, length, chunk size, metadata and creation time. |
| `data` | The file, created with the upload's length and, on file systems that can, with the space for it reserved, so a full disk fails the creation with `503` (`upload-storage-full`) rather than a chunk. On a file system that can't reserve space, such as NFS before version 4.2, a chunk that finds the disk full fails with that error instead. Chunks are written through to the disk. |
| `chunks/{index}` | An empty marker for each chunk, created once the chunk's bytes are on the disk, so a chunk whose write was interrupted is missing and is sent again. A chunk that is written again loses its marker until it is written. |
| `chunks/{index}.{id}.writing` | A marker of a write of a chunk, with the time the write was last alive. The run is not claimed while a write is alive, and a write that finds the run claimed leaves the file as it is. |
| `claim` | The claim of the handler's run, with the time it was last renewed, every 15 seconds while it runs. A claim not renewed for a minute has lapsed. |
| `outcome.json` | The outcome, once the handler has finished. |
| `deleted` | A marker of an upload that was aborted or expired, whose files are still being removed. |

The reservations of idempotency keys are in `{Directory}/{userKey}/keys`. The files that only one request may create, a claim, an outcome or a reservation, are created in one step that fails if the file exists, which the file system decides for all of its clients, also those of a share, so only one is stored, and their content is then written to them: a reader that finds such a file empty or partly written takes it as being written for a minute after the file's last write time, by the file system's clock (the file server's on a share), and as broken after that. A server that stalls for longer than that while it creates such a file, such as on a share whose server fails over, can find it taken over, and two servers then have the claim or the reservation. Over NFS, a creation that stalls between creating the file and setting its times can have its file taken for broken at once. The other files, such as the record and a renewed claim, are written under a temporary name and then moved to their name, so they are complete whenever they can be read. A release of a reservation creates a marker that only one release can create. Uploads that are receiving chunks survive restarts and deployments.

A failure of the store, such as a share that can't be reached for a moment, is logged, and the request fails with `503` (`upload-store-unavailable`) and a `Retry-After` of 2 seconds, which the toolkit's client retries. A completion whose stream has started ends with that error as its error record. A claim of a run that didn't take place, or that can take place again, which the store can't release, such as while another process has it open, would lapse and end the upload as interrupted: the server keeps it renewed and releases it again every 2 seconds, for up to 10 minutes or until the application stops, and answers the upload's chunks and completions with the same `503` meanwhile. Other servers take the claim for a running run: they report the upload as processing, wait for it with pings, and answer its chunks with `409` (`upload-closed`). The directory store's renewal fails along with its release while another process has the claim open or the share can't be reached, so its claim lapses once its lease runs out, about 30 to 45 seconds after the run: the releases made again cover a hold or an outage shorter than that. The directory store keeps a claim that backs off from a chunk that is being written and can't be removed in the same way, and every server takes it for a running run meanwhile.

When the app starts and every five minutes, the hosted service removes expired uploads and outcomes, ends the runs whose claims have lapsed as interrupted, removes what is left of failed creations and deletions, and computes the total size of the open uploads.

#### Stores of your own

An app that keeps uploads elsewhere, such as in an object store, registers an <xref:Singulink.Net.Http.Api.Service.Uploads.IUploadStore> of its own as a singleton, before or after <xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddChunkedUploads*>, and <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.Directory> is not used:

```csharp
services.AddSingleton<IUploadStore, ObjectUploadStore>();
services.AddChunkedUploads<SessionToken>();
```

The service applies the protocol's rules, such as the users and endpoints that uploads belong to, their states, limits and expiry, and the lengths of chunks, and keeps everything it needs in the store, so servers that share a store share its uploads. A store keeps what it is given, and guarantees the following, also when several servers use it at the same time:

- **Chunk writes are idempotent and durable.** Writing a chunk again replaces it with the same bytes, and a chunk is reported as received only once all its bytes are stored. A write that fails leaves the chunk missing, or as it was, also when it was received before.
- **Claims are atomic.** Of claims of an upload's run, at most one succeeds, and none succeeds while the upload has a claim or an outcome. The service claims with a lease of a minute and renews it every 15 seconds, and a store reports a claim whose lease has lapsed.
- **Claims exclude chunk writes.** A claim does not succeed while a chunk is being written, and a write that starts once the run is claimed leaves the file as it is and returns <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkWriteResult.Closed?displayProperty=nameWithType>. A write and a claim that start at the same time don't both fail: if the claim fails, the write is written. The directory store's write waits up to 2 seconds for a claim that it finds to back off, and is closed if the claim that backed off can't be removed in that time.
- **Outcomes are atomic and durable.** Only the first outcome of an upload is stored, and it is kept until the upload is deleted.
- **Key reservations are atomic.** Of reservations of a user's idempotency key, one succeeds, and the others get it.

A store can narrow the chunk sizes with <xref:Singulink.Net.Http.Api.Service.Uploads.IUploadStore.MinChunkSize>, <xref:Singulink.Net.Http.Api.Service.Uploads.IUploadStore.MaxChunkSize> and <xref:Singulink.Net.Http.Api.Service.Uploads.IUploadStore.MaxChunkCount>, which the service applies when it chooses an upload's chunk size, and the app fails to start if they have no size in common with the options. It provides the complete file as an <xref:Singulink.Net.Http.Api.Service.Uploads.UploadContent>, assembling it from the chunks first if it needs to, with a stream and, if it keeps the file in a file system, its path. <xref:Singulink.Net.Http.Api.Service.Uploads.UploadRecord.Metadata> and outcomes are text in a format of the service's own, which a store keeps as it is.

## Testing

The `Singulink.Net.Http.Api.Service.Testing` package has helpers for uploads:

- <xref:Singulink.Net.Http.Api.Service.Testing.TestUploadedFile> is an uploaded file with its content in a temporary file, which is deleted when it is disposed unless the handler moved it, so a handler can be called directly. <xref:Singulink.Net.Http.Api.Service.Testing.TestUploadedFile.Create*?displayProperty=nameWithType> creates one, with a command or without.
- <xref:Singulink.Net.Http.Api.Service.Testing.UploadStoreContract> checks that an upload store keeps the guarantees above, also when several callers use it at the same time, with uploads of a user of its own, `upload-store-contract`, that it deletes once their checks pass: a check that fails leaves its uploads in the store until they expire. <xref:Singulink.Net.Http.Api.Service.Testing.UploadStoreContract.CheckAsync*?displayProperty=nameWithType> throws an <xref:System.InvalidOperationException> that says which guarantee the store doesn't keep.
- <xref:Singulink.Net.Http.Api.Service.Testing.FaultInjectionHandler> is a message handler that fails chosen requests before or after they reach the service, answers them with a response of the test's own, or cuts off a streaming response after a number of records, and records the method, URI and content range of each request it receives (<xref:Singulink.Net.Http.Api.Service.Testing.FaultInjectionHandler.Requests>). A client whose handler wraps a test server's handler in it runs the upload protocol against the app's endpoints, with its retries and the recovery of completion streams, without a network.

```csharp
using var file = TestUploadedFile.Create(File.ReadAllBytes("sample.pdf"), "sample.pdf", "application/pdf");

await foreach (var result in FileEndpoints.UploadFileAsync(5, file, token, db, store, CancellationToken.None))
    results.Add(result);
```

## Next Steps

Continue with these related articles:

- [Chunked Uploads in Client Apps](uploads-client.md) - Sending files from client apps.
- [Upload Format](../concepts/upload-format.md) - The requests and responses of the protocol.
- [Running Multiple Servers](multiple-servers.md) - Sharing uploads between servers.
- [Cloudflare](cloudflare.md) - Running a service behind Cloudflare's proxy.
- [Exception Handling](exception-handling.md#error-codes) - The error codes of uploads.

</div>
