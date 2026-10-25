using System.Diagnostics;

namespace Singulink.Net.Http.Api.Client;

[PrefixTestClass]
public sealed class RequestTimeoutTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(200);

    private static FakeHttpMessageHandler CreateUnansweredHandler() => new(async (_, cancellationToken) => {
        await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
        return Responses.NoContent();
    });

    /// <summary>
    /// Waits for the task, failing the test if it doesn't complete in 30 seconds.
    /// </summary>
    private static async Task WithinTestTimeout(Task task)
    {
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30))) != task)
            throw new ShouldAssertException("The task did not complete in time.");

        await task;
    }

    /// <summary>
    /// Returns the exception that the task throws. Unlike Shouldly's ThrowAsync, it keeps the exception of a cancelled task.
    /// </summary>
    private static async Task<T> CatchAsync<T>(Func<Task> action)
        where T : Exception
    {
        try
        {
            await WithinTestTimeout(action());
        }
        catch (T ex)
        {
            return ex;
        }

        throw new ShouldAssertException($"The task did not throw {typeof(T).Name}.");
    }

    [TestMethod]
    public void Default_Is100SecondsAndIsInherited()
    {
        var parent = new TestApiClient(new FakeHttpMessageHandler());
        var child = new TestApiClient(parent);

        parent.CurrentRequestTimeout.ShouldBe(TimeSpan.FromSeconds(100));

        parent.Timeout = TimeSpan.FromMinutes(5);
        child.CurrentRequestTimeout.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [TestMethod]
    public async Task ResponseNotStartedInTime_ThrowsTimeout()
    {
        var client = new TestApiClient(CreateUnansweredHandler()) { Timeout = ShortTimeout };

        var ex = await CatchAsync<TaskCanceledException>(() => client.SendRequestAsync(client.CreateGetRequest("items")));

        ex.InnerException.ShouldBeOfType<TimeoutException>();
        ex.Message.ShouldContain("0.2 seconds");
    }

    [TestMethod]
    [DataRow(1, "of 1 second.")]
    [DataRow(2, "of 2 seconds.")]
    [DataRow(0.5, "of 0.5 seconds.")]
    public void TimeoutMessage_NamesTheTimeoutInSeconds(double seconds, string messageEnd)
    {
        var ex = ApiClientBase.CreateTimeoutException(TimeSpan.FromSeconds(seconds), new OperationCanceledException());

        ex.Message.ShouldEndWith(messageEnd);
        ex.InnerException.ShouldBeOfType<TimeoutException>().Message.ShouldBe(ex.Message);
    }

    [TestMethod]
    public async Task RequestTimeout_ReplacesClientTimeout()
    {
        var handler = new FakeHttpMessageHandler(async (_, cancellationToken) => {
            await Task.Delay(ShortTimeout + ShortTimeout, cancellationToken);
            return Responses.NoContent();
        });

        var client = new TestApiClient(handler) { Timeout = ShortTimeout };
        var request = client.CreatePostRequest("uploads");
        TestApiClient.SetTimeout(request, System.Threading.Timeout.InfiniteTimeSpan);

        await WithinTestTimeout(client.SendRequestAsync(request, "content"));
    }

    [TestMethod]
    public async Task RequestTimeout_TimesOutRequest()
    {
        var client = new TestApiClient(CreateUnansweredHandler());
        var request = client.CreateGetRequest("items");
        TestApiClient.SetTimeout(request, ShortTimeout);

        var ex = await CatchAsync<TaskCanceledException>(() => client.SendRequestAsync(request));

        ex.InnerException.ShouldBeOfType<TimeoutException>();
        ex.Message.ShouldContain("0.2 seconds");
    }

    [TestMethod]
    public async Task RequestTimeout_TimesOutStreamingResponse()
    {
        var client = new TestApiClient(CreateUnansweredHandler());
        var request = client.CreateGetRequest("stream");
        TestApiClient.SetTimeout(request, ShortTimeout);

        var ex = await CatchAsync<TaskCanceledException>(async () => {
            await foreach (var item in client.SendStreamingRequestAsync<TestItem>(request))
            {
            }
        });

        ex.InnerException.ShouldBeOfType<TimeoutException>();
    }

    [TestMethod]
    public void RequestTimeout_InvalidValue_ThrowsArgumentOutOfRange()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/v1/items");

        Should.Throw<ArgumentOutOfRangeException>(() => TestApiClient.SetTimeout(request, TimeSpan.Zero)).ParamName.ShouldBe("timeout");
        Should.Throw<ArgumentOutOfRangeException>(() => TestApiClient.SetTimeout(request, TimeSpan.FromSeconds(-2)));
    }

    [TestMethod]
    public async Task StreamingResponseNotStartedInTime_ThrowsTimeout()
    {
        var client = new TestApiClient(CreateUnansweredHandler()) { Timeout = ShortTimeout };

        var ex = await CatchAsync<TaskCanceledException>(ReadAllAsync);

        ex.InnerException.ShouldBeOfType<TimeoutException>();

        async Task ReadAllAsync()
        {
            await foreach (var item in client.SendStreamingRequestAsync<TestItem>(client.CreateGetRequest("items")))
            {
            }
        }
    }

    [TestMethod]
    public async Task CallerCancels_ThrowsWithoutTimeout()
    {
        var client = new TestApiClient(CreateUnansweredHandler()) { Timeout = TimeSpan.FromMinutes(10) };
        using var cts = new CancellationTokenSource(ShortTimeout);

        var ex = await CatchAsync<OperationCanceledException>(() => client.SendRequestAsync(client.CreateGetRequest("items"), cts.Token));

        (ex.InnerException is TimeoutException).ShouldBeFalse();
    }

    [TestMethod]
    public async Task InfiniteTimeout_DoesNotCancel()
    {
        var answer = new TaskCompletionSource<bool>();
        var handler = new FakeHttpMessageHandler(async (_, _) => {
            await answer.Task.WithTimeoutAsync();
            return Responses.Json("""{"id":1,"name":"a"}""");
        });

        var client = new TestApiClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        var task = client.SendRequestAsync<TestItem>(client.CreateGetRequest("items"));

        await Task.Delay(ShortTimeout);
        answer.SetResult(true);
        await WithinTestTimeout(task);

        task.Result.ShouldBe(new TestItem(1, "a"));
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(-1L)]
    [DataRow(-20_000L)]
    [DataRow(-10_001L)]
    public async Task InvalidTimeout_Throws(long ticks)
    {
        var handler = new FakeHttpMessageHandler(_ => Responses.Json("""{"id":1,"name":"a"}"""));
        var client = new TestApiClient(handler) { Timeout = TimeSpan.FromTicks(ticks) };

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => client.SendRequestAsync<TestItem>(client.CreateGetRequest("items")));
        var streamingEx = await Should.ThrowAsync<InvalidOperationException>(ReadAllAsync);

        ex.Message.ShouldContain("must be positive or Timeout.InfiniteTimeSpan");
        streamingEx.Message.ShouldBe(ex.Message);
        handler.Requests.ShouldBeEmpty();

        async Task ReadAllAsync()
        {
            await foreach (var item in client.SendStreamingRequestAsync<TestItem>(client.CreateGetRequest("items")))
            {
            }
        }
    }

    [TestMethod]
    [DataRow(30.0)]
    [DataRow(double.PositiveInfinity)]
    public async Task TimeoutLongerThanTimersSupport_DoesNotCancel(double days)
    {
        var handler = new FakeHttpMessageHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/stream", StringComparison.Ordinal) ?
            Responses.Stream("""{"item":{"id":1,"name":"a"}}""", """{"end":true}""") :
            Responses.Json("""{"id":1,"name":"a"}"""));

        var client = new TestApiClient(handler) { Timeout = double.IsInfinity(days) ? TimeSpan.MaxValue : TimeSpan.FromDays(days) };
        var streamed = new List<TestItem>();

        var item = await client.SendRequestAsync<TestItem>(client.CreateGetRequest("items"));

        await foreach (var streamedItem in client.SendStreamingRequestAsync<TestItem>(client.CreateGetRequest("stream")))
            streamed.Add(streamedItem);

        item.ShouldBe(new TestItem(1, "a"));
        streamed.ShouldBe([new TestItem(1, "a")]);
    }

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
    public async Task BodyNotReadInTime_ThrowsTimeout(BodyKind kind)
    {
        var (status, contentType) = GetResponseHead(kind);
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(status) {
            Content = new StreamContent(new EndlessStream()) { Headers = { ContentType = new(contentType) } },
        });

        var client = new TestApiClient(handler) { Timeout = ShortTimeout };

        var ex = await CatchAsync<TaskCanceledException>(() => SendAsync(client, kind));

        ex.InnerException.ShouldBeOfType<TimeoutException>();
    }

    [TestMethod]
    [DataRow(BodyKind.Json)]
    [DataRow(BodyKind.Text)]
    [DataRow(BodyKind.Error)]
    public async Task BodyStalledByServer_ThrowsTimeout(BodyKind kind)
    {
        var (status, contentType) = GetResponseHead(kind);

        // The server sends the headers and the start of the body, and then waits until the client closes the connection.
        using var server = new RawHttpServer(IPAddress.Loopback, async (_, stream) => {
            await RawHttpServer.WriteAsync(
                stream, $"HTTP/1.1 {(int)status} {status}\r\nContent-Type: {contentType}\r\nContent-Length: 100\r\nConnection: close\r\n\r\n{{");

            byte[] buffer = new byte[256];

            while (await stream.ReadAsync(buffer, 0, buffer.Length) > 0)
            {
            }
        });

        var client = new TestApiClient(handler: null) {
            BaseAddress = server.BaseAddress,
            Timeout = TimeSpan.FromMilliseconds(500),
        };

        var stopwatch = Stopwatch.StartNew();
        var ex = await CatchAsync<TaskCanceledException>(() => SendAsync(client, kind));
        stopwatch.Stop();

        ex.InnerException.ShouldBeOfType<TimeoutException>();

        // On .NET Framework, the client stops reading by disposing the response, which takes about half a second when it waits for the body.
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(900));
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
    /// A stream whose reads wait until they are cancelled or the stream is disposed, like the stream of a connection that stalls.
    /// </summary>
    private sealed class EndlessStream : Stream
    {
        private readonly CancellationTokenSource _disposed = new();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposed.Token);

            try
            {
                await Task.Delay(System.Threading.Timeout.Infinite, source.Token);
            }
            catch (OperationCanceledException) when (_disposed.IsCancellationRequested)
            {
                throw new ObjectDisposedException(nameof(EndlessStream));
            }

            return 0;
        }

        // .NET Framework's StreamContent reads with BeginRead.
        public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
        {
            var completion = new TaskCompletionSource<int>(state);

            ReadAsync(buffer, offset, count, CancellationToken.None).ContinueWith(
                task => {
                    if (task.Exception is { } exception)
                        completion.SetException(exception.InnerExceptions);
                    else
                        completion.SetResult(task.Result);

                    callback?.Invoke(completion.Task);
                },
                TaskScheduler.Default);

            return completion.Task;
        }

        public override int EndRead(IAsyncResult asyncResult) => ((Task<int>)asyncResult).GetAwaiter().GetResult();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _disposed.Cancel();

            base.Dispose(disposing);
        }
    }
}
