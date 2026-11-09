<div class="article">

# Chunked Uploads

This guide covers receiving files in chunks on the service: mapping an upload endpoint with a validator and a handler, how the handler runs and reports its result, the limits and expiry of uploads, and where uploads are kept.

### How chunked uploads work

A client creates an upload, sends the file in chunks of a few megabytes, and then completes the upload, which runs the app's handler with the complete file. Each chunk is a request of its own that can be sent again, in any order and in parallel, so a dropped connection costs at most a chunk, and no request carries more than 64 MiB, which keeps every request well under the request body limits of proxies such as Cloudflare. The completion streams the handler's result, or the items it reports its progress with, in the toolkit's [streaming format](../concepts/streaming-format.md), with ping records while the handler works.

The handler runs at most once for an upload, and keeps running when the client disconnects. A client that comes back with the completion again gets the run's items, or its outcome once it has ended, so a long run of the handler is never lost to a dropped connection.

A file that fits in one chunk can be uploaded directly instead, in one request whose body is the file: a direct upload creates the upload, stores the file as its chunk and completes the upload, with the same validator, handler, limits and outcomes (see [Direct Uploads](#direct-uploads)). The toolkit's client uploads files of up to its chunk size this way.

The toolkit's client sends uploads with <xref:Singulink.Net.Http.Api.Client.ApiClientBase.UploadAsync*> and <xref:Singulink.Net.Http.Api.Client.ApiClientBase.UploadStreamingAsync*> (see [Chunked Uploads in Client Apps](uploads-client.md)). [Upload Format](../concepts/upload-format.md) specifies the requests for clients built without the toolkit.

## Setting Up

The types of uploads are in the `Singulink.Net.Http.Api.Service.Uploads` namespace, and their registration and mapping methods, `AddChunkedUploads`, `AddDirectoryUploadStore` and `MapChunkedUpload`, in `Singulink.Net.Http.Api.Service`, like the toolkit's other registration methods. Register the services with your session token type, and the store that keeps the uploads, such as the directory store:

```csharp
services.AddChunkedUploads<SessionToken>();

// A Singulink.IO path, here in a "data" folder next to the app's folder, which is not a root, so it has a parent. ProcessLocal: only this process uses
// the directory. Servers that share uploads use the same directory, with StoreSharingMode.Shared.
var uploadsDirectory = DirectoryPath.GetAppBase().ParentDirectory!.CombineDirectory("data").CombineDirectory("uploads");
services.AddDirectoryUploadStore(options => {
    options.Directory = uploadsDirectory;
    options.SharingMode = StoreSharingMode.ProcessLocal;
});
```

<xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddChunkedUploads*> registers the services, a hosted service that removes expired uploads, and the short-lived store that counts the open uploads, unless one is registered, which it resolves with the key <xref:Singulink.Net.Http.Api.Service.ShortLivedStoreKeys.Uploads>. Uploads belong to users, so they require session handling for the token type (see [Session Handling](session-handling.md)), and the app fails to start without it. It also fails to start without an upload store: the directory store, which <xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddDirectoryUploadStore*> registers (see [The directory store](#the-directory-store)), or one of the app's own (see [Stores of Your Own](#stores-of-your-own)). The options are validated when the app starts.

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
| `DELETE {pattern}/uploads/{id}` | Cancels the upload (see [Cancelling an upload](#cancelling-an-upload)). |
| `POST {pattern}/uploads/{id}/complete` | Runs the handler, or attaches to its run, and streams its result. |
| `POST {pattern}/uploads/direct` | Uploads a file directly: creates an upload with the file as the body, runs the validator and then the handler, and streams its result. |

Every request needs a session of the user who created the upload, and is made to the endpoint and route values that the upload was created with: any other request finds no upload. As for any endpoint that takes a session token, a request without a session fails with `401` (`sign-in-required`), and one without the user ID precondition or for another user with `428` or `412`.

<xref:Singulink.Net.Http.Api.Service.ChunkedUploadEndpointRouteBuilderExtensions.MapChunkedUpload*> returns the <xref:Microsoft.AspNetCore.Routing.RouteGroupBuilder> of the uploads, whose conventions, such as `WithTags`, `RequireRateLimiting` or `ExcludeFromDescription`, apply to all six endpoints.

Endpoint filters of the group (`AddEndpointFilter`) run around the requests of the six endpoints, with only the `HttpContext` as an argument, rather than around the validator and the handler, and the validation of `AddValidation` doesn't apply to the parameters of the validator and the handler. The request of a direct upload has the file as its body, with the content type `application/octet-stream`, so a filter or a middleware, and a validator or a handler, that looks at a request's body, length or content type sees the file there, and at its path sees `{pattern}/uploads/direct`, without an upload's ID in its route values.

<xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadEndpointOptions.MaxSize?displayProperty=nameWithType> sets the largest upload of the endpoint.

> [!NOTE]
> The validator and the handler are built with `RequestDelegateFactory`, as for `MapPost(string, Delegate)`, so <xref:Singulink.Net.Http.Api.Service.ChunkedUploadEndpointRouteBuilderExtensions.MapChunkedUpload*> is annotated with `RequiresUnreferencedCode` and `RequiresDynamicCode` and is not supported in trimmed and Native AOT apps.

#### The validator

The validator runs when an upload is created, before any chunk is sent, and before the body of a direct upload is read, with the file's name, length and content type in an <xref:Singulink.Net.Http.Api.Service.Uploads.UploadRequest> parameter. It binds route values, the query string, services and the session as any endpoint does, returns nothing, and rejects an upload by throwing an <xref:Singulink.Net.Http.Api.ApiException>, which is the creation's error:

```csharp
static async Task ValidateFileAsync(long folderId, UploadRequest upload, SessionToken token, AppDbContext db)
{
    if (!AllowedTypes.Contains(Path.GetExtension(upload.FileName)))
        throw new ValidationApiException("This type of file is not supported.");

    await EnsureCanWriteAsync(db, folderId, token.UserId);
}
```

The completion of an upload must have the query string of its creation, other than the user ID precondition, so that the handler binds the values that the validator bound: a completion with another query string fails with `409` (`upload-query-mismatch`). The toolkit's client sends the same query string with both. Neither the validator nor the handler takes a request body. <xref:Singulink.Net.Http.Api.Service.Uploads.UploadRequest> has a public constructor, so a validator can be called in a test with one.

A validator can run more than once for one upload: creations of the upload made again while the first one still runs, such as by a client whose connection dropped, each run it before one of them creates the upload (see [Upload IDs](#upload-ids)). A creation made again once the upload exists gets it without running the validator.

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

<xref:Singulink.Net.Http.Api.Service.Uploads.UploadedFile> has the members of `IFormFile` that apply to it, with files as `IAbsoluteFilePath` paths of [Singulink.IO.FileSystem](https://github.com/Singulink/Singulink.IO.FileSystem):

| Member | What it is |
| --- | --- |
| <xref:Singulink.Net.Http.Api.Service.Uploads.UploadedFile.Id> | The upload ID. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.UploadedFile.FileName> | The file's name as the client sent it. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.UploadedFile.Length> | The file's length, which is the length the upload was created with. The handler gets exactly that many bytes. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.UploadedFile.ContentType> | The content type as the client sent it, if it sent one. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.UploadedFile.OpenReadStream*> | Opens a read-only stream of the file, which is seekable with the directory store. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.UploadedFile.File> | The file, with the directory store, or `null` with a store that keeps no file, such as one over an object store. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.UploadedFile.MoveToAsync*> | Moves the file to a file of the app's. With the directory store, a move on the same volume copies nothing and a move to another volume copies the file's content, as with stores that keep no file. |

The file is removed when the handler finishes, unless it was moved or the handler failed with a `503`, which leaves the file for the next run. A handler that moves the file and then fails with a `503` leaves no file for the next run, which then fails. Without its file, the upload also counts as one that received no chunk, so a completion made more than <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.UnstartedExpiry> after the upload's creation finds it expired, with a `404` (`upload-not-found`). A handler that reads the file through its stream works with every store.

#### Commands

The client can send a command of the app's with the creation, such as the ID of the document that the file is for. An <xref:Singulink.Net.Http.Api.Service.Uploads.UploadRequest`1> or <xref:Singulink.Net.Http.Api.Service.Uploads.UploadedFile`1> parameter has it in its `Command` property, read with the app's JSON options:

```csharp
static Task ValidateAttachmentAsync(UploadRequest<AttachmentCommand> upload, SessionToken token, AppDbContext db) => ...;

static Task<FileInfo> UploadAttachmentAsync(UploadedFile<AttachmentCommand> file, SessionToken token, AppDbContext db) => ...;
```

The command is read when the upload is created, so a creation whose command cannot be read, or that has none where the parameter does not allow `null`, fails right away with `400` (`upload-invalid`). The validator and the handler take the same command type. The body of a creation, command included, can be up to 64 KiB long, and a longer one fails with `413`. A direct upload has its command in a header, of up to 8 KiB in base64url, which is about 6 KiB of JSON: the toolkit's client sends a longer command with the three requests.

## Running the Handler

The first completion request runs the handler, unless the application is stopping, when it fails with `503` (`upload-server-stopping`) and a `Retry-After` of a second, so that the client completes the upload on another server or once the application has restarted.

The handler's `CancellationToken` parameter and the request's `HttpContext.RequestAborted` are the run's token, which is not cancelled when the client disconnects: the completion request stays open until the handler has finished, so the handler's scoped services stay alive and its outcome is kept.

The run's token is cancelled when the upload is cancelled with `DELETE` (see [Cancelling an upload](#cancelling-an-upload)), when the handler has run for <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.ProcessingTimeout?displayProperty=nameWithType>, when the server loses the run's claim, such as when the store fails to renew it before its lease runs out, and when the application stops.

A handler that is still running a minute after its time limit is left to finish on its own, and its upload ends as interrupted: the completion requests get that outcome, and the request that runs the handler stays open until the handler has finished, so the handler's scoped services and its `HttpContext` stay alive.

#### Cancelling an upload

`DELETE {pattern}/uploads/{id}` cancels an upload. A handler that has returned has finished its work, which a cancellation doesn't undo, so the answer says what the cancellation did:

| Answer | Meaning |
| --- | --- |
| `204 No Content` | The upload doesn't exist, or its handler isn't running. The upload is deleted, and no handler runs for it, also not for an upload that a request with its ID creates while the cancellation is kept. |
| `202 Accepted` | The handler is running. Its cancellation token is cancelled, and the run ends the upload. |
| `409 Conflict` (`upload-finished`) | The upload has finished. Its outcome stays until it is no longer kept (<xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.OutcomeRetention>). |

An ID that can't be an upload's is answered with `404` (`upload-not-found`), as the other requests of an upload are.

The cancellation is written to the short-lived store, under the key `httpapi:upload-cancel:{id}:{scope}:{userId}`, where `scope` is a hash of the endpoint and route values, before the upload's state is read, and a run reads it after it has claimed the upload and before it starts the handler. So either the cancellation finds the run and leaves the upload to it, or the run finds the cancellation and ends the upload without starting the handler.

That holds for an upload that doesn't exist yet too: the cancellation is written all the same, and a request that creates the upload later, such as a direct upload that a proxy held until it had the whole body, creates it, and its run finds the cancellation. The cancellation is kept for <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.ProcessingTimeout> and a minute, or for 2 minutes when the upload doesn't exist when it is made, which bounds what cancellations of uploads that don't exist can fill the short-lived store with: a request that a proxy held arrives within seconds of the client's abort. The run that ended the upload after it found the cancellation removes it, and so does a cancellation that deleted an upload whose handler wasn't running, unless the upload is no longer than the largest chunk, since a direct upload of it can still arrive: its cancellation then stays until it expires.

A cancellation that the short-lived store fails to write, such as one that can't be reached for a moment, is answered with `503` (`upload-cancellation-unavailable`) and a `Retry-After` of 2 seconds, which the toolkit's client retries. A run whose check fails before the handler starts leaves the handler unstarted and the upload with all its chunks, and its completion fails with the same error, which the client makes again.

The handler's token is cancelled at once on the server that answers the cancellation. With a shared upload store (see [The directory store](#the-directory-store)), the server that runs the handler reads the cancellation every <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.CancelCheckInterval> (1 second by default) while the handler runs, and a check that fails is logged and made again at the next interval. With a process-local store, every request for an upload reaches the process that keeps it, so no check is made.

A run whose token the cancellation cancelled ends as follows:

- A handler that returned its result, or failed with an error that is its outcome, finished: its outcome is stored and stands, and the completion requests get it.
- A handler that threw after its token was cancelled, a run that ended without an outcome after it, such as of a handler whose parameters couldn't be bound or that failed with a `503`, and a run that found the cancellation before the handler started, end the upload as cancelled: the upload is deleted, and the completion requests attached to the run end with `409` (`upload-cancelled`). A completion that waits for the run on another server finds the upload gone, and ends with `404` (`upload-not-found`), as later requests for the upload do.
- A run whose upload another server deleted while the handler ran, such as for a cancellation that found its claim lapsed, ends the same way once its next renewal finds the upload gone.

A handler that hasn't stopped or finished 10 seconds after its token was cancelled for the cancellation is logged as a warning, with the upload's ID, user and endpoint. A handler whose upload another server deleted is not.

The toolkit's client sends the cancellation, with the upload's ID, once a request that can start the handler was sent, and then returns the handler's result if it finished, or throws an <xref:System.OperationCanceledException> (see [Cancellation and Aborting](uploads-client.md#cancellation-and-aborting)). Clients of 4.x send it without reading its answer.

#### Outcomes

The handler's outcome is its last item, or its single result, or its error. It is kept for <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.OutcomeRetention> after the handler has finished, and the file is removed. The upload is then no longer open: it doesn't count toward the limits of open uploads, and the store keeps only its record and outcome. Completion requests get the run as follows:

- A request while the handler runs on the same server gets the latest item, then the items that follow. A request whose client reads more than 16 items behind the handler skips the oldest of them.
- A request while the handler runs on another server that shares the uploads sends ping records until the run has ended, and then gets its outcome. If its own server stops meanwhile, it fails with `503` (`upload-server-stopping`) and a `Retry-After` of a second.
- A request after the run has ended gets its outcome: the last item and the end record, or the error.

The handler runs at most once. A run that stops before it finishes, because the application stopped, the server failed, or the handler ran past <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.ProcessingTimeout>, ends the upload with a `500` with the error code `upload-interrupted` (<xref:Singulink.Net.Http.Api.UploadErrorCodes.Interrupted?displayProperty=nameWithType>), which is its outcome: the handler does not run again, and the client uploads the file again to retry. A handler that fails with a `503`, by throwing a <xref:Singulink.Net.Http.Api.ServiceUnavailableApiException> or an exception that the app's <xref:Singulink.Net.Http.Api.Service.IApiExceptionHandler> maps to a `503`, leaves the upload with all its chunks instead, so the client's next completion request runs the handler again.

#### Errors

The handler reports errors by throwing an <xref:Singulink.Net.Http.Api.ApiException>, and they follow the streaming rules: an error before the stream's first record, such as a handler that throws within the first ping interval, is a regular error response, and one after it is an error record. Either way the client throws the same exception type. Other exceptions are mapped by the registered <xref:Singulink.Net.Http.Api.Service.IApiExceptionHandler> once, as for any endpoint. An exception that it does not map, or fails to map, is logged once with a new reference ID and becomes a `500` with it, which every completion request for the upload gets. A request whose parameters cannot be bound, such as a header value of the wrong type, fails without running the handler and leaves the upload with all its chunks.

#### Pings

While the handler produces nothing, the completion stream sends a ping record every <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.CompletionPingInterval> (10 seconds by default), or at the interval of a <xref:Singulink.Net.Http.Api.Service.KeepAlivePingAttribute> on the handler, and the first one starts the response. Proxies that end a response that sends nothing for a while, such as Cloudflare after 125 seconds, therefore leave a completion open for as long as the handler runs. The toolkit's client repeats a completion that sends nothing for its completion idle timeout, 60 seconds by default, so a longer ping interval makes it repeat completions while the handler runs.

## Direct Uploads

`POST {pattern}/uploads/direct` uploads a file directly, in one request, with the file as the body. It takes what a creation takes in a header rather than in a JSON body, so a direct upload and a creation of the same file are the same upload, and the app's query string, which the validator and the handler bind. The `Content-Type` of the request isn't read, and the `chunkSize` of the creation isn't used:

| Header | What it holds |
| --- | --- |
| `Upload-Creation` | The JSON of a creation, as the body of `POST {pattern}/uploads` has it, in UTF-8, base64url-encoded with or without padding, of up to 8 KiB. A longer one fails with `431` (`upload-creation-header-too-large`). |
| `Content-Length` | The file's length, if the request has one, which must be the `length` of the creation. A body sent with chunked transfer encoding has none. |

The request creates the upload, with the checks, the validator and the limits of a creation, before it stores the body, so a rejected upload is answered without storing the file, after its body has arrived (see [Request size limits](#request-size-limits)). It then stores the body as the upload's one chunk and completes the upload: the response is the completion's [streaming response](../concepts/streaming-format.md), with `201 Created`, or `200 OK` for a request made again that got the upload of an earlier one, and a `Location` header with the upload's path, `{pattern}/uploads/{id}`, and it starts with a ping record. From then on the upload is one in chunks: a client whose response breaks off makes the completion request at that path, which attaches to the run or gets its outcome, and the status, chunk and abort requests work as for any upload.

A file that is longer than the largest chunk size of the service (<xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxChunkSize>, within the store's) fails with `413` (`upload-direct-too-large`), and is uploaded in chunks. A file that is longer than the endpoint allows fails with `413` (`upload-too-large`), as its creation does.

A request made again with the upload's ID gets the upload that the first one created: while the upload receives its file, it stores its own body as the chunk again; once the handler runs, it reads its body without storing it and attaches to the run; and once the handler has finished, it gets the outcome. A direct upload with the ID of an upload in chunks, which the toolkit's client doesn't make, reads its body without storing it, and its response ends with `409` (`upload-incomplete`) while chunks are missing.

A body that fails, such as of a client that disconnects, and a client that is gone before the run starts, leave the upload as a chunk that fails does: a request made again with the ID joins it, a client completes it at its path, the toolkit's client cancels it by its ID when the upload is cancelled, and otherwise it expires like any other upload.

The request has no request timeout of the request timeouts middleware, as the chunk and completion requests have none.

## Limits and Expiry

<xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions> has these settings:

| Option | Default | Purpose |
| --- | --- | --- |
| <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxSize> | 1 GiB | Largest upload of the endpoints whose <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadEndpointOptions.MaxSize?displayProperty=nameWithType> is not set. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.DefaultChunkSize> | 5 MiB | Chunk size of uploads whose client asks for none. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MinChunkSize> | 5 MiB | Smallest chunk size a client can ask for. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxChunkSize> | 64 MiB | Largest chunk size a client can ask for. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxChunkCount> | 10,000 | Largest number of chunks of an upload. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxUploadsPerUser> | 20 | Open uploads of a user, across all upload endpoints. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxTotalSizePerUser> | no limit | Total length of the open uploads of a user, across all upload endpoints. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxTotalSize> | no limit | Total length of all open uploads. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.IdleExpiry> | 15 minutes | How long an upload that is receiving chunks is kept after its last chunk. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.ProcessingTimeout> | 1 hour | How long the handler of an upload can run. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.UnstartedExpiry> | 15 minutes | How long an upload that has received no chunk is kept after its creation, up to the idle expiry. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.OutcomeRetention> | 1 hour | How long an upload's outcome is kept. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.CompletionPingInterval> | 10 seconds | Ping interval of completion streams. |
| <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.CancelCheckInterval> | 1 second | How often a server checks whether an upload whose handler it runs was cancelled on another server, with a shared upload store. |

Open uploads are the uploads that are receiving chunks and those whose handler is running. <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.IdleExpiry>, <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.ProcessingTimeout>, <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.UnstartedExpiry>, <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.OutcomeRetention>, <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.CompletionPingInterval> and <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.CancelCheckInterval> can be up to 24 days.

#### Chunk sizes

Chunk sizes are always from 5 MiB, the smallest part size that object stores such as Amazon S3 accept for all but the last part, to 64 MiB, about 67.1 MB, a third under the smallest request body limit of Cloudflare's plans. A file smaller than a chunk is sent as one chunk, and the toolkit's client uploads such a file directly, in one request. The service keeps the chunk size that a client asks for within <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MinChunkSize> and <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxChunkSize>, and within the bounds of the store (see <xref:Singulink.Net.Http.Api.Service.Uploads.IUploadStore.MinChunkSize?displayProperty=nameWithType>), and raises it if the upload would otherwise have more than <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxChunkCount> chunks. An upload that would need larger chunks than the largest size fails with `413` (`upload-too-large`). The created upload has the chunk size the service chose.

#### Limits of open uploads

A creation over <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxUploadsPerUser> fails with `429` (`upload-limit-reached`), one over <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxTotalSizePerUser> with `429` (`upload-size-limit-reached`), both with a `Retry-After` of a second, which the toolkit's client waits for before it makes the creation again, and one over <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxTotalSize> with `503` (`upload-storage-full`) and a `Retry-After` of a minute. An upload that is longer than <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxTotalSizePerUser> can never be created, and fails with `413` (`upload-exceeds-size-limit`), and one over the endpoint's largest upload or <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxTotalSize> with `413` (`upload-too-large`): an upload over both <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxTotalSizePerUser> and the endpoint's largest upload fails with the error of the smaller limit. A creation made again with the ID of an existing upload gets the upload, whatever the limits are now (see [Upload IDs](#upload-ids)).

The open uploads are counted in the short-lived store of <xref:Singulink.Net.Http.Api.Service.ShortLivedStoreKeys.Uploads> (see [The short-lived store](sign-in-codes.md#the-short-lived-store)), which <xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddChunkedUploads*> registers unless the app registers one: the number of each user's open uploads and the total of their lengths, and the total length of the open uploads of all users. A creation checks the counts first, and one over a limit is rejected without storing anything. A creation within the limits adds its upload to the counts, one after another, before it creates the upload, and takes it out of them again if it is then over a limit, so creations at the same time, also on servers that share the short-lived store, stay within the limits. An upload is taken out of the counts when it ends, with an outcome or a deletion, by the request or cleanup that ended it. A short-lived store that fails fails the creation with its exception, as it fails any request that uses it: a <xref:Singulink.Net.Http.Api.ServiceUnavailableApiException> is a `503`, and an exception that isn't an API exception is a `500` (see [Exception Handling](exception-handling.md)). No upload is created without its counts.

Each cleanup, when the app starts and every five minutes on each server, corrects the counts from a listing of the open uploads in the upload store. The listing also ends the runs whose claims have lapsed and removes the uploads that have expired, and counts the rest. A creation is checked against the counts only, so counts that are too high hold their places until the next cleanup.

An upload that expired counts until a cleanup removes it, or a request for the upload, which removes it too, since it takes up room until it is removed. The counts can differ from the open uploads until the next cleanup:

- An upload that could not be taken out of the counts still counts and holds its place, such as when a server stops between ending an upload and taking it out of the counts, or while the short-lived store fails. So does an upload that a deletion, such as of an expired upload, ended while its outcome was stored, if neither of them took it out. A total that is too high rejects creations with `503` (`upload-storage-full`).
- The cleanup leaves out the creations in progress while it lists the uploads, so they can take a user over a limit by as many uploads as are being created meanwhile. The uploads that are created or end while it lists them count as the listing found them.
- The cleanups of several servers that run at the same time can correct the same difference twice.
- Counts are kept for 15 minutes after they last changed, and each cleanup keeps those of the users who have open uploads. An upload that ends after its counts lapsed takes them below 0, which leaves room over the limits by as much until the next cleanup. The cleanup doesn't see a user whose counts are too high with no open upload: those counts lapse 15 minutes after they last changed.
- The memory store loses the counts when the app restarts, and until the first cleanup after the start has counted the open uploads, which takes as long as listing them, users can create uploads up to the limits on top of the ones that are open. A short-lived store that is kept across restarts keeps them.

The memory store refuses to start a counter when it is full (see [The memory store's size](sign-in-codes.md#the-memory-stores-size)), so it then fails the creations of users who have no counts with `503` (`short-lived-store-full`), while those of other users go on. The application logs a warning when it starts if <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxTotalSize> is not set.

Whose uploads the counts count follows from the stores. With an upload store that is process-local (see <xref:Singulink.Net.Http.Api.Service.Uploads.IUploadStore.SharingMode?displayProperty=nameWithType>) and a short-lived store that is shared (see <xref:Singulink.Net.Http.Api.Service.IShortLivedStore.SharingMode?displayProperty=nameWithType>), the keys of the counts include the name of the machine, <xref:System.Environment.MachineName?displayProperty=nameWithType>, which in a container is the container's host name: each machine counts the uploads of its own store, its cleanups correct its counts to them, and the limits apply to the open uploads of each machine. With the other combinations, the counts are kept for all processes that share the short-lived store. Processes on one machine with process-local directories of their own that share a short-lived store share the machine's counts, which the cleanups of each correct to the uploads of its own directory (see [Uploads](multiple-servers.md#uploads)).

#### Limits of each user

An <xref:Singulink.Net.Http.Api.Service.Uploads.IUploadLimitsProvider> that the app registers, as a service of any lifetime, sets the limits of a user's open uploads in place of <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxUploadsPerUser> and <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxTotalSizePerUser>. It is resolved from the request's services and called with the request's context and user ID for each creation and each direct upload whose ID, file name, length, content type and idempotency key are valid, and returns <xref:Singulink.Net.Http.Api.Service.Uploads.UploadLimits>, or `null` for the options' limits. A <xref:Singulink.Net.Http.Api.Service.Uploads.UploadLimits.MaxTotalSizePerUser> that is `null` sets no limit, as it does in the options:

```csharp
services.AddSingleton<IUploadLimitsProvider, RoleUploadLimits>();

sealed class RoleUploadLimits : IUploadLimitsProvider
{
    public async ValueTask<UploadLimits?> GetLimitsAsync(HttpContext context, string userId, CancellationToken cancellationToken)
    {
        var token = await context.GetRequiredSessionTokenAsync<SessionToken>();
        return token.Role is UserRole.Admin ? new UploadLimits { MaxUploadsPerUser = 200 } : null;
    }
}
```

The user's limits apply to the count of the user's open uploads, to the total of their lengths, and to the check of an upload that is longer than the user's total, as the options' limits do. <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.MaxTotalSize> applies to all uploads as it is. An exception of the provider fails the request.

#### Upload IDs

The client chooses an upload's ID, a GUID (<xref:Singulink.Net.Http.Api.DataContracts.CreateUploadCommand.Id?displayProperty=nameWithType>), which the paths of the upload's requests have, so it can cancel the upload by its ID before the service has answered its creation. IDs are the user's: uploads of different users can have the same ID, and everything the service keeps of an upload, in the upload store and in the short-lived store, is kept by its user and ID. A creation whose ID is not a GUID fails with `400` (`upload-creation-unreadable`), and one with the empty GUID with `400` (`upload-invalid`). A path with an ID in another case, or in a form other than a GUID's 36 characters, is not an upload's.

A creation with the ID of an upload of the user gets that upload, so a creation made again, such as after its response was lost, gets the upload that the first one created:

- A creation with the ID of an upload that is open, or finished while its outcome is kept, for the same endpoint, route values, query string, file and command, gets that upload with `200 OK` rather than `201 Created`, also when the user is at a limit of open uploads, or has lower limits than when the upload was created. The validator does not run again.
- A creation with the ID of an upload for another endpoint, other route values, another query string or another file or command fails with `409` (`upload-id-conflict`).
- Creations with the same ID at the same time each run the validator, and one of them creates the upload, which the others get.
- A creation with the ID of an upload that is being removed, such as one that was cancelled a moment ago, waits up to 5 seconds for it to be gone and then creates the upload, or fails with `503` (`upload-creation-pending`) and a `Retry-After` of a second.

An upload that was deleted, such as when it was cancelled or expired, or whose outcome is no longer kept, can be created again with its ID. A creation without an ID or an idempotency key, such as of a 4.x client that sends no key, gets a random ID (version 4) from the service. The ID of a creation with the idempotency key of a 4.x client (<xref:Singulink.Net.Http.Api.DataContracts.CreateUploadCommand.IdempotencyKey?displayProperty=nameWithType>) is made from the key, so a 4.x creation made again with its key gets the same upload, and one with the key of another upload fails with `409` (`upload-key-conflict`).

#### Expiry

An upload that has received no chunk is removed <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.UnstartedExpiry> after its creation. An upload that has received a chunk is removed <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.IdleExpiry> after its last chunk; a chunk whose bytes are still arriving keeps it, and one that has stalled does not. An upload whose handler runs does not expire, and its handler is stopped once it has run for <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.ProcessingTimeout>. A run that ends without an outcome, such as of a handler that fails with a `503`, leaves the upload receiving chunks again, and it is removed <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.IdleExpiry> after the run. An outcome is removed <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.OutcomeRetention> after the handler finished. Requests treat an expired upload as gone right away, and a hosted service removes expired uploads when the app starts and every five minutes. The status of an upload (<xref:Singulink.Net.Http.Api.DataContracts.UploadStatus.ExpiresUtc?displayProperty=nameWithType>) says when it expires.

#### Request size limits

The chunk endpoint sets the request size limit of each chunk request to the chunk's length, whatever the server's limit is, through `IHttpMaxRequestBodySizeFeature`. IIS checks its own `maxAllowedContentLength` (30,000,000 bytes by default) before ASP.NET Core runs, which admits chunks of up to about 28 MiB, and code cannot raise it for an endpoint. The creation sets the limit of its request to 64 KiB, the completion has no body, and a direct upload sets the limit of its request to the file's length, which IIS's `maxAllowedContentLength` limits as it limits chunks.

A chunk or a direct upload that the service rejects before it has stored the body, such as a direct upload over a limit of the user or a chunk of an upload whose handler runs, has its body read and left out before the error is sent, if it is no longer than 64 MiB, for up to a minute, also for a request without a session: once it has answered, a server closes the connection of a request whose unread body takes longer than a few seconds to arrive, as Kestrel does, and a client that is still sending the body then gets a failed connection rather than the error.

Kestrel answers a request body that arrives slower than its minimum data rate (240 bytes a second by default) with a `408`, which the toolkit's client retries for chunks. The chunk, completion and direct upload endpoints have no request timeout of the request timeouts middleware (`UseRequestTimeouts`), whatever its default policy or the group's conventions: a chunk takes as long as the client's connection needs, and a completion as long as the handler runs.

#### Rate limits

The conventions of the uploads' group, such as `RequireRateLimiting`, apply to all six endpoints, and a global limiter counts each chunk as a request: a 1 GiB upload in 5 MiB chunks is 205 chunk requests, and a direct upload is one request. Rejections use the app's rejection response (see [Rate Limiting](rate-limiting.md)), and the toolkit's client waits for their `Retry-After`.

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

The directory store keeps uploads in a directory on a local disk or a share that several servers mount at the same path (see [Running Multiple Servers](multiple-servers.md#uploads)). <xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddDirectoryUploadStore*> registers it with the <xref:Singulink.Net.Http.Api.Service.Uploads.DirectoryUploadStoreOptions> of the options system, which are set in the action passed to it or bound from a configuration section with `services.Configure<DirectoryUploadStoreOptions>(section)`, and whose <xref:Singulink.Net.Http.Api.Service.Uploads.DirectoryUploadStoreOptions.Directory> and <xref:Singulink.Net.Http.Api.Service.Uploads.DirectoryUploadStoreOptions.SharingMode> are required and have no default. The directory is an absolute path of [Singulink.IO.FileSystem](https://github.com/Singulink/Singulink.IO.FileSystem) (`IAbsoluteDirectoryPath`) with friendly names: a name with a leading or trailing space, a trailing dot, a control character or, on Windows, a reserved device name such as `CON` is rejected on every platform, since a typo such as a trailing space would otherwise create a directory with that name, which many tools handle badly. The reflection-based configuration binder binds it from a string, such as `{ "Directory": "D:\\data\\uploads", "SharingMode": "ProcessLocal" }`, through Singulink.IO.FileSystem's type converters, which read it as a path of the platform the app runs on. The configuration binding source generator, used by trimmed and native AOT apps, doesn't use type converters and leaves the directory unset, so such an app parses the path itself, such as with `DirectoryPath.ParseAbsolute(...)`, and sets it in the action passed to `AddDirectoryUploadStore`. The options can be bound from a configuration in any order with values set in code: the store checks them when the app starts, which fails with a message that names a required value that is not set, and they can't be changed after that.

<xref:Singulink.Net.Http.Api.Service.Uploads.DirectoryUploadStoreOptions.SharingMode> says whether other processes use the directory too. With <xref:Singulink.Net.Http.Api.Service.StoreSharingMode.ProcessLocal>, the directory store is process-local (see <xref:Singulink.Net.Http.Api.Service.Uploads.IUploadStore.SharingMode?displayProperty=nameWithType>): it holds a lock file in the directory, `uploads.lock`, open without sharing from the start of the app until it stops, and the app fails to start, without waiting, if another process holds it. Windows enforces the lock, also on an SMB share. .NET on Linux and macOS emulates it with an advisory lock, which holds on a local disk and is not guaranteed on an NFS mount. So an app whose new process starts while the previous one still runs, such as in an IIS application pool with overlapped recycling, which IIS uses by default, can't use a process-local directory: turn overlapped recycling off for it (`disallowOverlappingRotation="true"` in the pool's `recycling` settings). Since no other process can use the directory, the first cleanup ends the runs that a previous process left as interrupted at once, rather than once their claims lapse. With <xref:Singulink.Net.Http.Api.Service.StoreSharingMode.Shared>, the store takes no lock, and the short-lived store must be shared too (see <xref:Singulink.Net.Http.Api.Service.IShortLivedStore.SharingMode?displayProperty=nameWithType>), or the app fails to start.

Each open upload has a directory, `{Directory}/{userKey}/{uploadId}`, where `userKey` is a hash of the user ID:

| File | What it holds |
| --- | --- |
| `upload.json` | The upload's record: its owner, length, chunk size, metadata and creation time. |
| `data` | The file, created with the upload's length and, on file systems that can, with the space for it reserved, so a full disk fails the creation with `503` (`upload-storage-full`) rather than a chunk. On a file system that can't reserve space, such as NFS before version 4.2, a chunk that finds the disk full fails with that error instead. Chunks are written through to the disk. |
| `chunks/{index}` | An empty marker for each chunk, created once the chunk's bytes are on the disk, so a chunk whose write was interrupted is missing and is sent again. A chunk that is written again loses its marker until it is written. |
| `chunks/{index}.{id}.writing` | A marker of a write of a chunk, with the time the write was last alive, every 15 seconds, when the write also sets the time of the upload's last chunk if bytes of the chunk arrived since, so that a chunk that arrives slowly keeps its upload, and one that has stalled does not. The run is not claimed while a write is alive, and a write that finds the run claimed leaves the file as it is. Markers are not flushed to the disk, since the write that a marker shows is gone after a power loss. |
| `claim` | The claim of the handler's run, with the time it was last renewed, every 15 seconds while it runs. A claim not renewed for a minute has lapsed. |
| `finished.json` | The outcome with the record, once the handler has finished, while the upload leaves its directory. |
| `deleted` | A marker of an upload that was cancelled or expired, whose files are still being removed. |

A creation makes the upload's directory under a name of its own, `{uploadId}.{random}.new`, and moves it to the upload's name once its files are written, which fails while the user has an upload with the ID, so of creations of an upload at the same time one creates it, and an upload is complete once it exists. It then looks for an outcome of the ID once more, since an upload with the ID that finished in the meantime copied its outcome before it removed its directory, and removes its own if it finds one. A creation that stopped before it moved its directory leaves it, and the cleanup removes it once it is an hour old.

A finished upload leaves its directory: its outcome and record are copied to `{Directory}/{userKey}/outcomes/{uploadId}.{ticks}.json`, where `ticks` is the time the outcome was stored, by the server's clock, and the directory is then removed. The copy is written under a temporary name and moved to its name, so it is complete whenever it can be read, and servers that copy an outcome at the same time write the same file. The cleanup's listing of the open uploads lists only their directories, so it takes as long with thousands of finished uploads as with none, and the cleanup removes outcomes by the times in their names without opening them. A deletion whose checks straddle the removal of the directory of an upload that has just finished, with its check for the record before the finish removes the record, and its creation of the `deleted` marker and its check for `finished.json` after the removal has removed `finished.json` and before the directory is gone, reports that it deleted the open upload, as the finish does. The deletion then removes the outcome that the finish stored, so the client that completed the upload was told that it succeeded and the outcome is gone, and the counts are one upload too low until the next cleanup. A request for a finished upload, such as a completion made again, finds its outcome by the upload's ID among the names of the user's outcomes, which on a share lists them over the network.

The files that only one request may create, a claim or the `finished.json` of an upload, are created in one step that fails if the file exists, which the file system decides for all of its clients, also those of a share, so only one is stored, and their content is then written to them: a reader that finds such a file empty or partly written takes it as being written for a minute after the file's last write time, by the file system's clock (the file server's on a share), and as broken after that. A server that stalls for longer than that while it creates such a file, such as on a share whose server fails over, can find it taken over, and two servers then have the claim. Over NFS, a creation that stalls between creating the file and setting its times can have its file taken for broken at once.

The other files, such as the record and a renewed claim, are written under a temporary name and then moved to their name, so they are complete whenever they can be read. Uploads that are receiving chunks survive restarts and deployments.

A deletion that runs while the upload's outcome is stored, after `finished.json` was created and before it was copied, can find no outcome to remove and leave the copy: the upload then reads as finished, with the outcome of its handler, which did finish. An outcome file in `outcomes` that is damaged on the disk reads as an upload that doesn't exist, since it holds the upload's record too.

A failure of the store, such as a share that can't be reached for a moment, is logged, and the request fails with `503` (`upload-store-unavailable`) and a `Retry-After` of 2 seconds, which the toolkit's client retries. A completion whose stream has started ends with that error as its error record.

A claim of a run that didn't take place, or that can take place again, which the store can't release, such as while another process has it open, would lapse and end the upload as interrupted: the server keeps it renewed and releases it again every 2 seconds, for up to 10 minutes or until the application stops, and answers the upload's chunks and completions with the same `503` meanwhile.

Other servers take the claim for a running run: they report the upload as processing, wait for it with pings, and answer its chunks with `409` (`upload-closed`). The directory store's renewal fails along with its release while another process has the claim open or the share can't be reached, so its claim lapses once its lease runs out, about 30 to 45 seconds after the run: the releases made again cover a hold or an outage shorter than that. The directory store keeps a claim that backs off from a chunk that is being written and can't be removed in the same way, and every server takes it for a running run meanwhile.

When the app starts and every five minutes, the hosted service removes expired uploads and outcomes, ends the runs whose claims have lapsed as interrupted, and with a process-local store also the runs that a previous process left, removes what is left of failed creations and deletions, and corrects the counts of the open uploads. A correction that fails, such as while the short-lived store can't be reached, is logged at the Error level, in one entry for the cleanup however many counters failed, and the rest of the cleanup goes on. The directory store removes the directories that creations left, and the reservations of idempotency keys that 4.x kept, which uploads no longer have. It moves on the uploads that a server left finished in their directories, such as one that stopped while it finished an upload, and removes the directories of uploads that 4.x finished, whose outcomes are not kept (see [Upgrading to 5.0](../upgrading/upgrading-to-5.md)).

#### Stores of your own

An app that keeps uploads elsewhere, such as in an object store, registers an <xref:Singulink.Net.Http.Api.Service.Uploads.IUploadStore> of its own as a singleton, before or after <xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddChunkedUploads*>, in place of the directory store:

```csharp
services.AddSingleton<IUploadStore, ObjectUploadStore>();
services.AddChunkedUploads<SessionToken>();
```

The service applies the protocol's rules, such as the users and endpoints that uploads belong to, their states, limits and expiry, and the lengths of chunks, and keeps everything it needs in the store, so servers that share a store share its uploads. A store keeps what it is given, and guarantees the following, also when several servers use it at the same time:

- **Uploads are the user's.** Uploads are kept by their user and ID: uploads of different users can have the same ID, and are different uploads.
- **Creations are atomic.** Of creations of a user's upload with the same ID at the same time, at most one creates it, and none does while the user has an upload with the ID, open or with an outcome, or one that is being created or removed: those return <xref:Singulink.Net.Http.Api.Service.Uploads.UploadCreationResult.Exists?displayProperty=nameWithType>, after which the service reads the upload, and creates it again a moment later if the read finds none. An upload that was deleted, or whose outcome was removed, can be created again.
- **Chunk writes are idempotent and durable.** Writing a chunk again replaces it with the same bytes, and a chunk is reported as received only once all its bytes are stored. A write that fails leaves the chunk missing, or as it was, also when it was received before. A write that takes long keeps the upload's last write time (<xref:Singulink.Net.Http.Api.Service.Uploads.StoredUpload.LastWriteUtc?displayProperty=nameWithType>) current while bytes of its chunk arrive, so that the upload doesn't expire while its chunk arrives, and does expire while the chunk has stalled. The contract checks below don't cover this, since they would have to wait for it.
- **Claims are atomic.** Of claims of an upload's run, at most one succeeds, and none succeeds while the upload has a claim or an outcome. The service claims with a lease of a minute and renews it every 15 seconds, and a store reports a claim whose lease has lapsed.
- **Claims exclude chunk writes.** A claim does not succeed while a chunk is being written, and a write that starts once the run is claimed leaves the file as it is and returns <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkWriteResult.Closed?displayProperty=nameWithType>. A write and a claim that start at the same time don't both fail: if the claim fails, the write is written. The directory store's write waits up to 2 seconds for a claim that it finds to back off, and is closed if the claim that backed off can't be removed in that time.
- **Outcomes are atomic and durable.** Only the first outcome of an upload is stored, and it is kept until it is removed (<xref:Singulink.Net.Http.Api.Service.Uploads.IUploadStore.RemoveOutcomesAsync*>) or the upload is deleted, which the service does only once the outcome is no longer kept: a cancellation of a finished upload keeps it. An upload with an outcome is finished: it is no longer listed, it is read with its record and outcome, and a read while its outcome is stored gets the open upload or the finished one. Storing an outcome for an upload that has one removes what is left of the upload, such as the claim of a server that stopped while it stored the outcome.
- **Endings of open uploads are reported once at most.** Of deletions of an open upload at the same time, one returns `true` (<xref:Singulink.Net.Http.Api.Service.Uploads.IUploadStore.DeleteAsync*>), and a deletion of a finished upload, or of one that no longer exists, returns `false`. A deletion and the storing of the upload's outcome at the same time don't both return `true`, but can both return `false`. The service takes an upload out of the counts of open uploads when a deletion of it or the storing of its outcome returns `true`, so an upload that both ended with `false` counts until the next cleanup corrects the counts.

A store says with <xref:Singulink.Net.Http.Api.Service.Uploads.IUploadStore.SharingMode> whether only this process uses it, which decides whether servers check for cancellations while handlers run (see [Cancelling an upload](#cancelling-an-upload)), whether the first cleanup ends the runs of a previous process, which short-lived stores the app can start with, and the keys of the counts (see [Limits of open uploads](#limits-of-open-uploads)). A store can narrow the chunk sizes with <xref:Singulink.Net.Http.Api.Service.Uploads.IUploadStore.MinChunkSize>, <xref:Singulink.Net.Http.Api.Service.Uploads.IUploadStore.MaxChunkSize> and <xref:Singulink.Net.Http.Api.Service.Uploads.IUploadStore.MaxChunkCount>, which the service applies when it chooses an upload's chunk size, and the app fails to start if they have no size in common with the options. It provides the complete file as an <xref:Singulink.Net.Http.Api.Service.Uploads.UploadContent>, assembling it from the chunks first if it needs to, with a stream and, if it keeps the file in a file system, its path. <xref:Singulink.Net.Http.Api.Service.Uploads.UploadRecord.Metadata> and outcomes are text in a format of the service's own, which a store keeps as it is.

The service lists all open uploads in each cleanup, to correct their counts (see [Limits of open uploads](#limits-of-open-uploads)), reads a finished upload only for a request that comes back for its outcome, and calls <xref:Singulink.Net.Http.Api.Service.Uploads.IUploadStore.RemoveOutcomesAsync*> in each cleanup with the time before which outcomes are no longer kept. A store that keeps finished uploads apart from the open ones lists the open uploads as fast with many finished uploads as with none.

## Testing

The `Singulink.Net.Http.Api.Service.Testing` package has helpers for uploads:

- <xref:Singulink.Net.Http.Api.Service.Testing.TestUploadedFile> is an uploaded file with its content in a temporary file, which is deleted when it is disposed unless the handler moved it, so a handler can be called directly. <xref:Singulink.Net.Http.Api.Service.Testing.TestUploadedFile.Create*?displayProperty=nameWithType> creates one, with a command or without.
- <xref:Singulink.Net.Http.Api.Service.Testing.UploadStoreContract> checks that an upload store keeps the guarantees above, also when several callers use it at the same time, with uploads of two users of its own, `upload-store-contract` and `upload-store-contract-other`, that it deletes once their checks pass: a check that fails leaves its uploads in the store until they expire. <xref:Singulink.Net.Http.Api.Service.Testing.UploadStoreContract.CheckAsync*?displayProperty=nameWithType> checks all guarantees except the removal of outcomes, which <xref:Singulink.Net.Http.Api.Service.Testing.UploadStoreContract.CheckOutcomeRemovalAsync*?displayProperty=nameWithType> checks: it removes the outcomes of all users that were stored before one of its own, so run it only against a store that holds test data. Both throw an <xref:System.InvalidOperationException> that says which guarantee the store doesn't keep.
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
