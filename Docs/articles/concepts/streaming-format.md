<div class="article">

# Streaming Response Format

This article specifies the wire format used for endpoints that return <xref:System.Collections.Generic.IAsyncEnumerable`1>, so that other clients can consume it and so the guarantees the toolkit's reader relies on are explicit.

### Why JSON Lines

A JSON array cannot carry anything except its elements, and a partially received array is not valid JSON. Newline-delimited JSON (JSON Lines) makes every record independent: each line is a complete JSON value that can be parsed and acted on as soon as it arrives, and control records such as pings and errors can be interleaved with items. The `application/jsonl` media type is used, with UTF-8 encoding.

## Records

The body is a sequence of JSON objects, one per line, each with exactly one of the following properties:

| Record | Meaning |
| --- | --- |
| `{"item": ...}` | An item produced by the endpoint, serialized with the service's JSON options for the endpoint's declared item type. May be `null` if the endpoint yields a null item. |
| `{"ping": true}` | A keep-alive record sent while the endpoint is waiting for its next item. Readers ignore it. |
| `{"error": {"status": 403, "code": "denied", "message": "..."}}` | An error that terminates the stream. `code` is omitted when there is no error code. |
| `{"end": true}` | Successful completion of the stream. |

A complete successful stream:

```json
{"item":{"folderId":12,"folderName":"Invoices","hits":[...]}}
{"ping":true}
{"item":{"folderId":31,"folderName":"Receipts","hits":[...]}}
{"end":true}
```

Each record is flushed to the transport as soon as it is written, so items reach the client with no buffering delay.

## Guarantees

- **Every successful stream ends with an end record**, and every failed stream ends with an error record. A stream that ends without either was truncated, and the toolkit's reader throws <xref:System.Net.Http.HttpIOException> rather than treating the items received so far as the complete result. This makes truncation detectable independent of the transport, which matters behind proxies that re-chunk responses.
- **Errors before the first record do not use the stream.** The response has not started, so the exception produces a regular error response with the appropriate status code. Clients check the status before reading the body.
- **Errors after the first record preserve the items before them.** The reader yields every item that preceded the error record, then throws.
- **Pings only appear while waiting.** They are sent when the endpoint has produced nothing for the interval configured with <xref:Singulink.Net.Http.Api.Service.KeepAlivePingAttribute>, and never between records that are available immediately.

## Reading the Format

<xref:Singulink.Net.Http.Api.StreamingResponse.ReadItemsAsync*> implements a reader over any <xref:System.IO.Stream> and is what <xref:Singulink.Net.Http.Api.Client.ApiClientBase.SendStreamingAsync*> uses. Clients on other platforms can implement the same rules: read the body line by line, dispatch on the single property present, stop on `end` or `error`, and treat end-of-stream without either as a failure.

## Further Reading

These articles cover the related topics in more depth:

- [Streaming Responses](../guides/streaming-responses.md) - Writing and consuming streaming endpoints.
- [Error Response Format](error-format.md) - How the error record relates to regular error responses.

</div>
