namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// An HTTP message handler that records requests and answers them with responses produced by a callback, so clients can be tested without a server.
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
    private readonly List<RecordedRequest> _requests = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="FakeHttpMessageHandler"/> class that answers every request with the response produced by the callback,
    /// or with an empty 204 (No Content) response if no callback is provided.
    /// </summary>
    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        respond ??= _ => Responses.NoContent();
        _respond = (request, _) => Task.FromResult(respond(request));
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FakeHttpMessageHandler"/> class that answers every request with the response that the callback
    /// produces asynchronously, with the request's cancellation token.
    /// </summary>
    public FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respondAsync)
    {
        _respond = respondAsync;
    }

    /// <summary>
    /// Gets the requests received so far.
    /// </summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get {
            lock (_requests)
                return [.. _requests];
        }
    }

    /// <summary>
    /// Gets the single request received so far.
    /// </summary>
    public RecordedRequest SingleRequest => Requests.ShouldHaveSingleItem();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Requests are disposed by the client once they are sent, so everything the tests check is captured up front.

#if NET
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
#else
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync();
#endif

        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.TryGetValues("Cookie", out var cookies) ? string.Join("; ", cookies) : null,
            request.Headers.UserAgent.ToString(),
            request.Content?.Headers.ContentType?.MediaType,
            body);

        lock (_requests)
            _requests.Add(recorded);

        // A response can keep a request of its own, as one of a redirect that a handler followed does.
        var response = await _respond(request, cancellationToken);
        response.RequestMessage ??= request;

        return response;
    }
}

/// <summary>
/// A request received by a <see cref="FakeHttpMessageHandler"/>.
/// </summary>
public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Cookie, string UserAgent, string? ContentType, string? Body);
