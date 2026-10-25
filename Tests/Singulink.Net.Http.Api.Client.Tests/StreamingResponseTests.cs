using System.Diagnostics;

namespace Singulink.Net.Http.Api.Client;

[PrefixTestClass]
public sealed class StreamingResponseTests
{
    private static async Task<List<T>> ReadAllAsync<T>(TestApiClient client, List<T>? received = null)
    {
        received ??= [];

        await foreach (var item in client.SendStreamingRequestAsync<T>(client.CreateGetRequest("items")))
            received.Add(item);

        return received;
    }

    [TestMethod]
    public async Task Items_AreYieldedUntilEndRecord()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => Responses.Stream(
            """{"item":{"id":1,"name":"a"}}""",
            """{"ping":true}""",
            """{"item":{"id":2,"name":"b"}}""",
            """{"end":true}""")));

        var items = await ReadAllAsync<TestItem>(client);

        items.ShouldBe([new TestItem(1, "a"), new TestItem(2, "b")]);
    }

    [TestMethod]
    public async Task EmptyStream_YieldsNoItems()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => Responses.Stream("""{"end":true}""")));

        (await ReadAllAsync<TestItem>(client)).ShouldBeEmpty();
    }

    [TestMethod]
    public async Task NullItems_AreYieldedAsNull()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => Responses.Stream(
            """{"item":null}""",
            """{"item":{"id":1,"name":"a"}}""",
            """{"end":true}""")));

        var items = await ReadAllAsync<TestItem?>(client);

        items.ShouldBe([null, new TestItem(1, "a")]);
    }

    [TestMethod]
    [DataRow(400, typeof(BadRequestApiException))]
    [DataRow(401, typeof(UnauthorizedApiException))]
    [DataRow(403, typeof(ForbiddenApiException))]
    [DataRow(404, typeof(NotFoundApiException))]
    [DataRow(409, typeof(ConflictApiException))]
    [DataRow(412, typeof(UserChangedApiException))]
    [DataRow(413, typeof(ContentTooLargeApiException))]
    [DataRow(415, typeof(UnsupportedMediaTypeApiException))]
    [DataRow(422, typeof(ValidationApiException))]
    [DataRow(428, typeof(UserRequiredApiException))]
    [DataRow(429, typeof(TooManyRequestsApiException))]
    [DataRow(500, typeof(ServerErrorApiException))]
    [DataRow(503, typeof(ServiceUnavailableApiException))]
    [DataRow(418, typeof(ApiException))]
    [DataRow(502, typeof(ApiException))]
    public async Task ErrorRecord_ThrowsMatchingApiExceptionAfterPrecedingItems(int status, Type exceptionType)
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => Responses.Stream(
            """{"item":{"id":1,"name":"a"}}""",
            $$$"""{"error":{"status":{{{status}}},"code":"denied","message":"You do not have permission to edit this document."}}""")));

        var received = new List<TestItem>();
        var ex = await Should.ThrowAsync<ApiException>(() => ReadAllAsync(client, received));

        received.ShouldBe([new TestItem(1, "a")]);
        ex.ShouldBeOfType(exceptionType);
        ex.StatusCode.ShouldBe((HttpStatusCode)status);
        ex.ErrorCode.ShouldBe("denied");
        ex.Message.ShouldBe("You do not have permission to edit this document.");
    }

    [TestMethod]
    public async Task ErrorRecordWithoutCode_HasNoErrorCode()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => Responses.Stream("""{"error":{"status":500,"message":"Ref 42"}}""")));

        var ex = await Should.ThrowAsync<ServerErrorApiException>(() => ReadAllAsync<TestItem>(client));

        ex.ErrorCode.ShouldBeNull();
        ex.Message.ShouldBe("Ref 42");
    }

    [TestMethod]
    public async Task ErrorRecordWithInvalidCode_ThrowsGenericErrorWithItsStatus()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => Responses.Stream(
            """{"item":{"id":1,"name":"a"}}""",
            """{"error":{"status":403,"code":"not a code","message":"Not allowed.","retryAfter":5}}""")));

        var received = new List<TestItem>();
        var ex = await Should.ThrowAsync<ForbiddenApiException>(() => ReadAllAsync(client, received));

        received.ShouldBe([new TestItem(1, "a")]);
        ex.Message.ShouldBe("The service returned an error (403 Forbidden).");
        ex.ErrorCode.ShouldBeNull();
        ex.ErrorContent.ShouldNotBeNull().Content.ShouldBe("Not allowed.");
        ex.RetryAfter.ShouldBe(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task TruncatedStream_ThrowsIOExceptionAfterReceivedItems()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => Responses.Stream(
            """{"item":{"id":1,"name":"a"}}""",
            """{"item":{"id":2,"name":"b"}}""")));

        var received = new List<TestItem>();
        var ex = await Should.ThrowAsync<IOException>(() => ReadAllAsync(client, received));

        received.ShouldBe([new TestItem(1, "a"), new TestItem(2, "b")]);

#if NET
        ex.ShouldBeOfType<HttpIOException>().HttpRequestError.ShouldBe(HttpRequestError.ResponseEnded);
#endif
        ApiFailures.IsTransient(ex).ShouldBeTrue();
    }

    [TestMethod]
    public async Task UnrecognizedRecord_ThrowsFormatException()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => Responses.Stream("""{"unknown":1}""")));

        await Should.ThrowAsync<FormatException>(() => ReadAllAsync<TestItem>(client));
    }

    [TestMethod]
    public async Task NonStreamingResponse_ThrowsFormatException()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => Responses.Json("""[{"id":1,"name":"a"}]""")));

        await Should.ThrowAsync<FormatException>(() => ReadAllAsync<TestItem>(client));
    }

    [TestMethod]
    public async Task ErrorResponseBeforeStream_ThrowsMatchingApiException()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => Responses.Error(HttpStatusCode.Forbidden, "denied", "Not allowed.")));

        var received = new List<TestItem>();
        var ex = await Should.ThrowAsync<ForbiddenApiException>(() => ReadAllAsync(client, received));

        received.ShouldBeEmpty();
        ex.ErrorCode.ShouldBe("denied");
        ex.Message.ShouldBe("Not allowed.");
    }

    [TestMethod]
    public async Task SetCookie_IsPickedUpFromStreamingResponse()
    {
        var client = new TestApiClient(
            new FakeHttpMessageHandler(_ => Responses.Stream("""{"end":true}""").WithCookie("__Host-session-token=refreshed; path=/")));

        await ReadAllAsync<TestItem>(client);

        client.SessionToken.ShouldBe("refreshed");
    }

    [TestMethod]
    public async Task CancelWhileStreamIsIdle_EndsEnumeration()
    {
        using var server = StartIdleStreamServer();
        var client = new TestApiClient(handler: null) { BaseAddress = server.BaseAddress };
        using var cts = new CancellationTokenSource();
        var received = new List<TestItem>();
        var sinceCancel = new Stopwatch();

        var read = ReadAsync();

        (await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(5)))).ShouldBe(read, "The enumeration did not end.");
        sinceCancel.Stop();

        read.IsCanceled.ShouldBeTrue();
        received.ShouldBe([new TestItem(1, "a")]);
        sinceCancel.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));

        async Task ReadAsync()
        {
            await foreach (var item in client.SendStreamingRequestAsync<TestItem>(client.CreateGetRequest("items")).WithCancellation(cts.Token))
            {
                received.Add(item);

                _ = Task.Delay(100).ContinueWith(_ => {
                    sinceCancel.Start();
                    cts.Cancel();
                });
            }
        }
    }

    [TestMethod]
    public async Task BreakWhileStreamIsIdle_ReturnsPromptly()
    {
        using var server = StartIdleStreamServer();
        var client = new TestApiClient(handler: null) { BaseAddress = server.BaseAddress };
        var sinceBreak = new Stopwatch();

        await foreach (var item in client.SendStreamingRequestAsync<TestItem>(client.CreateGetRequest("items")))
        {
            sinceBreak.Start();
            break;
        }

        sinceBreak.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(250));
    }

    /// <summary>
    /// Starts a server whose streaming response sends one item and then nothing more, until the client closes the connection.
    /// </summary>
    private static RawHttpServer StartIdleStreamServer() => new(IPAddress.Loopback, async (_, stream) => {
        const string record = """{"item":{"id":1,"name":"a"}}""" + "\n";

        await RawHttpServer.WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Type: application/jsonl; charset=utf-8\r\nTransfer-Encoding: chunked\r\n\r\n");
        await RawHttpServer.WriteAsync(stream, $"{record.Length:X}\r\n{record}\r\n");

        byte[] buffer = new byte[256];

        while (await stream.ReadAsync(buffer, 0, buffer.Length) > 0)
        {
        }
    });
}
