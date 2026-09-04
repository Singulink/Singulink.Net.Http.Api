namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Maps exceptions thrown while processing API requests to the errors reported to clients. Used by <see cref="ApiExceptionMiddleware"/> for regular
/// responses and by streaming responses for exceptions thrown after the stream has started, so a single implementation provides consistent error handling
/// (e.g. logging unexpected exceptions and reporting a reference ID) for both.
/// </summary>
/// <remarks>
/// <see cref="ApiExceptionHandler"/> provides the recommended default behavior and can be registered directly via
/// <see cref="ServiceCollectionExtensions.AddApiExceptionHandler(IServiceCollection)"/> or derived from to add application-specific mappings. If no handler is
/// registered, <see cref="ApiException"/> instances are reported as-is and other exceptions propagate through the request pipeline (or, for streaming
/// responses that have already started, are reported as a generic server error, with details included only in development environments).
/// </remarks>
public interface IApiExceptionHandler
{
    /// <summary>
    /// Maps the specified exception to the <see cref="ApiException"/> that should be reported to the client.
    /// </summary>
    /// <param name="httpContext">The HTTP context of the request that threw the exception.</param>
    /// <param name="exception">The exception that was thrown.</param>
    /// <returns>
    /// The <see cref="ApiException"/> to report (which may be <paramref name="exception"/> itself if it is an <see cref="ApiException"/>), or
    /// <see langword="null"/> to apply the default handling described in the type remarks.
    /// </returns>
    ValueTask<ApiException?> HandleAsync(HttpContext httpContext, Exception exception);
}
