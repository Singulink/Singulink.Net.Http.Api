using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Singulink.Net.Http.Api.Client;
using Singulink.Net.Http.Api.Service.Testing;
using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The toolkit's client uploading files to upload endpoints of the service in process, through the test server's handler and a
/// <see cref="FaultInjectionHandler"/>: results and items, the retries of failed requests, the recovery of completions, errors, progress, cancellation and
/// servers that share a directory.
/// </summary>
[PrefixTestClass]
public sealed class UploadEndToEndTests
{
    private const string Pattern = "/folders/{folderId}/files";
    private const string EndpointPath = "folders/5/files";
    private const int ChunkSize = 5 << 20;

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    public sealed record Note(string Text);

    public sealed record FolderCommand(string Title);

    [TestMethod]
    public async Task Upload_CreatesTheUploadSendsItsChunksAndReturnsTheHandlersResult()
    {
        byte[] file = UploadTestHost.CreateFile((2 * ChunkSize) + 100);
        var validated = new List<string>();

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, ValidateAsync, HandleAsync));
        var faults = CreateFaults(host);
        var client = UploadClient.Create(host, faults);

        var result = await client.UploadFileAsync<Note>(Source(file, "report.pdf", "application/pdf"), [("tag", "q1")], new FolderCommand("Invoices"))
            .WaitAsync(WaitTimeout);

        result.ShouldBe(new Note($"5 q1 Invoices report.pdf application/pdf {file.Length} {UploadTestHost.Hash(file)}"));
        validated.ShouldBe([$"5 q1 Invoices report.pdf application/pdf {file.Length}"]);
        Describe(faults).ShouldBe(["POST uploads?tag=q1", "PUT 0", "PUT 1", "PUT 2", "POST complete?tag=q1"]);

        Task ValidateAsync(long folderId, string tag, UploadRequest<FolderCommand> upload)
        {
            lock (validated)
                validated.Add($"{folderId} {tag} {upload.Command.Title} {upload.FileName} {upload.ContentType} {upload.Length}");

            return Task.CompletedTask;
        }

        static async Task<Note> HandleAsync(long folderId, string tag, UploadedFile<FolderCommand> file, CancellationToken cancellationToken)
        {
            string hash = await UploadTestHost.HashAsync(file, cancellationToken);
            return new Note($"{folderId} {tag} {file.Command.Title} {file.FileName} {file.ContentType} {file.Length} {hash}");
        }
    }

    [TestMethod]
    public async Task UploadStreaming_YieldsTheHandlersItems()
    {
        byte[] file = UploadTestHost.CreateFile(ChunkSize + 1);

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, StreamAsync));
        var client = UploadClient.Create(host, CreateFaults(host));

        var items = await ReadAllAsync(client.UploadFileStreamingAsync<Note>(Source(file))).WaitAsync(WaitTimeout);

        items.ShouldBe([new Note("received"), new Note(UploadTestHost.Hash(file))]);

        static async IAsyncEnumerable<Note> StreamAsync(UploadedFile file, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new Note("received");
            yield return new Note(await UploadTestHost.HashAsync(file, cancellationToken));
        }
    }

    [TestMethod]
    public async Task EmptyFile_IsUploadedWithoutChunks()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HashAsync));
        var faults = CreateFaults(host);
        var client = UploadClient.Create(host, faults);

        var result = await client.UploadFileAsync<Note>(Source([], "empty.txt")).WaitAsync(WaitTimeout);

        result.ShouldBe(new Note($"empty.txt 0 {UploadTestHost.Hash([])}"));
        Describe(faults).ShouldBe(["POST uploads", "POST complete"]);
    }

    [TestMethod]
    public async Task FailedChunks_AreSentAgain()
    {
        byte[] file = UploadTestHost.CreateFile((2 * ChunkSize) + 100);
        var time = new ManualTimeProvider(manualTimers: true);

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HashAsync));

        // The first chunk reaches the service but its response is lost, and the second fails twice before it is sent.
        var faults = CreateFaults(host).FailAfterSending(r => IsChunk(r, 0)).FailBeforeSending(r => IsChunk(r, 1), count: 2);
        var client = UploadClient.Create(host, faults);

        var upload = client.UploadFileAsync<Note>(Source(file), options: Options(time));

        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(1));
        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(1));
        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(2));

        (await upload.WaitAsync(WaitTimeout)).ShouldBe(new Note($"file.bin {file.Length} {UploadTestHost.Hash(file)}"));
        Describe(faults).ShouldBe(["POST uploads", "PUT 0", "PUT 0", "PUT 1", "PUT 1", "PUT 1", "PUT 2", "POST complete"]);
    }

    [TestMethod]
    public async Task ChunkThatTheStoreFails_IsSentAgainAfterItsRetryAfter()
    {
        byte[] file = UploadTestHost.CreateFile(100);
        var time = new ManualTimeProvider(manualTimers: true);
        var store = new MemoryUploadStore(TimeProvider.System);
        int failures = 0;

        // The store can't be reached once, such as a share that reconnects.
        store.BeforeOperation = operation => {
            if (operation is "WriteChunk" && Interlocked.Increment(ref failures) is 1)
                throw new IOException("The network path was not found.");
        };

        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, HashAsync),
            options => options.Directory = null,
            configureServices: services => services.AddSingleton<IUploadStore>(store));

        var faults = CreateFaults(host);
        var upload = UploadClient.Create(host, faults).UploadFileAsync<Note>(Source(file), options: Options(time));
        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(2));

        (await upload.WaitAsync(WaitTimeout)).ShouldBe(new Note($"file.bin {file.Length} {UploadTestHost.Hash(file)}"));
        Describe(faults).ShouldBe(["POST uploads", "PUT 0", "PUT 0", "POST complete"]);
    }

    [TestMethod]
    public async Task CompletionCutOff_IsMadeAgain_AndItsItemsStartAgainWithTheLatest()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var next = new SemaphoreSlim(0);
        int runs = 0;

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, StreamAsync));
        var faults = CreateFaults(host).CutResponseAfter(IsCompletion, records: 1);
        var client = UploadClient.Create(host, faults);

        var items = client.UploadFileStreamingAsync<Note>(Source(UploadTestHost.CreateFile(100)), options: Options(time)).GetAsyncEnumerator();

        try
        {
            (await items.MoveNextAsync().AsTask().WaitAsync(WaitTimeout)).ShouldBeTrue();
            items.Current.ShouldBe(new Note("received"));

            // The stream is cut off after the first item. The completion made again after a wait attaches to the run, starting with its latest item.
            var moved = items.MoveNextAsync().AsTask();
            await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(1));

            (await moved.WaitAsync(WaitTimeout)).ShouldBeTrue();
            items.Current.ShouldBe(new Note("received"));

            next.Release();
            (await items.MoveNextAsync().AsTask().WaitAsync(WaitTimeout)).ShouldBeTrue();
            items.Current.ShouldBe(new Note("converted"));

            next.Release();
            (await items.MoveNextAsync().AsTask().WaitAsync(WaitTimeout)).ShouldBeTrue();
            items.Current.ShouldBe(new Note("done"));

            (await items.MoveNextAsync().AsTask().WaitAsync(WaitTimeout)).ShouldBeFalse();
        }
        finally
        {
            await items.DisposeAsync();
        }

        runs.ShouldBe(1);
        Describe(faults).ShouldBe(["POST uploads", "PUT 0", "POST complete", "POST complete"]);

        async IAsyncEnumerable<Note> StreamAsync(UploadedFile file, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref runs);
            yield return new Note("received");

            await next.WaitAsync(cancellationToken);
            yield return new Note("converted");

            await next.WaitAsync(cancellationToken);
            yield return new Note("done");
        }
    }

    [TestMethod]
    public async Task ChunkTheServiceLacks_IsSentAgainAfterTheCompletionReportsTheUploadIncomplete()
    {
        byte[] file = UploadTestHost.CreateFile((2 * ChunkSize) + 100);

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HashAsync));

        // The second chunk is acknowledged without reaching the service.
        var faults = CreateFaults(host).Respond(r => IsChunk(r, 1), _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = UploadClient.Create(host, faults);

        var result = await client.UploadFileAsync<Note>(Source(file)).WaitAsync(WaitTimeout);

        result.ShouldBe(new Note($"file.bin {file.Length} {UploadTestHost.Hash(file)}"));
        Describe(faults).ShouldBe(["POST uploads", "PUT 0", "PUT 1", "PUT 2", "POST complete", "GET upload", "PUT 1", "POST complete"]);
    }

    [TestMethod]
    public async Task CreationWhoseResponseIsLost_IsMadeAgainWithTheSameKey_AndGetsTheSameUpload()
    {
        byte[] file = UploadTestHost.CreateFile(100);
        var time = new ManualTimeProvider(manualTimers: true);
        int validations = 0;

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, ValidateAsync, HashAsync));
        var faults = CreateFaults(host).FailAfterSending(IsCreation);
        var client = UploadClient.Create(host, faults);

        var upload = client.UploadFileAsync<Note>(Source(file), options: Options(time));
        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(1));

        (await upload.WaitAsync(WaitTimeout)).ShouldBe(new Note($"file.bin {file.Length} {UploadTestHost.Hash(file)}"));
        validations.ShouldBe(1);
        CountUploads(host).ShouldBe(1);
        Describe(faults).ShouldBe(["POST uploads", "POST uploads", "PUT 0", "POST complete"]);

        Task ValidateAsync(UploadRequest upload)
        {
            Interlocked.Increment(ref validations);
            return Task.CompletedTask;
        }
    }

    [TestMethod]
    public async Task CreationWhileAnEarlierOneWithTheKeyIsStillCreating_IsMadeAgainAfterItsRetryAfter()
    {
        byte[] file = UploadTestHost.CreateFile(100);
        var serverClock = new ManualClock(manualTimestamps: true);
        var time = new ManualTimeProvider(manualTimers: true);
        var validating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var validated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int validations = 0;

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, ValidateAsync, HashAsync), timeProvider: serverClock);
        var faults = CreateFaults(host);

        // The connection of the first creation drops while the service is still creating its upload.
        var dropping = new DroppingHandler(faults, IsCreation);
        var client = UploadClient.Create(host, dropping);

        var upload = client.UploadFileAsync<Note>(Source(file), options: Options(time));
        await validating.Task.WaitAsync(WaitTimeout);

        // The creation made again waits for the first one's upload for 5 seconds, and then fails with a retry time of a second.
        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => serverClock.HasTimerDueIn(TimeSpan.FromMilliseconds(100)));
        serverClock.Advance(TimeSpan.FromSeconds(5));

        await WaitUntilAsync(() => time.PendingTimerCount > 0);
        validated.SetResult();
        (await dropping.DroppedResponse!.WaitAsync(WaitTimeout)).ShouldBe(HttpStatusCode.Created);

        // The creation made after the retry time gets the upload that the first one created.
        time.Advance(TimeSpan.FromSeconds(1));

        (await upload.WaitAsync(WaitTimeout)).ShouldBe(new Note($"file.bin {file.Length} {UploadTestHost.Hash(file)}"));
        validations.ShouldBe(1);
        CountUploads(host).ShouldBe(1);
        Describe(faults).ShouldBe(["POST uploads", "POST uploads", "POST uploads", "PUT 0", "POST complete"]);

        async Task ValidateAsync(UploadRequest upload)
        {
            Interlocked.Increment(ref validations);
            validating.SetResult();
            await validated.Task.WaitAsync(WaitTimeout);
        }
    }

    [TestMethod]
    public async Task HandlerThatIsServiceUnavailable_RunsAgainWhenTheCompletionIsMadeAgain()
    {
        byte[] file = UploadTestHost.CreateFile(100);
        var time = new ManualTimeProvider(manualTimers: true);
        int runs = 0;

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HandleAsync));
        var faults = CreateFaults(host);
        var client = UploadClient.Create(host, faults);

        var upload = client.UploadFileAsync<Note>(Source(file), options: Options(time));
        await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(3));

        (await upload.WaitAsync(WaitTimeout)).ShouldBe(new Note($"2 {UploadTestHost.Hash(file)}"));
        runs.ShouldBe(2);
        Describe(faults).ShouldBe(["POST uploads", "PUT 0", "POST complete", "POST complete"]);

        async Task<Note> HandleAsync(UploadedFile file, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref runs) is 1)
                throw new ServiceUnavailableApiException("The converter is busy.") { RetryAfter = TimeSpan.FromSeconds(3) };

            return new Note($"{runs} {await UploadTestHost.HashAsync(file, cancellationToken)}");
        }
    }

    [TestMethod]
    public async Task HandlerInterruptedByAShutdown_FailsTheUploadForGood()
    {
        string directory = UploadTestHost.CreateDirectoryPath();
        var dataProtection = new EphemeralDataProtectionProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int runs = 0;

        try
        {
            await using var first = await StartAsync(directory, dataProtection, HandleAsync);
            await using var second = await StartAsync(directory, dataProtection, HandleAsync);

            using var router = new RoutingHandler(first, second);
            var faults = new FaultInjectionHandler(router);
            var client = UploadClient.Create(first, faults);

            var upload = client.UploadFileAsync<Note>(Source(UploadTestHost.CreateFile(100)));
            await started.Task.WaitAsync(WaitTimeout);

            // The first server stops while the handler runs, and the requests that follow reach the second.
            router.Route = _ => 1;
            await first.Web.App.StopAsync();

            var ex = await Should.ThrowAsync<ServerErrorApiException>(() => upload.WaitAsync(WaitTimeout));

            ex.ErrorCode.ShouldBe(UploadErrorCodes.Interrupted);
            runs.ShouldBe(1);
            Describe(faults).ShouldBe(["POST uploads", "PUT 0", "POST complete", "DELETE upload"]);
            CountUploads(second).ShouldBe(0);
        }
        finally
        {
            UploadTestHost.DeleteDirectory(directory);
        }

        async Task<Note> HandleAsync(UploadedFile file, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref runs);
            started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);

            return new Note("done");
        }
    }

    [TestMethod]
    public async Task ValidatorsError_IsThrownByTheUpload()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, ValidateAsync, HashAsync));
        var faults = CreateFaults(host);
        var client = UploadClient.Create(host, faults);

        var ex = await Should.ThrowAsync<ValidationApiException>(
            () => client.UploadFileAsync<Note>(Source(UploadTestHost.CreateFile(100), "virus.exe")).WaitAsync(WaitTimeout));

        ex.Message.ShouldBe("Files of this type can't be uploaded.");
        Describe(faults).ShouldBe(["POST uploads"]);
        CountUploads(host).ShouldBe(0);

        static Task ValidateAsync(UploadRequest upload) => upload.FileName.EndsWith(".exe", StringComparison.Ordinal)
            ? throw new ValidationApiException("Files of this type can't be uploaded.")
            : Task.CompletedTask;
    }

    [TestMethod]
    public async Task HandlersError_IsThrownByTheUpload_AndTheUploadIsAborted()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HandleAsync));
        var faults = CreateFaults(host);
        var client = UploadClient.Create(host, faults);

        var ex = await Should.ThrowAsync<ForbiddenApiException>(
            () => client.UploadFileAsync<Note>(Source(UploadTestHost.CreateFile(100))).WaitAsync(WaitTimeout));

        ex.Message.ShouldBe("The folder is read-only.");
        Describe(faults).ShouldBe(["POST uploads", "PUT 0", "POST complete", "DELETE upload"]);
        CountUploads(host).ShouldBe(0);

        static Task<Note> HandleAsync(UploadedFile file) => throw new ForbiddenApiException("The folder is read-only.");
    }

    [TestMethod]
    public async Task HandlersErrorAfterAnItem_IsThrownAfterTheItem()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, StreamAsync));
        var client = UploadClient.Create(host, CreateFaults(host));
        var received = new List<Note>();

        var ex = await Should.ThrowAsync<ConflictApiException>(
            () => ReadAllAsync(client.UploadFileStreamingAsync<Note>(Source(UploadTestHost.CreateFile(100))), received).WaitAsync(WaitTimeout));

        ex.Message.ShouldBe("The video can't be decoded.");
        received.ShouldBe([new Note("converting")]);

        static async IAsyncEnumerable<Note> StreamAsync(UploadedFile file)
        {
            yield return new Note("converting");
            await Task.Yield();

            throw new ConflictApiException("The video can't be decoded.");
        }
    }

    [TestMethod]
    public async Task Progress_CountsTheBytesSentAndThenTheProcessing()
    {
        byte[] file = UploadTestHost.CreateFile(ChunkSize + 100);
        var progress = new ProgressRecorder();

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HashAsync));
        var client = UploadClient.Create(host, CreateFaults(host));

        await client.UploadFileAsync<Note>(Source(file), options: Options(progress: progress)).WaitAsync(WaitTimeout);

        var reports = progress.Reports;
        reports.ShouldAllBe(p => p.Length == file.Length);
        reports.Select(p => p.BytesUploaded).ShouldBeInOrder(SortDirection.Ascending);
        reports.ShouldContain(p => p.BytesUploaded > 0 && p.BytesUploaded < ChunkSize);
        reports.ShouldContain(p => p.BytesUploaded == ChunkSize && !p.IsProcessing);
        reports[^2].ShouldBe(new UploadProgress(file.Length, file.Length, false));
        reports[^1].ShouldBe(new UploadProgress(file.Length, file.Length, true));
    }

    [TestMethod]
    public async Task Cancellation_AbortsTheUpload_WhichStopsTheHandler()
    {
        using var cancellation = new CancellationTokenSource();
        var handlerCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, StreamAsync));
        var faults = CreateFaults(host);
        var client = UploadClient.Create(host, faults);

        await Should.ThrowAsync<OperationCanceledException>(async () => {
            await foreach (var item in client.UploadFileStreamingAsync<Note>(Source(UploadTestHost.CreateFile(100)), cancellationToken: cancellation.Token))
                cancellation.Cancel();
        });

        await handlerCancelled.Task.WaitAsync(WaitTimeout);
        await WaitUntilAsync(() => CountUploads(host) is 0);
        Describe(faults).ShouldBe(["POST uploads", "PUT 0", "POST complete", "DELETE upload"]);

        async IAsyncEnumerable<Note> StreamAsync(UploadedFile file, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new Note("converting");

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally
            {
                handlerCancelled.SetResult();
            }

            yield return new Note("done");
        }
    }

    [TestMethod]
    public async Task TwoServersSharingADirectory_TakeTheChunksAndTheCompletionsOfAnUpload()
    {
        string directory = UploadTestHost.CreateDirectoryPath();
        byte[] file = UploadTestHost.CreateFile((2 * ChunkSize) + 100);
        var dataProtection = new EphemeralDataProtectionProvider();
        var time = new ManualTimeProvider(manualTimers: true);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int runs = 0;
        int completions = 0;

        try
        {
            await using var first = await StartAsync(directory, dataProtection, StreamAsync);
            await using var second = await StartAsync(directory, dataProtection, StreamAsync);

            // The chunks go to the servers in turn. The first completion runs the handler on the first server and is cut off after the first item, and
            // the completion made again reaches the second server, which waits for the run's outcome.
            using var router = new RoutingHandler(first, second) {
                Route = r => r.Method == HttpMethod.Put ? (int)((r.Content!.Headers.ContentRange!.From!.Value / ChunkSize) % 2) :
                    IsCompletion(r) && Interlocked.Increment(ref completions) > 1 ? 1 : 0,
            };

            var faults = new FaultInjectionHandler(router).CutResponseAfter(IsCompletion, records: 1);
            var client = UploadClient.Create(first, faults);

            var items = client.UploadFileStreamingAsync<Note>(Source(file), options: Options(time)).GetAsyncEnumerator();

            try
            {
                (await items.MoveNextAsync().AsTask().WaitAsync(WaitTimeout)).ShouldBeTrue();
                items.Current.ShouldBe(new Note("received"));

                var moved = items.MoveNextAsync().AsTask();
                await AdvanceWhenWaitingAsync(time, TimeSpan.FromSeconds(1));
                await WaitUntilAsync(() => Volatile.Read(ref completions) is 2);
                release.SetResult();

                (await moved.WaitAsync(WaitTimeout)).ShouldBeTrue();
                items.Current.ShouldBe(new Note(UploadTestHost.Hash(file)));
                (await items.MoveNextAsync().AsTask().WaitAsync(WaitTimeout)).ShouldBeFalse();
            }
            finally
            {
                await items.DisposeAsync();
            }

            runs.ShouldBe(1);
            Describe(faults).ShouldBe(["POST uploads", "PUT 0", "PUT 1", "PUT 2", "POST complete", "POST complete"]);
        }
        finally
        {
            UploadTestHost.DeleteDirectory(directory);
        }

        async IAsyncEnumerable<Note> StreamAsync(UploadedFile file, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref runs);
            yield return new Note("received");

            await release.Task.WaitAsync(cancellationToken);
            yield return new Note(await UploadTestHost.HashAsync(file, cancellationToken));
        }
    }

    private static async Task<Note> HashAsync(UploadedFile file, CancellationToken cancellationToken)
    {
        return new Note($"{file.FileName} {file.Length} {await UploadTestHost.HashAsync(file, cancellationToken)}");
    }

    /// <summary>
    /// Starts a host whose uploads are in the specified directory, which another host shares, with the same data protection of session cookies.
    /// </summary>
    private static Task<UploadTestHost> StartAsync(string directory, IDataProtectionProvider dataProtection, Delegate handler)
    {
        return UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, handler), directory: directory, dataProtection: dataProtection);
    }

    private static FaultInjectionHandler CreateFaults(UploadTestHost host) => new(host.Web.App.GetTestServer().CreateHandler());

    private static UploadSource Source(byte[] file, string fileName = "file.bin", string? contentType = null) =>
        UploadSource.FromStream(new MemoryStream(file), fileName, contentType);

    private static UploadOptions Options(ManualTimeProvider? time = null, IProgress<UploadProgress>? progress = null) =>
        new() { TimeProvider = time, ChunkTimeout = Timeout.InfiniteTimeSpan, Progress = progress, CompletionIdleTimeout = Timeout.InfiniteTimeSpan };

    private static bool IsCreation(HttpRequestMessage request) =>
        request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/uploads", StringComparison.Ordinal);

    private static bool IsChunk(HttpRequestMessage request, int index) =>
        request.Method == HttpMethod.Put && request.Content?.Headers.ContentRange?.From == (long)index * ChunkSize;

    private static bool IsCompletion(HttpRequestMessage request) =>
        request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/complete", StringComparison.Ordinal);

    /// <summary>
    /// Describes the requests of an upload that a fault injection handler received: <c>POST uploads</c>, <c>PUT {chunk index}</c>, <c>GET upload</c>,
    /// <c>POST complete</c> and <c>DELETE upload</c>, with their query strings without the user ID precondition.
    /// </summary>
    private static List<string> Describe(FaultInjectionHandler faults) => [.. faults.Requests.Select(Describe)];

    private static string Describe(RecordedRequest request)
    {
        var uri = request.RequestUri!;
        string query = string.Join('&', uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Where(p => !p.StartsWith("if-userId=")));

        string what = request.Method == HttpMethod.Put ? $"PUT {request.ContentRange!.From / ChunkSize}" :
            uri.AbsolutePath.EndsWith("/uploads", StringComparison.Ordinal) ? $"{request.Method} uploads" :
            uri.AbsolutePath.EndsWith("/complete", StringComparison.Ordinal) ? $"{request.Method} complete" :
            $"{request.Method} upload";

        return query.Length > 0 ? $"{what}?{query}" : what;
    }

    /// <summary>
    /// Counts the uploads in the directory of a host's store, including those that have an outcome.
    /// </summary>
    private static int CountUploads(UploadTestHost host)
    {
        // On Windows, a directory that is being removed cannot be listed for a moment.
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        return Directory.Exists(host.Directory) ? Directory.GetFiles(host.Directory, "upload.json", options).Length : 0;
    }

    private static async Task<List<T>> ReadAllAsync<T>(IAsyncEnumerable<T> items, List<T>? received = null)
    {
        received ??= [];

        await foreach (var item in items)
            received.Add(item);

        return received;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(WaitTimeout);

        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    /// <summary>
    /// Waits until the upload waits on a timer of the time provider, such as before a retry, and then advances the time.
    /// </summary>
    private static async Task AdvanceWhenWaitingAsync(ManualTimeProvider time, TimeSpan advance)
    {
        await WaitUntilAsync(() => time.PendingTimerCount > 0);
        time.Advance(advance);
    }

    /// <summary>
    /// A client of the upload endpoint at <see cref="EndpointPath"/>, which sends its requests through a handler as user 1, with the user ID precondition.
    /// </summary>
    private sealed class UploadClient : ApiClientBase
    {
        private readonly Uri? _baseAddress;

        private UploadClient(Uri baseAddress, HttpMessageHandler handler, string sessionToken) : base(UploadEndToEndJsonContext.Default, sessionToken, null)
        {
            _baseAddress = baseAddress;
            HttpHandler = handler;
        }

        private UploadClient(UploadClient parent) : base(parent, userIdPrecondition: 1)
        {
        }

        protected override string UserAgent => "TestClient/1.0";

        /// <summary>
        /// Creates a client of the user's session on the host.
        /// </summary>
        public static UploadClient Create(UploadTestHost host, HttpMessageHandler handler)
        {
            var cookie = new TestSessionCookie(new TestSessionToken(1, 1, 1), host.Clock.GetUtcNow().UtcDateTime, TimeSpan.FromDays(30), 0, true);
            var parent = new UploadClient(host.Web.App.GetTestServer().BaseAddress, handler, TestSessionCookies.Protect(host.Services, cookie));

            return new UploadClient(parent);
        }

        public Task<T> UploadFileAsync<T>(
            UploadSource source,
            ReadOnlySpan<(string Name, object? Value)> query = default,
            object? command = null,
            UploadOptions? options = null,
            CancellationToken cancellationToken = default)
            where T : notnull
        {
            return UploadAsync<T>(EndpointPath, source, query, command, options ?? Options(), cancellationToken);
        }

        public IAsyncEnumerable<T> UploadFileStreamingAsync<T>(UploadSource source, UploadOptions? options = null, CancellationToken cancellationToken = default)
        {
            return UploadStreamingAsync<T>(EndpointPath, source, options: options ?? Options(), cancellationToken: cancellationToken);
        }

        protected override Uri GetBaseAddress() => _baseAddress ?? base.GetBaseAddress();
    }

    /// <summary>
    /// Sends the requests to the test servers of hosts, each to the server that <see cref="Route"/> chooses.
    /// </summary>
    private sealed class RoutingHandler(params UploadTestHost[] hosts) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker[] _servers = [.. hosts.Select(h => new HttpMessageInvoker(h.Web.App.GetTestServer().CreateHandler()))];

        /// <summary>
        /// Gets or sets a function that chooses the index of the host whose server a request is sent to. Default sends every request to the first.
        /// </summary>
        public Func<HttpRequestMessage, int> Route { get; set; } = _ => 0;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _servers[Route(request)].SendAsync(request, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var server in _servers)
                    server.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Fails the first request that matches right away, as a connection that drops while the service still handles the request, and sends a copy of it to
    /// the inner handler in the background.
    /// </summary>
    private sealed class DroppingHandler(HttpMessageHandler innerHandler, Func<HttpRequestMessage, bool> predicate) : DelegatingHandler(innerHandler)
    {
        private int _dropped;

        /// <summary>
        /// Gets the status of the response to the copy of the dropped request, once it has one.
        /// </summary>
        public Task<HttpStatusCode>? DroppedResponse { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!predicate(request) || Interlocked.Exchange(ref _dropped, 1) is 1)
                return await base.SendAsync(request, cancellationToken);

            var copy = new HttpRequestMessage(request.Method, request.RequestUri);

            foreach (var header in request.Headers)
                copy.Headers.TryAddWithoutValidation(header.Key, header.Value);

            if (request.Content is { } content)
            {
                copy.Content = new ByteArrayContent(await content.ReadAsByteArrayAsync(cancellationToken));

                foreach (var header in content.Headers)
                    copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            DroppedResponse = SendCopyAsync(copy);
            throw new HttpRequestException(HttpRequestError.ConnectionError, "The connection dropped (test fault).");
        }

        private async Task<HttpStatusCode> SendCopyAsync(HttpRequestMessage copy)
        {
            using (copy)
            {
                using var response = await base.SendAsync(copy, CancellationToken.None);
                return response.StatusCode;
            }
        }
    }

    private sealed class ProgressRecorder : IProgress<UploadProgress>
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

[JsonSerializable(typeof(UploadEndToEndTests.Note))]
[JsonSerializable(typeof(UploadEndToEndTests.FolderCommand))]
internal sealed partial class UploadEndToEndJsonContext : JsonSerializerContext;
