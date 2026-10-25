<div class="article">

# Error Response Format

This article documents how errors are encoded on the wire for regular responses, streaming responses and SignalR hubs, so that clients built without the toolkit can interoperate and so the behavior of the toolkit's client is predictable.

### Design goals

Errors should be readable in any HTTP tool, carry a message intended for the user, and optionally carry a machine-readable code, without a JSON envelope that every client must parse. The result is a plain text body with the status code doing most of the work.

## Regular Responses

An error is reported with the HTTP status code of the <xref:Singulink.Net.Http.Api.ApiException> and a `text/plain` body in UTF-8 containing the message:

```text
HTTP/1.1 404 Not Found
Content-Type: text/plain; charset=utf-8

The document does not exist.
```

When the exception has an <xref:Singulink.Net.Http.Api.ApiException.ErrorCode>, the content type gains a `format=error-code` parameter and the body is prefixed with the code in square brackets:

```text
HTTP/1.1 401 Unauthorized
Content-Type: text/plain; charset=utf-8; format=error-code

[email-confirmation-required] Please confirm your email address before signing in.
```

Error codes consist of ASCII letters, digits, hyphens and underscores.

When the exception has a <xref:Singulink.Net.Http.Api.ApiException.RetryAfter>, the response has a `Retry-After` header with it in whole seconds, rounded up, and at most 2147483647 (the largest 32-bit integer):

```text
HTTP/1.1 503 Service Unavailable
Content-Type: text/plain; charset=utf-8; format=error-code
Retry-After: 10

[cloudflare-ranges-unavailable] The service is starting. Please try again shortly.
```

When the exception has a <xref:Singulink.Net.Http.Api.ApiException.ReferenceId>, such as the ID that an unexpected exception was logged with, the response has a `Reference-Id` header with it, a GUID in its hyphenated form:

```text
HTTP/1.1 500 Internal Server Error
Content-Type: text/plain; charset=utf-8
Reference-Id: 0192f5a0-7c3e-7d3a-9f1e-3b2a1c0d4e5f

An unexpected error has occurred and has been logged. Reference ID: 0192f5a0-7c3e-7d3a-9f1e-3b2a1c0d4e5f.
```

Browsers only let a page read the `Retry-After` and `Reference-Id` headers of a response from another origin if CORS exposes them, as <xref:Singulink.Net.Http.Api.Service.WebApplicationExtensions.UseAllowedOrigins*> does, along with `Location`.

#### Client interpretation

The toolkit's client applies these rules to any `4xx` or `5xx` response:

1. If the media type is `text/plain`, whatever the other parameters of the content type, the body is the message, read in the content type's charset, or in UTF-8 without one. With `format=error-code`, the leading bracketed code is split off.
2. The status code selects the exception type: 400, 401, 403, 404, 409, 412, 413, 415, 422, 428, 429, 500 and 503 map to their dedicated types (see [Exception Handling](../guides/exception-handling.md)), and any other status produces the base <xref:Singulink.Net.Http.Api.ApiException>.
3. A non-text body (for example an HTML error page from a proxy) produces the exception type for its status with a message that names the status, such as "The service returned an error (502 Bad Gateway).", and the raw body and content type are available through <xref:Singulink.Net.Http.Api.ApiException.ErrorContent>. An empty message gets the same message, and a `format=error-code` body that doesn't start with a valid code in brackets is handled the same way.
4. A `Retry-After` header, as a number of seconds or an HTTP date, sets <xref:Singulink.Net.Http.Api.ApiException.RetryAfter>, whatever the status.
5. A `Reference-Id` header with a GUID sets <xref:Singulink.Net.Http.Api.ApiException.ReferenceId>, whatever the status and the body. A header that isn't a GUID is ignored.
6. The exception for a `412` gets the user ID precondition of the request, if it has one, as its <xref:Singulink.Net.Http.Api.UserChangedApiException.ExpectedUserId>, from the request rather than the response, which doesn't carry it. So does the exception for a `412` error record of a streaming response, and for a `412` hub error of a hub connection that an API client created, with the precondition of the connection.

A `3xx` response, which the client doesn't follow except on browser platforms, produces the base <xref:Singulink.Net.Http.Api.ApiException> with its status and a message that names the status, whatever the body. The body is available through <xref:Singulink.Net.Http.Api.ApiException.ErrorContent> and the `Location` header through <xref:Singulink.Net.Http.Api.ApiException.Location>.

## Streaming Responses

Once a streaming response has started, the status code is already sent, so an error is written as the final record of the stream:

```json
{"error":{"status":403,"code":"denied","message":"You do not have permission to edit this document."}}
```

The `code` property is omitted when there is no error code, the `referenceId` property, which has the reference ID as in the `Reference-Id` header, when there is no reference ID, and the `retryAfter` property, which has the retry time in whole seconds as in the `Retry-After` header, when there is no retry time:

```json
{"error":{"status":503,"code":"busy","message":"The service is busy. Please try again shortly.","retryAfter":30}}
```

The reader throws the same exception type the status would produce for a regular response, with <xref:Singulink.Net.Http.Api.ApiException.ReferenceId> and <xref:Singulink.Net.Http.Api.ApiException.RetryAfter> from the record. A negative `retryAfter` is ignored. A record whose `code` isn't a valid code produces the exception type for its status with a message that names the status, as an error response that isn't in the toolkit's format does, with the record's message in <xref:Singulink.Net.Http.Api.ApiException.ErrorContent>. An error thrown before the first record is written is reported as a regular error response instead. See [Streaming Response Format](streaming-format.md) for the complete record set.

## SignalR Hubs

SignalR reports failures to clients through a `HubException` message string. When <xref:Singulink.Net.Http.Api.Service.ApiExceptionHubFilter> is registered, an error is encoded into that string as the status code, the bracketed code (empty brackets when there is none) and the message:

```text
403 [denied] You do not have permission to edit this document.
```

A reference ID follows the code as `[ref:{GUID}]`:

```text
500 [] [ref:0192f5a0-7c3e-7d3a-9f1e-3b2a1c0d4e5f] An unexpected error has occurred and has been logged. Reference ID: 0192f5a0-7c3e-7d3a-9f1e-3b2a1c0d4e5f.
```

A retry time follows the code, and the reference ID if there is one, as `[retry:{seconds}]`, in whole seconds rounded up as in the `Retry-After` header:

```text
503 [busy] [retry:30] The service is busy. Please try again shortly.
```

SignalR prefixes the string with its own description, for example `An unexpected error occurred invoking 'Subscribe' on the server. HubException: 403 [denied] ...`, and the toolkit's <xref:Singulink.Net.Http.Api.Client.ApiHubConnection> locates and parses the encoded part. Messages that are not in this format, such as SignalR's own "method not found" errors, are left as `HubException` instances, as are messages whose bracketed code isn't a valid code. A `[ref:]` or `[retry:]` marker whose value is not valid is left in the message.

## Unexpected Exceptions

Exceptions that are not <xref:Singulink.Net.Http.Api.ApiException> instances and are not mapped by the application are reported as a 500 with an empty message outside of Development, so no internal details leak. With the default <xref:Singulink.Net.Http.Api.Service.ApiExceptionHandler> registered, the error instead has a reference ID, in its message and as its <xref:Singulink.Net.Http.Api.ApiException.ReferenceId>, which also appears in the service's log entry for the exception, so a user's report can be matched with the log without exposing anything else. In Development, streaming and hub errors include the exception type, message and stack trace, and regular responses propagate to the developer exception page.

## Further Reading

These articles cover the related topics in more depth:

- [Exception Handling](../guides/exception-handling.md) - Throwing, mapping and catching errors.
- [Streaming Response Format](streaming-format.md) - The other record types in a stream.

</div>
