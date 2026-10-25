using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The runs of the handlers of chunked uploads when something fails: the renewals of their claims, the store, and handlers that don't stop at their time
/// limit. The service's clock only moves when a test advances it.
/// </summary>
[PrefixTestClass]
public sealed class ChunkedUploadRunTests
{
    private const string Pattern = "/folders/{folderId}/files";

    private static readonly TimeSpan RenewInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    public sealed record Note(string Text);

    /// <summary>
    /// A scoped service, which is disposed with the services of its request.
    /// </summary>
    public sealed class RequestResource : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }

    [TestMethod]
    public async Task RenewalThatFails_IsMadeAgainAtTheNextInterval_AndTheRunEnds()
    {
        var clock = new ManualClock(manualTimestamps: true);
        var store = new MemoryUploadStore(clock);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var host = await StartAsync(clock, store, HandleAsync);
        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        var completion = host.CompleteAndReadAsync(session);
        await started.Task.WaitAsync(WaitTimeout);

        // A store whose request times out throws an OperationCanceledException that is not the renewal's.
        store.BeforeOperation = operation => {
            if (operation is "Renew")
                throw new OperationCanceledException("The request of the store timed out.");
        };

        await AdvanceToRenewalAsync(clock, host, warnings: 1);
        release.SetResult();

        WithoutPings(await completion.WaitAsync(WaitTimeout)).ShouldBe(["""{"item":{"text":"done"}}""", """{"end":true}"""]);

        async Task<Note> HandleAsync(UploadedFile file)
        {
            started.SetResult();
            await release.Task.WaitAsync(WaitTimeout);

            return new Note("done");
        }
    }

    [TestMethod]
    public async Task StoreThatFailsWhileACompletionWaitsForAnotherServer_EndsTheStreamWithAnErrorRecordOfTheExceptionHandler()
    {
        var clock = new ManualClock(manualTimestamps: true);
        var store = new MemoryUploadStore(clock);
        var handler = new CountingExceptionHandler(ex => ex as ApiException);

        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, Task<Note> (UploadedFile file) => Task.FromResult(new Note("done"))),
            options => options.Directory = null,
            configureServices: services => {
                services.AddSingleton<IUploadStore>(store);
                services.AddSingleton<IApiExceptionHandler>(handler);
            },
            timeProvider: clock);

        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        // Another server runs the handler, and the completion waits for it.
        var record = (await store.GetAsync("1", session.Id)).ShouldNotBeNull().Record;
        (await store.TryClaimRunAsync(record, TimeSpan.FromMinutes(1))).ShouldNotBeNull();

        var completion = host.CompleteAndReadAsync(session);
        await WaitUntilAsync(() => clock.HasTimerDueIn(TimeSpan.FromSeconds(1)));

        // The store can't be reached for a moment, when the request looks at the run again after its first ping.
        store.BeforeOperation = operation => {
            if (operation is "Get")
                throw new IOException("The share cannot be reached.");
        };

        clock.Advance(TimeSpan.FromSeconds(10));
        var lines = await completion.WaitAsync(WaitTimeout);

        lines.Count.ShouldBe(2);
        lines[0].ShouldBe("""{"ping":true}""");
        lines[1].ShouldStartWith("""{"error":{"status":503,"code":"upload-store-unavailable",""");
        handler.Calls.ShouldBe(1);
        host.Web.Logs.ShouldNotContain(l => l.Level >= LogLevel.Error);
    }

    [TestMethod]
    public async Task ClaimWhoseReleaseFails_IsKeptFromLapsingAndReleasedAgain_SoTheHandlerRunsAgain()
    {
        var clock = new ManualClock(manualTimestamps: true);
        var store = new MemoryUploadStore(clock);
        bool releaseFails = true;
        int runs = 0;

        store.BeforeOperation = operation => {
            if (operation is "Release" && Volatile.Read(ref releaseFails))
                throw new IOException("Another process has the claim open.");
        };

        await using var host = await StartAsync(clock, store, HandleAsync);
        var session = await host.CreateUploadAsync("a", 10);
        byte[] file = UploadTestHost.CreateFile(10);
        await host.PutChunksAsync(session, file);

        // The handler can't run at the moment, and its claim can't be released.
        using (var first = await host.CompleteAsync(session).WaitAsync(WaitTimeout))
            await UploadTestHost.ShouldBeErrorAsync(first, HttpStatusCode.ServiceUnavailable, null);

        // Meanwhile the server answers the upload's completions and chunks with a 503 that the client retries, and keeps the claim from lapsing.
        using (var pending = await host.CompleteAsync(session).WaitAsync(WaitTimeout))
            await UploadTestHost.ShouldBeErrorAsync(pending, HttpStatusCode.ServiceUnavailable, UploadErrorCodes.StoreUnavailable);

        using (var chunk = await host.PutChunkAsync(session, file, 0))
            await UploadTestHost.ShouldBeErrorAsync(chunk, HttpStatusCode.ServiceUnavailable, UploadErrorCodes.StoreUnavailable);

        for (int i = 0; i < 45; i++)
            await AdvanceAsync(TimeSpan.FromSeconds(2));

        (await store.GetAsync("1", session.Id)).ShouldNotBeNull().RunState.ShouldBe(UploadRunState.Running);

        // Once the release succeeds, the next completion runs the handler again.
        Volatile.Write(ref releaseFails, false);
        await AdvanceAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => store.GetAsync("1", session.Id).AsTask().Result!.RunState is UploadRunState.None);

        WithoutPings(await host.CompleteAndReadAsync(session).WaitAsync(WaitTimeout)).ShouldBe(["""{"item":{"text":"done"}}""", """{"end":true}"""]);
        runs.ShouldBe(2);

        async Task AdvanceAsync(TimeSpan time)
        {
            await WaitUntilAsync(() => clock.HasTimerDueIn(time));
            clock.Advance(time);
        }

        Task<Note> HandleAsync(UploadedFile file) =>
            Interlocked.Increment(ref runs) is 1 ? throw new ServiceUnavailableApiException("Not at the moment.") : Task.FromResult(new Note("done"));
    }

    [TestMethod]
    public async Task ClaimWhoseReleaseAndRenewalFail_LapsesOnceItsLeaseRunsOut()
    {
        var clock = new ManualClock(manualTimestamps: true);
        var store = new MemoryUploadStore(clock);
        bool isRun = false;

        // The store can neither release nor renew the claim, as the directory store can't while another process has the claim open.
        store.BeforeOperation = operation => {
            if (operation is "Release" or "Renew" && Volatile.Read(ref isRun))
                throw new IOException("Another process has the claim open.");
        };

        await using var host = await StartAsync(clock, store, Task<Note> (UploadedFile file) => {
            Volatile.Write(ref isRun, true);
            throw new ServiceUnavailableApiException("Not at the moment.");
        });

        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        using (var first = await host.CompleteAsync(session).WaitAsync(WaitTimeout))
            await UploadTestHost.ShouldBeErrorAsync(first, HttpStatusCode.ServiceUnavailable, null);

        // The release is made again every 2 seconds until the claim has gone a lease without a renewal.
        for (int i = 0; i < 30; i++)
        {
            await WaitUntilAsync(() => clock.HasTimerDueIn(TimeSpan.FromSeconds(2)));
            clock.Advance(TimeSpan.FromSeconds(2));
        }

        await WaitUntilAsync(() => host.Web.Logs.Any(l => l.Level == LogLevel.Error && l.Message.Contains("so it lapsed", StringComparison.Ordinal)));
        clock.Advance(TimeSpan.FromSeconds(5));

        using var interrupted = await host.CompleteAsync(session).WaitAsync(WaitTimeout);
        await UploadTestHost.ShouldBeErrorAsync(interrupted, HttpStatusCode.InternalServerError, UploadErrorCodes.Interrupted);
    }

    [TestMethod]
    public async Task ClaimWhoseReleaseAndRenewalFail_LapsesALeaseAfterItWasLastRenewed()
    {
        var clock = new ManualClock(manualTimestamps: true);
        var store = new MemoryUploadStore(clock);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool isRun = false;

        store.BeforeOperation = operation => {
            if (operation is "Release" or "Renew" && Volatile.Read(ref isRun))
                throw new IOException("Another process has the claim open.");
        };

        await using var host = await StartAsync(clock, store, HandleAsync);
        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        // The run ends 11 seconds after its claim was made, before its first renewal, and its claim can't be released.
        var completion = host.CompleteAndReadAsync(session);
        await started.Task.WaitAsync(WaitTimeout);
        clock.Advance(TimeSpan.FromSeconds(11));
        release.SetResult();
        (await completion.WaitAsync(WaitTimeout))[^1].ShouldStartWith("""{"error":{"status":503,""");

        // The server gives the claim up when it lapses in the store, a lease after it was made, rather than a lease after the run ended.
        for (int i = 0; i < 25; i++)
        {
            await WaitUntilAsync(() => clock.HasTimerDueIn(TimeSpan.FromSeconds(2)));
            host.Web.Logs.ShouldNotContain(l => l.Level == LogLevel.Error);
            clock.Advance(TimeSpan.FromSeconds(2));
        }

        await WaitUntilAsync(() => host.Web.Logs.Any(l => l.Level == LogLevel.Error && l.Message.Contains("so it lapsed", StringComparison.Ordinal)));
        (await store.GetAsync("1", session.Id)).ShouldNotBeNull().RunState.ShouldBe(UploadRunState.Lapsed);

        async Task<Note> HandleAsync(UploadedFile file)
        {
            started.SetResult();
            await release.Task.WaitAsync(WaitTimeout);
            Volatile.Write(ref isRun, true);

            throw new ServiceUnavailableApiException("Not at the moment.");
        }
    }

    [TestMethod]
    public async Task RenewalThatTheRunsEndCancelsAfterTheStoreWroteIt_CountsAsMadeWhenTheClaimCannotBeReleased()
    {
        var clock = new ManualClock(manualTimestamps: true);
        var store = new MemoryUploadStore(clock);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewalWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool isRun = false;

        store.BeforeOperation = operation => {
            if (operation is "Release" or "Renew" && Volatile.Read(ref isRun))
                throw new IOException("Another process has the claim open.");
        };

        // The store writes the run's renewal, and sees the cancellation of the renewal only afterwards, which it reports with an exception of its own.
        store.AfterRenewal = async cancellationToken => {
            if (!cancellationToken.CanBeCanceled)
                return;

            renewalWritten.TrySetResult();

            try
            {
                await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw new IOException("The connection to the store was closed.");
            }
        };

        await using var host = await StartAsync(clock, store, HandleAsync);
        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        // The run renews its claim after 15 seconds, and ends 5 seconds later, while the renewal waits. Its claim can't be released.
        var completion = host.CompleteAndReadAsync(session);
        await started.Task.WaitAsync(WaitTimeout);
        await WaitUntilAsync(() => clock.HasTimerDueIn(RenewInterval));
        clock.Advance(RenewInterval);
        await renewalWritten.Task.WaitAsync(WaitTimeout);
        clock.Advance(TimeSpan.FromSeconds(5));
        release.SetResult();
        (await completion.WaitAsync(WaitTimeout))[^1].ShouldStartWith("""{"error":{"status":503,""");

        // The claim counts as renewed when the run ended, so the server gives it up a lease after that, never before the store's lease runs out.
        for (int i = 0; i < 30; i++)
        {
            await WaitUntilAsync(() => clock.HasTimerDueIn(TimeSpan.FromSeconds(2)));
            host.Web.Logs.ShouldNotContain(l => l.Level == LogLevel.Error);
            clock.Advance(TimeSpan.FromSeconds(2));
        }

        await WaitUntilAsync(() => host.Web.Logs.Any(l => l.Level == LogLevel.Error && l.Message.Contains("so it lapsed", StringComparison.Ordinal)));
        (await store.GetAsync("1", session.Id)).ShouldNotBeNull().RunState.ShouldBe(UploadRunState.Lapsed);

        async Task<Note> HandleAsync(UploadedFile file)
        {
            started.SetResult();
            await release.Task.WaitAsync(WaitTimeout);
            Volatile.Write(ref isRun, true);

            throw new ServiceUnavailableApiException("Not at the moment.");
        }
    }

    [TestMethod]
    public async Task RenewalsThatFailUntilTheLeaseRunsOut_StopTheHandler()
    {
        var clock = new ManualClock(manualTimestamps: true);
        var store = new MemoryUploadStore(clock);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var host = await StartAsync(clock, store, HandleAsync);
        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        var completion = host.CompleteAndReadAsync(session);
        await started.Task.WaitAsync(WaitTimeout);

        store.BeforeOperation = operation => {
            if (operation is "Renew")
                throw new IOException("The store cannot be reached.");
        };

        // The third failure is a lease after the last renewal, when the claim lapses.
        await AdvanceToRenewalAsync(clock, host, warnings: 1);
        await AdvanceToRenewalAsync(clock, host, warnings: 2);
        await AdvanceToRenewalAsync(clock, host, warnings: 2);

        (await completion.WaitAsync(WaitTimeout))[^1].ShouldStartWith("""{"error":{"status":500,"code":"upload-interrupted",""");
        host.Web.Logs.ShouldContain(l => l.Level == LogLevel.Error && l.Message.Contains("before its lease ran out", StringComparison.Ordinal));

        async Task<Note> HandleAsync(UploadedFile file, CancellationToken cancellationToken)
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);

            return new Note("never");
        }
    }

    [TestMethod]
    public async Task LostClaimOfAnUploadThatCannotBeRead_StopsTheHandler()
    {
        var clock = new ManualClock(manualTimestamps: true);
        var store = new MemoryUploadStore(clock);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var host = await StartAsync(clock, store, HandleAsync);
        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        var completion = host.CompleteAndReadAsync(session);
        await started.Task.WaitAsync(WaitTimeout);

        // The upload is deleted, as by an abort on another server, so the renewal fails, and the store then fails to read it.
        var upload = (await store.GetAsync("1", session.Id)).ShouldNotBeNull();
        await store.DeleteAsync(upload.Record);

        store.BeforeOperation = operation => {
            if (operation is "Get")
                throw new IOException("The store cannot be reached.");
        };

        await WaitUntilAsync(() => clock.HasTimerDueIn(RenewInterval));
        clock.Advance(RenewInterval);

        await cancelled.Task.WaitAsync(WaitTimeout);
        (await completion.WaitAsync(WaitTimeout))[^1].ShouldStartWith("""{"error":{"status":500,"code":"upload-interrupted",""");

        async Task<Note> HandleAsync(UploadedFile file, CancellationToken cancellationToken)
        {
            started.SetResult();

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally
            {
                cancelled.SetResult();
            }

            return new Note("never");
        }
    }

    [TestMethod]
    public async Task HandlerThatDoesNotStopAtItsTimeLimit_EndsTheUploadAsInterrupted_AndKeepsItsRequestOpen()
    {
        var clock = new ManualClock(manualTimestamps: true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateState = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var idleExpiry = TimeSpan.FromHours(1);

        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, HandleAsync),
            options => options.IdleExpiry = idleExpiry,
            timeProvider: clock,
            configureServices: services => services.AddScoped<RequestResource>());

        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        var completion = host.CompleteAsync(session);
        await started.Task.WaitAsync(WaitTimeout);

        // The handler's token is cancelled at the limit, and the run ends a lease of a minute later.
        await WaitUntilAsync(() => clock.HasTimerDueIn(idleExpiry + TimeSpan.FromMinutes(1)));
        clock.Advance(idleExpiry + TimeSpan.FromMinutes(1));

        using var first = await completion.WaitAsync(WaitTimeout);
        using var reader = new StreamReader(await first.Content.ReadAsStreamAsync());
        string? line;

        do
            line = await reader.ReadLineAsync().WaitAsync(WaitTimeout);
        while (line == """{"ping":true}""");

        line.ShouldNotBeNull().ShouldStartWith("""{"error":{"status":500,"code":"upload-interrupted",""");
        await UploadTestHost.ShouldBeErrorAsync(await host.CompleteAsync(session), HttpStatusCode.InternalServerError, UploadErrorCodes.Interrupted);

        // The request that ran the handler stays open while the handler runs on, and so do the services that the handler took from it.
        var rest = reader.ReadToEndAsync();
        release.SetResult();

        (await lateState.Task.WaitAsync(WaitTimeout)).ShouldBe("disposed: False");
        (await rest.WaitAsync(WaitTimeout)).ShouldBeEmpty();
        await WaitUntilAsync(() => host.Web.Logs.Any(l => l.Message.Contains("finished after the upload was ended", StringComparison.Ordinal)));

        async Task<Note> HandleAsync(UploadedFile file, RequestResource resource)
        {
            started.SetResult();
            await release.Task.WaitAsync(WaitTimeout);
            lateState.SetResult($"disposed: {resource.IsDisposed}");

            return new Note("late");
        }
    }

    [TestMethod]
    public async Task UnexpectedExceptionAfterAnItem_IsLoggedWithTheReferenceIdOfTheOutcome()
    {
        var handler = new CountingExceptionHandler(_ => null);

        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, StreamAsync), configureServices: services => services.AddSingleton<IApiExceptionHandler>(handler));

        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        var lines = await host.CompleteAndReadAsync(session);
        lines[0].ShouldBe("""{"item":{"text":"converting"}}""");

        using var record = JsonDocument.Parse(lines[^1]);
        var error = record.RootElement.GetProperty("error");
        error.GetProperty("status").GetInt32().ShouldBe(500);
        string referenceId = error.GetProperty("referenceId").GetString().ShouldNotBeNull();
        error.GetProperty("message").GetString().ShouldBe($"An unexpected error has occurred and has been logged. Reference ID: {referenceId}.");

        var log = host.Web.Logs.Where(l => l.Level is LogLevel.Error).ShouldHaveSingleItem();
        log.Exception.ShouldBeOfType<InvalidOperationException>();
        log.Message.ShouldContain(referenceId);

        using var again = await host.CompleteAsync(session);
        await UploadTestHost.ShouldBeErrorAsync(again, HttpStatusCode.InternalServerError, null);
        again.Headers.GetValues("Reference-Id").ShouldBe([referenceId]);

        static async IAsyncEnumerable<Note> StreamAsync(UploadedFile file)
        {
            yield return new Note("converting");
            await Task.Yield();

            throw new InvalidOperationException("Disk exploded.");
        }
    }

    [TestMethod]
    public async Task HandlersErrorBeforeTheStreamStarts_IsMappedOnce()
    {
        var handler = new CountingExceptionHandler(ex => ex as ApiException ?? new ConflictApiException("The folder was moved."));

        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, Task<Note> (UploadedFile file) => throw new InvalidOperationException("Moved.")),
            configureServices: services => services.AddSingleton<IApiExceptionHandler>(handler));

        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        using var response = await host.CompleteAsync(session);

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.Conflict, null);
        handler.Calls.ShouldBe(1);

        // The next completion gets the error as the upload's outcome, which was mapped when the handler failed.
        using var again = await host.CompleteAsync(session);

        await UploadTestHost.ShouldBeErrorAsync(again, HttpStatusCode.Conflict, null);
        (await again.Content.ReadAsStringAsync()).ShouldBe("The folder was moved.");
        handler.Calls.ShouldBe(1);
    }

    [TestMethod]
    public async Task ExceptionHandlerThatThrows_LeavesTheHandlersExceptionUnmapped()
    {
        var handler = new CountingExceptionHandler(_ => throw new InvalidOperationException("The exception handler failed."));

        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, Task<Note> (UploadedFile file) => throw new FormatException("The file cannot be read.")),
            configureServices: services => services.AddSingleton<IApiExceptionHandler>(handler));

        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        using var response = await host.CompleteAsync(session).WaitAsync(WaitTimeout);

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.InternalServerError, null);
        host.Web.Logs.ShouldContain(l => l.Level == LogLevel.Error && l.Exception is FormatException);
        host.Web.Logs.ShouldContain(l => l.Level == LogLevel.Error && l.Exception is InvalidOperationException);
    }

    [TestMethod]
    public async Task ErrorsOfTheService_GoThroughTheExceptionHandler_WithTheValuesOfTheirMessages()
    {
        var clock = new ManualClock(manualTimestamps: true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, HandleAsync),
            options => {
                options.MaxSize = 100;
                options.MaxUploadsPerUser = 1;
            },
            timeProvider: clock,
            configureServices: services => services.AddSingleton<IApiExceptionHandler, WordingExceptionHandler>());

        using (var tooLarge = await host.SendCreateAsync(new { fileName = "a", length = 101 }))
            (await tooLarge.Content.ReadAsStringAsync()).ShouldBe("[upload-too-large] Up to 100 bytes.");

        using (var invalid = await host.SendCreateAsync(new { fileName = " ", length = 10 }))
            (await invalid.Content.ReadAsStringAsync()).ShouldBe("[upload-invalid] The fileName is wrong.");

        // An upload whose claim was left by a server that crashed has the outcome of an interrupted upload.
        var interrupted = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(interrupted, UploadTestHost.CreateFile(10));
        string renewedUtc = clock.UtcNow.AddMinutes(-4).ToString("O");

        await File.WriteAllTextAsync(
            Path.Combine(host.GetUploadDirectory(interrupted), "claim"),
            $$"""{"claimId":"crashed","leaseDuration":"00:01:00","startedUtc":"{{renewedUtc}}","renewedUtc":"{{renewedUtc}}"}""");

        using (var outcome = await host.CompleteAsync(interrupted))
            (await outcome.Content.ReadAsStringAsync()).ShouldBe("[upload-interrupted] Interrupted, in the app's words.");

        // An upload aborted while its handler runs, after the completion's first ping, ends the completion with an error record.
        var aborted = await host.CreateUploadAsync("b", 10);
        await host.PutChunksAsync(aborted, UploadTestHost.CreateFile(10));

        using (var limitReached = await host.SendCreateAsync(new { fileName = "c", length = 10 }))
            (await limitReached.Content.ReadAsStringAsync()).ShouldBe("[upload-limit-reached] At most 1 uploads.");

        var completion = host.CompleteAsync(aborted);
        await started.Task.WaitAsync(WaitTimeout);
        await WaitUntilAsync(() => clock.HasTimerDueIn(TimeSpan.FromSeconds(10)));
        clock.Advance(TimeSpan.FromSeconds(10));

        using var response = await completion.WaitAsync(WaitTimeout);
        (await host.AbortAsync(aborted)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await UploadTestHost.ReadLinesAsync(response)).ShouldBe([
            """{"ping":true}""", """{"error":{"status":404,"code":"upload-not-found","message":"Gone, in the app's words."}}""",
        ]);

        async Task<Note> HandleAsync(UploadedFile file, CancellationToken cancellationToken)
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);

            return new Note("never");
        }
    }

    [TestMethod]
    public async Task OutcomeThatCannotBeRead_IsServerError()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, (UploadedFile file) => Task.FromResult(new Note("a"))));
        var (session, _) = await host.UploadAsync(UploadTestHost.CreateFile(10));

        string outcomePath = Path.Combine(host.GetUploadDirectory(session), "outcome.json");
        await File.WriteAllTextAsync(outcomePath, $$"""{"outcome":"not an outcome","storedUtc":"{{DateTime.UtcNow:O}}"}""");

        await UploadTestHost.ShouldBeErrorAsync(await host.CompleteAsync(session), HttpStatusCode.InternalServerError, UploadErrorCodes.OutcomeUnreadable);
        host.Web.Logs.ShouldContain(l => l.Level == LogLevel.Error && l.Message.Contains("cannot be read", StringComparison.Ordinal));
    }

    private static Task<UploadTestHost> StartAsync(ManualClock clock, MemoryUploadStore store, Delegate handler)
    {
        return UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, handler),
            options => options.Directory = null,
            configureServices: services => services.AddSingleton<IUploadStore>(store),
            timeProvider: clock);
    }

    /// <summary>
    /// Advances the clock to the next renewal of the run's claim, and waits until the renewals have logged the specified number of warnings.
    /// </summary>
    private static async Task AdvanceToRenewalAsync(ManualClock clock, UploadTestHost host, int warnings)
    {
        await WaitUntilAsync(() => clock.HasTimerDueIn(RenewInterval));
        clock.Advance(RenewInterval);

        await WaitUntilAsync(() => host.Web.Logs.Count(l => l.Level == LogLevel.Warning && l.Message.Contains("could not be renewed", StringComparison.Ordinal))
            >= warnings);
    }

    private static List<string> WithoutPings(List<string> lines) => [.. lines.Where(l => l != """{"ping":true}""")];

    /// <summary>
    /// An app's exception handler that words the service's errors in its own way, by their error codes and with their values.
    /// </summary>
    private sealed class WordingExceptionHandler(ILogger<WordingExceptionHandler> logger, IHostEnvironment environment)
        : ApiExceptionHandler(logger, environment)
    {
        protected override ValueTask<ApiException> MapApiExceptionAsync(HttpContext httpContext, ApiException exception) => new(exception switch {
            { ErrorCode: UploadErrorCodes.TooLarge } when UploadErrorData.TryGetMaxSize(exception, out long maxSize) =>
                exception.WithMessage($"Up to {maxSize} bytes."),
            { ErrorCode: UploadErrorCodes.LimitReached } when UploadErrorData.TryGetMaxUploadsPerUser(exception, out int maxUploadsPerUser) =>
                exception.WithMessage($"At most {maxUploadsPerUser} uploads."),
            { ErrorCode: UploadErrorCodes.Invalid } when UploadErrorData.TryGetProperty(exception, out string? property) =>
                exception.WithMessage($"The {property} is wrong."),
            { ErrorCode: UploadErrorCodes.Interrupted } => exception.WithMessage("Interrupted, in the app's words."),
            { ErrorCode: UploadErrorCodes.NotFound } => exception.WithMessage("Gone, in the app's words."),
            _ => exception,
        });
    }

    /// <summary>
    /// An exception handler of the app that maps exceptions with a function, and counts its calls.
    /// </summary>
    private sealed class CountingExceptionHandler(Func<Exception, ApiException?> map) : IApiExceptionHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<ApiException?> HandleAsync(HttpContext httpContext, Exception exception)
        {
            Interlocked.Increment(ref _calls);
            return new(map(exception));
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(WaitTimeout);

        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}
