using System.Diagnostics;
using Microsoft.Extensions.Hosting;

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
    /// <param name="httpContext">The HTTP context of the request that threw the exception.</param>
    /// <param name="exception">The exception that was thrown.</param>
    /// <param name="traceExpected">Whether an API exception that is resolved without a handler is traced, as it is unless its error isn't reported.</param>
    public static async ValueTask<ApiException?> ResolveAsync(HttpContext httpContext, Exception exception, bool traceExpected = true)
    {
        var handler = httpContext.RequestServices.GetService<IApiExceptionHandler>();
        var apiException = handler is null ? null : await handler.HandleAsync(httpContext, exception);

        apiException ??= exception switch {
            ApiException e => e,
            BadHttpRequestException e => BadRequestMapping.ToApiException(
                httpContext, e, httpContext.RequestServices.GetService<IHostEnvironment>()?.IsDevelopment() is true),
            _ => null,
        };

        if (traceExpected && handler is null && apiException is not null && Trace.Listeners.Count > 0)
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
