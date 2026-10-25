using System.Collections.Concurrent;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// An HTTP message handler in front of a <see cref="FakeUploadService"/> that holds each write of the body of a chunk's request until the test lets it
/// through, so that a test controls when the bytes of a chunk go out, and counts the requests of each chunk by its first byte.
/// </summary>
public sealed class ChunkWriteGate(FakeUploadService service) : HttpMessageHandler
{
    private readonly HttpMessageInvoker _service = new(service, disposeHandler: false);
    private readonly SemaphoreSlim _waiting = new(0);
    private readonly SemaphoreSlim _released = new(0);
    private readonly ConcurrentDictionary<long, int> _attempts = new();
    private volatile bool _isOpen;

    /// <summary>
    /// Gets the number of requests of the chunk that starts at the specified byte.
    /// </summary>
    public int AttemptsOf(long first) => _attempts.TryGetValue(first, out int count) ? count : 0;

    /// <summary>
    /// Waits until a write of a chunk's body waits to go through.
    /// </summary>
    public async Task WaitForWriteAsync()
    {
        if (!await _waiting.WaitAsync(TimeSpan.FromSeconds(10)))
            throw new ShouldAssertException("No write of a chunk waited in time.");
    }

    /// <summary>
    /// Lets the write that waits go through.
    /// </summary>
    public void Release() => _released.Release();

    /// <summary>
    /// Lets all writes from now on go through right away.
    /// </summary>
    public void Open() => _isOpen = true;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Put || request.Content?.Headers.ContentRange?.From is not { } first)
            return await _service.SendAsync(request, cancellationToken);

        _attempts.AddOrUpdate(first, 1, (_, count) => count + 1);

        using var body = new GatedStream(this, cancellationToken);
#if NET
        await request.Content.CopyToAsync(body, cancellationToken);
#else
        await request.Content.CopyToAsync(body);
#endif

        var content = new ByteArrayContent(body.ToArray());

        foreach (var header in request.Content.Headers)
            content.Headers.TryAddWithoutValidation(header.Key, header.Value);

        var forwarded = new HttpRequestMessage(request.Method, request.RequestUri) { Content = content };

        foreach (var header in request.Headers)
            forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);

        var response = await _service.SendAsync(forwarded, cancellationToken);
        response.RequestMessage = request;

        return response;
    }

    /// <summary>
    /// The body of a chunk's request as the gate receives it, each write of which waits until the test lets it through.
    /// </summary>
    private sealed class GatedStream(ChunkWriteGate gate, CancellationToken requestToken) : Stream
    {
        private readonly MemoryStream _data = new();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _data.Length;

        public override long Position
        {
            get => _data.Position;
            set => throw new NotSupportedException();
        }

        public byte[] ToArray() => _data.ToArray();

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await WaitAsync(cancellationToken);
            _data.Write(buffer, offset, count);
        }

#if NET
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await WaitAsync(cancellationToken);
            _data.Write(buffer.Span);
        }
#endif

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _data.Dispose();

            base.Dispose(disposing);
        }

        private async Task WaitAsync(CancellationToken cancellationToken)
        {
            if (gate._isOpen)
                return;

            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, requestToken);
            gate._waiting.Release();
            await gate._released.WaitAsync(cancellation.Token);
        }
    }
}
