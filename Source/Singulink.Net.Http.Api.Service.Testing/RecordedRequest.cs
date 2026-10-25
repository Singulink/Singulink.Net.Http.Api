using System.Net.Http;
using System.Net.Http.Headers;

namespace Singulink.Net.Http.Api.Service.Testing;

/// <summary>
/// Represents a request that a <see cref="FaultInjectionHandler"/> received, as it was when the handler received it.
/// </summary>
public sealed class RecordedRequest
{
    internal RecordedRequest(HttpRequestMessage request)
    {
        Method = request.Method;
        RequestUri = request.RequestUri;
        ContentRange = request.Content?.Headers.ContentRange is { } range ? (ContentRangeHeaderValue)((ICloneable)range).Clone() : null;
    }

    /// <summary>
    /// Gets the request's HTTP method.
    /// </summary>
    public HttpMethod Method { get; }

    /// <summary>
    /// Gets the request's URI.
    /// </summary>
    public Uri? RequestUri { get; }

    /// <summary>
    /// Gets the <c>Content-Range</c> header of the request's content, such as the byte range of a chunk of an upload, or <see langword="null"/> if it has
    /// none.
    /// </summary>
    public ContentRangeHeaderValue? ContentRange { get; }
}
