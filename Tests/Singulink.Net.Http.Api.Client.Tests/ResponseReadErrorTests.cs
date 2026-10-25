namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Checks that an error while reading a response body is thrown as an <see cref="HttpRequestException"/>, like other transport errors.
/// </summary>
[PrefixTestClass]
public sealed class ResponseReadErrorTests
{
    public enum BodyKind
    {
        Json,
        Text,
        Error,
    }

    [TestMethod]
    [DataRow(BodyKind.Json)]
    [DataRow(BodyKind.Text)]
    [DataRow(BodyKind.Error)]
    public async Task IOExceptionWhileReadingBody_ThrowsHttpRequestException(BodyKind kind)
    {
        var (status, contentType) = GetResponseHead(kind);
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(status) {
            Content = new StreamContent(new FailingStream()) { Headers = { ContentType = new(contentType) } },
        });

        var client = new TestApiClient(handler);

        var ex = await Should.ThrowAsync<HttpRequestException>(() => SendAsync(client, kind));

        GetInnerExceptions(ex).OfType<IOException>().ShouldContain(e => e.Message == FailingStream.Message);
    }

    [TestMethod]
    [DataRow(BodyKind.Json)]
    [DataRow(BodyKind.Text)]
    [DataRow(BodyKind.Error)]
    public async Task ConnectionClosedBeforeBodyEnds_ThrowsHttpRequestException(BodyKind kind)
    {
        var (status, contentType) = GetResponseHead(kind);

        using var server = new RawHttpServer(IPAddress.Loopback, _ =>
            $"HTTP/1.1 {(int)status} {status}\r\nContent-Type: {contentType}\r\nContent-Length: 100\r\nConnection: close\r\n\r\n{{\"id\":1");

        var client = new TestApiClient(handler: null) { BaseAddress = server.BaseAddress };

        var ex = await Should.ThrowAsync<HttpRequestException>(() => SendAsync(client, kind));

        GetInnerExceptions(ex).OfType<IOException>().ShouldNotBeEmpty();
#if NET
        if (kind is BodyKind.Json)
            ex.HttpRequestError.ShouldBe(HttpRequestError.ResponseEnded);
#endif
    }

    private static IEnumerable<Exception> GetInnerExceptions(Exception exception)
    {
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
            yield return inner;
    }

    private static (HttpStatusCode Status, string ContentType) GetResponseHead(BodyKind kind) => kind switch {
        BodyKind.Json => (HttpStatusCode.OK, "application/json"),
        BodyKind.Text => (HttpStatusCode.OK, "text/plain"),
        _ => (HttpStatusCode.BadRequest, "text/plain"),
    };

    private static Task SendAsync(TestApiClient client, BodyKind kind) => kind switch {
        BodyKind.Json => client.SendRequestAsync<TestItem>(client.CreateGetRequest("items")),
        BodyKind.Text => client.SendRequestAsync<string>(client.CreateGetRequest("items")),
        _ => client.SendRequestAsync(client.CreateGetRequest("items")),
    };

    /// <summary>
    /// A stream whose reads fail with an <see cref="IOException"/>, like the stream of a connection that is cut off.
    /// </summary>
    private sealed class FailingStream : Stream
    {
        public const string Message = "The connection was cut off.";

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException<int>(new IOException(Message));

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException(Message);

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
