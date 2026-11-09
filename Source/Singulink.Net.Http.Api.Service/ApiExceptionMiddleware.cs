namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Middleware that handles exceptions thrown during request processing by reporting them to the client as API errors. Exceptions are mapped using the
/// registered <see cref="IApiExceptionHandler"/> (if any); otherwise <see cref="ApiException"/> instances are reported as-is, requests that ASP.NET Core
/// could not read (<see cref="BadHttpRequestException"/>) are reported with their status code, and other exceptions propagate. Requests that ASP.NET Core
/// answers with an empty error response, a request body that is too large or a content type the endpoint does not accept, are reported in the same format.
/// When the client has aborted the request before the response started, an <see cref="OperationCanceledException"/> or <see cref="IOException"/> (which
/// includes <see cref="BadHttpRequestException"/>) propagates without the <see cref="IApiExceptionHandler"/> being called. Any other exception is still
/// resolved, through the handler if one is registered: one that resolves to an API error ends the request without a response, and neither the middleware
/// nor the server logs it, and the rest propagate.
/// </summary>
public class ApiExceptionMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiExceptionMiddleware"/> class with the specified request delegate.
    /// </summary>
    public ApiExceptionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    /// <summary>
    /// Invokes the middleware to handle exceptions thrown during request processing.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex) when (!context.Response.HasStarted && !IsCausedByAbort(context, ex))
        {
            bool isAborted = context.RequestAborted.IsCancellationRequested;
            var apiException = await ApiExceptionHandling.ResolveAsync(context, ex, traceExpected: !isAborted);

            if (apiException is null)
                throw;

            // An API error is an intended response, and nobody can receive the response of a request that the client aborted: nothing to write or log.
            if (!isAborted)
                await context.Response.WriteApiErrorAsync(apiException);

            return;
        }

        // A request body that is too large (in minimal APIs) and a content type the endpoint does not accept (in routing) get an empty error response
        // rather than an exception, so the error is filled in here.
        if (!context.RequestAborted.IsCancellationRequested && BadRequestMapping.FromEmptyResponse(context) is { } emptyResponseException)
            await context.Response.WriteApiErrorAsync(emptyResponseException);
    }

    // A cancellation or I/O failure of a request that the client aborted comes from the abort itself. It propagates as it is, without the exception
    // handler, and the server doesn't log it as an error.
    private static bool IsCausedByAbort(HttpContext context, Exception exception) =>
        context.RequestAborted.IsCancellationRequested && exception is OperationCanceledException or IOException;
}
