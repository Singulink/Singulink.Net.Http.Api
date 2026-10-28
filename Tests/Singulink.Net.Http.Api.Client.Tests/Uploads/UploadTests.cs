using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Singulink.Net.Http.Api.DataContracts;

namespace Singulink.Net.Http.Api.Client;

[PrefixTestClass]
public sealed class UploadTests
{
    private const string Content = "0123456789";
    private const int KiB = 1024;

    public TestContext TestContext { get; set; } = null!;

    private CancellationToken CancellationToken => TestContext.CancellationToken;

    [TestMethod]
    public async Task Upload_CreatesTheUploadSendsItsChunksInOrderAndCompletesIt()
    {
        var service = new FakeUploadService();
        var client = new TestApiClient(service, sessionToken: "token");

        var result = await client.UploadFileAsync<TestItem>(
            "files", TextSource(Content), [("folderId", 5), ("skip", null)], options: Options(), cancellationToken: CancellationToken);

        result.ShouldBe(new TestItem(1, Content));

        var requests = service.Requests;
        requests.Select(r => r.Kind).ShouldBe([
            UploadRequestKind.Create, UploadRequestKind.Chunk, UploadRequestKind.Chunk, UploadRequestKind.Chunk, UploadRequestKind.Complete]);

        requests[0].Uri.PathAndQuery.ShouldBe("/v1/files/uploads?folderId=5");
        requests[0].ContentType.ShouldBe("application/json");
        requests[1].Uri.PathAndQuery.ShouldBe("/v1/files/uploads/upload-1%2F%2B");
        requests.Skip(1).Take(3).Select(r => r.ContentRange!.ToString()).ShouldBe(["bytes 0-3/10", "bytes 4-7/10", "bytes 8-9/10"]);
        requests.Skip(1).Take(3).ShouldAllBe(r => r.Method == HttpMethod.Put && r.ContentType == "application/octet-stream");
        requests[4].Uri.PathAndQuery.ShouldBe("/v1/files/uploads/upload-1%2F%2B/complete?folderId=5");
        requests.ShouldAllBe(r => r.Cookie == "__Host-session-token=token");

        var upload = service.SingleUpload;
        upload.Text.ShouldBe(Content);
        upload.Command.FileName.ShouldBe("notes.txt");
        upload.Command.Length.ShouldBe(10);
        upload.Command.ContentType.ShouldBe("text/plain");
        upload.Command.ChunkSize.ShouldBeNull();
        upload.Command.Command.ShouldBeNull();
        upload.IsAborted.ShouldBeFalse();
    }

    [TestMethod]
    public async Task Upload_SendsTheCommandAndChunkSizeAndTheUserIdPreconditionWithEveryRequest()
    {
        var service = new FakeUploadService();
        var child = new TestApiClient(new TestApiClient(service), userIdPrecondition: 42);

        await child.UploadFileAsync<TestItem>(
            "files/", TextSource(Content), command: new TestItem(7, "Receipts"), options: Options(chunkSize: 3), cancellationToken: CancellationToken);

        var upload = service.SingleUpload;
        upload.ChunkSize.ShouldBe(3);
        upload.Command.ChunkSize.ShouldBe(3);
        upload.Command.Command.ShouldNotBeNull().GetRawText().ShouldBe("""{"id":7,"name":"Receipts"}""");

        service.RequestsOf(UploadRequestKind.Chunk).Count().ShouldBe(4);
        service.Requests.ShouldAllBe(r => r.Uri.Query.EndsWith("if-userId=42", StringComparison.Ordinal));
        service.Requests[0].Uri.AbsolutePath.ShouldBe("/v1/files/uploads");
    }

    [TestMethod]
    public async Task EmptyFile_IsCompletedWithoutChunks()
    {
        var service = new FakeUploadService();
        var client = new TestApiClient(service);

        var result = await client.UploadFileAsync<TestItem>("files", TextSource(string.Empty), options: Options(), cancellationToken: CancellationToken);

        result.Name.ShouldBeEmpty();
        service.Requests.Select(r => r.Kind).ShouldBe([UploadRequestKind.Create, UploadRequestKind.Complete]);
    }

    [TestMethod]
    public async Task CreationWhoseResponseIsLost_IsMadeAgainWithTheSameIdempotencyKey_AndGetsTheSameUpload()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var service = new FakeUploadService();
        var client = new TestApiClient(service);
        int creates = 0;

        // The service creates the upload, but the response is lost.
        service.After = (request, response) => request.Kind is UploadRequestKind.Create && Interlocked.Increment(ref creates) is 1
            ? throw new HttpRequestException("The connection was reset.")
            : response;

        var upload = client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(time), cancellationToken: CancellationToken);
        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(1));

        (await WithinTimeout(upload)).Name.ShouldBe(Content);

        string?[] keys = [.. service.RequestsOf(UploadRequestKind.Create).Select(r => ReadCreation(r).IdempotencyKey)];
        keys.Length.ShouldBe(2);
        keys[0].ShouldNotBeNullOrEmpty();
        keys[0]!.Length.ShouldBeLessThanOrEqualTo(128);
        keys[1].ShouldBe(keys[0]);

        service.SingleUpload.Text.ShouldBe(Content);
        service.Requests.Select(r => r.Kind).Skip(2).ShouldBe([
            UploadRequestKind.Chunk, UploadRequestKind.Chunk, UploadRequestKind.Chunk, UploadRequestKind.Complete]);
    }

    [TestMethod]
    public async Task EachUpload_HasAnIdempotencyKeyOfItsOwn()
    {
        var service = new FakeUploadService();
        var client = new TestApiClient(service);
        var items = client.UploadFileStreamingAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken);

        await client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken);
        await client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken);
        await ReadAllAsync(items);
        await ReadAllAsync(items);

        service.Uploads.Count.ShouldBe(4);
        service.Uploads.Select(u => u.Command.IdempotencyKey).Distinct().Count().ShouldBe(4);
    }

    [TestMethod]
    public async Task CreationPending_IsMadeAgainAfterItsRetryAfter()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var service = new FakeUploadService();
        var client = new TestApiClient(service);
        int creates = 0;

        service.Before = (request, _) => Task.FromResult(request.Kind is UploadRequestKind.Create && Interlocked.Increment(ref creates) is 1
            ? ApiErrorResponse.Create(new ServiceUnavailableApiException("The upload is still being created.") {
                ErrorCode = UploadErrorCodes.CreationPending, RetryAfter = TimeSpan.FromSeconds(1),
            })
            : null);

        var upload = client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(time), cancellationToken: CancellationToken);
        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(1));

        (await WithinTimeout(upload)).Name.ShouldBe(Content);
        creates.ShouldBe(2);
        service.RequestsOf(UploadRequestKind.Create).Select(r => ReadCreation(r).IdempotencyKey).Distinct().Count().ShouldBe(1);
    }

    [TestMethod]
    public async Task KeyConflict_IsThrownWithoutRetrying()
    {
        var service = new FakeUploadService();
        var client = new TestApiClient(service);

        service.Before = (request, _) => Task.FromResult<HttpResponseMessage?>(
            ApiErrorResponse.Create(new ConflictApiException("The key is the key of another upload.") { ErrorCode = UploadErrorCodes.KeyConflict }));

        var ex = await Should.ThrowAsync<ConflictApiException>(
            () => client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken));

        ex.ErrorCode.ShouldBe(UploadErrorCodes.KeyConflict);
        service.Requests.Select(r => r.Kind).ShouldBe([UploadRequestKind.Create]);
    }

    [TestMethod]
    [DataRow("files?folderId=5")]
    [DataRow("files#top")]
    [DataRow("../files")]
    [DataRow("https://other.example.com/files")]
    public void PathWithQueryOrFragment_OrOutsideTheBaseAddress_ThrowsWhenTheMethodIsCalled(string path)
    {
        var client = new TestApiClient(new FakeUploadService());

        Should.Throw<ArgumentException>(() => client.UploadFileAsync<TestItem>(path, TextSource(Content))).ParamName.ShouldBe("path");
        Should.Throw<ArgumentException>(() => client.UploadFileStreamingAsync<TestItem>(path, TextSource(Content))).ParamName.ShouldBe("path");
    }

    [TestMethod]
    public async Task Completion_IsNotLimitedByTheRequestTimeout()
    {
        var service = new FakeUploadService();
        var client = new TestApiClient(service) { Timeout = TimeSpan.FromMilliseconds(200) };
        var completionStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        service.Before = async (request, cancellationToken) => {
            if (request.Kind is UploadRequestKind.Complete)
            {
                completionStarted.TrySetResult(true);
                await Task.WhenAny(releaseCompletion.Task, Task.Delay(Timeout.Infinite, cancellationToken));
                cancellationToken.ThrowIfCancellationRequested();
            }
            else if (request.Kind is UploadRequestKind.Other)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return null;
        };

        var upload = client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken);
        await WithinTimeout(completionStarted.Task);

        // A request that starts after the completion reaches the request timeout while the completion goes on.
        await Should.ThrowAsync<TaskCanceledException>(() => client.SendRequestAsync(client.CreateGetRequest($"other")));
        releaseCompletion.SetResult(true);

        (await WithinTimeout(upload)).Name.ShouldBe(Content);
        service.RequestsOf(UploadRequestKind.Complete).ShouldHaveSingleItem();
    }

    [TestMethod]
    public async Task FailedChunk_IsSentAgainFromItsBufferWithBackoff()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var service = new FakeUploadService();
        var client = new TestApiClient(service);
        var reads = new List<(long Offset, int Length)>();
        int failures = 0;

        service.Before = (request, _) => {
            if (request.Kind is UploadRequestKind.Chunk && request.ContentRange!.From is 4 && ++failures <= 2)
                throw new HttpRequestException("The connection was reset.");

            return Task.FromResult<HttpResponseMessage?>(null);
        };

        var upload = client.UploadFileAsync<TestItem>("files", RecordingSource(Content, reads), options: Options(time), cancellationToken: CancellationToken);

        // The first wait is at most 1 second and the second at most 2.
        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(1));
        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(2));

        (await WithinTimeout(upload)).Name.ShouldBe(Content);
        reads.ShouldBe([(0, 4), (4, 4), (8, 2)]);
        service.RequestsOf(UploadRequestKind.Chunk).Count(r => r.ContentRange!.From is 4).ShouldBe(3);
    }

    [TestMethod]
    public async Task RetryAfter_IsWaitedFor()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var service = new FakeUploadService();
        var client = new TestApiClient(service);
        int creates = 0;

        service.Before = (request, _) => Task.FromResult(request.Kind is UploadRequestKind.Create && Interlocked.Increment(ref creates) is 1
            ? ApiErrorResponse.Create(
                new ServiceUnavailableApiException("The uploads are full.") { ErrorCode = UploadErrorCodes.StorageFull, RetryAfter = TimeSpan.FromSeconds(30) })
            : null);

        var upload = client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(time), cancellationToken: CancellationToken);

        // The wait of 30 seconds is still pending after 29.
        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(29));
        time.PendingTimerCount.ShouldBe(1);
        Volatile.Read(ref creates).ShouldBe(1);

        time.Advance(TimeSpan.FromSeconds(1));
        (await WithinTimeout(upload)).Name.ShouldBe(Content);
        creates.ShouldBe(2);
    }

    public static IEnumerable<object[]> RetriedChunkFailures => [
        [new ApiException(HttpStatusCode.RequestTimeout, "The request body arrived too slowly.")],
        [new TooManyRequestsApiException("Too many requests.") { ErrorCode = ApiErrorCodes.RateLimited, RetryAfter = TimeSpan.FromSeconds(1) }],
        [new ServiceUnavailableApiException("Starting.")],
        [new ApiException(HttpStatusCode.BadGateway, "Bad gateway.")],
    ];

    [TestMethod]
    [DynamicData(nameof(RetriedChunkFailures))]
    public async Task ChunkFailure_IsRetried(ApiException failure)
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var service = new FakeUploadService();
        var client = new TestApiClient(service);
        int chunks = 0;

        service.Before = (request, _) => Task.FromResult(
            request.Kind is UploadRequestKind.Chunk && Interlocked.Increment(ref chunks) is 1 ? ApiErrorResponse.Create(failure) : null);

        var upload = client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(time), cancellationToken: CancellationToken);
        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(1));

        (await WithinTimeout(upload)).Name.ShouldBe(Content);
        chunks.ShouldBe(4);
    }

    [TestMethod]
    public async Task TimedOutChunk_IsRetried()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var service = new FakeUploadService();
        var client = new TestApiClient(service);
        int chunks = 0;

        service.Before = async (request, cancellationToken) => {
            if (request.Kind is UploadRequestKind.Chunk && Interlocked.Increment(ref chunks) is 1)
                await Task.Delay(Timeout.Infinite, cancellationToken);

            return null;
        };

        var options = new UploadOptions {
            ChunkTimeout = TimeSpan.FromMilliseconds(200), TimeProvider = time, CompletionIdleTimeout = Timeout.InfiniteTimeSpan,
        };

        var upload = client.UploadFileAsync<TestItem>("files", TextSource(Content), options: options, cancellationToken: CancellationToken);

        // The chunk timeout passes, and the wait before the chunk is sent again.
        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(1));
        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(1));

        (await WithinTimeout(upload)).Name.ShouldBe(Content);
        chunks.ShouldBe(4);
    }

    [TestMethod]
    public async Task ChunkTimeout_RunsWhileTheChunkSendsNothing()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var service = new FakeUploadService { DefaultChunkSize = 160 * KiB };
        var gate = new ChunkWriteGate(service);
        var client = new TestApiClient(gate);

        var options = new UploadOptions {
            TimeProvider = time, ChunkTimeout = TimeSpan.FromMinutes(1), CompletionIdleTimeout = Timeout.InfiniteTimeSpan, CountsWrittenBytes = true,
        };

        var upload = client.UploadFileAsync<TestItem>("files", BytesSource(200 * KiB), options: options, cancellationToken: CancellationToken);

        // The slices of the first chunk, of 64, 64 and 32 KiB, go out 50 seconds apart, which takes longer than the timeout in all.
        for (int slice = 0; slice < 3; slice++)
        {
            await gate.WaitForWriteAsync();
            time.Advance(TimeSpan.FromSeconds(50));
            gate.Release();
        }

        // The second chunk sends nothing for a minute, which ends its request, and it is sent again a second later.
        await gate.WaitForWriteAsync();
        time.Advance(TimeSpan.FromMinutes(1));
        gate.Open();
        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(1));

        await WithinTimeout(upload);
        (gate.AttemptsOf(0), gate.AttemptsOf(160 * KiB)).ShouldBe((1, 2));
    }

    [TestMethod]
    public async Task TooManyRequestsWithoutRetryAfter_FailsWithoutRetrying()
    {
        var service = new FakeUploadService();
        var client = new TestApiClient(service);

        service.Before = (request, _) => Task.FromResult<HttpResponseMessage?>(
            ApiErrorResponse.Create(new TooManyRequestsApiException("Too many uploads.") { ErrorCode = UploadErrorCodes.LimitReached }));

        var ex = await Should.ThrowAsync<TooManyRequestsApiException>(
            () => client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken));

        ex.ErrorCode.ShouldBe(UploadErrorCodes.LimitReached);
        service.Requests.Select(r => r.Kind).ShouldBe([UploadRequestKind.Create]);
    }

    [TestMethod]
    public async Task FailuresForLongerThanTheRetryTimeout_AreThrownAndAbortTheUpload()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var service = new FakeUploadService();
        var client = new TestApiClient(service);
        int chunks = 0;

        service.Before = (request, _) => {
            if (request.Kind is not UploadRequestKind.Chunk)
                return Task.FromResult<HttpResponseMessage?>(null);

            Interlocked.Increment(ref chunks);
            return Task.FromResult<HttpResponseMessage?>(
                ApiErrorResponse.Create(new ServiceUnavailableApiException("Busy.") { RetryAfter = TimeSpan.FromSeconds(4) }));
        };

        var options = new UploadOptions {
            RetryTimeout = TimeSpan.FromSeconds(10),
            TimeProvider = time,
            ChunkTimeout = Timeout.InfiniteTimeSpan,
            CompletionIdleTimeout = Timeout.InfiniteTimeSpan,
        };

        var upload = client.UploadFileAsync<TestItem>("files", TextSource(Content), options: options, cancellationToken: CancellationToken);

        // Attempts at 0, 4 and 8 seconds. Another wait would end 12 seconds after the upload was created.
        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(4));
        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(4));

        await Should.ThrowAsync<ServiceUnavailableApiException>(() => WithinTimeout(upload));
        chunks.ShouldBe(3);
        service.SingleUpload.IsAborted.ShouldBeTrue();
    }

    [TestMethod]
    public async Task Concurrency_SendsChunksAtTheSameTimeAndTakesAcknowledgementsInAnyOrder()
    {
        string content = string.Concat(Enumerable.Range(0, 9).Select(i => $"c{i}c{i}"));
        var service = new FakeUploadService();
        var client = new TestApiClient(service);
        var reads = new List<(long Offset, int Length)>();
        var acknowledged = new List<long>();
        var progress = new RecordingProgress<UploadProgress>();
        var firstThreeSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdChunkDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int inFlight = 0;
        int maxInFlight = 0;

        service.Before = async (request, _) => {
            if (request.Kind is UploadRequestKind.Chunk)
            {
                int count = Interlocked.Increment(ref inFlight);
                InterlockedMax(ref maxInFlight, count);

                if (count is 3)
                    firstThreeSent.TrySetResult(true);

                // The first three chunks are in flight together, and the first is acknowledged after the third.
                if (request.ContentRange!.From < 12)
                    await firstThreeSent.Task.WithTimeoutAsync();

                if (request.ContentRange.From is 0)
                    await thirdChunkDone.Task.WithTimeoutAsync();
            }

            return null;
        };

        service.After = (request, response) => {
            if (request.Kind is UploadRequestKind.Chunk)
            {
                lock (acknowledged)
                    acknowledged.Add(request.ContentRange!.From!.Value);

                if (request.ContentRange.From is 8)
                    thirdChunkDone.TrySetResult(true);

                Interlocked.Decrement(ref inFlight);
            }

            return response;
        };

        var options = new UploadOptions {
            MaxConcurrency = 3, Progress = progress, CompletionIdleTimeout = Timeout.InfiniteTimeSpan, CountsWrittenBytes = false,
        };

        var result = await WithinTimeout(
            client.UploadFileAsync<TestItem>("files", RecordingSource(content, reads), options: options, cancellationToken: CancellationToken));

        result.Name.ShouldBe(content);
        maxInFlight.ShouldBe(3);
        acknowledged.IndexOf(8).ShouldBeLessThan(acknowledged.IndexOf(0));
        reads.Select(r => r.Offset).ShouldBe([0, 4, 8, 12, 16, 20, 24, 28, 32]);

        // Changes made while another thread reports are reported together, as the latest progress.
        var reports = progress.Reports;
        reports.Select(p => p.BytesUploaded).ShouldBeInOrder(SortDirection.Ascending);
        reports.ShouldAllBe(p => p.BytesUploaded % 4 == 0);
        reports[^2].ShouldBe(new UploadProgress(36, 36, false));
        reports[^1].ShouldBe(new UploadProgress(36, 36, true));
    }

    [TestMethod]
    public async Task Progress_CountsTheBytesThatChunkRequestsHaveWritten()
    {
        var service = new FakeUploadService { DefaultChunkSize = 160 * KiB };
        var client = new TestApiClient(service);
        var progress = new RecordingProgress<UploadProgress>();

        var options = new UploadOptions { Progress = progress, CompletionIdleTimeout = Timeout.InfiniteTimeSpan, CountsWrittenBytes = true };
        await client.UploadFileAsync<TestItem>("files", BytesSource(200 * KiB), options: options, cancellationToken: CancellationToken);

        progress.Reports.ShouldBe([
            new(64 * KiB, 200 * KiB, false), new(128 * KiB, 200 * KiB, false), new(160 * KiB, 200 * KiB, false), new(200 * KiB, 200 * KiB, false),
            new(200 * KiB, 200 * KiB, true)]);
    }

    [TestMethod]
    public async Task ProgressInTheBrowser_CountsOnlyAcknowledgedChunks()
    {
        var service = new FakeUploadService { DefaultChunkSize = 160 * KiB };
        var client = new TestApiClient(service);
        var progress = new RecordingProgress<UploadProgress>();

        var options = new UploadOptions { Progress = progress, CompletionIdleTimeout = Timeout.InfiniteTimeSpan, CountsWrittenBytes = false };
        await client.UploadFileAsync<TestItem>("files", BytesSource(200 * KiB), options: options, cancellationToken: CancellationToken);

        progress.Reports.ShouldBe([new(160 * KiB, 200 * KiB, false), new(200 * KiB, 200 * KiB, false), new(200 * KiB, 200 * KiB, true)]);
    }

    [TestMethod]
    public void CountsWrittenBytes_IsOnByDefaultOutsideTheBrowser()
    {
        new UploadOptions().CountsWrittenBytes.ShouldBe(!OperatingSystem.IsBrowser());
    }

    [TestMethod]
    public async Task ChunkSentAgain_CountsItsBytesOnce()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var service = new FakeUploadService { DefaultChunkSize = 160 * KiB };
        var client = new TestApiClient(service);
        var progress = new RecordingProgress<UploadProgress>();
        int chunks = 0;

        // The service receives the first chunk, but its response is lost.
        service.After = (request, response) => request.Kind is UploadRequestKind.Chunk && Interlocked.Increment(ref chunks) is 1
            ? throw new HttpRequestException("The connection was reset.")
            : response;

        var options = new UploadOptions {
            Progress = progress, TimeProvider = time, ChunkTimeout = Timeout.InfiniteTimeSpan, CompletionIdleTimeout = Timeout.InfiniteTimeSpan,
            CountsWrittenBytes = true,
        };

        var upload = client.UploadFileAsync<TestItem>("files", BytesSource(200 * KiB), options: options, cancellationToken: CancellationToken);

        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(1));
        await WithinTimeout(upload);

        progress.Reports.Select(p => p.BytesUploaded / KiB).ShouldBe([64, 128, 160, 0, 64, 128, 160, 200, 200]);
        progress.Reports.Last().IsProcessing.ShouldBeTrue();
    }

    [TestMethod]
    public async Task SourceThatReadsAnotherSource_ReadsAndReleasesIt()
    {
        var service = new FakeUploadService();
        var client = new TestApiClient(service);
        var inner = new FailingReleaseSource(Encoding.UTF8.GetBytes(Content));

        var result = await client.UploadFileAsync<TestItem>("files", new MappingSource(inner), options: Options(), cancellationToken: CancellationToken);

        result.Name.ShouldBe(Content);
        inner.Releases.ShouldBe(1);
    }

    [TestMethod]
    public async Task ProgressThatThrowsWhileAChunkIsSent_FailsTheUploadWithItsException_AndIsNotReportedToAgain()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var service = new FakeUploadService { DefaultChunkSize = 160 * KiB };
        var client = new TestApiClient(service);

        var failure = new IOException("The progress could not be shown.");
        var progress = new ThrowingProgress(_ => failure);

        var options = new UploadOptions {
            Progress = progress, TimeProvider = time, ChunkTimeout = Timeout.InfiniteTimeSpan, CompletionIdleTimeout = Timeout.InfiniteTimeSpan,
            CountsWrittenBytes = true,
        };

        var upload = client.UploadFileAsync<TestItem>("files", BytesSource(200 * KiB), options: options, cancellationToken: CancellationToken);

        (await Should.ThrowAsync<IOException>(() => WithinTimeout(upload))).ShouldBeSameAs(failure);
        progress.Reports.ShouldBe(1);
        service.RequestsOf(UploadRequestKind.Abort).ShouldHaveSingleItem();
        service.RequestsOf(UploadRequestKind.Complete).ShouldBeEmpty();
    }

    [TestMethod]
    public async Task ProgressThatThrowsWhenTheUploadIsBeingCompleted_FailsTheUploadWithItsException_AndAbortsIt()
    {
        var service = new FakeUploadService();
        var client = new TestApiClient(service);
        var failure = new InvalidOperationException("The progress could not be shown.");
        var progress = new ThrowingProgress(p => p.IsProcessing ? failure : null);

        var options = new UploadOptions { Progress = progress, CompletionIdleTimeout = Timeout.InfiniteTimeSpan, CountsWrittenBytes = false };
        var upload = client.UploadFileAsync<TestItem>("files", TextSource(Content), options: options, cancellationToken: CancellationToken);

        (await Should.ThrowAsync<InvalidOperationException>(() => WithinTimeout(upload))).ShouldBeSameAs(failure);
        service.RequestsOf(UploadRequestKind.Abort).ShouldHaveSingleItem();
        service.RequestsOf(UploadRequestKind.Complete).ShouldBeEmpty();
    }

    [TestMethod]
    public async Task HandlerThatPingsAndThenCannotRun_IsRunAgainWithGrowingWaits_UntilTheRetryTimeout()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var service = new FakeUploadService();
        var client = new TestApiClient(service);

        // The handler works past the first ping and then finds that it can't run at the moment, every time it runs.
        service.Complete = _ => Responses.Stream(FakeUploadService.PingRecord, FakeUploadService.ErrorRecord(503, null, "The converter is down."));

        var upload = client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(time), cancellationToken: CancellationToken);
        await AdvanceUntilCompletedAsync(time, upload, toNextTimer: true);

        (await Should.ThrowAsync<ServiceUnavailableApiException>(() => upload)).Message.ShouldBe("The converter is down.");

        // Waits of 1 to 30 seconds, each reduced by up to half, add up to the retry timeout of 5 minutes within 9 to 26 completions.
        service.RequestsOf(UploadRequestKind.Complete).Count().ShouldBeInRange(9, 26);
    }

    [TestMethod]
    public async Task HandlerThatStreamsItemsAndThenCannotRun_IsRunAgainWithGrowingWaits_UntilTheRetryTimeout()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var service = new FakeUploadService();
        var client = new TestApiClient(service);

        // The handler reports its progress and then finds that it can't run at the moment, every time it runs.
        service.Complete = _ => Responses.Stream(
            FakeUploadService.ItemRecord(1, "importing"), FakeUploadService.ErrorRecord(503, null, "The converter is down."));

        var items = ReadAllAsync(
            client.UploadFileStreamingAsync<TestItem>("files", TextSource(Content), options: Options(time), cancellationToken: CancellationToken));
        await AdvanceUntilCompletedAsync(time, items, toNextTimer: true);

        (await Should.ThrowAsync<ServiceUnavailableApiException>(() => items)).Message.ShouldBe("The converter is down.");
        service.RequestsOf(UploadRequestKind.Complete).Count().ShouldBeInRange(9, 26);
    }

    [TestMethod]
    [DataRow(UploadErrorCodes.ServerStopping)]
    [DataRow(UploadErrorCodes.StoreUnavailable)]
    public async Task CompletionThatWaitedPastTheRetryTimeoutAndThenGetsAnErrorOfItsServer_IsMadeAgain(string errorCode)
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var service = new FakeUploadService();
        var client = new TestApiClient(service);

        // The completion waits with pings for the handler that runs on another server, for longer than the retry timeout, until its own server stops or
        // can't reach the store for a moment.
        service.Complete = completion => {
            if (completion.Index is 1)
            {
                time.Advance(TimeSpan.FromMinutes(10));
                return Responses.Stream(FakeUploadService.PingRecord, FakeUploadService.ErrorRecord(503, errorCode, "Not at the moment.", retryAfter: 1));
            }

            return Responses.Stream(FakeUploadService.ItemRecord(1, "done"), FakeUploadService.EndRecord);
        };

        var upload = client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(time), cancellationToken: CancellationToken);
        await AdvanceUntilCompletedAsync(time, upload);

        (await upload).Name.ShouldBe("done");
        service.RequestsOf(UploadRequestKind.Complete).Count().ShouldBe(2);
        service.SingleUpload.IsAborted.ShouldBeFalse();
    }

    [TestMethod]
    public async Task DroppedCompletionStream_IsPickedUpAgain()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var service = new FakeUploadService();
        var client = new TestApiClient(service);

        service.Complete = completion => completion.Index is 1
            ? Responses.Stream(FakeUploadService.ItemRecord(1, "processing"), FakeUploadService.ItemRecord(2, "converting"))
            : Responses.Stream(FakeUploadService.ItemRecord(2, "converting"), FakeUploadService.ItemRecord(3, "done"), FakeUploadService.EndRecord);

        var items = ReadAllAsync(
            client.UploadFileStreamingAsync<TestItem>("files", TextSource(Content), options: Options(time), cancellationToken: CancellationToken));
        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(1));

        (await WithinTimeout(items)).Select(i => i.Id).ShouldBe([1, 2, 2, 3]);
        service.RequestsOf(UploadRequestKind.Complete).Count().ShouldBe(2);
        service.SingleUpload.IsAborted.ShouldBeFalse();
    }

    [TestMethod]
    public async Task DroppedCompletionStream_OfAResult_IsPickedUpAgain()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var service = new FakeUploadService();
        var client = new TestApiClient(service);

        service.Complete = completion => completion.Index is 1
            ? Responses.Stream(FakeUploadService.PingRecord)
            : Responses.Stream(FakeUploadService.ItemRecord(1, "done"), FakeUploadService.EndRecord);

        var upload = client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(time), cancellationToken: CancellationToken);
        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(1));

        (await WithinTimeout(upload)).ShouldBe(new TestItem(1, "done"));
    }

    [TestMethod]
    public async Task SilentCompletionStream_IsPickedUpAgainAfterTheIdleTimeout()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var service = new FakeUploadService();
        var client = new TestApiClient(service);
        var live = new LiveRecordStream();

        service.Complete = completion => completion.Index is 1
            ? live.CreateResponse()
            : Responses.Stream(FakeUploadService.ItemRecord(1, "done"), FakeUploadService.EndRecord);

        var options = new UploadOptions { TimeProvider = time, ChunkTimeout = Timeout.InfiniteTimeSpan };
        var upload = client.UploadFileAsync<TestItem>("files", TextSource(Content), options: options, cancellationToken: CancellationToken);

        live.WriteRecord(FakeUploadService.PingRecord);
        await WaitUntilAsync(() => live.RecordsRead is 1 && live.IsWaiting);

        // Each ping starts the 60 seconds again.
        time.Advance(TimeSpan.FromSeconds(59));
        live.WriteRecord(FakeUploadService.PingRecord);
        await WaitUntilAsync(() => live.RecordsRead is 2 && live.IsWaiting);

        // The stream is still read 59 seconds after the last ping.
        time.Advance(TimeSpan.FromSeconds(59));
        live.WriteRecord(FakeUploadService.PingRecord);
        await WaitUntilAsync(() => live.RecordsRead is 3 && live.IsWaiting);
        service.RequestsOf(UploadRequestKind.Complete).Count().ShouldBe(1);

        time.Advance(TimeSpan.FromSeconds(60));
        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(1));

        (await WithinTimeout(upload)).Name.ShouldBe("done");
        service.RequestsOf(UploadRequestKind.Complete).Count().ShouldBe(2);
    }

    [TestMethod]
    public async Task OverrideOfSendStreamingAsyncThatSendsACopy_KeepsTheCompletionAliveWithPings()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var service = new FakeUploadService();
        var client = new CopyingClient(service);
        var live = new LiveRecordStream();

        service.Complete = completion => completion.Index is 1
            ? live.CreateResponse()
            : Responses.Stream(FakeUploadService.ItemRecord(1, "again"), FakeUploadService.EndRecord);

        var options = new UploadOptions { TimeProvider = time, ChunkTimeout = Timeout.InfiniteTimeSpan };
        var items = client.UploadItemsAsync<TestItem>(TextSource(Content), options).GetAsyncEnumerator(CancellationToken);

        try
        {
            var next = items.MoveNextAsync().AsTask();

            // Each ping starts the 60 seconds again, also though the stream is the response of another request than the one the upload made.
            live.WriteRecord(FakeUploadService.PingRecord);
            await WaitUntilAsync(() => live.RecordsRead is 1 && live.IsWaiting);
            time.Advance(TimeSpan.FromSeconds(59));

            live.WriteRecord(FakeUploadService.PingRecord);
            await WaitUntilAsync(() => live.RecordsRead is 2 && live.IsWaiting);
            time.Advance(TimeSpan.FromSeconds(59));

            live.WriteRecord(FakeUploadService.ItemRecord(1, "done"));
            live.WriteRecord(FakeUploadService.EndRecord);

            (await WithinTimeout(next)).ShouldBeTrue();
            items.Current.Name.ShouldBe("done");
        }
        finally
        {
            await items.DisposeAsync();
        }

        service.RequestsOf(UploadRequestKind.Complete).Count().ShouldBe(1);
    }

    [TestMethod]
    public async Task CompletionThatDoesNotStart_IsMadeAgainAfterTheIdleTimeout()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var service = new FakeUploadService();
        var client = new TestApiClient(service);
        int completions = 0;
        bool firstCancelled = false;

        service.Before = async (request, cancellationToken) => {
            if (request.Kind is UploadRequestKind.Complete && Interlocked.Increment(ref completions) is 1)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    firstCancelled = true;
                    throw;
                }
            }

            return null;
        };

        var options = new UploadOptions { TimeProvider = time, ChunkTimeout = Timeout.InfiniteTimeSpan };
        var upload = client.UploadFileAsync<TestItem>("files", TextSource(Content), options: options, cancellationToken: CancellationToken);

        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(60));
        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(1));

        (await WithinTimeout(upload)).Name.ShouldBe(Content);
        firstCancelled.ShouldBeTrue();
        completions.ShouldBe(2);
    }

    [TestMethod]
    public async Task ItemThatArrivesJustAsTheCompletionIsFoundSilent_IsYieldedOnce()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var service = new FakeUploadService();
        var client = new TestApiClient(service);
        var live = new LiveRecordStream();
        int reads = 0;

        service.Complete = completion => completion.Index is 1
            ? live.CreateResponse()
            : Responses.Stream(FakeUploadService.ItemRecord(1, "converting"), FakeUploadService.EndRecord);

        live.WriteRecord(FakeUploadService.ItemRecord(1, "converting"));

        // The idle timeout passes while the item is read, as when the stream was silent until just before it arrived.
        LateItemConverter.Reading.Value = () => {
            if (Interlocked.Increment(ref reads) is 1)
                time.Advance(TimeSpan.FromSeconds(61));
        };

        var options = new UploadOptions { TimeProvider = time, ChunkTimeout = Timeout.InfiniteTimeSpan };
        var items = ReadAllAsync(
            client.UploadFileStreamingAsync<LateItem>("files", TextSource(Content), options: options, cancellationToken: CancellationToken));

        await AdvanceUntilCompletedAsync(time, items);

        (await items).Select(i => i.Id).ShouldBe([1]);
        service.RequestsOf(UploadRequestKind.Complete).Count().ShouldBe(2);
    }

    [TestMethod]
    public async Task TimeTheAppTakesWithAnItem_IsNotSilence()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var service = new FakeUploadService();
        var client = new TestApiClient(service);
        var live = new LiveRecordStream();

        service.Complete = _ => live.CreateResponse();
        live.WriteRecord(FakeUploadService.ItemRecord(1, "converting"));

        var options = new UploadOptions { TimeProvider = time, ChunkTimeout = Timeout.InfiniteTimeSpan };
        var items = client.UploadFileStreamingAsync<TestItem>("files", TextSource(Content), options: options).GetAsyncEnumerator(CancellationToken);

        try
        {
            (await WithinTimeout(items.MoveNextAsync().AsTask())).ShouldBeTrue();
            items.Current.Id.ShouldBe(1);

            time.Advance(TimeSpan.FromMinutes(2));

            live.WriteRecord(FakeUploadService.ItemRecord(2, "done"));
            live.WriteRecord(FakeUploadService.EndRecord);

            (await WithinTimeout(items.MoveNextAsync().AsTask())).ShouldBeTrue();
            items.Current.Id.ShouldBe(2);
            (await WithinTimeout(items.MoveNextAsync().AsTask())).ShouldBeFalse();
        }
        finally
        {
            await items.DisposeAsync();
        }

        service.RequestsOf(UploadRequestKind.Complete).Count().ShouldBe(1);
    }

    [TestMethod]
    public async Task IncompleteUpload_SendsTheMissingChunksAndCompletesAgain()
    {
        var service = new FakeUploadService();
        var client = new TestApiClient(service);
        var progress = new RecordingProgress<UploadProgress>();
        var opened = new List<ForwardOnlyStream>();
        bool firstStreamDisposedAtCompletion = false;
        int completions = 0;

        service.Before = (request, _) => {
            if (request.Kind is UploadRequestKind.Complete && Interlocked.Increment(ref completions) is 1)
            {
                firstStreamDisposedAtCompletion = opened[0].IsDisposed;
                service.SingleUpload.MarkMissing(1);
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        };

        var source = UploadSource.FromStreamFactory("notes.txt", 10, _ => {
            var stream = new ForwardOnlyStream(Encoding.UTF8.GetBytes(Content));
            opened.Add(stream);

            return Task.FromResult<Stream>(stream);
        });

        var options = new UploadOptions { Progress = progress, CompletionIdleTimeout = Timeout.InfiniteTimeSpan, CountsWrittenBytes = false };
        var result = await client.UploadFileAsync<TestItem>("files", source, options: options, cancellationToken: CancellationToken);

        result.Name.ShouldBe(Content);
        service.Requests.Select(r => r.Kind).ShouldBe([
            UploadRequestKind.Create, UploadRequestKind.Chunk, UploadRequestKind.Chunk, UploadRequestKind.Chunk, UploadRequestKind.Complete,
            UploadRequestKind.Status, UploadRequestKind.Chunk, UploadRequestKind.Complete]);

        service.RequestsOf(UploadRequestKind.Chunk).Last().ContentRange!.ToString().ShouldBe("bytes 4-7/10");

        // The source opens a stream for the chunks and releases it once they are sent, and then opens another for the missing chunk.
        firstStreamDisposedAtCompletion.ShouldBeTrue();
        opened.Count.ShouldBe(2);
        opened[1].IsDisposed.ShouldBeTrue();

        progress.Reports.ShouldBe([new(4, 10, false), new(8, 10, false), new(10, 10, false), new(10, 10, true), new(6, 10, false), new(10, 10, false),
            new(10, 10, true)]);
    }

    [TestMethod]
    public async Task IncompleteUploadWithNothingMissing_IsCompletedAgainAfterAWait()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var service = new FakeUploadService();
        var client = new TestApiClient(service);
        int completions = 0;

        service.Before = (request, _) => Task.FromResult(request.Kind is UploadRequestKind.Complete && Interlocked.Increment(ref completions) is 1
            ? ApiErrorResponse.Create(new ConflictApiException("Chunks are missing.") { ErrorCode = UploadErrorCodes.Incomplete })
            : null);

        var upload = client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(time), cancellationToken: CancellationToken);
        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(1));

        (await WithinTimeout(upload)).Name.ShouldBe(Content);
        service.Requests.Select(r => r.Kind).Skip(4).ShouldBe([UploadRequestKind.Complete, UploadRequestKind.Status, UploadRequestKind.Complete]);
    }

    [TestMethod]
    public async Task StatusOfAnotherChunkSize_FailsTheUpload()
    {
        var service = new FakeUploadService();
        var client = new TestApiClient(service);

        service.Before = (request, _) => Task.FromResult(request.Kind switch {
            UploadRequestKind.Complete => ApiErrorResponse.Create(new ConflictApiException("Chunks are missing.") { ErrorCode = UploadErrorCodes.Incomplete }),
            UploadRequestKind.Status => Responses.Json(
                """{"state":"Receiving","length":10,"chunkSize":8,"missingChunks":[{"first":0,"last":0}],"expiresUtc":"2030-01-01T00:00:00Z"}"""),
            _ => null,
        });

        var ex = await Should.ThrowAsync<FormatException>(
            () => client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken));

        ex.Message.ShouldBe("The status of the upload has a length of 10 bytes and chunks of 8 bytes, rather than the upload's 10 and 4 bytes.");
        service.SingleUpload.IsAborted.ShouldBeTrue();
    }

    [TestMethod]
    public async Task UploadThatStaysIncomplete_FailsAfterFiveRounds()
    {
        var service = new FakeUploadService();
        var client = new TestApiClient(service);

        service.Before = (request, _) => {
            if (request.Kind is UploadRequestKind.Complete)
                service.SingleUpload.MarkMissing(0);

            return Task.FromResult<HttpResponseMessage?>(null);
        };

        var ex = await Should.ThrowAsync<ConflictApiException>(
            () => client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken));

        ex.ErrorCode.ShouldBe(UploadErrorCodes.Incomplete);
        service.RequestsOf(UploadRequestKind.Complete).Count().ShouldBe(6);
        service.SingleUpload.IsAborted.ShouldBeTrue();
    }

    [TestMethod]
    public async Task HandlersServiceUnavailable_IsRetried()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var service = new FakeUploadService();
        var client = new TestApiClient(service);

        service.Complete = completion => completion.Index is 1
            ? Responses.Stream(FakeUploadService.ItemRecord(1, "converting"), FakeUploadService.ErrorRecord(503, null, "The converter is busy."))
            : Responses.Stream(FakeUploadService.ItemRecord(2, "done"), FakeUploadService.EndRecord);

        var items = ReadAllAsync(
            client.UploadFileStreamingAsync<TestItem>("files", TextSource(Content), options: Options(time), cancellationToken: CancellationToken));
        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(1));

        (await WithinTimeout(items)).Select(i => i.Id).ShouldBe([1, 2]);
        service.SingleUpload.IsAborted.ShouldBeFalse();
    }

    [TestMethod]
    public async Task InterruptedHandler_FailsWithoutRetryingAndAbortsTheUpload()
    {
        var service = new FakeUploadService();
        var client = new TestApiClient(service);

        service.Complete = _ => Responses.Stream(
            FakeUploadService.ItemRecord(1, "converting"), FakeUploadService.ErrorRecord(500, UploadErrorCodes.Interrupted, "The upload was interrupted."));

        var received = new List<TestItem>();
        var ex = await Should.ThrowAsync<ServerErrorApiException>(() => ReadAllAsync(
            client.UploadFileStreamingAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken), received));

        ex.ErrorCode.ShouldBe(UploadErrorCodes.Interrupted);
        received.Select(i => i.Id).ShouldBe([1]);
        service.RequestsOf(UploadRequestKind.Complete).Count().ShouldBe(1);
        service.SingleUpload.IsAborted.ShouldBeTrue();
    }

    [TestMethod]
    public async Task HandlersError_IsThrownAndAbortsTheUpload()
    {
        var service = new FakeUploadService();
        var client = new TestApiClient(service);

        service.Complete = _ => ApiErrorResponse.Create(new ValidationApiException("The video can't be decoded."));

        var ex = await Should.ThrowAsync<ValidationApiException>(
            () => client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken));

        ex.Message.ShouldBe("The video can't be decoded.");
        service.RequestsOf(UploadRequestKind.Complete).Count().ShouldBe(1);
        service.SingleUpload.IsAborted.ShouldBeTrue();
    }

    [TestMethod]
    public async Task ResultUploadWhoseHandlerStreamsItems_ThrowsFormatException()
    {
        var service = new FakeUploadService();
        var client = new TestApiClient(service);

        service.Complete = _ => Responses.Stream(
            FakeUploadService.ItemRecord(1, "a"), FakeUploadService.ItemRecord(2, "b"), FakeUploadService.EndRecord);

        await Should.ThrowAsync<FormatException>(
            () => client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken));
    }

    [TestMethod]
    public async Task ResultUploadWithoutResult_ThrowsFormatException()
    {
        var service = new FakeUploadService();
        var client = new TestApiClient(service);

        service.Complete = _ => Responses.Stream(FakeUploadService.EndRecord);

        await Should.ThrowAsync<FormatException>(
            () => client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken));

        service.SingleUpload.IsAborted.ShouldBeFalse();
    }

    [TestMethod]
    public async Task EachEnumeration_UploadsTheFileAgain()
    {
        var service = new FakeUploadService();
        var client = new TestApiClient(service);
        var items = client.UploadFileStreamingAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken);

        service.Requests.ShouldBeEmpty();

        (await ReadAllAsync(items)).ShouldHaveSingleItem().Name.ShouldBe(Content);
        (await ReadAllAsync(items)).ShouldHaveSingleItem().Name.ShouldBe(Content);

        service.Uploads.Count.ShouldBe(2);
    }

    [TestMethod]
    public async Task EndingTheEnumerationEarly_DoesNotAbortTheUpload()
    {
        var service = new FakeUploadService();
        var client = new TestApiClient(service);

        service.Complete = _ => Responses.Stream(FakeUploadService.ItemRecord(1, "a"), FakeUploadService.ItemRecord(2, "b"), FakeUploadService.EndRecord);

        var items = client.UploadFileStreamingAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken);

        await foreach (var item in items)
            break;

        service.Requests.ShouldNotContain(r => r.Kind == UploadRequestKind.Abort);
    }

    [TestMethod]
    public async Task Cancellation_AbortsTheUploadAndReleasesTheSource()
    {
        var service = new FakeUploadService();
        var client = new TestApiClient(service);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        var opened = new List<ForwardOnlyStream>();

        service.Before = async (request, cancellationToken) => {
            if (request.Kind is UploadRequestKind.Chunk && request.ContentRange!.From is 4)
            {
                cancellation.Cancel();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return null;
        };

        var source = UploadSource.FromStreamFactory("notes.txt", 10, _ => {
            var stream = new ForwardOnlyStream(Encoding.UTF8.GetBytes(Content));
            opened.Add(stream);

            return Task.FromResult<Stream>(stream);
        });

        await Should.ThrowAsync<OperationCanceledException>(
            () => WithinTimeout(client.UploadFileAsync<TestItem>("files", source, options: Options(), cancellationToken: cancellation.Token)));

        await WaitUntilAsync(() => service.SingleUpload.IsAborted);
        service.Requests.Last().Kind.ShouldBe(UploadRequestKind.Abort);
        opened.ShouldHaveSingleItem().IsDisposed.ShouldBeTrue();
    }

    [TestMethod]
    public async Task CancellationWhileARequestFails_ThrowsOperationCanceledException()
    {
        var service = new FakeUploadService();
        var client = new TestApiClient(service);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        byte[] bytes = Encoding.UTF8.GetBytes(Content);

        // The source fails to read just as the upload is cancelled.
        var source = UploadSource.Create("notes.txt", 10, (offset, buffer, _) => {
            if (offset >= 4)
            {
                cancellation.Cancel();
                throw new IOException("The file can't be read.");
            }

            bytes.AsSpan((int)offset, buffer.Length).CopyTo(buffer.Span);
            return new ValueTask<int>(buffer.Length);
        });

        // Awaited without Shouldly, which replaces the exception of a cancelled task with one of its own.
        OperationCanceledException? ex = null;

        try
        {
            await WithinTimeout(client.UploadFileAsync<TestItem>("files", source, options: Options(), cancellationToken: cancellation.Token));
        }
        catch (OperationCanceledException caught)
        {
            ex = caught;
        }

        ex.ShouldNotBeNull().CancellationToken.ShouldBe(cancellation.Token);
        ex.InnerException.ShouldBeOfType<IOException>();
        await WaitUntilAsync(() => service.SingleUpload.IsAborted);
    }

    [TestMethod]
    public async Task Cancellation_DoesNotWaitForTheAbort()
    {
        var service = new FakeUploadService();
        var client = new TestApiClient(service);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        var abortStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var abortEnded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAbort = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        service.Before = async (request, cancellationToken) => {
            if (request.Kind is UploadRequestKind.Chunk)
            {
                cancellation.Cancel();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (request.Kind is UploadRequestKind.Abort)
            {
                abortStarted.TrySetResult(true);

                try
                {
                    await releaseAbort.Task.WithTimeoutAsync();
                }
                finally
                {
                    abortEnded.TrySetResult(true);
                }
            }

            return null;
        };

        await Should.ThrowAsync<OperationCanceledException>(
            () => WithinTimeout(client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: cancellation.Token)));

        // The upload threw while its abort was still being sent.
        await WithinTimeout(abortStarted.Task);
        abortEnded.Task.IsCompleted.ShouldBeFalse();

        releaseAbort.SetResult(true);
        await WaitUntilAsync(() => service.SingleUpload.IsAborted);
    }

    [TestMethod]
    public async Task CancellationDuringTheCompletion_AbortsTheUpload()
    {
        var service = new FakeUploadService();
        var client = new TestApiClient(service);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        var live = new LiveRecordStream();

        service.Complete = _ => live.CreateResponse();
        live.WriteRecord(FakeUploadService.ItemRecord(1, "converting"));

        var received = new List<TestItem>();

        await Should.ThrowAsync<OperationCanceledException>(async () => {
            var items = client.UploadFileStreamingAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: cancellation.Token);

            await foreach (var item in items)
            {
                received.Add(item);
                cancellation.Cancel();
            }
        });

        received.Select(i => i.Id).ShouldBe([1]);
        await WaitUntilAsync(() => service.SingleUpload.IsAborted);
    }

    [TestMethod]
    public async Task ChunkFailureWhileOthersAreInFlight_CancelsThemAndAbortsTheUpload()
    {
        var service = new FakeUploadService();
        var client = new TestApiClient(service);
        bool firstChunkCancelled = false;

        service.Before = async (request, cancellationToken) => {
            if (request.Kind is UploadRequestKind.Chunk && request.ContentRange!.From is 0)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    firstChunkCancelled = true;
                    throw;
                }
            }

            if (request.Kind is UploadRequestKind.Chunk && request.ContentRange!.From is 4)
                return ApiErrorResponse.Create(new BadRequestApiException("The chunk is not valid.") { ErrorCode = UploadErrorCodes.ChunkInvalid });

            return null;
        };

        var options = new UploadOptions { MaxConcurrency = 3, CompletionIdleTimeout = Timeout.InfiniteTimeSpan };

        await Should.ThrowAsync<BadRequestApiException>(
            () => WithinTimeout(client.UploadFileAsync<TestItem>("files", TextSource(Content), options: options, cancellationToken: CancellationToken)));

        firstChunkCancelled.ShouldBeTrue();
        service.Requests.Last().Kind.ShouldBe(UploadRequestKind.Abort);
    }

    [TestMethod]
    public async Task AbortThatFails_LeavesTheUploadsFailure()
    {
        var service = new FakeUploadService();
        var client = new TestApiClient(service);

        service.Before = (request, _) => request.Kind switch {
            UploadRequestKind.Chunk => Task.FromResult<HttpResponseMessage?>(ApiErrorResponse.Create(new ForbiddenApiException("Not allowed."))),
            UploadRequestKind.Abort => throw new HttpRequestException("The connection was reset."),
            _ => Task.FromResult<HttpResponseMessage?>(null),
        };

        var ex = await Should.ThrowAsync<ForbiddenApiException>(
            () => client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken));

        ex.Message.ShouldBe("Not allowed.");
        service.Requests.Last().Kind.ShouldBe(UploadRequestKind.Abort);
    }

    [TestMethod]
    public async Task NonTransientChunkFailure_IsThrownAndAbortsTheUpload()
    {
        var service = new FakeUploadService();
        var client = new TestApiClient(service);

        service.Before = (request, _) => Task.FromResult(request.Kind is UploadRequestKind.Chunk && request.ContentRange!.From is 4
            ? ApiErrorResponse.Create(new BadRequestApiException("The chunk is not valid.") { ErrorCode = UploadErrorCodes.ChunkInvalid })
            : null);

        var ex = await Should.ThrowAsync<BadRequestApiException>(
            () => client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken));

        ex.ErrorCode.ShouldBe(UploadErrorCodes.ChunkInvalid);
        service.Requests.Select(r => r.Kind).ShouldBe([UploadRequestKind.Create, UploadRequestKind.Chunk, UploadRequestKind.Chunk, UploadRequestKind.Abort]);
    }

    [TestMethod]
    public async Task SourceThatFailsToRead_IsThrownAndAbortsTheUpload()
    {
        var service = new FakeUploadService();
        var client = new TestApiClient(service);
        byte[] bytes = Encoding.UTF8.GetBytes(Content);

        var source = UploadSource.Create("notes.txt", 10, (offset, buffer, _) => {
            if (offset >= 4)
                throw new IOException("The file can't be read.");

            bytes.AsSpan((int)offset, buffer.Length).CopyTo(buffer.Span);
            return new ValueTask<int>(buffer.Length);
        });

        var ex = await Should.ThrowAsync<IOException>(
            () => client.UploadFileAsync<TestItem>("files", source, options: Options(), cancellationToken: CancellationToken));

        ex.Message.ShouldBe("The file can't be read.");
        service.Requests.Select(r => r.Kind).ShouldBe([UploadRequestKind.Create, UploadRequestKind.Chunk, UploadRequestKind.Abort]);
    }

    [TestMethod]
    public async Task SourceWhoseReleaseFails_IsUploadedAndCompleted()
    {
        var service = new FakeUploadService();
        var client = new TestApiClient(service);
        var source = new FailingReleaseSource(Encoding.UTF8.GetBytes(Content));

        var result = await client.UploadFileAsync<TestItem>("files", source, options: Options(), cancellationToken: CancellationToken);

        result.Name.ShouldBe(Content);
        source.Releases.ShouldBe(1);
        service.Requests.ShouldNotContain(r => r.Kind == UploadRequestKind.Abort);
    }

    [TestMethod]
    public async Task ChunkSizeOverTheLargest_IsNotValid()
    {
        var service = new FakeUploadService { DefaultChunkSize = (64 << 20) + 1 };
        var client = new TestApiClient(service);

        await Should.ThrowAsync<FormatException>(
            () => client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken));

        service.Requests.Select(r => r.Kind).ShouldBe([UploadRequestKind.Create, UploadRequestKind.Abort]);
    }

    [TestMethod]
    public async Task SignInRequired_EndsTheSessionWithoutAbortingTheUpload()
    {
        var service = new FakeUploadService();
        var client = new TestApiClient(service, sessionToken: "token");

        service.Before = (request, _) => Task.FromResult(request.Kind is UploadRequestKind.Chunk
            ? ApiErrorResponse.Create(new UnauthorizedApiException("Sign in again.") { ErrorCode = ApiErrorCodes.SignInRequired })
            : null);

        await Should.ThrowAsync<UnauthorizedApiException>(
            () => client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken));

        client.SessionEndedCount.ShouldBe(1);
        service.Requests.ShouldNotContain(r => r.Kind == UploadRequestKind.Abort);
    }

    [TestMethod]
    public async Task UserChanged_CarriesTheUserIdPrecondition_WithoutAbortingTheUpload()
    {
        var service = new FakeUploadService();
        var child = new TestApiClient(new TestApiClient(service, sessionToken: "token"), userIdPrecondition: 42);

        service.Before = (request, _) => Task.FromResult(request.Kind is UploadRequestKind.Chunk
            ? ApiErrorResponse.Create(new UserChangedApiException("The user changed."))
            : null);

        var ex = await Should.ThrowAsync<UserChangedApiException>(
            () => child.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken));

        ex.ExpectedUserId.ShouldBe("42");
        service.Requests.ShouldNotContain(r => r.Kind == UploadRequestKind.Abort);
    }

    [TestMethod]
    public async Task UserChangedInTheCompletionsStream_CarriesTheUserIdPrecondition()
    {
        var service = new FakeUploadService();
        var child = new TestApiClient(new TestApiClient(service, sessionToken: "token"), userIdPrecondition: 42);
        service.Complete = _ => Responses.Stream(FakeUploadService.PingRecord, FakeUploadService.ErrorRecord(412, null, "The user changed."));

        var ex = await Should.ThrowAsync<UserChangedApiException>(
            () => child.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken));

        ex.ExpectedUserId.ShouldBe("42");
        service.RequestsOf(UploadRequestKind.Complete).Count().ShouldBe(1);
        service.Requests.ShouldNotContain(r => r.Kind == UploadRequestKind.Abort);
    }

    [TestMethod]
    public async Task UploadNotFound_IsThrownWithoutAbortingTheUpload()
    {
        var service = new FakeUploadService();
        var client = new TestApiClient(service);

        service.Before = (request, _) => Task.FromResult(request.Kind is UploadRequestKind.Chunk
            ? ApiErrorResponse.Create(new NotFoundApiException("The upload does not exist.") { ErrorCode = UploadErrorCodes.NotFound })
            : null);

        await Should.ThrowAsync<NotFoundApiException>(
            () => client.UploadFileAsync<TestItem>("files", TextSource(Content), options: Options(), cancellationToken: CancellationToken));

        service.Requests.ShouldNotContain(r => r.Kind == UploadRequestKind.Abort);
    }

    /// <summary>
    /// A client whose override of <see cref="ApiClientBase.SendStreamingAsync{TItem}(HttpRequestMessage, object?, CancellationToken)"/> sends a copy of
    /// the request, without its options, as an override that makes requests again might.
    /// </summary>
    private sealed class CopyingClient : ApiClientBase
    {
        public CopyingClient(HttpMessageHandler handler) : base(TestJsonContext.Default, null, null)
        {
            HttpHandler = handler;
        }

        public IAsyncEnumerable<TItem> UploadItemsAsync<TItem>(UploadSource source, UploadOptions options) =>
            UploadStreamingAsync<TItem>("files", source, options: options);

        protected override Uri GetBaseAddress() => TestApiClient.DefaultBaseAddress;

        protected override async IAsyncEnumerable<TItem> SendStreamingAsync<TItem>(
            HttpRequestMessage request, object? content, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var copy = new HttpRequestMessage(request.Method, request.RequestUri);

            foreach (var header in request.Headers)
                copy.Headers.TryAddWithoutValidation(header.Key, header.Value);

            await foreach (var item in base.SendStreamingAsync<TItem>(copy, content, cancellationToken))
                yield return item;
        }
    }

    /// <summary>
    /// A progress that throws the exception that a function returns for a value, on the reporting thread, and counts the reports.
    /// </summary>
    private sealed class ThrowingProgress(Func<UploadProgress, Exception?> getException) : IProgress<UploadProgress>
    {
        private int _reports;

        public int Reports => Volatile.Read(ref _reports);

        public void Report(UploadProgress value)
        {
            Interlocked.Increment(ref _reports);

            if (getException(value) is { } exception)
                throw exception;
        }
    }

    /// <summary>
    /// A source of bytes whose release throws.
    /// </summary>
    private sealed class FailingReleaseSource(byte[] bytes) : UploadSource("notes.txt", bytes.Length)
    {
        private int _releases;

        public int Releases => Volatile.Read(ref _releases);

        public override ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            bytes.AsSpan((int)offset, buffer.Length).CopyTo(buffer.Span);
            return new ValueTask<int>(buffer.Length);
        }

        public override ValueTask ReleaseAsync()
        {
            Interlocked.Increment(ref _releases);
            throw new IOException("The file can't be closed.");
        }
    }

    /// <summary>
    /// A source that reads another, and reports the other's failed reads with an exception of its own.
    /// </summary>
    private sealed class MappingSource(UploadSource inner) : UploadSource(inner.FileName, inner.Length, inner.ContentType)
    {
        public override async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            try
            {
                return await inner.ReadAsync(offset, buffer, cancellationToken);
            }
            catch (IOException ex)
            {
                throw new InvalidDataException("The file could not be read.", ex);
            }
        }

        public override ValueTask ReleaseAsync() => inner.ReleaseAsync();
    }

    /// <summary>
    /// Advances the time whenever the upload waits on a timer, until the task completes: a second at a time, or with <paramref name="toNextTimer"/>,
    /// straight to the due time of the next timer, for an upload whose only timers are its waits before retries, which can add up to minutes.
    /// </summary>
    private async Task AdvanceUntilCompletedAsync(ManualTimeProvider time, Task task, bool toNextTimer = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        while (!task.IsCompleted)
        {
            if (toNextTimer)
                time.AdvanceToNextTimer();
            else if (time.PendingTimerCount > 0)
                time.Advance(TimeSpan.FromSeconds(1));

            await Task.Delay(10, timeout.Token);
        }
    }

    private static UploadOptions Options(ManualTimeProvider? time = null, int? chunkSize = null) =>
        new() { TimeProvider = time, ChunkTimeout = Timeout.InfiniteTimeSpan, ChunkSize = chunkSize, CompletionIdleTimeout = Timeout.InfiniteTimeSpan };

    private static CreateUploadCommand ReadCreation(UploadRequest request) =>
        JsonSerializer.Deserialize(request.Body!, UploadTestJsonContext.Default.CreateUploadCommand).ShouldNotBeNull();

    private static UploadSource TextSource(string text) =>
        UploadSource.FromStream(new MemoryStream(Encoding.UTF8.GetBytes(text)), "notes.txt", "text/plain");

    private static UploadSource BytesSource(int length) =>
        UploadSource.FromStream(new MemoryStream([.. Enumerable.Range(0, length).Select(i => (byte)i)]), "data.bin");

    private static UploadSource RecordingSource(string text, List<(long Offset, int Length)> reads)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);

        return UploadSource.Create("notes.txt", bytes.Length, (offset, buffer, _) => {
            lock (reads)
                reads.Add((offset, buffer.Length));

            bytes.AsSpan((int)offset, buffer.Length).CopyTo(buffer.Span);
            return new ValueTask<int>(buffer.Length);
        });
    }

    private static async Task<List<T>> ReadAllAsync<T>(IAsyncEnumerable<T> items, List<T>? received = null)
    {
        received ??= [];

        await foreach (var item in items)
            received.Add(item);

        return received;
    }

    private static void InterlockedMax(ref int location, int value)
    {
        int current;

        while ((current = Volatile.Read(ref location)) < value && Interlocked.CompareExchange(ref location, value, current) != current)
        {
        }
    }

    private async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    /// <summary>
    /// Waits until the upload waits on a timer of the time provider, such as before a retry, and then advances the time.
    /// </summary>
    private async Task AdvanceWhenWaitingAsync(ManualTimeProvider time, TimeSpan advance)
    {
        await WaitUntilAsync(() => time.PendingTimerCount > 0);
        time.Advance(advance);
    }

    private async Task<T> WithinTimeout<T>(Task<T> task)
    {
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10), CancellationToken)) != task)
            throw new ShouldAssertException("The task did not complete in time.");

        return await task;
    }
}
