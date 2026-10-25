#if NET
using System.Net.Http.Headers;
using System.Text;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Responses whose bodies the client reads to the end before it disposes them, since disposing a response whose body wasn't read aborts its request,
/// which browsers report as a failed request.
/// </summary>
[PrefixTestClass]
public sealed class ResponseDrainTests
{
    private const string TwoItems = """{"item":{"id":1,"name":"a"}}""" + "\n" + """{"item":{"id":2,"name":"b"}}""" + "\n" + """{"end":true}""" + "\n";

    [TestMethod]
    public async Task ResponseOfARequestWithoutAResult_IsReadToTheEnd()
    {
        var body = new ChunkedStream("ignored");
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }));

        await client.SendRequestAsync(client.CreatePostRequest($"items/5/archive"));

        body.IsReadToTheEnd.ShouldBeTrue();
    }

    [TestMethod]
    public async Task StreamingResponse_IsReadToTheEndAfterItsEndRecord()
    {
        // The end record arrives with one read, and the end of the body with the next.
        var body = new ChunkedStream("""{"item":{"id":1,"name":"a"}}""" + "\n" + """{"end":true}""" + "\n");
        var content = new StreamContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(StreamingResponse.MediaType);

        var client = new TestApiClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        var items = new List<TestItem>();

        await foreach (var item in client.SendStreamingRequestAsync<TestItem>(client.CreateGetRequest("items")))
            items.Add(item);

        items.ShouldBe([new TestItem(1, "a")]);
        body.IsReadToTheEnd.ShouldBeTrue();
    }

    [TestMethod]
    public async Task ResponseWhoseBodyDoesNotEnd_IsGivenUp_AndKeepsTheResult()
    {
        // A body whose read doesn't stop when it is cancelled, as on some platforms.
        var body = new EndlessStream();
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) })) {
            DrainTimeout = TimeSpan.FromMilliseconds(100),
        };

        await client.SendRequestAsync(client.CreatePostRequest($"items/5/archive")).WaitAsync(TimeSpan.FromSeconds(20));
        body.IsRead.ShouldBeTrue();
    }

    [TestMethod]
    public async Task ResponseWhoseBodyFailsAfterTheResult_KeepsTheResult()
    {
        var body = new ChunkedStream("ignored") { FailsAtTheEnd = true };
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }));

        await client.SendRequestAsync(client.CreatePostRequest($"items/5/archive"));
    }

    [TestMethod]
    public async Task StreamingResponse_IsReadToTheEndAfterAnErrorRecord()
    {
        var body = new ChunkedStream("""{"item":{"id":1,"name":"a"}}""" + "\n" + """{"error":{"status":409,"code":"conflict","message":"No."}}""" + "\n");
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => CreateStreamingResponse(body)));

        await Should.ThrowAsync<ConflictApiException>(async () => {
            await foreach (var item in client.SendStreamingRequestAsync<TestItem>(client.CreateGetRequest("items")))
            {
            }
        });

        body.IsReadToTheEnd.ShouldBeTrue();
    }

    [TestMethod]
    public async Task StreamingResponseEndedAtAnItem_IsReadToTheEndInTheBackgroundInTheBrowser()
    {
        var body = new ChunkedStream(TwoItems);
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => CreateStreamingResponse(body))) { DrainsStoppedResponses = true };

        await foreach (var item in client.SendStreamingRequestAsync<TestItem>(client.CreateGetRequest("items")))
            break;

        await body.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        body.IsReadToTheEnd.ShouldBeTrue();
    }

    [TestMethod]
    public async Task StreamingResponseEndedAtAnItem_IsDisposedWithoutReadingItOnOtherPlatforms()
    {
        var body = new ChunkedStream(TwoItems);
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => CreateStreamingResponse(body)));

        await foreach (var item in client.SendStreamingRequestAsync<TestItem>(client.CreateGetRequest("items")))
            break;

        body.Disposed.Task.IsCompleted.ShouldBeTrue();
        body.IsReadToTheEnd.ShouldBeFalse();
    }

    [TestMethod]
    public async Task StreamingResponseEndedAtAnItemByCancellation_IsDisposedWithoutReadingIt()
    {
        var body = new ChunkedStream(TwoItems);
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => CreateStreamingResponse(body))) { DrainsStoppedResponses = true };
        using var cancellation = new CancellationTokenSource();

        await foreach (var item in client.SendStreamingRequestAsync<TestItem>(client.CreateGetRequest("items")).WithCancellation(cancellation.Token))
        {
            cancellation.Cancel();
            break;
        }

        body.Disposed.Task.IsCompleted.ShouldBeTrue();
        body.IsReadToTheEnd.ShouldBeFalse();
    }

    private static HttpResponseMessage CreateStreamingResponse(Stream body)
    {
        var content = new StreamContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(StreamingResponse.MediaType);

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>
    /// A body whose reads never complete, also when they are cancelled.
    /// </summary>
    private sealed class EndlessStream : Stream
    {
        private readonly TaskCompletionSource<int> _never = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            IsRead = true;
            return new(_never.Task);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A body that returns its content with one read and its end with the next, and knows whether it was read to the end and disposed.
    /// </summary>
    private sealed class ChunkedStream(string content) : Stream
    {
        private readonly byte[] _content = Encoding.UTF8.GetBytes(content);
        private bool _isContentRead;

        public bool IsReadToTheEnd { get; private set; }

        public bool FailsAtTheEnd { get; init; }

        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_isContentRead)
            {
                if (FailsAtTheEnd)
                    throw new IOException("The connection was reset.");

                IsReadToTheEnd = true;
                return 0;
            }

            _content.CopyTo(buffer);
            _isContentRead = true;

            return _content.Length;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed.TrySetResult();
            base.Dispose(disposing);
        }
    }
}
#endif
