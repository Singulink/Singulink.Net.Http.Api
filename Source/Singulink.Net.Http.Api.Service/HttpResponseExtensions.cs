using System.Globalization;
using System.Text;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Extension methods for <see cref="HttpResponse"/>.
/// </summary>
public static class HttpResponseExtensions
{
    /// <summary>
    /// Writes an API exception as the response in the toolkit's error format, which clients built with the toolkit throw as the same exception type: the
    /// exception's status code, its message and error code as <c>text/plain</c> content in UTF-8, a <c>Retry-After</c> header with
    /// <see cref="ApiException.RetryAfter"/> in whole seconds, rounded up, if it is set, and a <c>Reference-Id</c> header with
    /// <see cref="ApiException.ReferenceId"/> if it is set.
    /// </summary>
    /// <param name="response">The response to write.</param>
    /// <param name="exception">The exception to write.</param>
    /// <param name="cancellationToken">A token to cancel writing the content.</param>
    /// <exception cref="InvalidOperationException">The response has already started.</exception>
    public static Task WriteApiErrorAsync(this HttpResponse response, ApiException exception, CancellationToken cancellationToken = default)
    {
        if (response.HasStarted)
            throw new InvalidOperationException("The response has already started.");

        var info = ResponseExceptionInfo.FromApiException(exception);

        response.StatusCode = info.StatusCode;
        response.ContentType = info.ContentType;

        if (info.RetryAfterSeconds is { } retryAfterSeconds)
            response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

        if (exception.ReferenceId is { } referenceId)
            response.Headers[ResponseExceptionInfo.ReferenceIdHeaderName] = referenceId.ToString("D");

        return response.WriteAsync(info.ToResponseString(), Encoding.UTF8, cancellationToken);
    }
}
