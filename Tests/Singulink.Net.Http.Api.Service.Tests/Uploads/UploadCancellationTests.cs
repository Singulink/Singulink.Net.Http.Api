using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Singulink.Net.Http.Api.DataContracts;
using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The cancellation of uploads against the start of their runs, on other servers and with slow handlers, and the locality of the stores: the lock of a
/// process-local directory, the checks when the application starts, and the runs that a previous process left.
/// </summary>
[PrefixTestClass]
public sealed class UploadCancellationTests
{
    private const string Pattern = "/folders/{folderId}/files";

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task CancelWrittenBeforeTheRunIsClaimed_IsFoundByTheRun_WhichEndsTheUploadWithoutStartingTheHandler()
    {
        var shortLivedStore = new InterceptingShortLivedStore(new MemoryShortLivedStore(TimeProvider.System));
        int runs = 0;

        await using var host = await StartAsync(CountAsync, shortLivedStore: shortLivedStore);
        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        // Once the cancellation is written, and before it looks at the upload, the upload is completed to the end. The hook blocks a thread of the pool
        // while it waits for a request of the same process, which pool starvation could hold up, but these tests never starve the pool.
        string? completion = null;
        string? cancelKey = null;

        shortLivedStore.After = (operation, key) => {
            // The first write of the cancellation, before the upload is read.
            if (operation is "Set" && IsCancelKey(key, session) && cancelKey is null)
            {
                cancelKey = key;
                using var response = host.CompleteAsync(session).WaitAsync(WaitTimeout).GetAwaiter().GetResult();
                completion = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
        };

        (await host.AbortAsync(session)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // The run ends before the first ping, so the cancellation is the completion's error response.
        completion.ShouldNotBeNull().ShouldStartWith("[upload-cancelled] ");
        runs.ShouldBe(0);
        await UploadTestHost.ShouldBeErrorAsync(await host.GetStatusResponseAsync(session), HttpStatusCode.NotFound, UploadErrorCodes.NotFound);

        // The run that ended the upload keeps the cancellation, since a direct upload of the ID can still arrive (see the test of a large upload below).
        (await shortLivedStore.GetAsync(cancelKey.ShouldNotBeNull())).ShouldNotBeNull();

        Task<Note> CountAsync(UploadedFile file)
        {
            Interlocked.Increment(ref runs);
            return Task.FromResult(new Note("done"));
        }
    }

    [TestMethod]
    public async Task CancelOfAnIdThatDoesNotExistYet_IsNoContent_AndTheDirectUploadThatArrivesLater_NeverStartsItsHandler()
    {
        int runs = 0;
        await using var host = await StartAsync(CountAsync);
        var id = Guid.NewGuid();
        var session = new UploadSession(id.ToString("D"), 0, DateTime.UtcNow);

        // The cancellation overtakes the direct upload, such as one that a proxy holds until it has the whole body.
        (await host.AbortAsync(session)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var response = await host.Client.SendAsync(host.CreateDirectRequest(new ByteArrayContent(UploadTestHost.CreateFile(10)), id: id));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        UploadTestHost.WithoutPings(await UploadTestHost.ReadLinesAsync(response)).ShouldHaveSingleItem().ShouldContain(UploadErrorCodes.Cancelled);
        runs.ShouldBe(0);
        await UploadTestHost.ShouldBeErrorAsync(await host.GetStatusResponseAsync(session), HttpStatusCode.NotFound, UploadErrorCodes.NotFound);

        // The same request made again, such as a copy that a proxy sent late too, doesn't start the handler either.
        using var again = await host.Client.SendAsync(host.CreateDirectRequest(new ByteArrayContent(UploadTestHost.CreateFile(10)), id: id));
        UploadTestHost.WithoutPings(await UploadTestHost.ReadLinesAsync(again)).ShouldHaveSingleItem().ShouldContain(UploadErrorCodes.Cancelled);
        runs.ShouldBe(0);

        Task<Note> CountAsync(UploadedFile file)
        {
            Interlocked.Increment(ref runs);
            return Task.FromResult(new Note("done"));
        }
    }

    [TestMethod]
    public async Task CancelOfAnIdThatDoesNotExistYet_IsKeptFor2Minutes()
    {
        var clock = new TestClock();
        var shortLivedStore = new MemoryShortLivedStore(clock);
        int runs = 0;

        await using var host = await StartAsync(CountAsync, shortLivedStore: shortLivedStore);
        var id = Guid.NewGuid();
        (await host.AbortAsync(new UploadSession(id.ToString("D"), 0, DateTime.UtcNow))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // A request that the cancellation overtook arrives within seconds, so a direct upload of the ID after 2 minutes runs its handler.
        clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));

        using var response = await host.Client.SendAsync(host.CreateDirectRequest(new ByteArrayContent(UploadTestHost.CreateFile(10)), id: id));
        UploadTestHost.WithoutPings(await UploadTestHost.ReadLinesAsync(response)).ShouldBe(["""{"item":{"text":"done"}}""", """{"end":true}"""]);
        runs.ShouldBe(1);

        Task<Note> CountAsync(UploadedFile file)
        {
            Interlocked.Increment(ref runs);
            return Task.FromResult(new Note("done"));
        }
    }

    [TestMethod]
    public async Task CancelOfAnUploadThatExists_IsKeptAsLongAsARunCanStart()
    {
        var clock = new TestClock();
        var shortLivedStore = new MemoryShortLivedStore(clock);
        int runs = 0;

        await using var host = await StartAsync(CountAsync, options => options.ProcessingTimeout = TimeSpan.FromMinutes(10), shortLivedStore: shortLivedStore);
        var session = await host.CreateUploadAsync("a", 10);
        var id = Guid.Parse(session.Id);
        (await host.AbortAsync(session)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // The deleted upload can be created again by a direct upload of its ID, whose handler still doesn't start after 2 minutes, but does once the
        // cancellation has expired, after the processing timeout and a minute.
        clock.Advance(TimeSpan.FromMinutes(10));

        using (var response = await host.Client.SendAsync(host.CreateDirectRequest(new ByteArrayContent(UploadTestHost.CreateFile(10)), id: id)))
            UploadTestHost.WithoutPings(await UploadTestHost.ReadLinesAsync(response)).ShouldHaveSingleItem().ShouldContain(UploadErrorCodes.Cancelled);

        runs.ShouldBe(0);
        clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));

        using (var response = await host.Client.SendAsync(host.CreateDirectRequest(new ByteArrayContent(UploadTestHost.CreateFile(10)), id: id)))
            UploadTestHost.WithoutPings(await UploadTestHost.ReadLinesAsync(response)).ShouldBe(["""{"item":{"text":"done"}}""", """{"end":true}"""]);

        runs.ShouldBe(1);

        Task<Note> CountAsync(UploadedFile file)
        {
            Interlocked.Increment(ref runs);
            return Task.FromResult(new Note("done"));
        }
    }

    [TestMethod]
    public async Task CancelOfAnUploadTooLargeToBeSentDirectly_DeletesIt_AndRemovesTheCancellation()
    {
        var shortLivedStore = new InterceptingShortLivedStore(new MemoryShortLivedStore(TimeProvider.System));
        string? cancelKey = null;

        await using var host = await StartAsync(
            (UploadedFile file) => new Note("done"), options => options.MaxChunkSize = 5 * 1024 * 1024, shortLivedStore: shortLivedStore);

        var session = await host.CreateUploadAsync("a", (5 * 1024 * 1024) + 1);

        shortLivedStore.After = (operation, key) => {
            if (operation is "Set" && IsCancelKey(key, session))
                cancelKey = key;
        };

        (await host.AbortAsync(session)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // No request can create the upload again with its body, so no run can start on it, and the cancellation is not needed.
        (await shortLivedStore.GetAsync(cancelKey.ShouldNotBeNull())).ShouldBeNull();
        await UploadTestHost.ShouldBeErrorAsync(await host.GetStatusResponseAsync(session), HttpStatusCode.NotFound, UploadErrorCodes.NotFound);
    }

    [TestMethod]
    public async Task CancelAtAnotherPath_DoesNotCancelTheUpload()
    {
        await using var host = await StartAsync((UploadedFile file) => new Note("done"));
        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        (await host.AbortAsync(session, "/folders/6/files")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        UploadTestHost.WithoutPings(await host.CompleteAndReadAsync(session)).ShouldBe(["""{"item":{"text":"done"}}""", """{"end":true}"""]);
    }

    [TestMethod]
    public async Task CancelWhoseShortLivedStoreFails_IsUnavailableForAMoment()
    {
        var shortLivedStore = new InterceptingShortLivedStore(new MemoryShortLivedStore(TimeProvider.System));
        await using var host = await StartAsync((UploadedFile file) => new Note("done"), shortLivedStore: shortLivedStore);
        var session = await host.CreateUploadAsync("a", 10);

        shortLivedStore.FailOnceWithPrefix("Set", ToolkitKeys.UploadCancel);

        using (var failed = await host.AbortAsync(session))
        {
            await UploadTestHost.ShouldBeErrorAsync(failed, HttpStatusCode.ServiceUnavailable, UploadErrorCodes.CancellationUnavailable);
            failed.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromSeconds(2));
        }

        // The upload is as it was, and the cancellation made again cancels it.
        (await host.GetStatusAsync(session)).State.ShouldBe(UploadState.Receiving);
        (await host.AbortAsync(session)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [TestMethod]
    public async Task RunWhoseCheckForACancellationFails_DoesNotStartTheHandler_AndRunsWhenTheCompletionIsMadeAgain()
    {
        var shortLivedStore = new InterceptingShortLivedStore(new MemoryShortLivedStore(TimeProvider.System));
        int runs = 0;

        await using var host = await StartAsync(CountAsync, shortLivedStore: shortLivedStore);
        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        shortLivedStore.FailOnceWithPrefix("Get", ToolkitKeys.UploadCancel);

        using (var failed = await host.CompleteAsync(session))
        {
            await UploadTestHost.ShouldBeErrorAsync(failed, HttpStatusCode.ServiceUnavailable, UploadErrorCodes.CancellationUnavailable);
            failed.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromSeconds(2));
        }

        runs.ShouldBe(0);
        (await host.GetStatusAsync(session)).State.ShouldBe(UploadState.Receiving);

        UploadTestHost.WithoutPings(await host.CompleteAndReadAsync(session)).ShouldBe(["""{"item":{"text":"done"}}""", """{"end":true}"""]);
        runs.ShouldBe(1);

        Task<Note> CountAsync(UploadedFile file)
        {
            Interlocked.Increment(ref runs);
            return Task.FromResult(new Note("done"));
        }
    }

    [TestMethod]
    public async Task CancelThatArrivesWhileARunReleasesItsClaim_IsFoundByTheNextRun()
    {
        var store = new MemoryUploadStore(TimeProvider.System);
        int runs = 0;
        HttpStatusCode? cancelStatus = null;

        // The upload is too large to be sent directly, so a cancellation that a run acted on is removed when the run ends.
        await using var host = await StartAsync(
            Task<Note> (UploadedFile file) => {
                Interlocked.Increment(ref runs);
                throw new ServiceUnavailableApiException("Busy.");
            },
            options => options.MaxChunkSize = 5 * 1024 * 1024,
            store: store);

        var session = await host.CreateUploadAsync("a", (5 * 1024 * 1024) + 1);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile((5 * 1024 * 1024) + 1));

        // The handler can't run at the moment, and while its run releases the claim, which keeps the run on this server until it is released, the upload is
        // cancelled.
        store.BeforeOperation = operation => {
            if (operation is "Release" && cancelStatus is null)
            {
                using var response = host.AbortAsync(session).WaitAsync(WaitTimeout).GetAwaiter().GetResult();
                cancelStatus = response.StatusCode;
            }
        };

        await UploadTestHost.ShouldBeErrorAsync(await host.CompleteAsync(session), HttpStatusCode.ServiceUnavailable, null);
        cancelStatus.ShouldBe(HttpStatusCode.Accepted);

        // The run had decided how it ended before the cancellation, so it leaves the cancellation, which the completion made again finds.
        await UploadTestHost.ShouldBeErrorAsync(await host.CompleteAsync(session), HttpStatusCode.Conflict, UploadErrorCodes.Cancelled);
        runs.ShouldBe(1);
    }

    [TestMethod]
    public async Task UploadDeletedWhileItsHandlerRuns_EndsAsCancelled_WithoutTheWarningOfASlowHandler()
    {
        var clock = new ManualClock(manualTimestamps: true);
        var store = new MemoryUploadStore(clock);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The handler takes its time to stop once its token is cancelled.
        await using var host = await StartAsync(
            async (UploadedFile file, CancellationToken cancellationToken) => {
                using var registration = cancellationToken.Register(() => stopping.TrySetResult());
                started.SetResult();
                await release.Task.WaitAsync(WaitTimeout, CancellationToken.None);
                cancellationToken.ThrowIfCancellationRequested();

                return new Note("done");
            },
            options => options.CompletionPingInterval = TimeSpan.FromDays(1),
            store: store,
            timeProvider: clock);

        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        var completion = host.CompleteAsync(session);
        await started.Task.WaitAsync(WaitTimeout);

        // Another server deleted the upload, such as for a cancellation that found its claim lapsed, which the next renewal finds.
        (await store.DeleteAsync((await store.GetAsync("1", session.Id)).ShouldNotBeNull().Record)).ShouldBeTrue();
        await WaitUntilAsync(() => clock.HasTimerDueIn(TimeSpan.FromSeconds(15)));
        clock.Advance(TimeSpan.FromSeconds(15));

        await stopping.Task.WaitAsync(WaitTimeout);
        clock.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(100);
        host.Web.Logs.ShouldNotContain(l => l.Message.Contains("seconds after", StringComparison.Ordinal));

        release.SetResult();
        await UploadTestHost.ShouldBeErrorAsync(await completion.WaitAsync(WaitTimeout), HttpStatusCode.Conflict, UploadErrorCodes.Cancelled);
    }

    [TestMethod]
    public async Task CancelOnAnotherServerOfARunClaimedBeforeItsCheck_IsAccepted_AndTheRunFindsIt()
    {
        var store = new MemoryUploadStore(TimeProvider.System) { SharingMode = StoreSharingMode.Shared };
        var shortLivedStore = new InterceptingShortLivedStore(new DeclaredSharedShortLivedStore(new MemoryShortLivedStore(TimeProvider.System)));
        var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int runs = 0;

        await using var first = await StartAsync(CountAsync, store: store, shortLivedStore: shortLivedStore);
        await using var second = await StartAsync(CountAsync, store: store, shortLivedStore: shortLivedStore);

        var session = await first.CreateUploadAsync("a", 10);
        await first.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        // The first server has claimed the run, and its check for a cancellation waits until the second server has answered the cancellation.
        shortLivedStore.Before = (operation, key) => {
            if (operation is "Get" && IsCancelKey(key, session) && !checking.Task.IsCompleted)
            {
                checking.SetResult();
                cancelled.Task.WaitAsync(WaitTimeout).GetAwaiter().GetResult();
            }
        };

        var completion = first.CompleteAsync(session);
        await checking.Task.WaitAsync(WaitTimeout);

        (await second.AbortAsync(session)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        cancelled.SetResult();

        await UploadTestHost.ShouldBeErrorAsync(await completion.WaitAsync(WaitTimeout), HttpStatusCode.Conflict, UploadErrorCodes.Cancelled);
        runs.ShouldBe(0);

        Task<Note> CountAsync(UploadedFile file)
        {
            Interlocked.Increment(ref runs);
            return Task.FromResult(new Note("done"));
        }
    }

    [TestMethod]
    public async Task CancelOfARunClaimedBeforeItIsWritten_IsAccepted_AndTheRunFindsItBeforeTheHandlerStarts()
    {
        var shortLivedStore = new InterceptingShortLivedStore(new MemoryShortLivedStore(TimeProvider.System));
        var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int runs = 0;

        await using var host = await StartAsync(CountAsync, shortLivedStore: shortLivedStore);
        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        // The run has claimed the upload, and its check for a cancellation waits until the cancellation has been answered.
        shortLivedStore.Before = (operation, key) => {
            if (operation is "Get" && IsCancelKey(key, session) && !checking.Task.IsCompleted)
            {
                checking.SetResult();
                cancelled.Task.WaitAsync(WaitTimeout).GetAwaiter().GetResult();
            }
        };

        var completion = host.CompleteAsync(session);
        await checking.Task.WaitAsync(WaitTimeout);

        (await host.AbortAsync(session)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        cancelled.SetResult();

        await UploadTestHost.ShouldBeErrorAsync(await completion.WaitAsync(WaitTimeout), HttpStatusCode.Conflict, UploadErrorCodes.Cancelled);
        runs.ShouldBe(0);
        await UploadTestHost.ShouldBeErrorAsync(await host.GetStatusResponseAsync(session), HttpStatusCode.NotFound, UploadErrorCodes.NotFound);

        Task<Note> CountAsync(UploadedFile file)
        {
            Interlocked.Increment(ref runs);
            return Task.FromResult(new Note("done"));
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RunOfASharedUploadStore_ChecksForACancellationAtTheInterval_AndOneOfAProcessLocalStoreOnlyBeforeTheHandler(bool isShared)
    {
        IShortLivedStore inner = new MemoryShortLivedStore(TimeProvider.System);
        var shortLivedStore = new InterceptingShortLivedStore(isShared ? new DeclaredSharedShortLivedStore(inner) : inner);
        int checks = 0;

        await using var host = await StartAsync(
            async (UploadedFile file, CancellationToken cancellationToken) => {
                await Task.Delay(500, cancellationToken);
                return new Note("done");
            },
            options => options.CancelCheckInterval = TimeSpan.FromMilliseconds(50),
            shortLivedStore: shortLivedStore);

        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        shortLivedStore.After = (operation, key) => {
            if (operation is "Get" && IsCancelKey(key, session))
                Interlocked.Increment(ref checks);
        };

        UploadTestHost.WithoutPings(await host.CompleteAndReadAsync(session)).ShouldBe(["""{"item":{"text":"done"}}""", """{"end":true}"""]);

        if (isShared)
            checks.ShouldBeGreaterThan(2);
        else
            checks.ShouldBe(1);
    }

    [TestMethod]
    public async Task HandlerThatRunsOnForTenSecondsAfterTheCancellation_IsLogged_AndItsOutcomeStands()
    {
        var clock = new ManualClock(manualTimestamps: true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The handler is past its last check of its token, and saves its work all the same.
        await using var host = await StartAsync(
            async (UploadedFile file) => {
                started.SetResult();
                await release.Task.WaitAsync(WaitTimeout);

                return new Note("saved");
            },
            options => options.CompletionPingInterval = TimeSpan.FromDays(1),
            timeProvider: clock);

        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        var completion = host.CompleteAndReadAsync(session);
        await started.Task.WaitAsync(WaitTimeout);

        (await host.AbortAsync(session)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await WaitUntilAsync(() => clock.HasTimerDueIn(TimeSpan.FromSeconds(10)));
        clock.Advance(TimeSpan.FromSeconds(9));
        await Task.Delay(100);
        host.Web.Logs.ShouldNotContain(l => l.Message.Contains("10 seconds after", StringComparison.Ordinal));

        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => host.Web.Logs.Any(l => l.Message.Contains("10 seconds after", StringComparison.Ordinal)));

        var warning = host.Web.Logs.Single(l => l.Message.Contains("10 seconds after", StringComparison.Ordinal));
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldBe(
            $"The handler of upload {session.Id} of user 1 at endpoint /folders/{{folderId}}/files has not stopped or finished 10 seconds after the upload " +
            "was cancelled.");

        release.SetResult();

        // The handler returned, so its outcome stands, and the upload can no longer be cancelled.
        UploadTestHost.WithoutPings(await completion.WaitAsync(WaitTimeout)).ShouldBe(["""{"item":{"text":"saved"}}""", """{"end":true}"""]);
        (await host.GetStatusAsync(session)).State.ShouldBe(UploadState.Completed);
        await UploadTestHost.ShouldBeErrorAsync(await host.AbortAsync(session), HttpStatusCode.Conflict, UploadErrorCodes.Finished);
    }

    [TestMethod]
    public async Task ProcessLocalDirectory_IsLockedWhileTheApplicationRuns_AndAnotherApplicationFailsToStart()
    {
        string directory = UploadTestHost.CreateDirectoryPath();
        string lockFile = Path.Combine(directory, "uploads.lock");

        try
        {
            var first = await StartAsync((UploadedFile file) => new Note("done"), directory: directory, backgroundCleanup: true);

            try
            {
                File.Exists(lockFile).ShouldBeTrue();

                var ex = await Should.ThrowAsync<IOException>(
                    () => StartAsync((UploadedFile file) => new Note("done"), directory: directory, backgroundCleanup: true));

                ex.Message.ShouldContain(lockFile);
                ex.Message.ShouldContain("DirectoryUploadStoreOptions.SharingMode is ProcessLocal");
            }
            finally
            {
                await first.DisposeAsync();
            }

            // The lock is released when the application stops, and the next one takes it.
            File.Exists(lockFile).ShouldBeFalse();

            await using var next = await StartAsync((UploadedFile file) => new Note("done"), directory: directory, backgroundCleanup: true);
            File.Exists(lockFile).ShouldBeTrue();
        }
        finally
        {
            UploadTestHost.DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task SharedDirectory_TakesNoLock()
    {
        var shortLivedStore = new DeclaredSharedShortLivedStore(new MemoryShortLivedStore(TimeProvider.System));
        await using var host = await StartAsync((UploadedFile file) => new Note("done"), shortLivedStore: shortLivedStore, backgroundCleanup: true);

        host.Services.GetRequiredService<IUploadStore>().SharingMode.ShouldBe(StoreSharingMode.Shared);
        File.Exists(Path.Combine(host.Directory, "uploads.lock")).ShouldBeFalse();
    }

    [TestMethod]
    public async Task SharedUploadStore_WithAProcessLocalShortLivedStore_FailsToStart()
    {
        string directory = UploadTestHost.CreateDirectoryPath();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, (UploadedFile file) => new Note("done")),
            configureServices: services => services.AddDirectoryUploadStore(
                o => (o.Directory, o.SharingMode) = (TestPaths.ToDirectory(directory), StoreSharingMode.Shared)),
            backgroundCleanup: true));

        ex.Message.ShouldContain("MemoryShortLivedStore");
        ex.Message.ShouldContain("DirectoryUploadStoreOptions.SharingMode");
    }

    [TestMethod]
    public async Task SharedUploadStore_WithASharedStoreForTheUploadsKeyOnly_Starts()
    {
        string directory = UploadTestHost.CreateDirectoryPath();
        var shared = new DeclaredSharedShortLivedStore(new MemoryShortLivedStore(TimeProvider.System));

        try
        {
            await using var host = await UploadTestHost.StartAsync(
                app => app.MapChunkedUpload(Pattern, (UploadedFile file) => new Note("done")),
                configureServices: services => {
                    services.AddDirectoryUploadStore(o => (o.Directory, o.SharingMode) = (TestPaths.ToDirectory(directory), StoreSharingMode.Shared));
                    services.AddKeyedSingleton<IShortLivedStore>(ShortLivedStoreKeys.Uploads, shared);
                },
                backgroundCleanup: true);

            host.Services.GetRequiredService<IShortLivedStore>().SharingMode.ShouldBe(StoreSharingMode.ProcessLocal);
            UploadTestHost.WithoutPings((await host.UploadAsync(UploadTestHost.CreateFile(10))).Lines).ShouldBe(
                ["""{"item":{"text":"done"}}""", """{"end":true}"""]);
        }
        finally
        {
            UploadTestHost.DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task SharedUploadStore_WithAProcessLocalStoreForTheUploadsKey_FailsToStart_AlsoWhenTheAppsStoreIsShared()
    {
        string directory = UploadTestHost.CreateDirectoryPath();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, (UploadedFile file) => new Note("done")),
            configureServices: services => {
                services.AddDirectoryUploadStore(o => (o.Directory, o.SharingMode) = (TestPaths.ToDirectory(directory), StoreSharingMode.Shared));
                services.AddKeyedSingleton<IShortLivedStore>(ShortLivedStoreKeys.Uploads, new MemoryShortLivedStore(TimeProvider.System));
            },
            backgroundCleanup: true,
            shortLivedStore: new DeclaredSharedShortLivedStore(new MemoryShortLivedStore(TimeProvider.System))));

        ex.Message.ShouldContain("ShortLivedStoreKeys.Uploads");
        UploadTestHost.DeleteDirectory(directory);
    }

    [TestMethod]
    public async Task SharedUploadStore_WithAProcessLocalStoreForAnyKey_FailsToStart_NamingThatStore()
    {
        string directory = UploadTestHost.CreateDirectoryPath();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, (UploadedFile file) => new Note("done")),
            configureServices: services => {
                services.AddDirectoryUploadStore(o => (o.Directory, o.SharingMode) = (TestPaths.ToDirectory(directory), StoreSharingMode.Shared));
                services.AddKeyedSingleton<IShortLivedStore>(
                    KeyedService.AnyKey, (_, _) => new InterceptingShortLivedStore(new MemoryShortLivedStore(TimeProvider.System)));
            },
            backgroundCleanup: true,
            shortLivedStore: new DeclaredSharedShortLivedStore(new MemoryShortLivedStore(TimeProvider.System))));

        ex.Message.ShouldContain($"({nameof(InterceptingShortLivedStore)})");
        ex.Message.ShouldContain("ShortLivedStoreKeys.Uploads");
        UploadTestHost.DeleteDirectory(directory);
    }

    [TestMethod]
    public async Task StartWithoutAnUploadStore_FailsWithAnErrorThatNamesTheDirectoryStore()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, (UploadedFile file) => new Note("done")),
            configureServices: services => services.RemoveAll<IUploadStore>()));

        ex.Message.ShouldContain(nameof(IUploadStore));
        ex.Message.ShouldContain("AddDirectoryUploadStore");
    }

    [TestMethod]
    public async Task StoreThatReportsNoSharingMode_FailsToStart()
    {
        var store = new MemoryUploadStore(TimeProvider.System) { SharingMode = StoreSharingMode.Unspecified };

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => StartAsync((UploadedFile file) => new Note("done"), store: store));
        ex.Message.ShouldBe("MemoryUploadStore reports Unspecified as its StoreSharingMode. A store reports ProcessLocal or Shared.");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RunLeftByAPreviousProcess_OfAProcessLocalStore_IsEndedAsInterruptedByTheFirstCleanup(bool isProcessLocal)
    {
        var store = new MemoryUploadStore(TimeProvider.System) { SharingMode = isProcessLocal ? StoreSharingMode.ProcessLocal : StoreSharingMode.Shared };
        IShortLivedStore? shortLivedStore = isProcessLocal ? null : new DeclaredSharedShortLivedStore(new MemoryShortLivedStore(TimeProvider.System));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The first host's handler runs, and its claim is current: to the host that starts next, it is the run of a process that has ended.
        await using var before = await StartAsync(
            async (UploadedFile file) => {
                started.TrySetResult();
                await release.Task.WaitAsync(WaitTimeout);

                return new Note("done");
            },
            store: store,
            shortLivedStore: shortLivedStore);

        var session = await before.CreateUploadAsync("a", 10);
        await before.PutChunksAsync(session, UploadTestHost.CreateFile(10));
        var running = before.CompleteAsync(session);
        await started.Task.WaitAsync(WaitTimeout);

        try
        {
            await using var after = await StartAsync((UploadedFile file) => new Note("again"), store: store, shortLivedStore: shortLivedStore);
            await after.Services.GetRequiredService<ChunkedUploads>().CleanUpAsync(CancellationToken.None);

            var upload = (await store.GetAsync("1", session.Id)).ShouldNotBeNull();

            if (isProcessLocal)
            {
                upload.Outcome.ShouldNotBeNull();
                await UploadTestHost.ShouldBeErrorAsync(await after.CompleteAsync(session), HttpStatusCode.InternalServerError, UploadErrorCodes.Interrupted);
            }
            else
            {
                // Another server that shares the store may still run it.
                upload.Outcome.ShouldBeNull();
                upload.RunState.ShouldBe(UploadRunState.Running);
            }
        }
        finally
        {
            release.TrySetResult();
            (await running.WaitAsync(WaitTimeout)).Dispose();
        }
    }

    [TestMethod]
    public async Task ClaimThatThisProcessReleasesInTheBackground_IsNotTakenForTheRunOfAnEndedProcess_AfterAStepOfTheClock()
    {
        var clock = new ManualClock();
        var store = new MemoryUploadStore(clock);

        await using var host = await StartAsync(
            Task<Note> (UploadedFile file) => throw new ServiceUnavailableApiException("Busy."), store: store, timeProvider: clock);

        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        // The system's clock steps back after the application started, so the claim reads as older than this process, and the release of the claim fails, so
        // that this process releases it in the background.
        clock.Advance(TimeSpan.FromHours(-1));

        store.BeforeOperation = operation => {
            if (operation is "Release")
                throw new IOException("The store failed.");
        };

        await UploadTestHost.ShouldBeErrorAsync(await host.CompleteAsync(session), HttpStatusCode.ServiceUnavailable, null);
        await host.Services.GetRequiredService<ChunkedUploads>().CleanUpAsync(CancellationToken.None);

        var upload = (await store.GetAsync("1", session.Id)).ShouldNotBeNull();
        upload.Outcome.ShouldBeNull();
        upload.RunState.ShouldBe(UploadRunState.Running);
    }

    [TestMethod]
    public async Task RunLeftByAPreviousProcess_OfAProcessLocalDirectory_IsEndedAsInterruptedByTheFirstCleanup()
    {
        string directory = UploadTestHost.CreateDirectoryPath();

        try
        {
            UploadSession session;

            await using (var before = await StartAsync((UploadedFile file) => new Note("done"), directory: directory))
            {
                session = await before.CreateUploadAsync("a", 10);
                await before.PutChunksAsync(session, UploadTestHost.CreateFile(10));
            }

            // The previous process had claimed the run, and its claim is current.
            string uploadDirectory = Directory.GetDirectories(directory, session.Id, SearchOption.AllDirectories).Single();
            string claimedUtc = DateTime.UtcNow.AddSeconds(-10).ToString("O");

            await File.WriteAllTextAsync(
                Path.Combine(uploadDirectory, "claim"),
                $$"""{"claimId":"previous","leaseDuration":"00:01:00","startedUtc":"{{claimedUtc}}","renewedUtc":"{{claimedUtc}}"}""");

            await using var after = await StartAsync((UploadedFile file) => new Note("again"), directory: directory);
            await after.Services.GetRequiredService<ChunkedUploads>().CleanUpAsync(CancellationToken.None);

            (await after.Services.GetRequiredService<IUploadStore>().GetAsync("1", session.Id)).ShouldNotBeNull().Outcome.ShouldNotBeNull();
            await UploadTestHost.ShouldBeErrorAsync(await after.CompleteAsync(session), HttpStatusCode.InternalServerError, UploadErrorCodes.Interrupted);
            Directory.Exists(uploadDirectory).ShouldBeFalse();
        }
        finally
        {
            UploadTestHost.DeleteDirectory(directory);
        }
    }

    private static Task<UploadTestHost> StartAsync(
        Delegate handler,
        Action<ChunkedUploadOptions>? configure = null,
        string? directory = null,
        MemoryUploadStore? store = null,
        IShortLivedStore? shortLivedStore = null,
        TimeProvider? timeProvider = null,
        bool backgroundCleanup = false)
    {
        return UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, handler),
            options => {
                options.CompletionPingInterval = TimeSpan.FromMilliseconds(50);

                configure?.Invoke(options);
            },
            directory,
            configureServices: services => {
                if (store is not null)
                    services.AddSingleton<IUploadStore>(store);
            },
            timeProvider: timeProvider,
            backgroundCleanup: backgroundCleanup,
            shortLivedStore: shortLivedStore);
    }

    /// <summary>
    /// Gets a value indicating whether a key of the short-lived store is the cancellation of an upload: <c>{prefix}{uploadId}:{scope}:{userId}</c>.
    /// </summary>
    private static bool IsCancelKey(string key, UploadSession session)
    {
        return key.StartsWith(ToolkitKeys.UploadCancel + session.Id + ":", StringComparison.Ordinal);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(WaitTimeout);

        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}
