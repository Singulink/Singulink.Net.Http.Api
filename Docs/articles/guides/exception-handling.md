<div class="article">

# Exception Handling

Errors travel between service and client as typed exceptions. This guide covers throwing them on the service, mapping unexpected exceptions with a handler, and catching them on the client.

### The exception hierarchy

<xref:Singulink.Net.Http.Api.ApiException> carries an HTTP status code, a message intended for the user, and an optional <xref:Singulink.Net.Http.Api.ApiException.ErrorCode> for clients that need to react to specific conditions programmatically. Derived types exist for the statuses the conventions use:

| Exception | Status | Meaning |
| --- | --- | --- |
| <xref:Singulink.Net.Http.Api.BadRequestApiException> | 400 | Malformed request. |
| <xref:Singulink.Net.Http.Api.UnauthorizedApiException> | 401 | Not signed in, or credentials rejected. |
| <xref:Singulink.Net.Http.Api.ForbiddenApiException> | 403 | Signed in but not permitted, or blocked cross-origin request. |
| <xref:Singulink.Net.Http.Api.NotFoundApiException> | 404 | The resource does not exist (or is hidden from the user). |
| <xref:Singulink.Net.Http.Api.ConflictApiException> | 409 | The request conflicts with the current state of the resource. |
| <xref:Singulink.Net.Http.Api.UserChangedApiException> | 412 | The user ID precondition does not match the session user. |
| <xref:Singulink.Net.Http.Api.ContentTooLargeApiException> | 413 | The request content is larger than the service accepts. |
| <xref:Singulink.Net.Http.Api.UnsupportedMediaTypeApiException> | 415 | The request content type is not supported. |
| <xref:Singulink.Net.Http.Api.ValidationApiException> | 422 | Input failed validation. |
| <xref:Singulink.Net.Http.Api.UserRequiredApiException> | 428 | The user ID precondition is missing. |
| <xref:Singulink.Net.Http.Api.TooManyRequestsApiException> | 429 | The client sent too many requests or attempts. |
| <xref:Singulink.Net.Http.Api.ServerErrorApiException> | 500 | Unexpected failure, reported with a reference ID. |
| <xref:Singulink.Net.Http.Api.ServiceUnavailableApiException> | 503 | The service cannot handle requests at the moment. |

Any other status is reported as the base <xref:Singulink.Net.Http.Api.ApiException> with the status code set. The wire format is described in [Error Response Format](../concepts/error-format.md).

## Throwing Errors on the Service

An endpoint reports an error by throwing the exception for its status. The message is sent to the client verbatim:

```csharp
if (!await HasPermissionAsync(token.UserId, document))
    throw new ForbiddenApiException("You do not have permission to edit this document.");
```

<xref:Singulink.Net.Http.Api.ApiException.ErrorCode> tells apart cases with the same status:

```csharp
throw new UnauthorizedApiException("Please confirm your email address before signing in.") {
    ErrorCode = "email-confirmation-required",
};
```

Error codes consist of ASCII letters, digits, hyphens and underscores.

<xref:Singulink.Net.Http.Api.ApiException.RetryAfter> tells the client how long to wait before making the request again. It is sent in whole seconds, rounded up: in the `Retry-After` header, and in the errors of streaming responses and hubs (see [Error Response Format](../concepts/error-format.md)):

```csharp
throw new TooManyRequestsApiException("Too many attempts. Please try again in a few minutes.") {
    RetryAfter = TimeSpan.FromMinutes(5),
};
```

<xref:Singulink.Net.Http.Api.Service.ApiExceptionMiddleware>, installed by <xref:Singulink.Net.Http.Api.Service.WebApplicationExtensions.UseApiResponseHandling*>, catches exceptions from endpoints and writes the error response. Exceptions thrown after a streaming response has started are reported inside the stream instead; see [Streaming Responses](streaming-responses.md).

<xref:Singulink.Net.Http.Api.Service.HttpResponseExtensions.WriteApiErrorAsync*> writes an <xref:Singulink.Net.Http.Api.ApiException> to a response in the same format, `Retry-After` header included, without throwing it. The toolkit's [rate limit](rate-limiting.md) rejections and its [Cloudflare](cloudflare.md) middleware write their errors with it.

## Mapping Exceptions with a Handler

An <xref:Singulink.Net.Http.Api.Service.IApiExceptionHandler> decides how every exception is reported. It is consulted by the middleware, by streaming responses, and by the SignalR hub filter, so one implementation gives consistent behavior everywhere. With no handler registered, <xref:Singulink.Net.Http.Api.ApiException> instances are reported as-is, requests that could not be read are reported as described in [Requests That Cannot Be Read](#requests-that-cannot-be-read), and anything else propagates through the pipeline.

<xref:Singulink.Net.Http.Api.Service.ApiExceptionHandler> is a handler with this behavior:

- <xref:Singulink.Net.Http.Api.ApiException> instances are reported as <xref:Singulink.Net.Http.Api.Service.ApiExceptionHandler.MapApiExceptionAsync*> returns them, unchanged by default.
- <xref:Singulink.Net.Http.Api.Service.ApiExceptionHandler.MapAsync*> is called for other exceptions so an application can map its own types.
- A `BadHttpRequestException`, thrown for a request that could not be read, is reported with its status code (see [Requests That Cannot Be Read](#requests-that-cannot-be-read)).
- Anything still unmapped is treated as unexpected: a reference ID is generated, the exception is logged through <xref:Microsoft.Extensions.Logging.ILogger> with the request method, path and reference ID as structured properties, and a <xref:Singulink.Net.Http.Api.ServerErrorApiException> whose message includes the reference ID is reported. The original exception is attached as the inner exception.
- In the Development environment unexpected exceptions propagate instead, so the developer exception page can display them.

Register the default behavior with <xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddApiExceptionHandler*>, or derive from the base class to add mappings:

```csharp
public sealed class AppExceptionHandler(ILogger<AppExceptionHandler> logger, IHostEnvironment environment)
    : ApiExceptionHandler(logger, environment)
{
    protected override ValueTask<ApiException?> MapAsync(HttpContext httpContext, Exception exception)
    {
        return new(exception is ValidationException ex ? new ValidationApiException(string.Join("\n", ex.Errors)) : null);
    }

    protected override string CreateServerErrorMessage(Guid referenceId) =>
        $"Something went wrong on our end. Please contact support and quote reference {referenceId}.";
}
```

```csharp
services.AddApiExceptionHandler<AppExceptionHandler>();
```

An override of <xref:Singulink.Net.Http.Api.Service.ApiExceptionHandler.MapApiExceptionAsync*> rewords an <xref:Singulink.Net.Http.Api.ApiException> by its error code and returns any other as it is. <xref:Singulink.Net.Http.Api.ApiException.WithMessage*> copies an exception with another message. The copy keeps the status code, the error code, the `Retry-After`, the reference ID, the error content, the location, the data and the inner exception. It is of the toolkit's exception type for the status code, also when the exception is of a type derived from it, whose own members it doesn't keep. It has no stack trace, and doesn't keep `HResult`, `HelpLink` or `Source`:

```csharp
protected override ValueTask<ApiException> MapApiExceptionAsync(HttpContext httpContext, ApiException exception) => new(exception.ErrorCode switch {
    "document-locked" => exception.WithMessage("Someone else is editing this document."),
    _ => exception,
});
```

The toolkit's client acts on the status code, the error code and the `Retry-After` of the toolkit's errors, such as a completion of an upload that fails with `upload-incomplete`, after which it sends the missing chunks, and handles an exception that is reported without them as one without a code.

<xref:Singulink.Net.Http.Api.Service.ApiExceptionHandler.HandleUnexpectedAsync*>, <xref:Singulink.Net.Http.Api.Service.ApiExceptionHandler.LogUnexpected*> and <xref:Singulink.Net.Http.Api.Service.ApiExceptionHandler.PropagateUnexpectedExceptions> are virtual too. An app can also implement <xref:Singulink.Net.Http.Api.Service.IApiExceptionHandler> itself.

## Requests That Cannot Be Read

ASP.NET Core rejects requests that a minimal API endpoint cannot read: a malformed JSON body, a value of the wrong type, a missing or unreadable route or query value, or an unsupported content type. Which properties a body must have and which can be `null` is up to the app's JSON options: a missing `required` property is always rejected, a missing constructor parameter only with `RespectRequiredConstructorParameters`, and a `null` for a property that is not nullable only with `RespectNullableAnnotations`. <xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddApiJson*> turns both on, and makes required members that are nullable optional (see [JSON Serialization](json-serialization.md)). By default ASP.NET Core answers such a request with an empty `400` or `415` response in production and shows the developer exception page in development, so a client would get an error without a message.

<xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddApiExceptionHandler*> configures `RouteHandlerOptions.ThrowOnBadRequest` to be `true`, so that endpoints throw a `BadHttpRequestException` for these requests in every environment, and the middleware reports it with its status code and a message:

```text
HTTP/1.1 400 Bad Request
Content-Type: text/plain

The request is not valid. Path: $.count | LineNumber: 0 | BytePositionInLine: 42.
```

ASP.NET Core's and System.Text.Json's messages name the service's types, so outside the Development environment the message is a general one for the status: "The request is not valid.", "The request is too large." or "The request was not received in time.". For an error in JSON it is followed by the location that System.Text.Json gives, as above. A `415` names the request's content type instead, as in `The request content type "text/plain" is not supported.`, or says that the request has none. In the Development environment the message is ASP.NET Core's, followed by System.Text.Json's for an error in JSON. A <xref:Singulink.Net.Http.Api.BadRequestApiException> that an endpoint throws keeps its message. The original exception is kept as the inner exception.

The client throws it as the exception type for its status: a <xref:Singulink.Net.Http.Api.BadRequestApiException> for a `400`, a <xref:Singulink.Net.Http.Api.ContentTooLargeApiException> for a `413` and an <xref:Singulink.Net.Http.Api.UnsupportedMediaTypeApiException> for a `415`. Other statuses that ASP.NET Core reports, such as `408` for a request body that arrives too slowly, are passed through and thrown as the base <xref:Singulink.Net.Http.Api.ApiException>. Two requests that ASP.NET Core rejects with an empty response rather than an exception are reported in the same format: a body over the request size limit (`413`) and a content type that the endpoint does not accept (`415`).

The messages are written for developers. <xref:Singulink.Net.Http.Api.Service.ApiExceptionHandler.MapAsync*> can map `BadHttpRequestException` to other wording.

Options configurations run in the order they are registered, so the service's own configuration of the option applies when it is registered after <xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddApiExceptionHandler*>, and is overridden when it is registered before. A service that sets it to `false` after the call keeps ASP.NET Core's empty responses. A service without a handler gets the same reports by setting it to `true` itself:

```csharp
services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
```

## Catching Errors on the Client

<xref:Singulink.Net.Http.Api.Client.ApiClientBase.SendAsync*?displayProperty=nameWithType> throws the exception type that matches the response status, with the message and error code from the response. <xref:Singulink.Net.Http.Api.ApiException.RetryAfter> is set from the response's `Retry-After` header, as a number of seconds or an HTTP date, on any error response that has one, and <xref:Singulink.Net.Http.Api.ApiException.ReferenceId> from its `Reference-Id` header, such as the ID that the service's exception handler logged an unexpected exception with (see [Error Response Format](../concepts/error-format.md)). Calling code can catch the types it acts on, such as here for a sign-in:

```csharp
try
{
    await client.SignInAsync(command);
}
catch (UnauthorizedApiException ex) when (ex.ErrorCode == ErrorCodes.EmailConfirmationRequired)
{
    OfferToResendConfirmation();
}
catch (UnauthorizedApiException ex)
{
    ShowError(ex.Message);
}
catch (ServerErrorApiException ex)
{
    ShowError(ex.Message, ex.ReferenceId);
}
```

These errors concern the session rather than the request:

- <xref:Singulink.Net.Http.Api.UnauthorizedApiException> with the `sign-in-required` error code (<xref:Singulink.Net.Http.Api.ApiErrorCodes.SignInRequired>) means the request has no session: it ended (expired, signed out elsewhere, or invalidated), or there was none. When the exception is thrown, the client has already ended the session on the device, for requests, streaming responses and hub connections alike, unless the token has changed since the request or connection used it, as it has for a hub connection once a request has refreshed the session (see [Session Persistence](api-clients.md#session-persistence)).
- Other `401` errors, such as a rejected sign-in credential, leave the session as it is, except one that the session store throws to end a session (see [Ending Sessions](session-handling.md#ending-sessions)): its response clears the cookie, which ends the session on the device. A hub connection or a streaming response that has started can't clear the cookie, and the error has the store's error code, if any, rather than `sign-in-required`, so the session stays on the device until a later request finds it ended.
- <xref:Singulink.Net.Http.Api.UserChangedApiException> and <xref:Singulink.Net.Http.Api.UserRequiredApiException> mean that the user the client made the request for is not the session's user, or that the request didn't say which user it was made for. The <xref:Singulink.Net.Http.Api.UserChangedApiException.ExpectedUserId> of a <xref:Singulink.Net.Http.Api.UserChangedApiException> is the user ID precondition of the request or hub connection, the user it was made for, which the client sets from what it sent: the error doesn't say whose session the service found.

#### Transient failures

<xref:Singulink.Net.Http.Api.Client.ApiFailures.IsTransient*?displayProperty=nameWithType> tells whether a request or hub connection failed because the service could not be reached or could not answer for the moment, so that making it again may work: the request could not be sent or its response could not be read in full, it timed out, a hub connection's WebSocket could not connect, the service answered with a `503`, or a gateway in front of it, such as a reverse proxy or Cloudflare, answered with a `502`, `504` or `520` to `524` without an error code. Other errors aren't transient by this measure, including a `408` (Request Timeout), which ASP.NET Core sends for a request body that arrives too slowly, and a `429` (Too Many Requests), whose <xref:Singulink.Net.Http.Api.ApiException.RetryAfter> says how long to wait. In the browser, a request that cannot reach the service throws an <xref:System.Net.Http.HttpRequestException> whose message is the browser's, such as "TypeError: Failed to fetch".

```csharp
try
{
    await client.SaveDocumentAsync(document);
}
catch (Exception ex) when (ApiFailures.IsTransient(ex))
{
    if (await AskToRetryAsync("The service could not be reached."))
        await client.SaveDocumentAsync(document);
}
```

#### Responses not in the toolkit's format

Error responses that are not in the toolkit's format (for example a proxy error page) surface as the exception type for their status, such as a <xref:Singulink.Net.Http.Api.ServiceUnavailableApiException> for a `503`, with a message that names the status, here "The service returned an error (503 Service Unavailable).", and the raw body available through <xref:Singulink.Net.Http.Api.ApiException.ErrorContent>. Clients don't follow redirects, except on browser platforms, so a `3xx` response is thrown the same way, as an <xref:Singulink.Net.Http.Api.ApiException>. The message of a redirect names the scheme, host, port and path of the address it leads to, and <xref:Singulink.Net.Http.Api.ApiException.Location> has the whole address, query included.

## Error Codes

The errors that the toolkit reports itself carry an error code. The codes are constants of <xref:Singulink.Net.Http.Api.ApiErrorCodes>, <xref:Singulink.Net.Http.Api.SignInErrorCodes>, <xref:Singulink.Net.Http.Api.PasskeyErrorCodes> and <xref:Singulink.Net.Http.Api.UploadErrorCodes> in the shared `Singulink.Net.Http.Api` package, which clients reference too:

| Error code | Constant | Status | Reported when |
| --- | --- | --- | --- |
| `sign-in-required` | <xref:Singulink.Net.Http.Api.ApiErrorCodes.SignInRequired> | 401 | A required session token is read, for example for a session token parameter that is not nullable, and the request has no valid session. |
| `origin-blocked` | <xref:Singulink.Net.Http.Api.ApiErrorCodes.OriginBlocked> | 403 | The session is accessed from an origin that is not allowed. |
| `user-agent-required` | <xref:Singulink.Net.Http.Api.ApiErrorCodes.UserAgentRequired> | 400 | The session needs the device, to sign in or to validate the session, and the request has no `User-Agent` header or an empty one. |
| `rate-limited` | <xref:Singulink.Net.Http.Api.ApiErrorCodes.RateLimited> | 429 | A rate limit rejected the request (see [Rate Limiting](rate-limiting.md#rejection-responses)). |
| `short-lived-store-full` | <xref:Singulink.Net.Http.Api.ApiErrorCodes.ShortLivedStoreFull> | 503 | The memory short-lived store has no room for the state the request needs (see [The memory store's size](sign-in-codes.md#the-memory-stores-size)). |
| `cloudflare-ranges-unavailable` | <xref:Singulink.Net.Http.Api.ApiErrorCodes.CloudflareRangesUnavailable> | 503 | The Cloudflare forwarding middleware refuses the request because Cloudflare's IP ranges are not loaded yet (see [Requests before the ranges are available](cloudflare.md#requests-before-the-ranges-are-available)). |
| `sign-in-code-invalid` | <xref:Singulink.Net.Http.Api.SignInErrorCodes.SignInCodeInvalid> | 401 | A sign-in code is unknown, expired or used up, or its verifier is wrong (see [Sign-In Codes](sign-in-codes.md#sign-in-codes)). |
| `code-challenge-invalid` | <xref:Singulink.Net.Http.Api.SignInErrorCodes.CodeChallengeInvalid> | 400 | A PKCE code challenge is malformed, or a passkey sign-in begins without one while one is required. |
| `device-link-too-many-wrong-codes` | <xref:Singulink.Net.Http.Api.SignInErrorCodes.DeviceLinkTooManyWrongCodes> | 429 | A user entered too many wrong device link codes (see [Device Linking](sign-in-codes.md#device-linking)). |
| `device-link-expired` | <xref:Singulink.Net.Http.Api.SignInErrorCodes.DeviceLinkExpired> | 401 | A new device checks the status of a device link that is unknown or expired, or with a wrong poll secret (see [Device Linking](sign-in-codes.md#device-linking)). |
| `passkey-ceremony-expired` | <xref:Singulink.Net.Http.Api.PasskeyErrorCodes.CeremonyExpired> | 401 or 422 | A passkey ceremony is unknown, expired, already finished or for someone else (see [Passkeys](passkeys.md#errors)). |
| `passkey-code-verifier-mismatch` | <xref:Singulink.Net.Http.Api.PasskeyErrorCodes.CodeVerifierMismatch> | 401 | A passkey sign-in finishes without the code verifier that matches its code challenge. |
| `passkey-none` | <xref:Singulink.Net.Http.Api.PasskeyErrorCodes.NoPasskeys> | 401 or 409 | A passkey sign-in or verification begins for a user who has no passkeys. |
| `passkey-not-registered` | <xref:Singulink.Net.Http.Api.PasskeyErrorCodes.NotRegistered> | 401 or 422 | The passkey is not registered. |
| `passkey-wrong-account` | <xref:Singulink.Net.Http.Api.PasskeyErrorCodes.WrongAccount> | 401 or 422 | The passkey is registered to a different user than the one a sign-in or verification is for. |
| `passkey-verification-failed` | <xref:Singulink.Net.Http.Api.PasskeyErrorCodes.VerificationFailed> | 401 or 422 | A credential fails verification. |
| `passkey-already-registered` | <xref:Singulink.Net.Http.Api.PasskeyErrorCodes.AlreadyRegistered> | 409 | A new passkey is already registered. |
| `upload-invalid` | <xref:Singulink.Net.Http.Api.UploadErrorCodes.Invalid> | 400 | The file name, length, content type, chunk size, idempotency key or command of an upload's creation is not valid, or the command is missing where the endpoint requires one (see [Chunked Uploads](uploads.md)). |
| `upload-creation-unreadable` | <xref:Singulink.Net.Http.Api.UploadErrorCodes.CreationUnreadable> | 400 | The body of an upload's creation cannot be read as a creation. |
| `upload-creation-not-json` | <xref:Singulink.Net.Http.Api.UploadErrorCodes.CreationNotJson> | 415 | The body of an upload's creation is not JSON. |
| `upload-creation-too-large` | <xref:Singulink.Net.Http.Api.UploadErrorCodes.CreationTooLarge> | 413 | The body of an upload's creation is larger than 64 KiB. |
| `upload-too-large` | <xref:Singulink.Net.Http.Api.UploadErrorCodes.TooLarge> | 413 | An upload is larger than its endpoint allows, or would need more chunks than the service allows at its largest chunk size. |
| `upload-exceeds-size-limit` | <xref:Singulink.Net.Http.Api.UploadErrorCodes.ExceedsSizeLimit> | 413 | An upload is larger than the open uploads of a user can be together, so it can never be created. |
| `upload-limit-reached` | <xref:Singulink.Net.Http.Api.UploadErrorCodes.LimitReached> | 429 | A user who creates an upload already has the most open uploads that the service allows. |
| `upload-size-limit-reached` | <xref:Singulink.Net.Http.Api.UploadErrorCodes.SizeLimitReached> | 429 | The open uploads of a user who creates an upload would together be longer than the service allows for a user. |
| `upload-storage-full` | <xref:Singulink.Net.Http.Api.UploadErrorCodes.StorageFull> | 503 | The open uploads have reached their total size limit, or the upload store has no room for a new upload. |
| `upload-key-conflict` | <xref:Singulink.Net.Http.Api.UploadErrorCodes.KeyConflict> | 409 | The idempotency key of an upload's creation is the key of another upload of the user. |
| `upload-creation-pending` | <xref:Singulink.Net.Http.Api.UploadErrorCodes.CreationPending> | 503 | An earlier creation with the idempotency key of an upload's creation has not created its upload yet, or a stale reservation of the key can't be released yet. |
| `upload-not-found` | <xref:Singulink.Net.Http.Api.UploadErrorCodes.NotFound> | 404 | An upload is unknown, expired or aborted, or belongs to another user, endpoint or route values. |
| `upload-chunk-invalid` | <xref:Singulink.Net.Http.Api.UploadErrorCodes.ChunkInvalid> | 400 | A chunk's `Content-Range` is missing or does not cover exactly one chunk of its upload, or its body's length differs from it. |
| `upload-closed` | <xref:Singulink.Net.Http.Api.UploadErrorCodes.Closed> | 409 | A chunk arrives for an upload whose handler is running or has finished. |
| `upload-incomplete` | <xref:Singulink.Net.Http.Api.UploadErrorCodes.Incomplete> | 409 | An upload is completed while chunks of it are missing. |
| `upload-query-mismatch` | <xref:Singulink.Net.Http.Api.UploadErrorCodes.QueryMismatch> | 409 | The completion of an upload has another query string than its creation, other than the user ID precondition. |
| `upload-server-stopping` | <xref:Singulink.Net.Http.Api.UploadErrorCodes.ServerStopping> | 503 | The server that a completion reached is stopping, while the completion waits for the handler that runs on another server or before it starts the handler. |
| `upload-store-unavailable` | <xref:Singulink.Net.Http.Api.UploadErrorCodes.StoreUnavailable> | 503 | The store of uploads failed, such as a share that the service could not reach for a moment. |
| `upload-interrupted` | <xref:Singulink.Net.Http.Api.UploadErrorCodes.Interrupted> | 500 | An upload's handler stopped before it finished, because the service stopped or failed, its server lost the claim of its run, or it ran past its time limit. This is the upload's final outcome. |
| `upload-outcome-unreadable` | <xref:Singulink.Net.Http.Api.UploadErrorCodes.OutcomeUnreadable> | 500 | An upload's stored outcome cannot be read, such as a file of the directory store that was damaged. This is the upload's final outcome. |

Passkey errors are `401` in a sign-in and `422` in a verification or registration, which runs in a signed-in session. A verification for a user without passkeys and a passkey that is already registered are `409`. An interrupted upload is a `500`, which is not a transient failure (see <xref:Singulink.Net.Http.Api.Client.ApiFailures.IsTransient*?displayProperty=nameWithType>), since the upload's handler does not run again. Other errors that the toolkit reports, such as a request that cannot be read or a user ID precondition that does not match, are told apart by their status and have no error code.

## Next Steps

Continue with these related articles:

- [Error Response Format](../concepts/error-format.md) - The wire format for regular, streaming and hub errors.
- [Streaming Responses](streaming-responses.md) - How errors are reported after a stream has started.
- [SignalR Hubs](signalr-hubs.md) - Reporting hub exceptions with the same types.

</div>
