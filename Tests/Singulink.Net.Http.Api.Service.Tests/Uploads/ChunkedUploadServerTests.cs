using System.Runtime.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Singulink.Net.Http.Api.DataContracts;
using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Chunked uploads on several servers that share a directory, across restarts and crashes, and the cleanup of what they leave behind.
/// </summary>
[PrefixTestClass]
public sealed class ChunkedUploadServerTests
{
    private const string Pattern = "/folders/{folderId}/files";
    private const int MiB = 1 << 20;

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    public sealed record Note(string Text);

    [TestMethod]
    public async Task TwoServers_ChunksAndCompletionOnEither_MakeTheWholeFile()
    {
        string directory = UploadTestHost.CreateDirectoryPath();

        try
        {
            await using var first = await StartAsync(directory, HashAsync);
            await using var second = await StartAsync(directory, HashAsync);

            byte[] file = UploadTestHost.CreateFile((10 * MiB) + 1);
            var session = await first.CreateUploadAsync("file.bin", file.Length);

            for (int index = 0; index < 3; index++)
                (await (index % 2 is 0 ? first : second).PutChunkAsync(session, file, index)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

            var lines = WithoutPings(await second.CompleteAndReadAsync(session));

            lines[0].ShouldContain(UploadTestHost.Hash(file));
            (await first.CompleteAndReadAsync(session)).ShouldBe(lines);
        }
        finally
        {
            UploadTestHost.DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task TwoServers_CompletionOnOtherServerWhileRunning_WaitsForOutcome()
    {
        string directory = UploadTestHost.CreateDirectoryPath();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int runs = 0;

        try
        {
            await using var first = await StartAsync(directory, StreamAsync);
            await using var second = await StartAsync(directory, StreamAsync);

            var session = await first.CreateUploadAsync("a", 10);
            await first.PutChunksAsync(session, UploadTestHost.CreateFile(10));

            using var running = await first.CompleteAsync(session);
            using var runningReader = new StreamReader(await running.Content.ReadAsStreamAsync());
            (await runningReader.ReadLineAsync()).ShouldBe("""{"item":{"text":"working"}}""");

            (await second.GetStatusAsync(session)).State.ShouldBe(UploadState.Processing);

            using var waiting = await second.CompleteAsync(session);
            using var waitingReader = new StreamReader(await waiting.Content.ReadAsStreamAsync());
            (await waitingReader.ReadLineAsync()).ShouldBe("""{"ping":true}""");

            release.SetResult();

            var lines = (await waitingReader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            lines.Where(l => l != """{"ping":true}""").ShouldBe(["""{"item":{"text":"done"}}""", """{"end":true}"""]);
            runs.ShouldBe(1);
        }
        finally
        {
            UploadTestHost.DeleteDirectory(directory);
        }

        async IAsyncEnumerable<Note> StreamAsync(UploadedFile file)
        {
            Interlocked.Increment(ref runs);
            yield return new Note("working");
            await release.Task.WaitAsync(WaitTimeout);
            yield return new Note("done");
        }
    }

    [TestMethod]
    public async Task Shutdown_WhileHandlerRuns_EndsUploadAsInterrupted()
    {
        string directory = UploadTestHost.CreateDirectoryPath();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int runs = 0;

        try
        {
            UploadSession session;

            await using (var first = await StartAsync(directory, HandleAsync))
            {
                session = await first.CreateUploadAsync("a", 10);
                await first.PutChunksAsync(session, UploadTestHost.CreateFile(10));

                using var completion = await first.CompleteAsync(session);
                await started.Task.WaitAsync(WaitTimeout);
                await first.Web.App.StopAsync();

                (await UploadTestHost.ReadLinesAsync(completion))[^1].ShouldStartWith("""{"error":{"status":500,"code":"upload-interrupted",""");

                // The shutdown stopped the handler through its token, so the warning has no exception.
                first.Web.WarningLogs.ShouldContain(l => l.Message.Contains("stopped by the application's shutdown", StringComparison.Ordinal));
                first.Web.WarningLogs.ShouldAllBe(l => l.Exception == null);
            }

            await using var second = await StartAsync(directory, HandleAsync);

            (await second.GetStatusAsync(session)).State.ShouldBe(UploadState.Completed);
            await UploadTestHost.ShouldBeErrorAsync(await second.CompleteAsync(session), HttpStatusCode.InternalServerError, UploadErrorCodes.Interrupted);
            runs.ShouldBe(1);
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

            return new Note("never");
        }
    }

    [TestMethod]
    public async Task AbortOnAnotherServer_StopsTheHandlerWhenItsClaimIsRenewed()
    {
        string directory = UploadTestHost.CreateDirectoryPath();
        var clock = new ManualClock(manualTimestamps: true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await using var first = await UploadTestHost.StartAsync(
                app => app.MapChunkedUpload(Pattern, HandleAsync), directory: directory, timeProvider: clock);

            await using var second = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HandleAsync), directory: directory);

            var session = await first.CreateUploadAsync("a", 10);
            await first.PutChunksAsync(session, UploadTestHost.CreateFile(10));

            var completion = first.CompleteAndReadAsync(session);
            await started.Task.WaitAsync(WaitTimeout);

            (await second.AbortAsync(session)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

            // The first server finds the upload gone when it renews the claim of the run, every 15 seconds.
            await WaitUntilAsync(() => clock.HasTimerDueIn(TimeSpan.FromSeconds(15)));
            clock.Advance(TimeSpan.FromSeconds(15));

            await cancelled.Task.WaitAsync(WaitTimeout);
            (await completion)[^1].ShouldStartWith("""{"error":{"status":404,"code":"upload-not-found",""");
            Directory.EnumerateFiles(directory, "upload.json", SearchOption.AllDirectories).ShouldBeEmpty();
        }
        finally
        {
            UploadTestHost.DeleteDirectory(directory);
        }

        async Task<Note> HandleAsync(UploadedFile file, CancellationToken cancellationToken)
        {
            started.TrySetResult();

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally
            {
                cancelled.TrySetResult();
            }

            return new Note("never");
        }
    }

    [TestMethod]
    public async Task ServerThatStopsWhileWaitingForTheRunOnAnotherServer_IsServiceUnavailable()
    {
        string directory = UploadTestHost.CreateDirectoryPath();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new ManualClock(manualTimestamps: true);

        try
        {
            await using var first = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HandleAsync), directory: directory);

            await using var second = await UploadTestHost.StartAsync(
                app => app.MapChunkedUpload(Pattern, HandleAsync), directory: directory, timeProvider: clock);

            var session = await first.CreateUploadAsync("a", 10);
            await first.PutChunksAsync(session, UploadTestHost.CreateFile(10));

            var running = first.CompleteAsync(session);
            await started.Task.WaitAsync(WaitTimeout);

            // The second server checks on the run every second, and stops before its first ping.
            var waiting = second.CompleteAsync(session);
            await WaitUntilAsync(() => clock.HasTimerDueIn(TimeSpan.FromSeconds(1)));
            await second.Web.App.StopAsync();

            using var stopped = await waiting;
            await UploadTestHost.ShouldBeErrorAsync(stopped, HttpStatusCode.ServiceUnavailable, UploadErrorCodes.ServerStopping);
            stopped.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(1));

            release.SetResult();
            using var done = await running;
        }
        finally
        {
            UploadTestHost.DeleteDirectory(directory);
        }

        async Task<Note> HandleAsync(UploadedFile file)
        {
            started.TrySetResult();
            await release.Task.WaitAsync(WaitTimeout);

            return new Note("done");
        }
    }

    [TestMethod]
    public async Task CompletionWhileTheApplicationStops_IsServiceUnavailable_WithoutRunningTheHandler()
    {
        int runs = 0;
        await using var host = await StartAsync(null, HandleAsync);
        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        // The application is stopping while its server still takes requests, as a load balancer sends them for a few seconds more.
        host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        using var response = await host.CompleteAsync(session);
        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.ServiceUnavailable, UploadErrorCodes.ServerStopping);
        response.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromSeconds(1));

        runs.ShouldBe(0);
        (await host.GetStatusAsync(session)).State.ShouldBe(UploadState.Receiving);

        Task<Note> HandleAsync(UploadedFile file)
        {
            Interlocked.Increment(ref runs);
            return Task.FromResult(new Note("done"));
        }
    }

    [TestMethod]
    public async Task OtherServer_FindsClaimLapsed_EndsUploadAsInterrupted()
    {
        string directory = UploadTestHost.CreateDirectoryPath();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await using var first = await StartAsync(directory, HandleAsync);

            // The second server's clock is past the first claim's lease, as if the first server had stopped renewing it.
            await using var second = await StartAsync(directory, HandleAsync, new TestClock(DateTimeOffset.UtcNow.AddMinutes(2)));

            var session = await first.CreateUploadAsync("a", 10);
            await first.PutChunksAsync(session, UploadTestHost.CreateFile(10));

            using var running = await first.CompleteAsync(session);
            await started.Task.WaitAsync(WaitTimeout);

            await UploadTestHost.ShouldBeErrorAsync(await second.CompleteAsync(session), HttpStatusCode.InternalServerError, UploadErrorCodes.Interrupted);

            // The first server's handler finishes, but the upload's outcome is the one stored first.
            release.SetResult();

            (await UploadTestHost.ReadLinesAsync(running))[^1].ShouldStartWith("""{"error":{"status":500,"code":"upload-interrupted",""");
            await UploadTestHost.ShouldBeErrorAsync(await first.CompleteAsync(session), HttpStatusCode.InternalServerError, UploadErrorCodes.Interrupted);
        }
        finally
        {
            UploadTestHost.DeleteDirectory(directory);
        }

        async Task<Note> HandleAsync(UploadedFile file)
        {
            started.TrySetResult();
            await release.Task.WaitAsync(WaitTimeout);

            return new Note("late");
        }
    }

    [TestMethod]
    public async Task Startup_ClaimLeftByCrashedServer_EndsUploadAsInterrupted()
    {
        string directory = UploadTestHost.CreateDirectoryPath();
        int runs = 0;

        try
        {
            UploadSession session;
            string uploadDirectory;

            await using (var first = await StartAsync(directory, HandleAsync))
            {
                session = await first.CreateUploadAsync("a", 10);
                await first.PutChunksAsync(session, UploadTestHost.CreateFile(10));
                uploadDirectory = first.GetUploadDirectory(session);
            }

            // A server that claimed the run crashed: its claim stays without an outcome, and is no longer renewed.
            string claimPath = Path.Combine(uploadDirectory, "claim");
            string startedUtc = DateTime.UtcNow.AddMinutes(-5).ToString("O");
            string renewedUtc = DateTime.UtcNow.AddMinutes(-4).ToString("O");

            await File.WriteAllTextAsync(
                claimPath, $$"""{"claimId":"crashed","leaseDuration":"00:01:00","startedUtc":"{{startedUtc}}","renewedUtc":"{{renewedUtc}}"}""");

            await using var second = await StartAsync(directory, HandleAsync);
            await WaitUntilAsync(() => File.Exists(Path.Combine(uploadDirectory, "outcome.json")));

            await UploadTestHost.ShouldBeErrorAsync(await second.CompleteAsync(session), HttpStatusCode.InternalServerError, UploadErrorCodes.Interrupted);
            runs.ShouldBe(0);
        }
        finally
        {
            UploadTestHost.DeleteDirectory(directory);
        }

        Task<Note> HandleAsync(UploadedFile file)
        {
            Interlocked.Increment(ref runs);
            return Task.FromResult(new Note("never"));
        }
    }

    [TestMethod]
    public async Task ChunkMarkerMissing_ChunkIsSentAgain()
    {
        await using var host = await StartAsync(null, HashAsync);
        byte[] file = UploadTestHost.CreateFile((5 * MiB) + 10);

        var session = await host.CreateUploadAsync("file.bin", file.Length);
        await host.PutChunksAsync(session, file);

        // The server stopped after the chunk's bytes were written and before its marker was.
        File.Delete(Path.Combine(host.GetUploadDirectory(session), "chunks", "1"));

        (await host.GetStatusAsync(session)).MissingChunks.ShouldBe([new ChunkRange(1, 1)]);
        await UploadTestHost.ShouldBeErrorAsync(await host.CompleteAsync(session), HttpStatusCode.Conflict, UploadErrorCodes.Incomplete);

        (await host.PutChunkAsync(session, file, 1)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        WithoutPings(await host.CompleteAndReadAsync(session))[0].ShouldContain(UploadTestHost.Hash(file));
    }

    [TestMethod]
    public async Task Abort_ManyAtTheSameTime_AllSucceed()
    {
        await using var host = await StartAsync(null, HashAsync);

        for (int round = 0; round < 10; round++)
        {
            var session = await host.CreateUploadAsync("a", 10);
            await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

            var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => host.AbortAsync(session)));

            responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.NoContent);
            Directory.Exists(Path.Combine(Directory.GetDirectories(host.Directory).Single(), session.Id)).ShouldBeFalse();
        }
    }

    [TestMethod]
    public async Task UploadWhoseDirectoryCannotBeListed_IsUnavailable_AndGone_OnlyWhileItIsDeleted()
    {
        await using var host = await StartAsync(null, HashAsync);
        var session = await host.CreateUploadAsync("a", 10);
        string directory = host.GetUploadDirectory(session);

        using (AccessDenial.DenyListing(directory))
        {
            // A store that fails, such as a share that can't be reached for a moment, makes the client try again a moment later.
            using (var unavailable = await host.GetStatusResponseAsync(session))
            {
                await UploadTestHost.ShouldBeErrorAsync(unavailable, HttpStatusCode.ServiceUnavailable, UploadErrorCodes.StoreUnavailable);
                unavailable.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromSeconds(2));
            }

            // A deletion at the same time has removed the record, and Windows doesn't let the directory be listed while the deletion removes it.
            File.Delete(Path.Combine(directory, "upload.json"));

            await UploadTestHost.ShouldBeErrorAsync(await host.GetStatusResponseAsync(session), HttpStatusCode.NotFound, UploadErrorCodes.NotFound);
            (await host.AbortAsync(session)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }
    }

    [TestMethod]
    public async Task ClaimThatCannotBeRead_EndsUploadAsInterrupted()
    {
        await using var host = await StartAsync(null, HashAsync);
        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        // A claim that is still partly written a minute after it was created is broken, such as by a server that stopped while it wrote the claim.
        string claimPath = Path.Combine(host.GetUploadDirectory(session), "claim");
        await File.WriteAllTextAsync(claimPath, "{");
        File.SetLastWriteTimeUtc(claimPath, DateTime.UtcNow.AddMinutes(-2));

        (await host.GetStatusAsync(session)).State.ShouldBe(UploadState.Completed);
        await UploadTestHost.ShouldBeErrorAsync(await host.CompleteAsync(session), HttpStatusCode.InternalServerError, UploadErrorCodes.Interrupted);
    }

    [TestMethod]
    public async Task ClaimThatIsBeingWritten_IsARun()
    {
        await using var host = await StartAsync(null, HashAsync);
        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        // Another server has just created the claim, and has not written its content yet.
        string claimPath = Path.Combine(host.GetUploadDirectory(session), "claim");
        await File.WriteAllBytesAsync(claimPath, []);

        (await host.GetStatusAsync(session)).State.ShouldBe(UploadState.Processing);

        // The file that gave the file system's time to judge the claim's age is gone.
        Directory.GetFiles(host.GetUploadDirectory(session), "*.tmp").ShouldBeEmpty();
        using var chunk = await host.PutChunkAsync(session, UploadTestHost.CreateFile(10), 0);
        await UploadTestHost.ShouldBeErrorAsync(chunk, HttpStatusCode.Conflict, UploadErrorCodes.Closed);

        File.Delete(claimPath);
        (await host.GetStatusAsync(session)).State.ShouldBe(UploadState.Receiving);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task ClaimThatCannotBeReadBack_IsRemovedOnceItCanBe()
    {
        await using var host = await StartAsync(null, HashAsync);
        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        var store = host.Services.GetRequiredService<IUploadStore>();
        var record = (await store.GetAsync("1", session.Id)).ShouldNotBeNull().Record;
        string directory = host.GetUploadDirectory(session);

        // The new claim can't be read for a while after it is written, as when another process has opened it without sharing it.
        using (AccessDenial.DenyReadingNewFiles(directory))
            await Should.ThrowAsync<UnauthorizedAccessException>(async () => await store.TryClaimRunAsync(record, TimeSpan.FromMinutes(1)));

        // It is removed in the background rather than left to lapse, which would end the upload as interrupted.
        await WaitUntilAsync(() => !File.Exists(Path.Combine(directory, "claim")));
        (await host.GetStatusAsync(session)).State.ShouldBe(UploadState.Receiving);
    }

    [TestMethod]
    public async Task ClaimWhoseChecksFail_IsRemoved()
    {
        await using var host = await StartAsync(null, HashAsync);
        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        var store = host.Services.GetRequiredService<IUploadStore>();
        var record = (await store.GetAsync("1", session.Id)).ShouldNotBeNull().Record;
        string directory = host.GetUploadDirectory(session);

        // The claim looks for writes of chunks that are alive by listing the chunks.
        using (AccessDenial.DenyListing(Path.Combine(directory, "chunks")))
            await Should.ThrowAsync<UnauthorizedAccessException>(async () => await store.TryClaimRunAsync(record, TimeSpan.FromMinutes(1)));

        File.Exists(Path.Combine(directory, "claim")).ShouldBeFalse();
    }

    [TestMethod]
    public async Task ReleaseOfAClaimThatCannotBeRemoved_Fails()
    {
        await using var host = await StartAsync(null, HashAsync);
        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        var store = host.Services.GetRequiredService<IUploadStore>();
        var record = (await store.GetAsync("1", session.Id)).ShouldNotBeNull().Record;
        var claim = (await store.TryClaimRunAsync(record, TimeSpan.FromMinutes(1))).ShouldNotBeNull();
        string claimPath = Path.Combine(host.GetUploadDirectory(session), "claim");

        // On Windows, another process, such as a virus scanner, has the claim open and doesn't let it be removed. Elsewhere, the directory can't be written.
        using (OperatingSystem.IsWindows()
            ? (IDisposable)new FileStream(claimPath, FileMode.Open, FileAccess.Read, FileShare.Read)
            : AccessDenial.DenyCreatingFiles(Path.GetDirectoryName(claimPath)!))
        {
            var exception = await Should.ThrowAsync<Exception>(async () => await claim.ReleaseAsync());
            (exception is IOException or UnauthorizedAccessException).ShouldBeTrue(exception.ToString());
        }

        File.Exists(claimPath).ShouldBeTrue();

        await claim.ReleaseAsync();
        File.Exists(claimPath).ShouldBeFalse();
    }

    [TestMethod]
    public async Task OutcomeFileThatCannotBeRead_IsCompletedWithServerError()
    {
        await using var host = await StartAsync(null, HashAsync);
        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));
        await host.CompleteAndReadAsync(session);

        string outcomePath = Path.Combine(host.GetUploadDirectory(session), "outcome.json");
        await File.WriteAllTextAsync(outcomePath, "{");
        File.SetLastWriteTimeUtc(outcomePath, DateTime.UtcNow.AddMinutes(-2));

        (await host.GetStatusAsync(session)).State.ShouldBe(UploadState.Completed);
        await UploadTestHost.ShouldBeErrorAsync(await host.CompleteAsync(session), HttpStatusCode.InternalServerError, UploadErrorCodes.OutcomeUnreadable);
    }

    [TestMethod]
    public async Task List_WithoutOutcomes_LeavesOutTheUploadsThatHaveOne()
    {
        await using var host = await StartAsync(null, HashAsync);
        var (completed, _) = await host.UploadAsync(UploadTestHost.CreateFile(10));
        var open = await host.CreateUploadAsync("a", 10);
        var store = host.Services.GetRequiredService<IUploadStore>();

        (await store.ListAsync("1").Select(u => u.Record.Id).ToListAsync()).Order().ShouldBe(new[] { completed.Id, open.Id }.Order());
        (await store.ListAsync("1", withoutOutcomes: true).Select(u => u.Record.Id).ToListAsync()).ShouldBe([open.Id]);
    }

    [TestMethod]
    public async Task Startup_RemovesLeftoversAndExpiredUploads()
    {
        string directory = UploadTestHost.CreateDirectoryPath();

        try
        {
            UploadSession expired;
            string expiredDirectory, userDirectory;

            await using (var first = await StartAsync(directory, HashAsync))
            {
                expired = await first.CreateUploadAsync("a", 10);
                expiredDirectory = first.GetUploadDirectory(expired);
                userDirectory = Path.GetDirectoryName(expiredDirectory)!;
            }

            // A creation that failed long ago, an upload whose deletion could not finish, and a temporary file left by a write.
            string failedCreation = Directory.CreateDirectory(Path.Combine(userDirectory, new string('B', 43))).FullName;
            Directory.SetCreationTimeUtc(failedCreation, DateTime.UtcNow.AddHours(-2));

            string unfinishedDeletion = Directory.CreateDirectory(Path.Combine(userDirectory, new string('C', 43))).FullName;
            await File.WriteAllBytesAsync(Path.Combine(unfinishedDeletion, "upload.json"), []);
            await File.WriteAllBytesAsync(Path.Combine(unfinishedDeletion, "deleted"), []);

            // The second server starts after the first upload's idle expiry.
            await using var second = await StartAsync(directory, HashAsync, new TestClock(DateTimeOffset.UtcNow.AddDays(2)));

            await WaitUntilAsync(() => !Directory.Exists(failedCreation) && !Directory.Exists(unfinishedDeletion) && !Directory.Exists(expiredDirectory));
        }
        finally
        {
            UploadTestHost.DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task Cleanup_RemovesTheEmptyDirectoryOfAUser_OnlyWhenItHasNotChangedLately()
    {
        await using var host = await StartAsync(null, HashAsync);
        var session = await host.CreateUploadAsync("a", 10);
        string userDirectory = Path.GetDirectoryName(host.GetUploadDirectory(session))!;
        (await host.AbortAsync(session)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // A creation of an upload creates the user's directory before the upload's directory in it, so a cleanup can find it empty.
        var store = host.Services.GetRequiredService<IUploadStore>();
        await store.CleanUpAsync();
        Directory.Exists(userDirectory).ShouldBeTrue();

        Directory.SetLastWriteTimeUtc(userDirectory, DateTime.UtcNow.AddHours(-2));
        await store.CleanUpAsync();
        Directory.Exists(userDirectory).ShouldBeFalse();
    }

    [TestMethod]
    public async Task Cleanup_WithAClockAheadOfTheSystemClock_KeepsTheFilesOfWritesInProgress()
    {
        // The service's clock is ahead of the system's clock, which sets the times of files, by more than the age of leftovers, as a test's clock can be.
        await using var host = await StartAsync(null, HashAsync, new TestClock(DateTimeOffset.UtcNow.AddHours(2)));
        var session = await host.CreateUploadAsync("a", 10);
        string uploadDirectory = host.GetUploadDirectory(session);
        string userDirectory = Path.GetDirectoryName(uploadDirectory)!;

        // What writes in progress have just created: the temporary file of an outcome, the marker of a write of a chunk and the temporary file of its
        // renewal, the temporary file of a key's reservation, the directory of an upload whose record is not written yet, and the directory of a user.
        string[] files = [
            Path.Combine(uploadDirectory, "outcome.json.1.tmp"),
            Path.Combine(uploadDirectory, "chunks", "0.1.writing"),
            Path.Combine(uploadDirectory, "chunks", "0.1.writing.2.tmp"),
            Path.Combine(userDirectory, "keys", "key.1.tmp"),
        ];

        string[] directories = [Path.Combine(userDirectory, new string('B', 43)), Path.Combine(host.Directory, "user")];

        foreach (string file in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllBytesAsync(file, []);
        }

        foreach (string directory in directories)
            Directory.CreateDirectory(directory);

        await host.Services.GetRequiredService<IUploadStore>().CleanUpAsync();

        files.Where(f => !File.Exists(f)).Concat(directories.Where(d => !Directory.Exists(d))).ShouldBeEmpty();
    }

    private static Task<UploadTestHost> StartAsync(string? directory, Delegate handler, TestClock? clock = null)
    {
        return UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, handler),
            options => options.CompletionPingInterval = TimeSpan.FromMilliseconds(50),
            directory,
            clock: clock);
    }

    private static async Task<Note> HashAsync(UploadedFile file, CancellationToken cancellationToken)
    {
        return new Note(await UploadTestHost.HashAsync(file, cancellationToken));
    }

    private static List<string> WithoutPings(List<string> lines) => [.. lines.Where(l => l != """{"ping":true}""")];

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(WaitTimeout);

        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}
