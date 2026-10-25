using System.Net.Http;

namespace Singulink.Net.Http.Api.Service.Testing;

/// <summary>
/// A message handler for testing API clients that makes chosen requests fail as an unreliable network or service would: before or after they reach the
/// service, with a response of the test's own, or by cutting off a streaming response after a number of records. Other requests are sent to the inner
/// handler, such as the handler of a test server.
/// </summary>
/// <remarks>
/// Each fault applies to the next requests that its predicate matches, as many times as its count, and faults are checked in the order they were added.
/// The failures are exceptions that API clients treat as transient (see <c>ApiFailures.IsTransient</c>).
/// </remarks>
public sealed class FaultInjectionHandler : DelegatingHandler
{
    private readonly Lock _lock = new();
    private readonly List<Fault> _faults = [];
    private readonly List<RecordedRequest> _requests = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="FaultInjectionHandler"/> class without an inner handler, which is set with
    /// <see cref="DelegatingHandler.InnerHandler"/>.
    /// </summary>
    public FaultInjectionHandler()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FaultInjectionHandler"/> class with an inner handler, such as the handler of a test server.
    /// </summary>
    /// <param name="innerHandler">The handler that requests are sent to.</param>
    public FaultInjectionHandler(HttpMessageHandler innerHandler) : base(innerHandler)
    {
    }

    /// <summary>
    /// Gets the requests that the handler received, in the order it received them, whether it failed them or sent them on. They are recorded when they are
    /// received, so they stay readable after the requests are disposed.
    /// </summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get {
            lock (_lock)
                return [.. _requests];
        }
    }

    /// <summary>
    /// Fails the next requests that match before they are sent: they throw an <see cref="HttpRequestException"/> and never reach the service.
    /// </summary>
    /// <param name="predicate">Selects the requests to fail.</param>
    /// <param name="count">The number of requests to fail.</param>
    /// <returns>This handler.</returns>
    public FaultInjectionHandler FailBeforeSending(Func<HttpRequestMessage, bool> predicate, int count = 1)
    {
        return Add(new Fault(FaultKind.BeforeSending, predicate, count));
    }

    /// <summary>
    /// Fails the next requests that match after the service has handled them, as if the connection dropped before the response arrived: they throw an
    /// <see cref="HttpRequestException"/> instead of returning the response.
    /// </summary>
    /// <param name="predicate">Selects the requests to fail.</param>
    /// <param name="count">The number of requests to fail.</param>
    /// <returns>This handler.</returns>
    public FaultInjectionHandler FailAfterSending(Func<HttpRequestMessage, bool> predicate, int count = 1)
    {
        return Add(new Fault(FaultKind.AfterSending, predicate, count));
    }

    /// <summary>
    /// Answers the next requests that match with a response of the test's own instead of sending them, such as a <c>503</c> with a <c>Retry-After</c>
    /// header or a proxy's error page.
    /// </summary>
    /// <param name="predicate">Selects the requests to answer.</param>
    /// <param name="createResponse">Creates the response to a request.</param>
    /// <param name="count">The number of requests to answer.</param>
    /// <returns>This handler.</returns>
    public FaultInjectionHandler Respond(Func<HttpRequestMessage, bool> predicate, Func<HttpRequestMessage, HttpResponseMessage> createResponse, int count = 1)
    {
        return Add(new Fault(FaultKind.Respond, predicate, count) { CreateResponse = createResponse });
    }

    /// <summary>
    /// Cuts off the responses of the next requests that match after the specified number of records, which are the lines of a streaming response, as a
    /// dropped connection would: reading past them throws an <see cref="HttpIOException"/>, and the service sees the client disconnect.
    /// </summary>
    /// <param name="predicate">Selects the requests whose responses are cut off.</param>
    /// <param name="records">The number of records that the response has before it is cut off.</param>
    /// <param name="count">The number of responses to cut off.</param>
    /// <returns>This handler.</returns>
    public FaultInjectionHandler CutResponseAfter(Func<HttpRequestMessage, bool> predicate, int records, int count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(records);
        return Add(new Fault(FaultKind.CutResponse, predicate, count) { Records = records });
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Fault? fault;

        lock (_lock)
        {
            _requests.Add(new RecordedRequest(request));
            fault = _faults.FirstOrDefault(f => f.Remaining > 0 && f.Predicate(request));

            if (fault is not null)
                fault.Remaining--;
        }

        switch (fault?.Kind)
        {
            case FaultKind.BeforeSending:
                throw new HttpRequestException(HttpRequestError.ConnectionError, "The request failed before it was sent (injected fault).");

            case FaultKind.Respond:
                return fault.CreateResponse!(request);
        }

        var response = await base.SendAsync(request, cancellationToken);

        switch (fault?.Kind)
        {
            case FaultKind.AfterSending:
                response.Dispose();
                throw new HttpRequestException(HttpRequestError.ResponseEnded, "The response was lost after the request was sent (injected fault).");

            case FaultKind.CutResponse:
                var content = response.Content;
                var cutContent = new StreamContent(new CutStream(await content.ReadAsStreamAsync(cancellationToken), fault.Records));

                foreach (var header in content.Headers)
                    cutContent.Headers.TryAddWithoutValidation(header.Key, header.Value);

                cutContent.Headers.ContentLength = null;
                response.Content = cutContent;
                break;
        }

        return response;
    }

    private FaultInjectionHandler Add(Fault fault)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fault.Remaining, "count");

        lock (_lock)
            _faults.Add(fault);

        return this;
    }

    private enum FaultKind
    {
        BeforeSending,
        AfterSending,
        Respond,
        CutResponse,
    }

    private sealed class Fault(FaultKind kind, Func<HttpRequestMessage, bool> predicate, int count)
    {
        public FaultKind Kind { get; } = kind;

        public Func<HttpRequestMessage, bool> Predicate { get; } = predicate;

        public int Remaining { get; set; } = count;

        public Func<HttpRequestMessage, HttpResponseMessage>? CreateResponse { get; init; }

        public int Records { get; init; }
    }

    /// <summary>
    /// A response stream that ends with an error after a number of lines, and disposes the stream it reads from then, which the service sees as the client
    /// disconnecting.
    /// </summary>
    private sealed class CutStream(Stream inner, int records) : Stream
    {
        private int _remaining = records;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining is 0)
                throw Cut();

            int read = await inner.ReadAsync(buffer, cancellationToken);

            if (read is 0)
                return 0;

            // The bytes up to the end of the last record that is kept are returned, and the next read fails.
            for (int i = 0; i < read; i++)
            {
                if (buffer.Span[i] is (byte)'\n' && --_remaining is 0)
                    return i + 1;
            }

            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();

            base.Dispose(disposing);
        }

        private HttpIOException Cut()
        {
            inner.Dispose();
            return new HttpIOException(HttpRequestError.ResponseEnded, "The response was cut off (injected fault).");
        }
    }
}
