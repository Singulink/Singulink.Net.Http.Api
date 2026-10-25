<div class="article">

# Chunked Uploads in Client Apps

<xref:Singulink.Net.Http.Api.Client.ApiClientBase.UploadAsync*> and <xref:Singulink.Net.Http.Api.Client.ApiClientBase.UploadStreamingAsync*> upload a file in chunks to an upload endpoint of the service, send again the chunks whose requests fail, and return what the endpoint's handler produces. This guide covers the upload methods, upload sources, the options, progress, how failures are retried and recovered, and cancellation.

### How it works

An upload takes three steps, each with requests that the client makes again when they fail for a reason that may not last:

1. A `POST` to `{path}/uploads` creates the upload, with the app's query string and command, and a random idempotency key that is the same each time the request is made, so a creation made again after its response was lost gets the upload it created. The endpoint's validator runs, before any bytes are sent. The service chooses the chunk size, within its bounds: 5 MiB to 64 MiB, and 5 MiB by default.
2. A `PUT` to `{path}/uploads/{id}` sends each chunk, with its byte range in a `Content-Range` header. Chunks are sent in order, one at a time by default.
3. A `POST` to `{path}/uploads/{id}/complete`, with the app's query string, runs the endpoint's handler and streams its items or its result, with keep-alive pings while it works.

The service side, which maps the upload endpoints and runs their validators and handlers, is covered in [Chunked Uploads](uploads.md), and the requests and responses of the protocol in [Upload Format](../concepts/upload-format.md).

## Uploading a File

<xref:Singulink.Net.Http.Api.Client.ApiClientBase.UploadAsync*> returns the result of a handler that returns one, and <xref:Singulink.Net.Http.Api.Client.ApiClientBase.UploadStreamingAsync*> returns the items of a handler that streams them, as <xref:Singulink.Net.Http.Api.Client.ApiClientBase.PostStreamingAsync*> does:

```csharp
public Task<FileInfo> UploadFileAsync(long folderId, UploadSource file, IProgress<UploadProgress>? progress, CancellationToken cancellationToken) =>
    UploadAsync<FileInfo>($"folders/{folderId}/files", file, options: new() { Progress = progress }, cancellationToken: cancellationToken);

public IAsyncEnumerable<ImportProgress> ImportAsync(UploadSource file, ImportCommand command, CancellationToken cancellationToken) =>
    UploadStreamingAsync<ImportProgress>("imports", file, [("mode", "merge")], command, cancellationToken: cancellationToken);
```

The path is the path of the upload endpoint, relative to the base address, and can't have a query string: a path with a `?` or a `#`, or one that is not valid or leads outside the base address, throws an <xref:System.ArgumentException> when the method is called. The query string parameters go with the requests that create and complete the upload, so the validator and the handler bind the same values. The command goes in the body of the request that creates the upload, serialized with <xref:Singulink.Net.Http.Api.Client.ApiClientBase.SerializerOptions>, and the result or items are read with them too, so the app's serializer context lists their types as for any request. The toolkit's own contracts of the protocol use type info of their own.

The requests are created with <xref:Singulink.Net.Http.Api.Client.ApiClientBase.CreateRequest*> and sent with <xref:Singulink.Net.Http.Api.Client.ApiClientBase.SendAsync*> and <xref:Singulink.Net.Http.Api.Client.ApiClientBase.SendStreamingAsync*>, so the session cookie, the user ID precondition, the user agent, <xref:Singulink.Net.Http.Api.Client.ApiClientBase.HttpHandler> and <xref:Singulink.Net.Http.Api.Client.ApiClientBase.OnSessionEnded*> apply to them as to any request.

<xref:Singulink.Net.Http.Api.Client.ApiClientBase.UploadAsync*> throws a <xref:System.FormatException> if the completion streams no result, or more than one item, and a <xref:System.Text.Json.JsonException> if the result is `null`. <xref:Singulink.Net.Http.Api.Client.ApiClientBase.UploadStreamingAsync*> starts the upload when the enumeration starts, and again for each enumeration. Ending the enumeration early stops reading the items without aborting the upload, and the handler runs on.

## Upload Sources

An <xref:Singulink.Net.Http.Api.Client.UploadSource> has the file's name, length and content type, which the service receives, and reads any byte range of the content. Its factories create sources for files, streams and functions:

| Factory | Reads |
| --- | --- |
| <xref:Singulink.Net.Http.Api.Client.UploadSource.FromFile*> | A file, which each read opens. A read throws an <xref:System.IO.IOException> if the file's length or last write time has changed since the source was created. |
| <xref:Singulink.Net.Http.Api.Client.UploadSource.FromStream*> | A readable and seekable stream, by setting its position, one read at a time. The stream is not disposed. A stream that can't seek throws an <xref:System.ArgumentException>. |
| <xref:Singulink.Net.Http.Api.Client.UploadSource.FromStreamFactory*> | Streams that a function opens, one read at a time. It keeps a stream open between reads and reads it forward, setting its position if it is seekable, and opens a new one to go back in one that is not, after a read fails, and after <xref:Singulink.Net.Http.Api.Client.UploadSource.ReleaseAsync*>. It disposes the streams. |
| <xref:Singulink.Net.Http.Api.Client.UploadSource.Create*> | The ranges that a function reads. |

An upload reads one range at a time, in order, into the buffer of a chunk, and sends a chunk whose request fails again from that buffer, so a retry never reads the source again. It reads a range again only for a chunk that the service still lacks when the upload completes. It calls <xref:Singulink.Net.Http.Api.Client.UploadSource.ReleaseAsync*> once the chunks are sent and when the upload fails, which closes the stream of <xref:Singulink.Net.Http.Api.Client.UploadSource.FromStreamFactory*>. A read that throws ends the upload, which then aborts the upload on the service and throws the read's exception. An exception of <xref:Singulink.Net.Http.Api.Client.UploadSource.ReleaseAsync*> doesn't change how the upload goes on, and is traced as a warning with <xref:System.Diagnostics.Trace>.

A class derived from <xref:Singulink.Net.Http.Api.Client.UploadSource> implements <xref:Singulink.Net.Http.Api.Client.UploadSource.ReadAsync*>, which returns the number of bytes that it read at an absolute offset, and <xref:Singulink.Net.Http.Api.Client.UploadSource.ReleaseAsync*> if it keeps something open between reads. <xref:Singulink.Net.Http.Api.Client.UploadSource.ReadAsync*> and <xref:Singulink.Net.Http.Api.Client.UploadSource.ReleaseAsync*> are both public, so a source can read and release another source.

#### WebAssembly

The toolkit has no source for browser files. A source made with <xref:Singulink.Net.Http.Api.Client.UploadSource.Create*> reads ranges of a `File` that the app's JavaScript holds, such as one that a file picker returned, here in a module that the app imported as `files`. `slice` reads a range without moving a cursor:

```js
export async function readRangeAsync(fileId, start, length) {
    const file = openFiles.get(fileId);
    if (!file) throw new Error("The file is no longer open.");
    return { data: new Uint8Array(await file.slice(start, start + length).arrayBuffer()) };
}
```

```csharp
[JSImport("readRangeAsync", "files")]
private static partial Task<JSObject> ReadRangeAsync(int fileId, [JSMarshalAs<JSType.Number>] long start, int length);

private static UploadSource CreateSource(int fileId, string name, long size, string? type) =>
    UploadSource.Create(name, size, async (offset, buffer, cancellationToken) => {
        using var range = await ReadRangeAsync(fileId, offset, buffer.Length).WaitAsync(cancellationToken);
        byte[] bytes = range.GetPropertyAsByteArray("data")!;
        bytes.CopyTo(buffer);
        return bytes.Length;
    }, type);
```

Chrome rejects reads of a picked file that has changed since, even one whose modification time alone has changed, with a `NotReadableError`, which fails the upload.

## Options

<xref:Singulink.Net.Http.Api.Client.UploadOptions> sets how an upload runs:

| Option | Default | Meaning |
| --- | --- | --- |
| <xref:Singulink.Net.Http.Api.Client.UploadOptions.ChunkSize> | The service's | The chunk size to ask the service for, which it keeps within its bounds. |
| <xref:Singulink.Net.Http.Api.Client.UploadOptions.MaxConcurrency> | 1 | How many chunks are sent at the same time, from 1 to 4. |
| <xref:Singulink.Net.Http.Api.Client.UploadOptions.ChunkTimeout> | 5 minutes | How long the request of a chunk can go without sending any of the chunk's bytes, or, after the last of them, without its response. In the browser, which sends the content of a request at once, and on Android and iOS, whose HTTP handlers read the content of a request into memory before they send it, in effect how long the request can take, which a 5 MiB chunk takes at about 140 kbit/s. A chunk whose request times out is sent again. |
| <xref:Singulink.Net.Http.Api.Client.UploadOptions.RetryTimeout> | 5 minutes | How long after the last request of the upload that succeeded the requests that fail for a reason that may not last are made again. |
| <xref:Singulink.Net.Http.Api.Client.UploadOptions.CompletionIdleTimeout> | 60 seconds | How long the completion can send nothing, not even a ping, before it is made again. The service pings every 10 seconds by default. |
| <xref:Singulink.Net.Http.Api.Client.UploadOptions.Progress> | None | The progress that the upload reports to (see [Progress](#progress)). |
| <xref:Singulink.Net.Http.Api.Client.UploadOptions.TimeProvider> | <xref:System.TimeProvider.System?displayProperty=nameWithType> | The time provider that measures the retry timeout, the waits before retries, the chunk timeout and the completion idle timeout. |

Each chunk that is being sent is held in a buffer of its size from the shared array pool. In the browser, a chunk also passes through a few copies of about its size on its way to `fetch`, so the memory that an upload needs there is a few times the chunk size times <xref:Singulink.Net.Http.Api.Client.UploadOptions.MaxConcurrency>, whatever the size of the file.

## Progress

The upload reports an <xref:Singulink.Net.Http.Api.Client.UploadProgress> to <xref:Singulink.Net.Http.Api.Client.UploadOptions.Progress> whenever its progress changes:

- <xref:Singulink.Net.Http.Api.Client.UploadProgress.BytesUploaded> counts the bytes of the chunks that the service has acknowledged. On platforms other than the browser, it also counts the bytes that the requests of the chunks being sent have written to the connection so far, in steps of 64 KiB, so it moves within a chunk. In the browser, which reads the content of a request into memory before it sends any of it, only acknowledged chunks count. On Android and iOS, whose HTTP handlers read the content of a request into memory before they send it too, a chunk counts in full once the handler has read it, before its bytes go out.
- <xref:Singulink.Net.Http.Api.Client.UploadProgress.Length> is the length of the file.
- <xref:Singulink.Net.Http.Api.Client.UploadProgress.IsProcessing> is `true` once the service has every chunk and the upload is being completed, while the handler runs.

The bytes of a request that fails are no longer counted, so <xref:Singulink.Net.Http.Api.Client.UploadProgress.BytesUploaded> goes back when a chunk is sent again, and when the service reports chunks missing that it had acknowledged. The upload reports from the threads that send its requests and read their responses, one report at a time: a report that takes long holds up only the thread that makes it, and when the progress changes meanwhile, the latest progress is reported next. A <xref:System.Progress`1> reports on the synchronization context of the thread that created it. A report that throws fails the upload: nothing more is reported, the upload is aborted on the service, and the upload throws the report's exception. The handler's own progress, such as how far a conversion has come, arrives as items of <xref:Singulink.Net.Http.Api.Client.ApiClientBase.UploadStreamingAsync*>.

## Failures, Retries and Recovery

#### Requests that are made again

A request that fails for one of these reasons is made again after a wait:

- a failure that <xref:Singulink.Net.Http.Api.Client.ApiFailures.IsTransient*?displayProperty=nameWithType> counts as transient, such as a connection that failed, a timeout, a `503` or a gateway's `502`, `504` or `520` to `524`;
- a `408` (Request Timeout), which the service sends for a chunk that arrives too slowly;
- a `429` (Too Many Requests) with a <xref:Singulink.Net.Http.Api.ApiException.RetryAfter>, such as one from a rate limit.

The wait is the failure's <xref:Singulink.Net.Http.Api.ApiException.RetryAfter> if it has one, and otherwise 1 second after the first failure of the request, doubling up to 30 seconds, each reduced by up to half at random so that clients that failed together don't come back together. Failures are retried until no request of the upload has succeeded for <xref:Singulink.Net.Http.Api.Client.UploadOptions.RetryTimeout>, and a failure whose wait would end after that is thrown right away. A `429` without a <xref:Singulink.Net.Http.Api.ApiException.RetryAfter>, such as <xref:Singulink.Net.Http.Api.UploadErrorCodes.LimitReached>, is thrown without a retry.

#### The completion

The handler keeps running when the connection drops. When the completion's stream ends early, or sends nothing for <xref:Singulink.Net.Http.Api.Client.UploadOptions.CompletionIdleTimeout>, the completion is made again, which waits for the handler that is running instead of starting another, or returns its outcome if it has finished. Its items then start again with the latest one that the handler produced, so <xref:Singulink.Net.Http.Api.Client.ApiClientBase.UploadStreamingAsync*> can yield the last item again. The time that the app takes with an item doesn't count as silence, and an item that arrives just as the stream is found silent is left to the completion that is made again. An override of <xref:Singulink.Net.Http.Api.Client.ApiClientBase.SendStreamingAsync*> that sends another request in place of the completion's doesn't change this.

When the service answers the completion with a `409` with <xref:Singulink.Net.Http.Api.UploadErrorCodes.Incomplete>, the client reads the upload's status, reads and sends the chunks that it lists as missing, and completes the upload again, up to five times. A handler that fails with a `503` (<xref:Singulink.Net.Http.Api.ServiceUnavailableApiException>) is retried like any other request, which runs the handler again. A completion counts as a success of the upload's requests, for the retry timeout and the waits, when its stream delivers items or pings and then breaks off, since the handler runs. A stream that ends with an error of the service, such as the handler's `503`, doesn't count, items or not, since the handler that runs again starts over, except for `upload-server-stopping` and `upload-store-unavailable`, which say nothing about the handler: a completion that waited with pings for a handler on another server is made again after them, also past the retry timeout. A handler that was interrupted, such as by a restart of the service, fails the upload for good with <xref:Singulink.Net.Http.Api.UploadErrorCodes.Interrupted>.

#### Errors

Other failures are thrown as the exception types of any request (see [Exception Handling](exception-handling.md)), with the error codes of <xref:Singulink.Net.Http.Api.UploadErrorCodes> for the errors of the protocol:

- The validator's errors are thrown by the request that creates the upload, and the handler's errors by the completion, whether the stream had started or not.
- A file that is too large for the endpoint throws a <xref:Singulink.Net.Http.Api.ContentTooLargeApiException> with <xref:Singulink.Net.Http.Api.UploadErrorCodes.TooLarge>, one larger than a user's open uploads can be together with <xref:Singulink.Net.Http.Api.UploadErrorCodes.ExceedsSizeLimit>, and an upload that the service no longer has, such as one that expired, a <xref:Singulink.Net.Http.Api.NotFoundApiException> with <xref:Singulink.Net.Http.Api.UploadErrorCodes.NotFound>.
- A source that fails to read throws its exception, such as an <xref:System.IO.IOException> for a file that changed, or ended before its length.

## Cancellation and Aborting

When the upload is cancelled, its source fails to read, a progress report throws, or a request fails for good or for longer than the retry timeout, the client aborts the upload with a `DELETE` to `{path}/uploads/{id}`, which has a timeout of 10 seconds of its own, and throws: once the abort has ended for a failure, and right away for a cancellation, while the abort goes on. A cancelled upload throws an <xref:System.OperationCanceledException>, also when a request failed at the same time, whose exception is then its inner exception. Aborting cancels the handler if it is running and deletes what the service has received. The client doesn't abort an upload whose failure is a `401`, `412` or `428`, which the `DELETE` would get too, or a `404` with <xref:Singulink.Net.Http.Api.UploadErrorCodes.NotFound>. An upload that isn't aborted is removed by the service when it expires. That includes an upload whose creation got no response, also when the upload is cancelled while it is created, since the client doesn't know its ID: the service removes it after <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.UnstartedExpiry?displayProperty=nameWithType>, 15 minutes by default.

```csharp
try
{
    var info = await client.UploadFileAsync(folderId, file, progress, cancellationToken);
}
catch (OperationCanceledException)
{
    // The upload is aborted on the service.
}
catch (Exception ex) when (ApiFailures.IsTransient(ex))
{
    ShowMessage("The upload could not reach the service. Try again later.");
}
```

## Next Steps

Continue with these related articles:

- [Chunked Uploads](uploads.md) - The service side: upload endpoints, validators, handlers and the storage of uploads.
- [Upload Format](../concepts/upload-format.md) - The requests and responses of the protocol.
- [Building API Clients](api-clients.md) - Request methods, sessions and child clients.
- [Streaming Responses](streaming-responses.md) - The streaming format of the completion's items.
- [Exception Handling](exception-handling.md) - The exception types and error codes that requests throw.

</div>
