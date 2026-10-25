namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Middleware that handles exceptions thrown during request processing by reporting them to the client as API errors. Exceptions are mapped using the
/// registered <see cref="IApiExceptionHandler"/> (if any); otherwise <see cref="ApiException"/> instances are reported as-is, requests that ASP.NET Core
/// could not read (<see cref="BadHttpRequestException"/>) are reported with their status code, and other exceptions propagate. Requests that ASP.NET Core
/// answers with an empty error response, a request body that is too large or a content type the endpoint does not accept, are reported in the same format.
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
        catch (Exception ex) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
        {
            var apiException = await ApiExceptionHandling.ResolveAsync(context, ex);

            if (apiException is null)
                throw;

            await context.Response.WriteApiErrorAsync(apiException);
            return;
        }

        // A request body that is too large (in minimal APIs) and a content type the endpoint does not accept (in routing) get an empty error response
        // rather than an exception, so the error is filled in here.
        if (!context.RequestAborted.IsCancellationRequested && BadRequestMapping.FromEmptyResponse(context) is { } emptyResponseException)
            await context.Response.WriteApiErrorAsync(emptyResponseException);
    }
}
