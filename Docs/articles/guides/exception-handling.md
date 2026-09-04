<div class="article">

# Exception Handling

Errors travel between service and client as typed exceptions. This guide covers throwing them on the service, mapping unexpected exceptions with a handler, and catching them on the client.

### The exception hierarchy

<xref:Singulink.Net.Http.Api.ApiException> carries an HTTP status code, a message intended for the user, and an optional <xref:Singulink.Net.Http.Api.ApiException.ErrorCode> for clients that need to react to specific conditions programmatically. Derived types exist for the statuses the conventions use:

| Exception | Status | Typical use |
| --- | --- | --- |
| <xref:Singulink.Net.Http.Api.BadRequestApiException> | 400 | Malformed request. |
| <xref:Singulink.Net.Http.Api.UnauthorizedApiException> | 401 | Not signed in, or credentials rejected. |
| <xref:Singulink.Net.Http.Api.ForbiddenApiException> | 403 | Signed in but not permitted, or blocked cross-origin request. |
| <xref:Singulink.Net.Http.Api.NotFoundApiException> | 404 | The resource does not exist (or is hidden from the user). |
| <xref:Singulink.Net.Http.Api.UserChangedApiException> | 412 | The user ID precondition does not match the session user. |
| <xref:Singulink.Net.Http.Api.ValidationApiException> | 422 | Input failed validation. |
| <xref:Singulink.Net.Http.Api.UserRequiredApiException> | 428 | The user ID precondition is missing. |
| <xref:Singulink.Net.Http.Api.ServerErrorApiException> | 500 | Unexpected failure, reported with a reference ID. |

Any other status is reported as the base <xref:Singulink.Net.Http.Api.ApiException> with the status code set. The wire format is described in [Error Response Format](../concepts/error-format.md).

## Throwing Errors on the Service

Throw the exception that matches the condition. The message is sent to the client verbatim, so write it for the user:

```csharp
if (!await HasPermissionAsync(token.UserId, document))
    throw new ForbiddenApiException("You do not have permission to edit this document.");
```

Set <xref:Singulink.Net.Http.Api.ApiException.ErrorCode> when a client needs to distinguish a case beyond the status code, such as offering a specific recovery action:

```csharp
throw new UnauthorizedApiException("Please confirm your email address before signing in.") {
    ErrorCode = "email-confirmation-required",
};
```

Error codes consist of ASCII letters, digits, hyphens and underscores. Define them as constants in a data contracts assembly so both sides refer to the same strings.

<xref:Singulink.Net.Http.Api.Service.ApiExceptionMiddleware>, installed by <xref:Singulink.Net.Http.Api.Service.WebApplicationExtensions.UseApiResponseHandling*>, catches exceptions from endpoints and writes the error response. Exceptions thrown after a streaming response has started are reported inside the stream instead; see [Streaming Responses](streaming-responses.md).

## Mapping Exceptions with a Handler

An <xref:Singulink.Net.Http.Api.Service.IApiExceptionHandler> decides how every exception is reported. It is consulted by the middleware, by streaming responses, and by the SignalR hub filter, so one implementation gives consistent behavior everywhere. With no handler registered, <xref:Singulink.Net.Http.Api.ApiException> instances are reported as-is and anything else propagates through the pipeline.

<xref:Singulink.Net.Http.Api.Service.ApiExceptionHandler> is the recommended base. Its default behavior:

- <xref:Singulink.Net.Http.Api.ApiException> instances are reported unchanged.
- <xref:Singulink.Net.Http.Api.Service.ApiExceptionHandler.MapAsync*> is called for other exceptions so an application can map its own types.
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

<xref:Singulink.Net.Http.Api.Service.ApiExceptionHandler.HandleUnexpectedAsync*>, <xref:Singulink.Net.Http.Api.Service.ApiExceptionHandler.LogUnexpected*> and <xref:Singulink.Net.Http.Api.Service.ApiExceptionHandler.PropagateUnexpectedExceptions> are also virtual for deeper customization. Implement <xref:Singulink.Net.Http.Api.Service.IApiExceptionHandler> directly only if none of the base behavior applies.

> [!TIP]
> Because the handler logs through <xref:Microsoft.Extensions.Logging.ILogger>, unexpected exceptions land in the same place as framework logging. A file sink such as Serilog with a rolling file and structured properties makes the reference ID greppable, and the client shows the same ID to the user.

## Catching Errors on the Client

<xref:Singulink.Net.Http.Api.Client.ApiClientBase.SendAsync*> throws the exception type that matches the response status, with the message and error code from the response. Catch the types the calling code can act on and let the rest propagate to a general handler:

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
    ShowError(ex.Message);   // includes the reference ID
}
```

Two exceptions deserve special handling in most clients:

- <xref:Singulink.Net.Http.Api.UnauthorizedApiException> on a request that previously worked means the session ended (expired, signed out elsewhere, or invalidated). The client's stored session token has already been cleared by the time the exception is thrown, so navigating to sign-in is usually all that is needed.
- <xref:Singulink.Net.Http.Api.UserChangedApiException> and <xref:Singulink.Net.Http.Api.UserRequiredApiException> indicate the client's notion of the current user is stale. Reload the current session and rebuild any user-scoped state.

Error responses that are not in the toolkit's format (for example a proxy error page) surface as the base <xref:Singulink.Net.Http.Api.ApiException> with the raw body available through <xref:Singulink.Net.Http.Api.ApiException.ErrorContent>.

## Next Steps

Continue with these related articles:

- [Error Response Format](../concepts/error-format.md) - The wire format for regular, streaming and hub errors.
- [Streaming Responses](streaming-responses.md) - How errors are reported after a stream has started.
- [SignalR Hubs](signalr-hubs.md) - Reporting hub exceptions with the same types.

</div>
