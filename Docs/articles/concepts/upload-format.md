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
| `DELETE {path}/uploads/{id}` | none | `204 No Content`, also for an upload that does not exist |

Every request is a request of a signed-in user, with the session cookie and the user ID precondition as for any endpoint, so each can also fail with `401` (`sign-in-required`), `403` (`origin-blocked`), `412` and `428` (see [Error Response Format](error-format.md)). An upload belongs to the user who created it, and to the endpoint and route values it was created at: every later request for it is a request of that user, to that `{path}`. The creation and the completion take the app's query string, which the endpoint's validator and handler bind, and the client sends the same one to both.

JSON is UTF-8 with camelCase property names, and properties whose value is `null` can be left out. Errors are in the toolkit's [error format](error-format.md), and the errors of uploads have the error codes of <xref:Singulink.Net.Http.Api.UploadErrorCodes> (see [Errors](#errors)).

## Creating an Upload

The body of `POST {path}/uploads` is an <xref:Singulink.Net.Http.Api.DataContracts.CreateUploadCommand>, with the content type `application/json`:

```json
{"fileName":"video.mp4","length":12345678,"contentType":"video/mp4","chunkSize":5242880,"command":{"folderId":5},"idempotencyKey":"Jv2m..."}
```

| Property | Required | Meaning |
| --- | --- | --- |
| `fileName` | yes | The file's name, of 1 to 255 characters, not only white space. |
| `length` | yes | The file's length in bytes, 0 or more. The upload has exactly this many bytes. |
| `contentType` | no | The file's content type, of up to 255 characters. |
| `chunkSize` | no | The chunk size the client asks for, more than 0. The service keeps it within its bounds, which are always within 5 MiB to 64 MiB. Without it, the service chooses its default. |
| `command` | no | The app's command, any JSON value, which the endpoint reads with its own contract. |
| `idempotencyKey` | no | A key of 1 to 128 characters that the client chose for the upload, so that the creation made again gets the same upload (see below). |

The body can be up to 64 KiB long.

The response is an <xref:Singulink.Net.Http.Api.DataContracts.UploadSession>, with a `Location` header with the upload's path:

```text
HTTP/1.1 201 Created
Content-Type: application/json; charset=utf-8
Location: /folders/5/files/uploads/q8Xb2v...

{"id":"q8Xb2v...","chunkSize":5242880,"expiresUtc":"2026-10-24T12:30:00Z"}
```

`id` is the upload ID: 256 random bits as 43 base64url characters. `chunkSize` is the chunk size the service chose, which can differ from the one the client asked for, and `expiresUtc` the time the upload expires if no chunk arrives before it.

A creation with an `idempotencyKey` that an earlier creation of the user had, for the same endpoint, route values, query string (other than the user ID precondition), file name, length, content type and command, gets the upload that the earlier one created, with `200 OK`, which can be receiving chunks, running its handler or finished, as its status says. With another endpoint, query string, file or command, it fails with `409` (`upload-key-conflict`). While the earlier creation has not created its upload yet, or the service can't release a stale reservation of the key yet, the service waits for a few seconds, and then fails with `503` (`upload-creation-pending`) and a `Retry-After` header. Of creations with the same key, also at the same time, one creates the upload. A key is free again once its upload is deleted.

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

Sending a chunk again stores it again, so a chunk whose response did not arrive is sent again. A chunk that is sent again is missing until it is stored, and stays missing if its request fails. Chunks can be sent in any order, and several at once. A chunk that arrives once the handler's run has started fails with `409` (`upload-closed`), also while it is being stored, and the upload's file stays as it was.

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

## Aborting an Upload

`DELETE {path}/uploads/{id}` deletes the upload and answers `204 No Content`, also for an upload that does not exist. While the handler runs, it is stopped. Later requests for the upload fail with `404` (`upload-not-found`), and a completion request that is attached to the running handler ends with that error.

## Errors

The errors of uploads have these error codes (<xref:Singulink.Net.Http.Api.UploadErrorCodes>):

| Error code | Status | Request | Reported when |
| --- | --- | --- | --- |
| `upload-invalid` | 400 | Creation | The file name, length, content type, chunk size, idempotency key or command is not valid, or a command is missing where the endpoint requires one. |
| `upload-creation-unreadable` | 400 | Creation | The body cannot be read as a creation: it is not valid JSON, it is `null`, or it lacks a value that a creation requires or has one of the wrong type. |
| `upload-creation-not-json` | 415 | Creation | The body is not JSON, by its content type. |
| `upload-creation-too-large` | 413 | Creation | The body is larger than 64 KiB. |
| `upload-too-large` | 413 | Creation | The length is over the endpoint's largest upload, or the upload would need more chunks than the service allows at its largest chunk size. |
| `upload-exceeds-size-limit` | 413 | Creation | The length is over the most that a user's open uploads can have together, so the upload can never be created. |
| `upload-limit-reached` | 429 | Creation | The user has the most open uploads that the service allows. |
| `upload-size-limit-reached` | 429 | Creation | The user's open uploads would together be longer than the service allows for a user. |
| `upload-storage-full` | 503 | Creation | The open uploads have reached their total size limit, or the store has no room for the file. With a `Retry-After` header. |
| `upload-key-conflict` | 409 | Creation | The idempotency key is the key of another upload of the user. |
| `upload-creation-pending` | 503 | Creation | An earlier creation with the idempotency key has not created its upload yet, or a stale reservation of the key can't be released yet. With a `Retry-After` header. |
| `upload-not-found` | 404 | Chunk, status and completion | The upload is unknown, expired or aborted, or belongs to another user, endpoint or route values. |
| `upload-chunk-invalid` | 400 | Chunk | `Content-Range` is missing or does not cover exactly one chunk of the upload, or the body's length differs from it. |
| `upload-closed` | 409 | Chunk | The upload's handler is running or has finished. |
| `upload-incomplete` | 409 | Completion | Chunks are missing. The status lists them. |
| `upload-query-mismatch` | 409 | Completion | The query string differs from the creation's, other than the user ID precondition. |
| `upload-server-stopping` | 503 | Completion | The server is stopping, while it waits for the handler that runs on another server or before it starts the handler. With a `Retry-After` header. |
| `upload-store-unavailable` | 503 | Any | The service's store of uploads failed, such as a share that the service could not reach for a moment. With a `Retry-After` header. |
| `upload-interrupted` | 500 | Completion | The handler stopped before it finished. This is the upload's outcome. |
| `upload-outcome-unreadable` | 500 | Completion | The upload's stored outcome cannot be read, such as a file that was damaged. This is the upload's outcome. |

Errors of the app's validator, at the creation, and of its handler, at the completion, are the app's own.

## Further Reading

These articles cover the related topics in more depth:

- [Chunked Uploads](../guides/uploads.md) - Upload endpoints on the service.
- [Chunked Uploads in Client Apps](../guides/uploads-client.md) - Sending files with the toolkit's client.
- [Streaming Response Format](streaming-format.md) - The records of the completion's response.

</div>
