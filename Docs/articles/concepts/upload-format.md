<div class="article">

# Upload Format

This article specifies the requests and responses of chunked uploads, so that clients built without the toolkit can upload files to an upload endpoint, and so that the behavior that the toolkit's client relies on is explicit.

### Overview

An upload endpoint at `{path}`, such as `folders/5/files`, has these requests:

| Request | Body | Success |
| --- | --- | --- |
| `POST {path}/uploads` | JSON creation | `201 Created`, or `200 OK` for a repeated creation, with the upload as JSON |
| `PUT {path}/uploads/{id}` | A chunk's bytes | `204 No Content` |
| `GET {path}/uploads/{id}` | none | `200 OK` with the upload's status as JSON |
| `POST {path}/uploads/{id}/complete` | none | `200 OK` with a [streaming response](streaming-format.md) |
| `DELETE {path}/uploads/{id}` | none | `204 No Content`, also for an upload that does not exist yet, or `202 Accepted` while the handler runs (see [Cancelling an Upload](#cancelling-an-upload)) |
| `POST {path}/uploads/direct` | The file | `201 Created`, or `200 OK` for a repeated request, with a [streaming response](streaming-format.md), as the completion's |

Every request is a request of a signed-in user, with the session cookie and the user ID precondition as for any endpoint, so each can also fail with `401` (`sign-in-required`), `403` (`origin-blocked`), `412` and `428` (see [Error Response Format](error-format.md)). An upload belongs to the user who created it, and to the endpoint and route values it was created at: every later request for it is a request of that user, to that `{path}`. The client chooses the upload's `{id}`, a GUID, in its creation (see [Creating an Upload](#creating-an-upload)), and IDs are the user's: uploads of different users can have the same ID. The creation and the completion take the app's query string, which the endpoint's validator and handler bind, and the client sends the same one to both. A file can also be uploaded directly, in one request that creates and completes the upload (see [Direct Uploads](#direct-uploads)).

JSON is UTF-8 with camelCase property names, and properties whose value is `null` can be left out. Errors are in the toolkit's [error format](error-format.md), and the errors of uploads have the error codes of <xref:Singulink.Net.Http.Api.UploadErrorCodes> (see [Errors](#errors)).

## Creating an Upload

The body of `POST {path}/uploads` is an <xref:Singulink.Net.Http.Api.DataContracts.CreateUploadCommand>, with the content type `application/json`:

```json
{"id":"0f8fad5b-d9cb-469f-a165-70867728950e","fileName":"video.mp4","length":12345678,"contentType":"video/mp4","chunkSize":5242880,"command":{"folderId":5}}
```

| Property | Required | Meaning |
| --- | --- | --- |
| `id` | no | The upload's ID, a GUID other than the empty one, which the client chooses at random, so that the creation made again gets the same upload, and the client can cancel the upload before the service has answered (see below). Without it, the service chooses one. |
| `fileName` | yes | The file's name, of 1 to 255 characters, not only white space. |
| `length` | yes | The file's length in bytes, 0 or more. The upload has exactly this many bytes. |
| `contentType` | no | The file's content type, of up to 255 characters. |
| `chunkSize` | no | The chunk size the client asks for, more than 0. The service keeps it within its bounds, which are always within 5 MiB to 64 MiB. Without it, the service chooses its default. |
| `command` | no | The app's command, any JSON value, which the endpoint reads with its own contract. |
| `idempotencyKey` | no | The key of 1 to 128 characters of a 4.x client, which sends no `id`: the service makes the upload's ID from it, so that the creation made again gets the same upload. A creation with an `id` ignores it. |

The body can be up to 64 KiB long.

The response is an <xref:Singulink.Net.Http.Api.DataContracts.UploadSession>, with a `Location` header with the upload's path:

```text
HTTP/1.1 201 Created
Content-Type: application/json; charset=utf-8
Location: /folders/5/files/uploads/0f8fad5b-d9cb-469f-a165-70867728950e

{"id":"0f8fad5b-d9cb-469f-a165-70867728950e","chunkSize":5242880,"expiresUtc":"2026-10-24T12:30:00Z"}
```

`id` is the upload ID: the creation's, or one that the service chose, a GUID in its 36-character form. `chunkSize` is the chunk size the service chose, which can differ from the one the client asked for, and `expiresUtc` the time the upload expires if no chunk arrives before it.

A creation with the `id` of an upload of the user, for the same endpoint, route values, query string (other than the user ID precondition), file name, length, content type and command, gets that upload, with `200 OK`, which can be receiving chunks, running its handler or finished, as its status says. With another endpoint, query string, file or command, it fails with `409` (`upload-id-conflict`), or `409` (`upload-key-conflict`) for an ID made from an `idempotencyKey`. Of creations with the same ID, also at the same time, one creates the upload. While an upload with the ID is being removed, such as one that was cancelled a moment ago, the service waits for a few seconds, and then fails with `503` (`upload-creation-pending`) and a `Retry-After` header. An ID is free again once its upload is deleted, or once the upload has finished and its outcome is no longer kept. An `id` that is not a GUID fails with `400` (`upload-creation-unreadable`), and the empty GUID with `400` (`upload-invalid`).

## Sending Chunks

The file is sent in chunks of `chunkSize` bytes, except the last, which has the rest of the file. Chunk `i` starts at byte `i * chunkSize`. A file of length 0 has no chunks.

Each chunk is a `PUT {path}/uploads/{id}` with a `Content-Range` header that covers exactly the chunk, and the chunk's bytes as the body, with the content type `application/octet-stream`:

```text
PUT /folders/5/files/uploads/q8Xb2v...?if-userId=42 HTTP/1.1
Content-Type: application/octet-stream
Content-Range: bytes 5242880-10485759/12345678
Content-Length: 5242880

(the chunk's bytes)
```

The range is `bytes {first}-{last}/{length}`, where `first` is the chunk's first byte, `last` its last byte and `length` the file's length. The body has exactly the bytes of the range, and a `Content-Length` header, if the request has one, has their number. The response is `204 No Content` once the chunk is stored.

Sending a chunk again stores it again, so a chunk whose response did not arrive is sent again. A chunk that is sent again is missing until it is stored, and stays missing if its request fails. Chunks can be sent in any order, and several at once. A chunk that arrives once the handler's run has started fails with `409` (`upload-closed`), also while it is being stored, and the upload's file stays as it was: the client then completes the upload, which attaches to the run or gets its outcome. The service reads the body of a chunk that it rejects before it has stored it, if it is no longer than 64 MiB, for up to a minute, before it answers, so that a client that is still sending the body gets the error rather than a closed connection.

## Status

`GET {path}/uploads/{id}` returns an <xref:Singulink.Net.Http.Api.DataContracts.UploadStatus>:

```json
{"state":"Receiving","length":12345678,"chunkSize":5242880,"missingChunks":[{"first":0,"last":0}],"expiresUtc":"2026-10-24T12:30:00Z"}
```

| Property | Meaning |
| --- | --- |
| `state` | `Receiving` while the upload receives chunks, `Processing` while its handler runs, and `Completed` once the handler has finished and its outcome is kept. |
| `length` | The file's length. |
| `chunkSize` | The chunk size. |
| `missingChunks` | The ranges of the indexes of the chunks that have not arrived, in ascending order, with `first` and `last` included. Empty once all have arrived. |
| `expiresUtc` | While the upload is receiving chunks, the time it expires if no chunk arrives before it. While its handler runs, the time the handler is stopped if it is still running. Once it has finished, the time its outcome is no longer kept. |

## Completing an Upload

`POST {path}/uploads/{id}/complete` runs the endpoint's handler once all chunks have arrived. A chunk that is being stored, also one that is sent again, is missing until it is stored, so a completion meanwhile fails with `409` (`upload-incomplete`). Its query string must be the one of the creation, other than the user ID precondition, and a completion with another one fails with `409` (`upload-query-mismatch`). The response is a [streaming response](streaming-format.md): the handler's items, or its single result as one item, then the end record. While the handler produces nothing, the response has a ping record at the endpoint's ping interval, 10 seconds by default, and the first ping starts the response, with its headers.

The handler runs at most once. A completion request for an upload whose handler is running, or has finished, does not run it again:

- While the handler runs on the server that the request reaches, the response has the latest item, then the items that follow, and the end. A client that reads more than 16 items behind the handler misses the oldest of them.
- While it runs on another server, the response has ping records until it has finished, then its outcome. If the server that the request reaches stops meanwhile, the request fails with `503` (`upload-server-stopping`) and a `Retry-After` header.
- Once it has finished, the response has its outcome: its last item, if it produced one, and the end record, or its error.

The outcome is kept for an hour by default. So a client whose completion response broke off, or never arrived, makes the request again and gets the run's items or its outcome, however often it does.

The handler keeps running when the client disconnects. An upload whose handler stops before it finishes, because the service stopped or failed or the handler ran past its time limit, ends with a `500` with the error code `upload-interrupted`, which is its outcome: the handler does not run again, and the file is uploaded again to retry. A handler that reports that it cannot run at the moment, with a `503`, leaves the upload with all its chunks and its file, so the next completion request runs it again.

An error before the response's first record is a regular error response, and one after it an error record, as in any streaming response.

## Direct Uploads

`POST {path}/uploads/direct` uploads a file directly, in one request: it creates an upload, stores its body as the upload's one chunk, and completes the upload. Its query string is the app's, as the creation's and the completion's, and the values of a creation are in a header, as the same JSON as the body of a creation:

```text
POST /folders/5/files/uploads/direct?if-userId=42 HTTP/1.1
Content-Type: application/octet-stream
Content-Length: 2345678
Upload-Creation: eyJmaWxlTmFtZSI6InBob3RvLmpwZyIsImxlbmd0aCI6MjM0NTY3OH0

(the file's bytes)
```

| Header | Required | Meaning |
| --- | --- | --- |
| `Upload-Creation` | yes | The creation: its JSON in UTF-8, base64url-encoded with or without padding, of up to 8 KiB: a longer one fails with `431` (`upload-creation-header-too-large`). It has the properties of the body of a creation, and `chunkSize` isn't used. |
| `Content-Length` | no | The file's length, which must be the `length` of the creation. A body sent with chunked transfer encoding has none. |

The `Content-Type` of the request isn't read. The service checks the values, the endpoint's validator and the limits as for a creation before it stores the body, and reads the body of a rejected request, if it is no longer than 64 MiB, for up to a minute, before it answers, as for a chunk. The file can be as long as the service's largest chunk, whose size is set between 5 MiB and 64 MiB and is 64 MiB by default: a longer one fails with `413` (`upload-direct-too-large`), and is uploaded with the requests of a creation, its chunks and a completion.

The response is the completion's [streaming response](streaming-format.md), with `201 Created`, or `200 OK` for a request that got the upload of an earlier one, and a `Location` header with the upload's path, `{path}/uploads/{id}`. It starts with a ping record once the file is stored, so a client has the path before any error of the completion. From then on, the upload is an upload in chunks: a client whose response broke off makes `POST {path}/uploads/{id}/complete`, which gets the run's items or its outcome, and sends the chunk again, of the chunk size of the upload's status, if the completion fails with `409` (`upload-incomplete`).

A request with the ID of an earlier one gets the upload that it created, as a creation does: it stores its own body as the chunk again while the upload receives its file, and attaches to the run or gets the outcome once the handler has started. A request with the ID of an upload in chunks reads its body without storing it, and its response ends with `409` (`upload-incomplete`) while chunks are missing. A body that fails or a client that disconnects leaves the upload as it is, as for a chunk, and a request made again with the ID stores the body again and completes the upload.

A client that got no response makes the request again with the same ID, or cancels the upload with `DELETE {path}/uploads/{id}`, also while the request still runs on the service, or before it reaches the service, such as while a proxy holds it until it has the whole body (see [Cancelling an Upload](#cancelling-an-upload)). An upload whose client vanishes expires like any other.

A service that has no direct uploads answers with a `404` or a `405` without an error of the toolkit, and a server or a proxy in front of a service can answer the request with an error of its own. After a `4xx` that is not an error of the toolkit, other than a `408`, and after a `429` in any format, a client uploads the file with the three requests instead, with the same ID, and a rejection that holds for them too repeats on them.

## Cancelling an Upload

`DELETE {path}/uploads/{id}` cancels the upload. A handler that has returned has finished, and the cancellation doesn't undo it:

- `204 No Content`: the upload does not exist, or its handler isn't running. The upload is deleted, and no handler runs for it. Later requests for the upload fail with `404` (`upload-not-found`). The cancellation is kept for a while, also for an ID that has no upload yet, so a request that creates the upload afterwards, such as a direct upload that arrives late, creates it, and the handler doesn't start: its response ends with `409` (`upload-cancelled`). The cancellation of an ID that has no upload yet is kept for 2 minutes.
- `202 Accepted`: the handler is running, and its cancellation token is cancelled. If the handler stops without finishing, the upload is deleted and the completion requests attached to the run end with `409` (`upload-cancelled`). If it finishes all the same, its outcome stands, and the completion requests get it as usual.
- `409 Conflict` (`upload-finished`): the upload has finished, and its outcome is kept.
- `404 Not Found` (`upload-not-found`): the ID can't be an upload's, as for the other requests of an upload.
- `503 Service Unavailable` (`upload-cancellation-unavailable`): the service could not write the cancellation, and makes no change. With a `Retry-After` header.

After a `202` or a `409`, a client that wants to know how the upload ended makes its completion and reads it to the end: a result or an outcome means that the handler finished, and `409` (`upload-cancelled`) or `404` (`upload-not-found`) that it didn't. After a `204`, it knows that no handler ran.

## Errors

The errors of uploads have these error codes (<xref:Singulink.Net.Http.Api.UploadErrorCodes>):

| Error code | Status | Request | Reported when |
| --- | --- | --- | --- |
| `upload-invalid` | 400 | Creation and direct upload | The ID, file name, length, content type, chunk size, idempotency key or command is not valid, or a command is missing where the endpoint requires one. |
| `upload-creation-unreadable` | 400 | Creation and direct upload | The body, or the `Upload-Creation` header of a direct upload, cannot be read as a creation: it is missing or not valid JSON, it is `null`, or it lacks a value that a creation requires or has one of the wrong type. |
| `upload-creation-not-json` | 415 | Creation | The body is not JSON, by its content type. |
| `upload-creation-too-large` | 413 | Creation | The body is larger than 64 KiB, or than a smaller request size limit of the server. |
| `upload-creation-header-too-large` | 431 | Direct upload | The `Upload-Creation` header is larger than 8 KiB. |
| `upload-too-large` | 413 | Creation | The length is over the endpoint's largest upload or the total size limit of the open uploads, or the upload would need more chunks than the service allows at its largest chunk size. |
| `upload-exceeds-size-limit` | 413 | Creation | The length is over the most that a user's open uploads can have together, so the upload can never be created. |
| `upload-direct-too-large` | 413 | Direct upload | The length is over the largest chunk of the service, which is the most that one request can carry. The file is uploaded in chunks. |
| `upload-limit-reached` | 429 | Creation and direct upload | The user has the most open uploads that the service allows. With a `Retry-After` header of a second. |
| `upload-size-limit-reached` | 429 | Creation and direct upload | The user's open uploads would together be longer than the service allows for a user. With a `Retry-After` header of a second. |
| `upload-storage-full` | 503 | Creation and direct upload | The open uploads have reached their total size limit, or the store has no room for the file. With a `Retry-After` header. |
| `upload-id-conflict` | 409 | Creation and direct upload | The ID is the ID of another upload of the user. |
| `upload-key-conflict` | 409 | Creation | The idempotency key of a 4.x client is the key of another upload of the user. |
| `upload-creation-pending` | 503 | Creation and direct upload | An upload of the user with the ID is still being removed. With a `Retry-After` header. |
| `upload-not-found` | 404 | Chunk, status, completion and direct upload | The upload is unknown, expired or cancelled, or belongs to another user, endpoint or route values. |
| `upload-chunk-invalid` | 400 | Chunk and direct upload | `Content-Range` is missing or does not cover exactly one chunk of the upload, or the body's length differs from it, or the length of a direct upload's body differs from its creation's. |
| `upload-closed` | 409 | Chunk | The upload's handler is running or has finished. |
| `upload-incomplete` | 409 | Completion | Chunks are missing. The status lists them. |
| `upload-query-mismatch` | 409 | Completion | The query string differs from the creation's, other than the user ID precondition. |
| `upload-server-stopping` | 503 | Completion | The server is stopping, while it waits for the handler that runs on another server or before it starts the handler. With a `Retry-After` header. |
| `upload-store-unavailable` | 503 | Any | The service's store of uploads failed, such as a share that the service could not reach for a moment. With a `Retry-After` header. |
| `upload-cancelled` | 409 | Completion | The upload was cancelled while its handler ran, or before it started, and the handler didn't finish. The upload is removed. |
| `upload-finished` | 409 | Cancellation | The upload has finished. Its outcome is kept. |
| `upload-cancellation-unavailable` | 503 | Cancellation and completion | The service could not write or read the upload's cancellation, which it keeps in its short-lived store, such as one that it could not reach for a moment. A completion's handler is not started. With a `Retry-After` header. |
| `upload-interrupted` | 500 | Completion | The handler stopped before it finished. This is the upload's outcome. |
| `upload-outcome-unreadable` | 500 | Completion | The upload's stored outcome cannot be read, such as one that a server left partly written when it stopped while it stored it. This is the upload's outcome. |

The errors of a creation, of a chunk and of a completion are also the errors of a direct upload: error responses until its response starts, and error records after it. Errors of the app's validator, at the creation, and of its handler, at the completion, are the app's own.

## Further Reading

These articles cover the related topics in more depth:

- [Chunked Uploads](../guides/uploads.md) - Upload endpoints on the service.
- [Chunked Uploads in Client Apps](../guides/uploads-client.md) - Sending files with the toolkit's client.
- [Streaming Response Format](streaming-format.md) - The records of the completion's response.

</div>
