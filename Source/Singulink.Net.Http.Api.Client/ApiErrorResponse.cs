using System.Net.Http.Headers;
using System.Text;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Creates HTTP responses in the toolkit's error format, which API clients throw as the exception they were created from.
/// </summary>
public static class ApiErrorResponse
{
    /// <summary>
    /// Creates a response in the toolkit's error format for the specified exception, as a service built with the toolkit writes it: the exception's status
    /// code, its message and error code as <c>text/plain</c> content in UTF-8, a <c>Retry-After</c> header with <see cref="ApiException.RetryAfter"/> in whole
    /// seconds, rounded up, a <c>Reference-Id</c> header with <see cref="ApiException.ReferenceId"/>, and a <c>Location</c> header with
    /// <see cref="ApiException.Location"/>, each if it is set.
    /// </summary>
    /// <param name="exception">The exception to create the response for.</param>
    public static HttpResponseMessage Create(ApiException exception)
    {
        var info = ResponseExceptionInfo.FromApiException(exception);
        var content = new StringContent(info.ToResponseString(), Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(info.ContentType);

        var response = new HttpResponseMessage(exception.StatusCode) { Content = content };

        if (info.RetryAfterSeconds is { } retryAfterSeconds)
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds));

        if (exception.ReferenceId is { } referenceId)
            response.Headers.TryAddWithoutValidation(ResponseExceptionInfo.ReferenceIdHeaderName, referenceId.ToString("D"));

        if (exception.Location is { } location)
            response.Headers.Location = location;

        return response;
    }
}
