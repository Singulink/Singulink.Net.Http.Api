namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Middleware that handles exceptions thrown during request processing by reporting them to the client as API errors. Exceptions are mapped using the
/// registered <see cref="IApiExceptionHandler"/> (if any); otherwise <see cref="ApiException"/> instances are reported as-is and other exceptions propagate.
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

            var info = ResponseExceptionInfo.FromApiException(apiException);

            context.Response.ContentType = info.MimeType;
            context.Response.StatusCode = info.StatusCode;

            await context.Response.WriteAsync(info.ToResponseString());
        }
    }
}
