using System.Diagnostics;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Shared exception mapping used by <see cref="ApiExceptionMiddleware"/> and streaming responses.
/// </summary>
internal static class ApiExceptionHandling
{
    /// <summary>
    /// Maps an exception to the <see cref="ApiException"/> to report using the registered <see cref="IApiExceptionHandler"/> (if any). Returns
    /// <see langword="null"/> if the exception is not an <see cref="ApiException"/> or a <see cref="BadHttpRequestException"/> and no handler mapped it.
    /// </summary>
    public static async ValueTask<ApiException?> ResolveAsync(HttpContext httpContext, Exception exception)
    {
        var handler = httpContext.RequestServices.GetService<IApiExceptionHandler>();
        var apiException = handler is null ? null : await handler.HandleAsync(httpContext, exception);

        apiException ??= exception switch {
            ApiException e => e,
            BadHttpRequestException e => BadRequestMapping.ToApiException(
                httpContext, e, httpContext.RequestServices.GetService<IHostEnvironment>()?.IsDevelopment() is true),
            _ => null,
        };

        if (handler is null && apiException is not null && Trace.Listeners.Count > 0)
            Trace.TraceWarning($"[Singulink.Net.Http.Api] Expected API exception handled ({apiException.StatusCode}): {apiException}");

        return apiException;
    }

    /// <summary>
    /// Traces an exception that could not be reported to the client.
    /// </summary>
    public static void TraceSuppressed(Exception exception, string context)
    {
        if (Trace.Listeners.Count > 0)
            Trace.TraceWarning($"[Singulink.Net.Http.Api] Unexpected exception suppressed ({context}): {exception}");
    }
}
