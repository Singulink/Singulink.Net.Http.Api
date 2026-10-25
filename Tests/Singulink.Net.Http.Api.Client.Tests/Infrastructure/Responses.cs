using System.Net.Http.Headers;
using System.Text;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Creates the responses that a service built with the toolkit produces.
/// </summary>
public static class Responses
{
    public static HttpResponseMessage NoContent() => new(HttpStatusCode.NoContent);

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    public static HttpResponseMessage Text(string text, HttpStatusCode status = HttpStatusCode.OK) => new(status) {
        Content = new StringContent(text, Encoding.UTF8, "text/plain"),
    };

    public static HttpResponseMessage Content(HttpStatusCode status, string content, string contentType)
    {
        var httpContent = new StringContent(content, Encoding.UTF8);
        httpContent.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        return new HttpResponseMessage(status) { Content = httpContent };
    }

    public static HttpResponseMessage Error(HttpStatusCode status, string message) => Content(status, message, "text/plain; charset=utf-8");

    public static HttpResponseMessage Error(HttpStatusCode status, string errorCode, string message) =>
        Content(status, $"[{errorCode}] {message}", "text/plain; charset=utf-8; format=error-code");

    /// <summary>
    /// Creates a streaming response with the specified JSON records, one per line.
    /// </summary>
    public static HttpResponseMessage Stream(params string[] records) =>
        Content(HttpStatusCode.OK, string.Concat(records.Select(r => r + "\n")), StreamingResponse.ContentType);

    public static HttpResponseMessage WithCookie(this HttpResponseMessage response, string setCookie)
    {
        response.Headers.TryAddWithoutValidation("Set-Cookie", setCookie);
        return response;
    }

    public static HttpResponseMessage WithHeader(this HttpResponseMessage response, string name, string value)
    {
        response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }

    public static HttpResponseMessage WithRetryAfter(this HttpResponseMessage response, string retryAfter)
    {
        response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return response;
    }
}
