<div class="article">

# Error Response Format

This article documents how errors are encoded on the wire for regular responses, streaming responses and SignalR hubs, so that clients built without the toolkit can interoperate and so the behavior of the toolkit's client is predictable.

### Design goals

Errors should be readable in any HTTP tool, carry a message intended for the user, and optionally carry a machine-readable code, without a JSON envelope that every client must parse. The result is a plain text body with the status code doing most of the work.

## Regular Responses

An error is reported with the HTTP status code of the <xref:Singulink.Net.Http.Api.ApiException> and a `text/plain` body containing the message:

```text
HTTP/1.1 404 Not Found
Content-Type: text/plain

The document does not exist.
```

When the exception has an <xref:Singulink.Net.Http.Api.ApiException.ErrorCode>, the content type gains a `format=error-code` parameter and the body is prefixed with the code in square brackets:

```text
HTTP/1.1 401 Unauthorized
Content-Type: text/plain;format=error-code

[email-confirmation-required] Please confirm your email address before signing in.
```

Error codes consist of ASCII letters, digits, hyphens and underscores.

#### Client interpretation

The toolkit's client applies these rules to any non-2xx response:

1. If the content type is `text/plain`, the body is the message. With `format=error-code`, the leading bracketed code is split off.
2. The status code selects the exception type: 400, 401, 403, 404, 412, 422, 428 and 500 map to their dedicated types (see [Exception Handling](../guides/exception-handling.md)), and any other status produces the base <xref:Singulink.Net.Http.Api.ApiException>.
3. A non-text body (for example an HTML error page from a proxy) produces the base <xref:Singulink.Net.Http.Api.ApiException> with a generic message, and the raw body and content type are available through <xref:Singulink.Net.Http.Api.ApiException.ErrorContent>.

## Streaming Responses

Once a streaming response has started, the status code is already sent, so an error is written as the final record of the stream:

```json
{"error":{"status":403,"code":"denied","message":"You do not have permission to edit this document."}}
```

The `code` property is omitted when there is no error code. The reader throws the same exception type the status would produce for a regular response. An error thrown before the first record is written is reported as a regular error response instead. See [Streaming Response Format](streaming-format.md) for the complete record set.

## SignalR Hubs

SignalR reports failures to clients through a `HubException` message string. When <xref:Singulink.Net.Http.Api.Service.ApiExceptionHubFilter> is registered, an error is encoded into that string as the status code, the bracketed code (empty brackets when there is none) and the message:

```text
403 [denied] You do not have permission to edit this document.
```

SignalR prefixes the string with its own description, for example `An unexpected error occurred invoking 'Subscribe' on the server. HubException: 403 [denied] ...`, and the toolkit's <xref:Singulink.Net.Http.Api.Client.ApiHubConnection> locates and parses the encoded part. Messages that are not in this format, such as SignalR's own "method not found" errors, are left as `HubException` instances.

## Unexpected Exceptions

Exceptions that are not <xref:Singulink.Net.Http.Api.ApiException> instances and are not mapped by the application are reported as a 500 with an empty message outside of Development, so no internal details leak. With the default <xref:Singulink.Net.Http.Api.Service.ApiExceptionHandler> registered, the message instead contains a reference ID that also appears in the service's log entry for the exception, giving support a way to correlate a user's report with the log without exposing anything else. In Development, streaming and hub errors include the exception type, message and stack trace, and regular responses propagate to the developer exception page.

## Further Reading

These articles cover the related topics in more depth:

- [Exception Handling](../guides/exception-handling.md) - Throwing, mapping and catching errors.
- [Streaming Response Format](streaming-format.md) - The other record types in a stream.

</div>
