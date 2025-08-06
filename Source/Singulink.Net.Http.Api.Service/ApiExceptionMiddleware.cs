using System.Diagnostics;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Middleware that handles API exceptions thrown during request processing.
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
    /// Invokes the middleware to handle API exceptions during request processing.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (ApiException ex)
        {
            context.Response.ContentType = "text/plain";
            context.Response.StatusCode = (int)ex.StatusCode;

            if (Trace.Listeners.Count > 0)
                Trace.TraceWarning($"[Singulink.Net.Http.Api] Expected API exception handled ({ex.StatusCode}): {ex}");

            await context.Response.WriteAsync(ex.Message).ConfigureAwait(false);
        }
    }
}
