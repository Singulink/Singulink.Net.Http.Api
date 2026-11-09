using System.Buffers.Text;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Singulink.Net.Http.Api.Client;
using Singulink.Net.Http.Api.Service.Testing;
using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service;

/// <content>
/// The toolkit's client uploading files that fit in one chunk directly, in one request: its result, the request made again, a response that breaks
/// off, and the uploads in chunks that it falls back to.
/// </content>
public sealed partial class UploadEndToEndTests
{
    [TestMethod]
    public async Task SmallFile_IsUploadedDirectly_WithOneRequestThatRunsTheValidatorAndTheHandler()
    {
        byte[] file = UploadTestHost.CreateFile(1000);
        int validations = 0;

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, ValidateAsync, HandleAsync));
        var faults = CreateFaults(host);
        var client = UploadClient.CreateWithDirectUploads(host, faults);

        var result = await client.UploadFileAsync<Note>(
            Source(file, "résumé 履歴書.pdf", "application/pdf"), [("tag", "q1")], new FolderCommand("Invoices"), DirectOptions()).WaitAsync(WaitTimeout);

        result.ShouldBe(new Note($"5 q1 Invoices résumé 履歴書.pdf application/pdf {file.Length} {UploadTestHost.Hash(file)}"));
        validations.ShouldBe(1);
        Describe(faults).ShouldBe(["POST direct?tag=q1"]);

        Task ValidateAsync(UploadRequest<FolderCommand> upload)
        {
            Interlocked.Increment(ref validations);
            return Task.CompletedTask;
        }

        static async Task<Note> HandleAsync(long folderId, string tag, UploadedFile<FolderCommand> file, CancellationToken cancellationToken)
        {
            string hash = await UploadTestHost.HashAsync(file, cancellationToken);
            return new Note($"{folderId} {tag} {file.Command.Title} {file.FileName} {file.ContentType} {file.Length} {hash}");
        }
    }

    [TestMethod]
    public async Task DirectUpload_OfAnEmptyFile_UploadsIt()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HashAsync));
        var faults = CreateFaults(host);
        var client = UploadClient.CreateWithDirectUploads(host, faults);

        var result = await client.UploadFileAsync<Note>(Source([]), options: DirectOptions()).WaitAsync(WaitTimeout);

        result.Text.ShouldBe($"file.bin 0 {UploadTestHost.Hash([])}");
        Describe(faults).ShouldBe(["POST direct"]);
    }

    [TestMethod]
    public async Task DirectUploadWhoseResponseIsLost_IsMadeAgainWithTheSameKey_AndTheHandlerRunsOnce()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        int runs = 0;

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, (UploadedFile file) => {
            Interlocked.Increment(ref runs);
            return new Note("done");
        }));

        var faults = CreateFaults(host).FailAfterSending(IsDirect);
        var client = UploadClient.CreateWithDirectUploads(host, faults);

        var upload = client.UploadFileAsync<Note>(Source(UploadTestHost.CreateFile(100)), options: DirectOptions(time));
        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(1));

        (await upload.WaitAsync(WaitTimeout)).ShouldBe(new Note("done"));
        Describe(faults).ShouldBe(["POST direct", "POST direct"]);
        runs.ShouldBe(1);
        host.CountUploads().ShouldBe(1);
    }

    [TestMethod]
    public async Task DirectUploadCutOffAfterItStarted_IsCompletedByTheUploadsPath()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var release = new SemaphoreSlim(0);
        int runs = 0;

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, StreamAsync));

        // The response is cut off after its first record, the ping that starts it with the upload's path.
        var faults = CreateFaults(host).CutResponseAfter(IsDirect, records: 1);
        var client = UploadClient.CreateWithDirectUploads(host, faults);

        var items = ReadAllAsync(client.UploadFileStreamingAsync<Note>(Source(UploadTestHost.CreateFile(100)), DirectOptions(time)));
        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(1));
        release.Release();

        (await items.WaitAsync(WaitTimeout)).ShouldBe([new Note("done")]);
        Describe(faults).ShouldBe(["POST direct", "POST complete"]);
        runs.ShouldBe(1);

        async IAsyncEnumerable<Note> StreamAsync(UploadedFile file, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref runs);
            await release.WaitAsync(cancellationToken);
            yield return new Note("done");
        }
    }

    [TestMethod]
    public async Task DirectUploadToAServiceWithSmallerChunks_FallsBackToChunks()
    {
        byte[] file = UploadTestHost.CreateFile(ChunkSize + 100);

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HashAsync), options => options.MaxChunkSize = ChunkSize);
        var faults = CreateFaults(host);
        var client = UploadClient.CreateWithDirectUploads(host, faults);

        var options = new UploadOptions { ChunkSize = 8 << 20, ChunkTimeout = Timeout.InfiniteTimeSpan, CompletionIdleTimeout = Timeout.InfiniteTimeSpan };
        (await client.UploadFileAsync<Note>(Source(file), options: options).WaitAsync(WaitTimeout)).Text.ShouldEndWith(UploadTestHost.Hash(file));

        Describe(faults).ShouldBe(["POST direct", "POST uploads", "PUT 0", "PUT 1", "POST complete"]);
    }

    [TestMethod]
    public async Task FileLargerThanAChunk_OrWithALongCommand_IsUploadedInChunks()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, (UploadedFile<FolderCommand> file) => new Note("done")));
        var faults = CreateFaults(host);
        var client = UploadClient.CreateWithDirectUploads(host, faults);

        await client.UploadFileAsync<Note>(Source(UploadTestHost.CreateFile(ChunkSize + 1)), command: new FolderCommand("a"), options: DirectOptions())
            .WaitAsync(WaitTimeout);

        await client.UploadFileAsync<Note>(Source(UploadTestHost.CreateFile(10)), command: new FolderCommand(new string('a', 7000)), options: DirectOptions())
            .WaitAsync(WaitTimeout);

        Describe(faults).ShouldBe(["POST uploads", "PUT 0", "PUT 1", "POST complete", "POST uploads", "PUT 0", "POST complete"]);
    }

    [TestMethod]
    public async Task DirectUpload_RejectedByTheValidator_ThrowsItsError()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(
            Pattern, (Action<UploadRequest>)(_ => throw new ValidationApiException("Not this file.")), HashAsync));

        var faults = CreateFaults(host);
        var client = UploadClient.CreateWithDirectUploads(host, faults);

        var ex = await Should.ThrowAsync<ValidationApiException>(
            () => client.UploadFileAsync<Note>(Source(UploadTestHost.CreateFile(100)), options: DirectOptions()).WaitAsync(WaitTimeout));

        ex.Message.ShouldBe("Not this file.");
        Describe(faults).ShouldBe(["POST direct"]);
        host.CountUploads().ShouldBe(0);
    }

    [TestMethod]
    public async Task DirectUpload_WhoseHandlerFails_ThrowsItsError_AndTheAbortKeepsItsOutcome()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HandleAsync));
        var faults = CreateFaults(host);
        var client = UploadClient.CreateWithDirectUploads(host, faults);

        var ex = await Should.ThrowAsync<ForbiddenApiException>(
            () => client.UploadFileAsync<Note>(Source(UploadTestHost.CreateFile(100)), options: DirectOptions()).WaitAsync(WaitTimeout));

        ex.Message.ShouldBe("The folder is read-only.");
        Describe(faults).ShouldBe(["POST direct", "DELETE upload"]);
        host.CountUploads().ShouldBe(1);

        static Task<Note> HandleAsync(UploadedFile file) => throw new ForbiddenApiException("The folder is read-only.");
    }

    [TestMethod]
    public async Task DirectUpload_ReportsTheBytesAndThenTheProcessing()
    {
        byte[] file = UploadTestHost.CreateFile(200 * 1024);
        var progress = new SynchronousProgress();

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HashAsync));
        var client = UploadClient.CreateWithDirectUploads(host, CreateFaults(host));

        var options = new UploadOptions { Progress = progress, ChunkTimeout = Timeout.InfiniteTimeSpan, CompletionIdleTimeout = Timeout.InfiniteTimeSpan };
        await client.UploadFileAsync<Note>(Source(file), options: options).WaitAsync(WaitTimeout);

        progress.Reports[^1].ShouldBe(new UploadProgress(file.Length, file.Length, true));
        progress.Reports.Where(p => !p.IsProcessing).Select(p => p.BytesUploaded).ShouldBeInOrder();
        progress.Reports.ShouldContain(p => !p.IsProcessing && p.BytesUploaded > 0 && p.BytesUploaded < file.Length);
    }

    [TestMethod]
    public async Task TwoServers_DirectUploadCutOffOnOne_IsCompletedOnTheOther()
    {
        string directory = UploadTestHost.CreateDirectoryPath();
        var dataProtection = new EphemeralDataProtectionProvider();
        var shortLivedStore = new DeclaredSharedShortLivedStore(new MemoryShortLivedStore(TimeProvider.System));
        var time = new ManualTimeProvider(manualTimers: true);
        var release = new SemaphoreSlim(0);
        int runs = 0;

        try
        {
            await using var first = await StartAsync(directory, dataProtection, shortLivedStore, StreamAsync);
            await using var second = await StartAsync(directory, dataProtection, shortLivedStore, StreamAsync);

            // The direct upload runs the handler on the first server and is cut off after it started, and the completion reaches the second server.
            using var router = new RoutingHandler(first, second) { Route = r => IsCompletion(r) ? 1 : 0 };
            var faults = new FaultInjectionHandler(router).CutResponseAfter(IsDirect, records: 1);
            var client = UploadClient.CreateWithDirectUploads(first, faults);

            var items = ReadAllAsync(client.UploadFileStreamingAsync<Note>(Source(UploadTestHost.CreateFile(100)), DirectOptions(time)));
            await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(1));
            await WaitUntilAsync(() => faults.Requests.Count is 2);
            release.Release();

            (await items.WaitAsync(WaitTimeout)).ShouldBe([new Note("done")]);
            Describe(faults).ShouldBe(["POST direct", "POST complete"]);
            runs.ShouldBe(1);
        }
        finally
        {
            UploadTestHost.DeleteDirectory(directory);
        }

        async IAsyncEnumerable<Note> StreamAsync(UploadedFile file, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref runs);
            await release.WaitAsync(cancellationToken);
            yield return new Note("done");
        }
    }

    [TestMethod]
    public async Task DirectUploadThatFailsTwiceAfterTheServiceGotIt_OfAnOpenUpload_IsUploadedWithTheThreeRequests()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        int successes = 0;
        FaultInjectionHandler? faults = null;

        // The handler can't run at the moment for the two direct uploads, which leaves the upload open.
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, (UploadedFile file) => {
            if (faults!.Requests.Count < 3)
                throw new ServiceUnavailableApiException("Not at the moment.");

            Interlocked.Increment(ref successes);
            return new Note("done");
        }));

        faults = CreateFaults(host).FailAfterSending(IsDirect, count: 2);
        var client = UploadClient.CreateWithDirectUploads(host, faults);

        var upload = client.UploadFileAsync<Note>(Source(UploadTestHost.CreateFile(100)), options: DirectOptions(time));
        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(1));

        (await upload.WaitAsync(WaitTimeout)).ShouldBe(new Note("done"));
        Describe(faults).Take(3).ShouldBe(["POST direct", "POST direct", "POST uploads"]);
        successes.ShouldBe(1);
    }

    [TestMethod]
    public async Task DirectUploadThatFailsTwiceAfterTheServiceGotIt_WhileTheHandlerRuns_AttachesToTheRun()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var release = new SemaphoreSlim(0);
        int runs = 0;

        async Task<Note> HandleAsync(UploadedFile file, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref runs);
            await release.WaitAsync(cancellationToken);

            return new Note("done");
        }

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HandleAsync));

        var faults = CreateFaults(host).FailAfterSending(IsDirect, count: 2);
        var client = UploadClient.CreateWithDirectUploads(host, faults);

        var upload = client.UploadFileAsync<Note>(Source(UploadTestHost.CreateFile(100)), options: DirectOptions(time));
        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => faults.Requests.Count is 5);
        release.Release();

        (await upload.WaitAsync(WaitTimeout)).ShouldBe(new Note("done"));
        Describe(faults).ShouldBe(["POST direct", "POST direct", "POST uploads", "PUT 0", "POST complete"]);
        runs.ShouldBe(1);
    }

    [TestMethod]
    public async Task DirectUploadThatFailsTwiceAfterTheServiceGotIt_OfAFinishedUpload_GetsItsOutcome()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        int runs = 0;

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, (UploadedFile file) => {
            Interlocked.Increment(ref runs);
            return new Note("done");
        }));

        var faults = CreateFaults(host).FailAfterSending(IsDirect, count: 2);
        var client = UploadClient.CreateWithDirectUploads(host, faults);

        var upload = client.UploadFileAsync<Note>(Source(UploadTestHost.CreateFile(100)), options: DirectOptions(time));
        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(1));

        (await upload.WaitAsync(WaitTimeout)).ShouldBe(new Note("done"));
        Describe(faults).Take(3).ShouldBe(["POST direct", "POST direct", "POST uploads"]);
        runs.ShouldBe(1);
    }

    [TestMethod]
    public async Task DirectUploadCancelledWhileItsRequestStillRuns_IsCancelledByItsId_AndTheRequestEndsAsNotFound()
    {
        var filter = new ServiceHoldBodyFilter();
        int runs = 0;

        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, (UploadedFile file) => {
                Interlocked.Increment(ref runs);
                return new Note("done");
            }).AddEndpointFilter(filter.InvokeAsync));

        var faults = CreateFaults(host);
        var unanswered = new UnansweredDirectUploadsHandler(faults);
        var client = UploadClient.CreateWithDirectUploads(host, unanswered);
        using var cancellation = new CancellationTokenSource();

        var upload = client.UploadFileAsync<Note>(Source(UploadTestHost.CreateFile(100)), options: DirectOptions(), cancellationToken: cancellation.Token);
        await filter.Holding.WaitAsync(WaitTimeout);
        await cancellation.CancelAsync();

        // The service deleted the upload, whose body was still arriving, so no handler runs on it.
        await Should.ThrowAsync<OperationCanceledException>(() => upload.WaitAsync(WaitTimeout));
        unanswered.Aborted.IsCompleted.ShouldBeTrue();
        host.CountUploads().ShouldBe(0);
        Describe(faults).ShouldBe(["POST direct", "DELETE upload"]);
        faults.Requests[1].RequestUri!.AbsolutePath.ShouldEndWith($"/uploads/{filter.Id:D}");

        filter.Release();

        using var response = await unanswered.Sent!.WaitAsync(WaitTimeout);
        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.NotFound, UploadErrorCodes.NotFound);

        var store = host.Services.GetRequiredService<IUploadStore>();
        (await store.GetAsync("1", filter.Id.ShouldNotBeNull().ToString("D"))).ShouldBeNull();
        host.CountUploads().ShouldBe(0);
        runs.ShouldBe(0);
    }

    [TestMethod]
    public async Task DirectUploadCancelledWhileItsRequestStillRuns_FreesTheSlotOfTheUser()
    {
        var filter = new ServiceHoldBodyFilter();

        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, HashAsync).AddEndpointFilter(filter.InvokeAsync),
            options => options.MaxUploadsPerUser = 1);

        var unanswered = new UnansweredDirectUploadsHandler(CreateFaults(host));
        var client = UploadClient.CreateWithDirectUploads(host, unanswered);
        using var cancellation = new CancellationTokenSource();

        try
        {
            var upload = client.UploadFileAsync<Note>(Source(UploadTestHost.CreateFile(100)), options: DirectOptions(), cancellationToken: cancellation.Token);
            await filter.Holding.WaitAsync(WaitTimeout);
            await cancellation.CancelAsync();

            await Should.ThrowAsync<OperationCanceledException>(() => upload.WaitAsync(WaitTimeout));
            unanswered.Aborted.IsCompleted.ShouldBeTrue();
            host.CountUploads().ShouldBe(0);

            // The next upload takes the user's one slot at once, while the request of the first one still runs.
            var faults = CreateFaults(host);
            byte[] file = UploadTestHost.CreateFile(50);

            (await UploadClient.CreateWithDirectUploads(host, faults).UploadFileAsync<Note>(Source(file), options: DirectOptions()).WaitAsync(WaitTimeout))
                .Text.ShouldEndWith(UploadTestHost.Hash(file));

            Describe(faults).ShouldBe(["POST direct"]);
        }
        finally
        {
            filter.Release();
        }
    }

    [TestMethod]
    public async Task DirectUploadCancelledBeforeItsRequestReachesTheService_IsCancelledByItsId_WithoutCreatingAnUpload()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HashAsync));

        var faults = CreateFaults(host);
        var unanswered = new UnansweredDirectUploadsHandler(faults) { ReachesTheService = false };
        var client = UploadClient.CreateWithDirectUploads(host, unanswered);
        using var cancellation = new CancellationTokenSource();

        var upload = client.UploadFileAsync<Note>(Source(UploadTestHost.CreateFile(100)), options: DirectOptions(), cancellationToken: cancellation.Token);
        await unanswered.Started.WaitAsync(WaitTimeout);
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => upload.WaitAsync(WaitTimeout));
        Describe(faults).ShouldBe(["DELETE upload"]);
        host.CountUploads().ShouldBe(0);
    }

    [TestMethod]
    public async Task DirectUploadCancelledWhileItsHandlerRuns_WithoutItsResponse_StopsTheHandler_AndThrowsOnceTheCompletionEnds()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, async (UploadedFile file, CancellationToken cancellationToken) => {
                started.TrySetResult();
                cancellationToken.Register(stopped.SetResult);
                await Task.Delay(Timeout.Infinite, cancellationToken);

                return new Note("done");
            }));

        var faults = CreateFaults(host);
        var unanswered = new UnansweredDirectUploadsHandler(faults);
        var client = UploadClient.CreateWithDirectUploads(host, unanswered);
        using var cancellation = new CancellationTokenSource();

        var upload = client.UploadFileAsync<Note>(Source(UploadTestHost.CreateFile(100)), options: DirectOptions(), cancellationToken: cancellation.Token);
        await started.Task.WaitAsync(WaitTimeout);
        await cancellation.CancelAsync();

        var exception = await CancellationAssertions.ShouldBeCancelledAsync(upload.WaitAsync(WaitTimeout));

        // The handler stops at once, so the completion made after the 202 attaches to the run (upload-cancelled) or finds that the run deleted the upload
        // (upload-not-found), whichever comes first. Either says that the handler didn't finish.
        var error = exception.InnerException.ShouldBeAssignableTo<ApiException>().ShouldNotBeNull();
        (error.GetType(), error.ErrorCode).ShouldBeOneOf(
            (typeof(ConflictApiException), UploadErrorCodes.Cancelled),
            (typeof(NotFoundApiException), UploadErrorCodes.NotFound));

        await stopped.Task.WaitAsync(WaitTimeout);
        host.CountUploads().ShouldBe(0);
        Describe(faults).ShouldBe(["POST direct", "DELETE upload", "POST complete"]);

        using var response = await unanswered.Sent!.WaitAsync(WaitTimeout);
    }

    /// <summary>
    /// Gets the options of uploads that send a file that fits in one chunk directly, the defaults but for the timeouts.
    /// </summary>
    private static UploadOptions DirectOptions(ManualTimeProvider? time = null) =>
        new() { TimeProvider = time, ChunkTimeout = Timeout.InfiniteTimeSpan, CompletionIdleTimeout = Timeout.InfiniteTimeSpan };

    private static bool IsDirect(HttpRequestMessage request) =>
        request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/uploads/direct", StringComparison.Ordinal);

    /// <summary>
    /// Holds the body of the first direct upload on the service until it is released, so that its request and its write stay alive, and takes the
    /// upload's ID.
    /// </summary>
    private sealed class ServiceHoldBodyFilter
    {
        private readonly TaskCompletionSource _holding = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requests;

        public Task Holding => _holding.Task;

        public Guid? Id { get; private set; }

        public void Release() => _release.TrySetResult();

        public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var request = context.HttpContext.Request;

            if (request.Path.Value!.EndsWith("/uploads/direct", StringComparison.Ordinal) && Interlocked.Increment(ref _requests) is 1)
            {
                byte[] creation = Base64Url.DecodeFromChars(request.Headers["Upload-Creation"].ToString());
                Id = JsonDocument.Parse(creation).RootElement.GetProperty("id").GetGuid();
                request.Body = new ServiceHeldBody(request.Body, this);
            }

            return next(context);
        }

        private sealed class ServiceHeldBody(Stream inner, ServiceHoldBodyFilter filter) : ReadOnlyBodyStream
        {
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                filter._holding.TrySetResult();
                await filter._release.Task.WaitAsync(cancellationToken);

                return await inner.ReadAsync(buffer, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Leaves the client of a direct upload without a response, whatever happens to its request, which goes on on the service when the client cancels, as
    /// on a server that has not noticed yet that the client is gone. The other requests go through. Once the service has answered a request that cancels
    /// the upload, the request of a client that waits for it fails without a response, as a connection that broke off.
    /// </summary>
    private sealed class UnansweredDirectUploadsHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _aborted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Gets a value indicating whether the request of a direct upload reaches the service.
        /// </summary>
        public bool ReachesTheService { get; init; } = true;

        public Task Started => _started.Task;

        /// <summary>
        /// Gets a task that completes once the service has answered the request that aborts the upload, by when the upload has ended: its files are gone, its
        /// key is free, and it no longer counts toward the limits of the user.
        /// </summary>
        public Task Aborted => _aborted.Task;

        /// <summary>
        /// Gets the response of the direct upload that the service answered.
        /// </summary>
        public Task<HttpResponseMessage>? Sent { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!IsDirect(request))
            {
                var response = await base.SendAsync(request, cancellationToken);

                if (request.Method == HttpMethod.Delete)
                    _aborted.TrySetResult();

                return response;
            }

            if (ReachesTheService)
                Sent = base.SendAsync(request, CancellationToken.None);

            _started.TrySetResult();
            await _aborted.Task.WaitAsync(cancellationToken);

            throw new HttpRequestException("The connection was reset.");
        }
    }

    /// <summary>
    /// Records the reports of an upload's progress as they are made.
    /// </summary>
    private sealed class SynchronousProgress : IProgress<UploadProgress>
    {
        private readonly List<UploadProgress> _reports = [];

        public List<UploadProgress> Reports
        {
            get {
                lock (_reports)
                    return [.. _reports];
            }
        }

        public void Report(UploadProgress value)
        {
            lock (_reports)
                _reports.Add(value);
        }
    }
}
