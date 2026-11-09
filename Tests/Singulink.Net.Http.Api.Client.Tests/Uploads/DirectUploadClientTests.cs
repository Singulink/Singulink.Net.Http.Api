using System.Text;
using System.Text.Json;
using Singulink.Net.Http.Api.DataContracts;
using static Singulink.Net.Http.Api.Client.CancellationAssertions;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Direct uploads of files that fit in one chunk, in one request: the request, the services without direct uploads that the upload falls back to chunks
/// for, the request made again, and the upload that goes on in chunks once the service answered it.
/// </summary>
[PrefixTestClass]
public sealed class DirectUploadClientTests
{
    private const string Content = "0123456789";

    /// <summary>
    /// Specifies the format of a 429: with an error code of the toolkit, in plain text, a page, or empty.
    /// </summary>
    public enum TooManyRequestsFormat
    {
        ErrorCode,
        PlainText,
        Html,
        Empty,
    }

    public TestContext TestContext { get; set; } = null!;

    private CancellationToken CancellationToken => TestContext.CancellationToken;

    [TestMethod]
    public async Task SmallFile_IsUploadedDirectly_WithItsCreationInAHeader()
    {
        var service = new FakeUploadService { HasDirectUploads = true };
        var client = new TestApiClient(service, sessionToken: "token");
        var source = UploadSource.FromStream(new MemoryStream(Encoding.UTF8.GetBytes(Content)), "résumé 履歴書.txt", "text/plain; charset=utf-8");

        var result = await client.UploadFileAsync<TestItem>(
            "files", source, [("folderId", 5)], new TestItem(7, "Receipts"), Options(), CancellationToken);

        result.ShouldBe(new TestItem(1, Content));

        var request = service.Requests.ShouldHaveSingleItem();
        request.Kind.ShouldBe(UploadRequestKind.Direct);
        request.Uri.PathAndQuery.ShouldBe("/v1/files/uploads/direct?folderId=5");
        request.ContentType.ShouldBe("application/octet-stream");
        request.Headers.ContainsKey("Content-Disposition").ShouldBeFalse();

        var upload = service.SingleUpload;
        upload.Command.FileName.ShouldBe("résumé 履歴書.txt");
        upload.Command.ContentType.ShouldBe("text/plain; charset=utf-8");
        upload.Command.Length.ShouldBe(Content.Length);
        upload.Command.Id.ShouldNotBeNull().ToString("D")[14].ShouldBe('4');
        upload.Command.IdempotencyKey.ShouldBeNull();
        upload.Id.ShouldBe(upload.Command.Id.Value.ToString("D"));
        upload.Command.Command!.Value.GetRawText().ShouldBe("""{"id":7,"name":"Receipts"}""");
    }

    [TestMethod]
    public async Task ServiceWithoutDirectUploads_GetsTheUploadInChunks()
    {
        // A 4.x service answers with an empty 405 of its routing, and a service whose path has no such route with an empty 404.
        foreach (var status in new[] { HttpStatusCode.MethodNotAllowed, HttpStatusCode.NotFound, (HttpStatusCode)431 })
        {
            var service = new FakeUploadService { HasDirectUploads = true };
            service.Before = (request, _) => Task.FromResult(request.Kind is UploadRequestKind.Direct
                ? new HttpResponseMessage(status) { Content = new ByteArrayContent([]) }
                : null);

            var client = new TestApiClient(service);

            (await client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken))
                .ShouldBe(new TestItem(1, Content));

            service.Requests.Select(r => r.Kind).ShouldBe([
                UploadRequestKind.Direct, UploadRequestKind.Create, UploadRequestKind.Chunk, UploadRequestKind.Chunk, UploadRequestKind.Chunk,
                UploadRequestKind.Complete,
            ]);
        }
    }

    [TestMethod]
    [DataRow(400, false)]
    [DataRow(400, true)]
    [DataRow(403, false)]
    [DataRow(403, true)]
    [DataRow(404, false)]
    [DataRow(404, true)]
    [DataRow(405, false)]
    [DataRow(405, true)]
    [DataRow(413, false)]
    [DataRow(413, true)]
    [DataRow(431, false)]
    [DataRow(431, true)]
    public async Task RejectionThatIsNotAnErrorOfTheToolkit_GetsTheUploadInChunks(int status, bool isHtml)
    {
        var service = new FakeUploadService { HasDirectUploads = true };
        service.Before = (request, _) => Task.FromResult(request.Kind is UploadRequestKind.Direct ? NotOfTheToolkit((HttpStatusCode)status, isHtml) : null);

        var client = new TestApiClient(service);

        (await client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken))
            .ShouldBe(new TestItem(1, Content));

        service.Requests.Select(r => r.Kind).ShouldBe([
            UploadRequestKind.Direct, UploadRequestKind.Create, UploadRequestKind.Chunk, UploadRequestKind.Chunk, UploadRequestKind.Chunk,
            UploadRequestKind.Complete,
        ]);
    }

    [TestMethod]
    public async Task RejectionThatHoldsForTheCreationToo_IsThrownFromIt_WithoutAnAbort()
    {
        var service = new FakeUploadService { HasDirectUploads = true };
        service.Before = (_, _) => Task.FromResult(NotOfTheToolkit(HttpStatusCode.Forbidden, isHtml: true));

        var client = new TestApiClient(service);

        await Should.ThrowAsync<ForbiddenApiException>(
            () => client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken));

        service.Requests.Select(r => r.Kind).ShouldBe([UploadRequestKind.Direct, UploadRequestKind.Create]);
    }

    [TestMethod]
    public async Task RequestTimeoutThatIsNotAnErrorOfTheToolkit_MakesTheDirectUploadAgain()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var service = new FakeUploadService { HasDirectUploads = true };
        int requests = 0;

        service.Before = (request, _) => Task.FromResult(request.Kind is UploadRequestKind.Direct && Interlocked.Increment(ref requests) is 1
            ? NotOfTheToolkit(HttpStatusCode.RequestTimeout, isHtml: true)
            : null);

        var client = new TestApiClient(service);
        var upload = client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(time), cancellationToken: CancellationToken);
        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(2));

        (await WithinTimeout(upload)).ShouldBe(new TestItem(1, Content));
        service.Requests.Select(r => r.Kind).ShouldBe([UploadRequestKind.Direct, UploadRequestKind.Direct]);
    }

    [TestMethod]
    [DataRow(TooManyRequestsFormat.ErrorCode)]
    [DataRow(TooManyRequestsFormat.PlainText)]
    [DataRow(TooManyRequestsFormat.Html)]
    [DataRow(TooManyRequestsFormat.Empty)]
    public async Task TooManyRequests_WaitsWithTheCreation_AndDoesNotSendTheDirectRequestAgain(TooManyRequestsFormat format)
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var service = new FakeUploadService { HasDirectUploads = true };
        int creations = 0;

        service.Before = (request, _) => Task.FromResult(
            request.Kind is UploadRequestKind.Direct || (request.Kind is UploadRequestKind.Create && Interlocked.Increment(ref creations) is 1)
                ? TooManyRequests(format).WithRetryAfter("1")
                : null);

        var client = new TestApiClient(service);
        var upload = client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(time), cancellationToken: CancellationToken);
        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(1.5));

        (await WithinTimeout(upload)).ShouldBe(new TestItem(1, Content));

        service.Requests.Select(r => r.Kind).ShouldBe([
            UploadRequestKind.Direct, UploadRequestKind.Create, UploadRequestKind.Create, UploadRequestKind.Chunk, UploadRequestKind.Chunk,
            UploadRequestKind.Chunk, UploadRequestKind.Complete,
        ]);
    }

    [TestMethod]
    [DataRow(TooManyRequestsFormat.ErrorCode)]
    [DataRow(TooManyRequestsFormat.PlainText)]
    [DataRow(TooManyRequestsFormat.Html)]
    [DataRow(TooManyRequestsFormat.Empty)]
    public async Task TooManyRequestsWithoutARetryAfter_GetsTheUploadInChunks(TooManyRequestsFormat format)
    {
        var service = new FakeUploadService { HasDirectUploads = true };
        service.Before = (request, _) => Task.FromResult(request.Kind is UploadRequestKind.Direct ? TooManyRequests(format) : null);

        var client = new TestApiClient(service);

        (await client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken))
            .ShouldBe(new TestItem(1, Content));

        service.Requests.Select(r => r.Kind).ShouldBe([
            UploadRequestKind.Direct, UploadRequestKind.Create, UploadRequestKind.Chunk, UploadRequestKind.Chunk, UploadRequestKind.Chunk,
            UploadRequestKind.Complete,
        ]);
    }

    [TestMethod]
    [DataRow(TooManyRequestsFormat.ErrorCode)]
    [DataRow(TooManyRequestsFormat.PlainText)]
    [DataRow(TooManyRequestsFormat.Html)]
    [DataRow(TooManyRequestsFormat.Empty)]
    public async Task TooManyRequestsWithoutARetryAfter_ThatHoldsForTheCreationToo_IsThrownFromIt(TooManyRequestsFormat format)
    {
        var service = new FakeUploadService { HasDirectUploads = true };
        service.Before = (_, _) => Task.FromResult<HttpResponseMessage?>(TooManyRequests(format));

        var client = new TestApiClient(service);

        await Should.ThrowAsync<TooManyRequestsApiException>(
            () => client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken));

        service.Requests.Select(r => r.Kind).ShouldBe([UploadRequestKind.Direct, UploadRequestKind.Create]);
    }

    [TestMethod]
    public async Task ServiceThatRejectsTheCreationHeader_GetsTheUploadInChunks()
    {
        var service = new FakeUploadService { HasDirectUploads = true };
        service.Before = (request, _) => Task.FromResult(request.Kind is UploadRequestKind.Direct
            ? Responses.Error((HttpStatusCode)431, UploadErrorCodes.CreationHeaderTooLarge, "The creation header of a direct upload is larger than 8 KiB.")
            : null);

        var client = new TestApiClient(service);

        (await client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken))
            .ShouldBe(new TestItem(1, Content));

        service.Requests[0].Kind.ShouldBe(UploadRequestKind.Direct);
        service.Requests[1].Kind.ShouldBe(UploadRequestKind.Create);
    }

    [TestMethod]
    public async Task ServiceWhoseChunksAreSmallerThanTheFile_GetsTheUploadInChunks()
    {
        var service = new FakeUploadService { HasDirectUploads = true, MaxDirectUploadLength = 4 };
        var client = new TestApiClient(service);

        (await client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken))
            .ShouldBe(new TestItem(1, Content));

        service.Requests[0].Kind.ShouldBe(UploadRequestKind.Direct);
        service.Requests[1].Kind.ShouldBe(UploadRequestKind.Create);
    }

    [TestMethod]
    public async Task NotFoundOfTheApp_IsThrownWithoutFallingBack()
    {
        var service = new FakeUploadService { HasDirectUploads = true };
        service.Before = (request, _) => Task.FromResult<HttpResponseMessage?>(Responses.Error(HttpStatusCode.NotFound, "The folder does not exist."));
        var client = new TestApiClient(service);

        var ex = await Should.ThrowAsync<NotFoundApiException>(
            () => client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken));

        ex.Message.ShouldBe("The folder does not exist.");
        service.Requests.Select(r => r.Kind).ShouldBe([UploadRequestKind.Direct]);
    }

    [TestMethod]
    public async Task SuccessThatIsNotTheUploadsStream_GetsTheUploadInChunks()
    {
        // A service without direct uploads whose fallback route answers any request with a page.
        var service = new FakeUploadService { HasDirectUploads = true };
        service.Before = (request, _) => Task.FromResult(request.Kind is UploadRequestKind.Direct
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html></html>", Encoding.UTF8, "text/html") }
            : null);

        var client = new TestApiClient(service);

        (await client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken))
            .ShouldBe(new TestItem(1, Content));

        service.Requests[0].Kind.ShouldBe(UploadRequestKind.Direct);
        service.Requests[1].Kind.ShouldBe(UploadRequestKind.Create);
    }

    [TestMethod]
    public async Task DirectUploadThatFailsTwiceWithoutAResponse_IsUploadedInChunksWithTheSameKey()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var service = new FakeUploadService { HasDirectUploads = true };

        // As a service without direct uploads that closes the connection while the client still sends the body, after it answered.
        service.Before = (request, _) => request.Kind is UploadRequestKind.Direct
            ? throw new HttpRequestException("The connection was reset.")
            : Task.FromResult<HttpResponseMessage?>(null);

        var client = new TestApiClient(service);
        var upload = client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(time), cancellationToken: CancellationToken);
        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(1));

        (await WithinTimeout(upload)).ShouldBe(new TestItem(1, Content));

        var requests = service.Requests;
        requests.Select(r => r.Kind).ShouldBe([
            UploadRequestKind.Direct, UploadRequestKind.Direct, UploadRequestKind.Create, UploadRequestKind.Chunk, UploadRequestKind.Chunk,
            UploadRequestKind.Chunk, UploadRequestKind.Complete,
        ]);

        requests[0].CreationJson.ShouldBe(requests[2].Body);
    }

    [TestMethod]
    public async Task DirectUploadThatFailsBeforeItsResponse_IsMadeAgainWithTheSameKey()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var service = new FakeUploadService { HasDirectUploads = true };
        int attempts = 0;

        // The first attempt fails as a connection does, and the second as a service that can't take it at the moment.
        service.Before = (request, _) => Interlocked.Increment(ref attempts) switch {
            1 => throw new HttpRequestException("The connection was reset."),
            2 => Task.FromResult<HttpResponseMessage?>(Responses.Error(HttpStatusCode.ServiceUnavailable, "Not at the moment.")),
            _ => Task.FromResult<HttpResponseMessage?>(null),
        };

        var client = new TestApiClient(service);
        var upload = client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(time), cancellationToken: CancellationToken);

        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(1));
        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(2));

        (await WithinTimeout(upload)).ShouldBe(new TestItem(1, Content));
        service.Requests.Select(r => r.Kind).ShouldBe([UploadRequestKind.Direct, UploadRequestKind.Direct, UploadRequestKind.Direct]);
        service.Requests.Select(r => r.CreationJson).Distinct().Count().ShouldBe(1);
    }

    [TestMethod]
    public async Task DirectUploadCutOffAfterItsResponseStarted_IsCompletedByTheUploadsPath_AndSendsTheChunkThatTheServiceLacks()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var service = new FakeUploadService { HasDirectUploads = true };

        // The response starts with the upload's path and breaks off, and the service lost the file meanwhile, as one that stopped before it ran the
        // handler does.
        service.After = (request, response) => {
            if (request.Kind is not UploadRequestKind.Direct)
                return response;

            var upload = service.SingleUpload;
            upload.MarkMissing(0);
            upload.IsCompleted = false;

            var cut = Responses.Stream(FakeUploadService.PingRecord);
            cut.Headers.Location = response.Headers.Location;

            return cut;
        };

        var client = new TestApiClient(service);
        var upload = client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(time), cancellationToken: CancellationToken);

        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(1));

        (await WithinTimeout(upload)).ShouldBe(new TestItem(1, Content));

        var requests = service.Requests;
        requests.Select(r => r.Kind).ShouldBe([
            UploadRequestKind.Direct, UploadRequestKind.Complete, UploadRequestKind.Status, UploadRequestKind.Chunk, UploadRequestKind.Complete]);

        requests[1].Uri.PathAndQuery.ShouldBe($"/v1/files/uploads/{service.SingleUpload.Id}/complete");
        requests[3].ContentRange!.ToString().ShouldBe("bytes 0-9/10");
    }

    [TestMethod]
    public async Task CreationOfADirectUpload_IsTheCreationOfTheThreeRequests_WhateverItsNameAndContentType()
    {
        var uploads = new (string FileName, string? ContentType)[] {
            ("a.txt", null), ("a.txt", string.Empty), ("a.txt", " text/plain "), ("a.txt", "text/plain; name=é"), ("  résumé \uD800 😀 \"x\".txt ", "a"),
        };

        foreach (var (fileName, contentType) in uploads)
        {
            var service = new FakeUploadService { HasDirectUploads = true };
            service.Before = (request, _) => Task.FromResult(request.Kind is UploadRequestKind.Direct
                ? new HttpResponseMessage(HttpStatusCode.MethodNotAllowed) { Content = new ByteArrayContent([]) }
                : null);

            var client = new TestApiClient(service);
            var source = UploadSource.FromStream(new MemoryStream([1]), fileName, contentType);

            await client.UploadFileAsync<TestItem>("files", source, options: Options(), cancellationToken: CancellationToken);

            var requests = service.Requests;
            requests.Select(r => r.Kind).Take(2).ShouldBe([UploadRequestKind.Direct, UploadRequestKind.Create]);
            requests[0].CreationJson.ShouldBe(requests[1].Body);
        }
    }

    [TestMethod]
    public async Task CreationThatDoesNotFitInItsHeader_IsUploadedInChunks()
    {
        var service = new FakeUploadService { HasDirectUploads = true };
        var client = new TestApiClient(service);

        await client.UploadFileAsync<TestItem>(
            "files", TextSource(Content), command: new TestItem(1, new string('a', 7000)), options: Options(), cancellationToken: CancellationToken);

        service.Requests[0].Kind.ShouldBe(UploadRequestKind.Create);
    }

    [TestMethod]
    [DataRow(UploadErrorCodes.LimitReached, 429)]
    [DataRow(UploadErrorCodes.SizeLimitReached, 429)]
    [DataRow(UploadErrorCodes.StorageFull, 503)]
    public async Task DirectUploadAtALimit_WaitsWithTheCreation_AndSendsTheFileOnceThereIsRoom(string errorCode, int status)
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var service = new FakeUploadService { HasDirectUploads = true };
        int creations = 0;

        // The service answers the direct upload and the first creation with the limit's error.
        service.Before = (request, _) => {
            bool isAtLimit = request.Kind is UploadRequestKind.Direct ||
                (request.Kind is UploadRequestKind.Create && Interlocked.Increment(ref creations) is 1);

            var error = new ApiException((HttpStatusCode)status, "At a limit.") { ErrorCode = errorCode, RetryAfter = TimeSpan.FromSeconds(1) };
            return Task.FromResult(isAtLimit ? ApiErrorResponse.Create(error) : null);
        };

        var client = new TestApiClient(service);
        var upload = client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(time), cancellationToken: CancellationToken);
        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(1.5));

        (await WithinTimeout(upload)).ShouldBe(new TestItem(1, Content));

        var requests = service.Requests;
        requests.Select(r => r.Kind).ShouldBe([
            UploadRequestKind.Direct, UploadRequestKind.Create, UploadRequestKind.Create, UploadRequestKind.Chunk, UploadRequestKind.Chunk,
            UploadRequestKind.Chunk, UploadRequestKind.Complete,
        ]);

        requests[0].CreationJson.ShouldBe(requests[2].Body);
    }

    [TestMethod]
    public async Task CancellationAfterTheBodyWasSent_BeforeTheResponse_StopsTheRequest_AndCancelsTheUploadByItsId()
    {
        var (service, started) = ServiceThatLeavesDirectUploadsUnanswered();
        var client = new TestApiClient(service);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);

        var upload = client.UploadFileAsync<TestItem>(
            "files", TextSource(Content), [("folderId", 5)], options: Options(), cancellationToken: cancellation.Token);

        await WithinTimeout(started.Task);
        cancellation.Cancel();

        // The service has no upload with the ID, so it deletes nothing, and none created later runs its handler (204).
        var exception = await ShouldBeCancelledAsync(WithinTimeout(upload));
        exception.CancellationToken.ShouldBe(cancellation.Token);
        exception.InnerException.ShouldBeNull();

        var requests = service.Requests;
        requests.Select(r => r.Kind).ShouldBe([UploadRequestKind.Direct, UploadRequestKind.Abort]);
        requests[1].UploadId.ShouldBe(ReadCreationHeader(requests[0]).Id.ShouldNotBeNull().ToString("D"));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CancellationWhileALongBodyIsSent_StopsTheRequestAtOnce_AndCancelsTheUploadByItsId(bool countsWrittenBytes)
    {
        // Where the client can't see the bytes go out, as in the browser, the request is stopped all the same.
        var service = new FakeUploadService { HasDirectUploads = true };
        var sending = new SlowBodyHandler(service);
        var client = new TestApiClient(sending);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);

        int length = 3 << 20;
        var source = UploadSource.FromStream(new MemoryStream(new byte[length]), "data.bin");
        var options = new UploadOptions {
            ChunkTimeout = Timeout.InfiniteTimeSpan, CompletionIdleTimeout = Timeout.InfiniteTimeSpan, CountsWrittenBytes = countsWrittenBytes,
        };

        var upload = client.UploadFileAsync<TestItem>("files", source, options: options, cancellationToken: cancellation.Token);

        await WithinTimeout(sending.Started.Task);
        cancellation.Cancel();

        await ShouldBeCancelledAsync(WithinTimeout(upload));
        sending.IsStopped.ShouldBeTrue();
        sending.BytesSent.ShouldBeLessThan(length);
        service.Requests.ShouldHaveSingleItem().Kind.ShouldBe(UploadRequestKind.Abort);
    }

    [TestMethod]
    public async Task CancellationAfterTheBodyWasSent_OfAHandlerThatFinishesAnyway_ReturnsItsResult()
    {
        var service = new FakeUploadService { HasDirectUploads = true };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);

        // The upload is cancelled once the response of the direct upload has started, the service accepts the cancellation of the running handler, which
        // finishes all the same, and the completion gets its result.
        service.Complete = completion => completion.Index is 1
            ? new LiveRecordStream().CreateResponse()
            : Responses.Stream(FakeUploadService.ItemRecord(7, "saved"), FakeUploadService.EndRecord);

        service.Before = (request, _) => Task.FromResult(request.Kind is UploadRequestKind.Abort
            ? new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new ByteArrayContent([]) }
            : null);

        var client = new TestApiClient(service);
        var progress = new CancellingProgress(p => p.IsProcessing, cancellation);
        var options = new UploadOptions { ChunkTimeout = Timeout.InfiniteTimeSpan, CompletionIdleTimeout = Timeout.InfiniteTimeSpan, Progress = progress };
        var upload = client.UploadFileAsync<TestItem>("files", TextSource(Content), options: options, cancellationToken: cancellation.Token);

        (await WithinTimeout(upload)).ShouldBe(new TestItem(7, "saved"));

        var requests = service.Requests;
        requests.Select(r => r.Kind).ShouldBe([UploadRequestKind.Direct, UploadRequestKind.Abort, UploadRequestKind.Complete]);
        requests[2].UploadId.ShouldBe(service.SingleUpload.Id);
    }

    [TestMethod]
    public async Task CancellationWhileARequestThatGotNoResponseWaitsToBeMadeAgain_CancelsTheUploadByItsId_AndGetsTheOutcomeOfItsHandler()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var service = new FakeUploadService { HasDirectUploads = true };

        // The request got no response, so the service may have the file, and its handler ran: the service answers the cancellation with a 409, and the
        // completion with the handler's outcome.
        service.Before = (request, _) => request.Kind switch {
            UploadRequestKind.Direct => throw new HttpRequestException("The connection was reset."),
            UploadRequestKind.Abort => Task.FromResult<HttpResponseMessage?>(
                ApiErrorResponse.Create(new ConflictApiException("The upload has finished.") { ErrorCode = UploadErrorCodes.Finished })),
            _ => Task.FromResult<HttpResponseMessage?>(Responses.Stream(FakeUploadService.ItemRecord(7, "saved"), FakeUploadService.EndRecord)),
        };

        var client = new TestApiClient(service);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        var upload = client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(time), cancellationToken: cancellation.Token);

        await WaitUntilAsync(() => time.PendingTimerCount > 0);
        cancellation.Cancel();

        (await WithinTimeout(upload)).ShouldBe(new TestItem(7, "saved"));

        var requests = service.Requests;
        requests.Select(r => r.Kind).ShouldBe([UploadRequestKind.Direct, UploadRequestKind.Abort, UploadRequestKind.Complete]);
        string id = ReadCreationHeader(requests[0]).Id.ShouldNotBeNull().ToString("D");
        requests[1].UploadId.ShouldBe(id);
        requests[2].UploadId.ShouldBe(id);
    }

    [TestMethod]
    public async Task FailureWithoutARejection_AbortsTheUploadByItsId_AndThrowsTheFailure()
    {
        var service = new FakeUploadService { HasDirectUploads = true };
        service.Before = (request, _) => Task.FromResult(request.Kind is UploadRequestKind.Direct
            ? Responses.Error(HttpStatusCode.InternalServerError, "It failed.")
            : null);

        var client = new TestApiClient(service);

        var ex = await Should.ThrowAsync<ServerErrorApiException>(
            () => client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken));

        ex.Message.ShouldBe("It failed.");

        var requests = service.Requests;
        requests.Select(r => r.Kind).ShouldBe([UploadRequestKind.Direct, UploadRequestKind.Abort]);
        requests[1].UploadId.ShouldBe(ReadCreationHeader(requests[0]).Id.ShouldNotBeNull().ToString("D"));
    }

    [TestMethod]
    [DataRow(400, UploadErrorCodes.CreationUnreadable)]
    [DataRow(400, "")]
    [DataRow(401, "")]
    [DataRow(403, "")]
    [DataRow(404, UploadErrorCodes.NotFound)]
    [DataRow(409, UploadErrorCodes.KeyConflict)]
    [DataRow(412, "")]
    [DataRow(413, UploadErrorCodes.TooLarge)]
    [DataRow(413, "")]
    [DataRow(422, "app-file-rejected")]
    [DataRow(428, "")]
    public async Task Rejection_IsThrown_WithoutARequestToFindTheUpload(int status, string errorCode)
    {
        // An error in plain text, without an error code, can be a validator's, whichever part of the service or a proxy answered with it.
        var service = new FakeUploadService { HasDirectUploads = true };
        service.Before = (request, _) => Task.FromResult<HttpResponseMessage?>(errorCode.Length > 0
            ? Responses.Error((HttpStatusCode)status, errorCode, "Rejected.")
            : Responses.Error((HttpStatusCode)status, "Rejected."));

        var client = new TestApiClient(service);

        var ex = await Should.ThrowAsync<ApiException>(
            () => client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken));

        ex.StatusCode.ShouldBe((HttpStatusCode)status);
        service.Requests.Select(r => r.Kind).ShouldBe([UploadRequestKind.Direct]);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancellationAfterARequestTimeout_CancelsTheUploadByItsId(bool isHtml)
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var service = new FakeUploadService { HasDirectUploads = true };

        var status = HttpStatusCode.RequestTimeout;
        service.Before = (request, _) => Task.FromResult(request.Kind is not UploadRequestKind.Direct
            ? null
            : isHtml ? NotOfTheToolkit(status, isHtml: true) : Responses.Error(status, "The body came too slowly."));

        var client = new TestApiClient(service);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        var upload = client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(time), cancellationToken: cancellation.Token);

        await WaitUntilAsync(() => time.PendingTimerCount > 0);
        cancellation.Cancel();

        await ShouldBeCancelledAsync(WithinTimeout(upload));
        service.Requests.Select(r => r.Kind).ShouldBe([UploadRequestKind.Direct, UploadRequestKind.Abort]);
    }

    [TestMethod]
    public async Task CancellationWhileTheCreationAfterARejectedDirectUploadRuns_StopsIt_AndCancelsTheUploadByItsId()
    {
        var service = new FakeUploadService { HasDirectUploads = true };
        var creating = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // A service without direct uploads, and the upload is cancelled while its creation runs. The direct upload was sent, so the service is asked to cancel
        // the upload, which costs one request more than an upload without a direct upload would make.
        service.Before = async (request, cancellationToken) => {
            if (request.Kind is UploadRequestKind.Direct)
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed) { Content = new ByteArrayContent([]) };

            if (request.Kind is UploadRequestKind.Create)
            {
                creating.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return null;
        };

        var client = new TestApiClient(service);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        var upload = client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: cancellation.Token);

        await WithinTimeout(creating.Task);
        cancellation.Cancel();

        await ShouldBeCancelledAsync(WithinTimeout(upload));
        service.Requests.Select(r => r.Kind).ShouldBe([UploadRequestKind.Direct, UploadRequestKind.Create, UploadRequestKind.Abort]);
    }

    [TestMethod]
    public async Task CancellationWhileTheCreationWaitsForALimit_CancelsTheUploadByItsId()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var service = new FakeUploadService { HasDirectUploads = true };
        var error = new TooManyRequestsApiException("At a limit.") { ErrorCode = UploadErrorCodes.LimitReached, RetryAfter = TimeSpan.FromSeconds(1) };

        service.Before = (request, _) => Task.FromResult(request.Kind is UploadRequestKind.Abort ? null : ApiErrorResponse.Create(error));

        var client = new TestApiClient(service);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        var upload = client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(time), cancellationToken: cancellation.Token);

        await WaitUntilAsync(() => time.PendingTimerCount > 0);
        cancellation.Cancel();

        await ShouldBeCancelledAsync(WithinTimeout(upload));
        service.Requests.Select(r => r.Kind).ShouldBe([UploadRequestKind.Direct, UploadRequestKind.Create, UploadRequestKind.Abort]);
    }

    [TestMethod]
    public async Task CancellationBeforeTheDirectUploadIsSent_ThrowsAtOnce_AndSendsNothing()
    {
        var service = new FakeUploadService { HasDirectUploads = true };
        var client = new TestApiClient(service);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);

        // The source is read before the request is sent, and the upload is cancelled meanwhile.
        var source = UploadSource.Create("notes.txt", 10, (offset, buffer, _) => {
            cancellation.Cancel();
            return new ValueTask<int>(buffer.Length);
        });

        var upload = client.UploadFileAsync<TestItem>("files", source, options: Options(), cancellationToken: cancellation.Token);

        await ShouldBeCancelledAsync(WithinTimeout(upload));
        await Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken);
        service.Requests.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task SourceThatFailsToRead_SendsNothing()
    {
        var service = new FakeUploadService { HasDirectUploads = true };
        var client = new TestApiClient(service);
        var source = UploadSource.FromStream(new FailingStream(), "notes.txt");

        await Should.ThrowAsync<IOException>(() => client.UploadFileAsync<TestItem>("files", source, options: Options(), cancellationToken: CancellationToken));
        service.Requests.ShouldBeEmpty();
    }

    [TestMethod]
    [DataRow(429, ApiErrorCodes.RateLimited)]
    [DataRow(422, "app-file-rejected")]
    [DataRow(404, UploadErrorCodes.NotFound)]
    [DataRow(503, UploadErrorCodes.CancellationUnavailable)]
    public async Task AbortThatFails_EndsWithoutARetry_AndThrowsTheFailureOfTheUpload(int status, string errorCode)
    {
        var service = new FakeUploadService { HasDirectUploads = true };

        service.Before = (request, _) => Task.FromResult<HttpResponseMessage?>(request.Kind is UploadRequestKind.Abort
            ? Responses.Error((HttpStatusCode)status, errorCode, "Not this one.").WithRetryAfter("1")
            : Responses.Error(HttpStatusCode.InternalServerError, "It failed."));

        var client = new TestApiClient(service);

        // An upload that fails waits for its abort before it throws, so no request of the abort can follow.
        var ex = await Should.ThrowAsync<ServerErrorApiException>(
            () => client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken));

        ex.Message.ShouldBe("It failed.");
        service.Requests.Select(r => r.Kind).ShouldBe([UploadRequestKind.Direct, UploadRequestKind.Abort]);
    }

    [TestMethod]
    public async Task AbortThatGetsNoResponse_EndsWithinItsTimeout_AndThrowsTheFailureOfTheUpload()
    {
        foreach (bool isReset in new[] { true, false })
        {
            var service = new FakeUploadService { HasDirectUploads = true };

            service.Before = async (request, cancellationToken) => {
                if (request.Kind is UploadRequestKind.Direct)
                    return Responses.Error(HttpStatusCode.InternalServerError, "It failed.");

                if (isReset)
                    throw new HttpRequestException("The connection was reset.");

                await Task.Delay(Timeout.Infinite, cancellationToken);
                return null;
            };

            var client = new TestApiClient(service);

            var ex = await Should.ThrowAsync<ServerErrorApiException>(
                () => WithinTimeout(client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken)));

            ex.Message.ShouldBe("It failed.");
            service.Requests.Select(r => r.Kind).ShouldBe([UploadRequestKind.Direct, UploadRequestKind.Abort]);
        }
    }

    private static UploadOptions Options(ManualTimeProvider? time = null) =>
        new() { TimeProvider = time, ChunkTimeout = Timeout.InfiniteTimeSpan, CompletionIdleTimeout = Timeout.InfiniteTimeSpan };

    private static UploadSource TextSource(string text) =>
        UploadSource.FromStream(new MemoryStream(Encoding.UTF8.GetBytes(text)), "notes.txt", "text/plain");

    /// <summary>
    /// Creates an error response that is not in the toolkit's error format, as from a server or a proxy: an empty one without a content type, or a page.
    /// </summary>
    private static HttpResponseMessage? NotOfTheToolkit(HttpStatusCode status, bool isHtml)
    {
        return isHtml
            ? Responses.Content(status, "<html><body>Rejected.</body></html>", "text/html")
            : new HttpResponseMessage(status) { Content = new ByteArrayContent([]) };
    }

    private static HttpResponseMessage TooManyRequests(TooManyRequestsFormat format) => format switch {
        TooManyRequestsFormat.ErrorCode => Responses.Error((HttpStatusCode)429, ApiErrorCodes.RateLimited, "Too many requests."),
        TooManyRequestsFormat.PlainText => Responses.Error((HttpStatusCode)429, "Too many requests."),
        TooManyRequestsFormat.Html => NotOfTheToolkit((HttpStatusCode)429, isHtml: true)!,
        TooManyRequestsFormat.Empty => NotOfTheToolkit((HttpStatusCode)429, isHtml: false)!,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    private static CreateUploadCommand ReadCreationHeader(UploadRequest request) =>
        JsonSerializer.Deserialize(request.CreationJson!, UploadTestJsonContext.Default.CreateUploadCommand).ShouldNotBeNull();

    /// <summary>
    /// Creates a service that leaves the request of a direct upload without a response until it is cancelled, and a task that ends when the request arrives.
    /// </summary>
    private static (FakeUploadService Service, TaskCompletionSource<bool> Started) ServiceThatLeavesDirectUploadsUnanswered()
    {
        var service = new FakeUploadService { HasDirectUploads = true };
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        service.Before = async (request, cancellationToken) => {
            if (request.Kind is not UploadRequestKind.Direct)
                return null;

            started.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, cancellationToken);

            return null;
        };

        return (service, started);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private static async Task AdvanceWhenWaitingAsync(ManualTimeProvider time, TimeSpan advance)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        while (time.PendingTimerCount is 0)
            await Task.Delay(10, timeout.Token);

        time.Advance(advance);
    }

    private static async Task<T> WithinTimeout<T>(Task<T> task)
    {
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(20))) != task)
            throw new ShouldAssertException("The task did not complete in time.");

        return await task;
    }

    /// <summary>
    /// Sends the body of a direct upload slowly, as over a slow connection: a slice of up to 64 KiB every 200 milliseconds, and then waits for a response
    /// that never comes, until the request is stopped. Other requests go to the service.
    /// </summary>
    private sealed class SlowBodyHandler(HttpMessageHandler service) : DelegatingHandler(service)
    {
        private long _bytesSent;
        private int _isStopped;

        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long BytesSent => Interlocked.Read(ref _bytesSent);

        public bool IsStopped => Volatile.Read(ref _isStopped) is 1;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/uploads/direct", StringComparison.Ordinal))
                return await base.SendAsync(request, cancellationToken);

            Started.TrySetResult(true);

            try
            {
                var connection = new SlowConnection(this, cancellationToken);
#if NET
                await request.Content!.CopyToAsync(connection, cancellationToken);
#else
                await request.Content!.CopyToAsync(connection);
#endif
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new InvalidOperationException("The request was not stopped.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Volatile.Write(ref _isStopped, 1);
                throw;
            }
        }

        private sealed class SlowConnection(SlowBodyHandler handler, CancellationToken cancellationToken) : Stream
        {
            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken writeCancellationToken)
            {
                for (int sent = 0; sent < count; sent += 64 * 1024)
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, writeCancellationToken);
                    await Task.Delay(TimeSpan.FromMilliseconds(200), linked.Token);
                    Interlocked.Add(ref handler._bytesSent, Math.Min(64 * 1024, count - sent));
                }
            }
        }
    }

    /// <summary>
    /// A stream of a length that fails when it is read.
    /// </summary>
    private sealed class FailingStream : MemoryStream
    {
        public FailingStream() : base(new byte[10])
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("The file can't be read.");

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            throw new IOException("The file can't be read.");

#if NET
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("The file can't be read.");
#endif
    }
}
