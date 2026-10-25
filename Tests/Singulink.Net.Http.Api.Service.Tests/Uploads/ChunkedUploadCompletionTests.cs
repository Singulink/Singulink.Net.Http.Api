using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Singulink.Net.Http.Api.DataContracts;
using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The completion of chunked uploads: how the handler runs, how its result, items and errors are streamed, and how completion requests that come back
/// get its outcome.
/// </summary>
[PrefixTestClass]
public sealed class ChunkedUploadCompletionTests
{
    private const string Pattern = "/folders/{folderId}/files";

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    public sealed record Progress(double Fraction);

    public sealed record Done(string Name, string Hash);

    public sealed record Note(string Text);

    public sealed record FolderCommand(string Title, int Priority);

    [TestMethod]
    public async Task Complete_StreamingHandler_StreamsItems()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, StreamAsync));
        byte[] file = UploadTestHost.CreateFile(100);

        var (_, lines) = await host.UploadAsync(file, "photo.jpg");

        lines.ShouldBe([
            """{"item":{"fraction":0.5}}""",
            """{"item":{"fraction":1}}""",
            $$$"""{"item":{"name":"photo.jpg","hash":"{{{UploadTestHost.Hash(file)}}}"}}""",
            """{"end":true}""",
        ]);

        static async IAsyncEnumerable<object> StreamAsync(UploadedFile file, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new Progress(0.5);
            await Task.Yield();
            yield return new Progress(1);
            yield return new Done(file.FileName, await UploadTestHost.HashAsync(file, cancellationToken));
        }
    }

    [TestMethod]
    public async Task Complete_Again_GetsLastItemWithoutRunningHandler()
    {
        int runs = 0;

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, StreamAsync));
        var (session, _) = await host.UploadAsync(UploadTestHost.CreateFile(10));

        (await host.CompleteAndReadAsync(session)).ShouldBe(["""{"item":{"text":"done"}}""", """{"end":true}"""]);
        (await host.CompleteAndReadAsync(session)).ShouldBe(["""{"item":{"text":"done"}}""", """{"end":true}"""]);
        runs.ShouldBe(1);

        async IAsyncEnumerable<Note> StreamAsync(UploadedFile file)
        {
            Interlocked.Increment(ref runs);
            yield return new Note("working");
            await Task.Yield();
            yield return new Note("done");
        }
    }

    [TestMethod]
    public async Task Complete_HandlerWithoutItems_EndsStream()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, StreamAsync));

        var (session, lines) = await host.UploadAsync(UploadTestHost.CreateFile(10));

        lines.ShouldBe(["""{"end":true}"""]);
        (await host.CompleteAndReadAsync(session)).ShouldBe(["""{"end":true}"""]);

        static async IAsyncEnumerable<Note> StreamAsync(UploadedFile file)
        {
            await Task.Yield();
            yield break;
        }
    }

    [TestMethod]
    public async Task Complete_NullResult_IsNullItem()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, (UploadedFile file) => Task.FromResult<Note?>(null)));

        var (session, lines) = await host.UploadAsync(UploadTestHost.CreateFile(10));

        lines.ShouldBe(["""{"item":null}""", """{"end":true}"""]);
        (await host.CompleteAndReadAsync(session)).ShouldBe(["""{"item":null}""", """{"end":true}"""]);
    }

    [TestMethod]
    public async Task Complete_WhileRunning_AttachesToRunFromLatestItem()
    {
        int runs = 0;
        var release = new TaskCompletionSource();

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, StreamAsync));
        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        using var first = await host.CompleteAsync(session);
        using var firstReader = new StreamReader(await first.Content.ReadAsStreamAsync());
        (await firstReader.ReadLineAsync()).ShouldBe("""{"item":{"text":"one"}}""");
        (await firstReader.ReadLineAsync()).ShouldBe("""{"item":{"text":"two"}}""");

        using var second = await host.CompleteAsync(session);
        using var secondReader = new StreamReader(await second.Content.ReadAsStreamAsync());
        (await secondReader.ReadLineAsync()).ShouldBe("""{"item":{"text":"two"}}""");

        release.SetResult();

        foreach (var reader in new[] { firstReader, secondReader })
        {
            (await reader.ReadLineAsync()).ShouldBe("""{"item":{"text":"three"}}""");
            (await reader.ReadLineAsync()).ShouldBe("""{"end":true}""");
        }

        runs.ShouldBe(1);

        async IAsyncEnumerable<Note> StreamAsync(UploadedFile file)
        {
            Interlocked.Increment(ref runs);
            yield return new Note("one");
            yield return new Note("two");
            await release.Task.WaitAsync(WaitTimeout);
            yield return new Note("three");
        }
    }

    [TestMethod]
    public async Task Complete_ClientDisconnects_HandlerKeepsRunningAndOutcomeIsKept()
    {
        int runs = 0;
        var clock = new ManualClock(manualTimestamps: true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerDone = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientGone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var host = await UploadTestHost.StartAsync(
            app => {
                // Records when the server sees the client of a completion go away, before the run's token takes the place of the request's.
                app.Use(async (context, next) => {
                    if (context.Request.Path.Value!.EndsWith("/complete", StringComparison.Ordinal))
                        context.RequestAborted.Register(() => clientGone.TrySetResult());

                    await next(context);
                });

                app.MapChunkedUpload(Pattern, HandleAsync);
            },
            timeProvider: clock);

        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        var completion = host.CompleteAsync(session);
        await started.Task.WaitAsync(WaitTimeout);
        await WaitUntilAsync(() => clock.HasTimerDueIn(TimeSpan.FromSeconds(10)));
        clock.Advance(TimeSpan.FromSeconds(10));

        var first = await completion;
        var firstReader = new StreamReader(await first.Content.ReadAsStreamAsync());
        (await firstReader.ReadLineAsync()).ShouldBe("""{"ping":true}""");

        // The client goes away while the handler runs.
        firstReader.Dispose();
        first.Dispose();
        await clientGone.Task.WaitAsync(TimeSpan.FromSeconds(20));
        release.SetResult();

        (await handlerDone.Task.WaitAsync(WaitTimeout)).ShouldBe("cancelled: False, request aborted: False");
        (await host.CompleteAndReadAsync(session)).ShouldBe(["""{"item":{"text":"done"}}""", """{"end":true}"""]);
        runs.ShouldBe(1);

        async Task<Note> HandleAsync(UploadedFile file, HttpContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref runs);
            started.SetResult();
            await release.Task.WaitAsync(WaitTimeout, cancellationToken);
            handlerDone.SetResult($"cancelled: {cancellationToken.IsCancellationRequested}, request aborted: {context.RequestAborted.IsCancellationRequested}");

            return new Note("done");
        }
    }

    [TestMethod]
    public async Task Complete_ClientThatFallsBehind_SkipsOlderItems_AndGetsTheLastOne()
    {
        var produced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string padding = new('x', 64 * 1024);

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, StreamAsync));
        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        // The client reads nothing until the handler has produced all its items.
        using var response = await host.CompleteAsync(session);
        await produced.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var numbers = (await UploadTestHost.ReadLinesAsync(response)).SkipLast(1)
            .Select(l => int.Parse(JsonDocument.Parse(l).RootElement.GetProperty("item").GetProperty("text").GetString()!.Split(' ')[0]))
            .ToList();

        numbers.Count.ShouldBeLessThan(100);
        numbers.ShouldBeInOrder(SortDirection.Ascending);
        numbers[^1].ShouldBe(199);

        async IAsyncEnumerable<Note> StreamAsync(UploadedFile file)
        {
            for (int i = 0; i < 200; i++)
                yield return new Note($"{i} {padding}");

            await Task.Yield();
            produced.SetResult();
        }
    }

    [TestMethod]
    public async Task Complete_HandlerThrowsBeforeFirstRecord_IsErrorResponseAndOutcome()
    {
        int runs = 0;

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, Task<Note> (UploadedFile file) => {
            Interlocked.Increment(ref runs);
            throw new ValidationApiException("The video cannot be read.") { ErrorCode = "video-unreadable" };
        }));

        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));
        string directory = host.GetUploadDirectory(session);

        for (int i = 0; i < 2; i++)
        {
            using var response = await host.CompleteAsync(session);

            await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.UnprocessableEntity, "video-unreadable");
            (await response.Content.ReadAsStringAsync()).ShouldBe("[video-unreadable] The video cannot be read.");
        }

        runs.ShouldBe(1);
        (await host.GetStatusAsync(session)).State.ShouldBe(UploadState.Completed);
        File.Exists(Path.Combine(directory, "data")).ShouldBeFalse();
    }

    [TestMethod]
    public async Task Complete_HandlerThrowsAfterItems_IsErrorRecordAndThenErrorResponse()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, StreamAsync));

        var (session, lines) = await host.UploadAsync(UploadTestHost.CreateFile(10));

        lines.ShouldBe([
            """{"item":{"text":"working"}}""",
            """{"error":{"status":403,"code":"denied","message":"The folder is read-only."}}""",
        ]);

        using var again = await host.CompleteAsync(session);
        await UploadTestHost.ShouldBeErrorAsync(again, HttpStatusCode.Forbidden, "denied");

        static async IAsyncEnumerable<Note> StreamAsync(UploadedFile file)
        {
            yield return new Note("working");
            await Task.Yield();
            throw new ForbiddenApiException("The folder is read-only.") { ErrorCode = "denied" };
        }
    }

    [TestMethod]
    public async Task Complete_HandlerThrowsServiceUnavailable_LeavesUploadToRunAgain()
    {
        int runs = 0;

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, async (UploadedFile file) => {
            await Task.Yield();

            if (Interlocked.Increment(ref runs) is 1)
                throw new ServiceUnavailableApiException("The converter is busy.") { RetryAfter = TimeSpan.FromSeconds(5) };

            return new Note("converted");
        }));

        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        using (var busy = await host.CompleteAsync(session))
        {
            await UploadTestHost.ShouldBeErrorAsync(busy, HttpStatusCode.ServiceUnavailable, null);
            busy.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromSeconds(5));
        }

        var status = await host.GetStatusAsync(session);
        status.State.ShouldBe(UploadState.Receiving);
        status.MissingChunks.ShouldBeEmpty();

        (await host.CompleteAndReadAsync(session)).ShouldBe(["""{"item":{"text":"converted"}}""", """{"end":true}"""]);
        runs.ShouldBe(2);
    }

    [TestMethod]
    public async Task Complete_UnexpectedException_IsServerErrorWithReferenceId()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, Task<Note> (UploadedFile file) => {
            throw new InvalidOperationException("Disk exploded.");
        }));

        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        using var first = await host.CompleteAsync(session);
        using var second = await host.CompleteAsync(session);

        await UploadTestHost.ShouldBeErrorAsync(first, HttpStatusCode.InternalServerError, null);
        await UploadTestHost.ShouldBeErrorAsync(second, HttpStatusCode.InternalServerError, null);

        string referenceId = first.Headers.GetValues("Reference-Id").ShouldHaveSingleItem();
        second.Headers.GetValues("Reference-Id").ShouldBe([referenceId]);
        (await second.Content.ReadAsStringAsync()).ShouldNotContain("exploded");

        var error = host.Web.Logs.Where(l => l.Level is LogLevel.Error).ShouldHaveSingleItem();
        error.Message.ShouldContain(referenceId);
    }

    [TestMethod]
    public async Task Complete_NoItemForPingInterval_SendsPings()
    {
        var clock = new ManualClock(manualTimestamps: true);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, async (UploadedFile file) => {
                await release.Task.WaitAsync(WaitTimeout);
                return new Note("done");
            }),
            timeProvider: clock);

        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        // The first ping starts the response, and each interval without an item has one.
        var completion = host.CompleteAsync(session);
        await AdvanceToPingAsync(clock, TimeSpan.FromSeconds(10));

        using var response = await completion;
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        (await reader.ReadLineAsync()).ShouldBe("""{"ping":true}""");

        for (int ping = 0; ping < 2; ping++)
        {
            await AdvanceToPingAsync(clock, TimeSpan.FromSeconds(10));
            (await reader.ReadLineAsync()).ShouldBe("""{"ping":true}""");
        }

        release.SetResult();
        (await reader.ReadToEndAsync()).ShouldBe("""{"item":{"text":"done"}}""" + "\n" + """{"end":true}""" + "\n");
    }

    [TestMethod]
    public async Task Complete_KeepAlivePingOnHandler_SetsPingInterval()
    {
        var clock = new ManualClock(manualTimestamps: true);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HandleAsync), timeProvider: clock);
        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        var completion = host.CompleteAndReadAsync(session);
        await AdvanceToPingAsync(clock, TimeSpan.FromSeconds(0.05));
        release.SetResult();

        (await completion).ShouldBe(["""{"ping":true}""", """{"item":{"text":"done"}}""", """{"end":true}"""]);

        [KeepAlivePing(0.05)]
        async Task<Note> HandleAsync(UploadedFile file)
        {
            await release.Task.WaitAsync(WaitTimeout);
            return new Note("done");
        }
    }

    [TestMethod]
    public async Task Complete_HandlerThrowsAfterPing_IsErrorRecord()
    {
        var clock = new ManualClock(manualTimestamps: true);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, async Task<Note> (UploadedFile file) => {
                await release.Task.WaitAsync(WaitTimeout);
                throw new ValidationApiException("The video cannot be read.");
            }),
            timeProvider: clock);

        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        var completion = host.CompleteAndReadAsync(session);
        await AdvanceToPingAsync(clock, TimeSpan.FromSeconds(10));
        release.SetResult();

        (await completion).ShouldBe(["""{"ping":true}""", """{"error":{"status":422,"message":"The video cannot be read."}}"""]);

        await UploadTestHost.ShouldBeErrorAsync(await host.CompleteAsync(session), HttpStatusCode.UnprocessableEntity, null);
    }

    [TestMethod]
    public async Task Abort_WhileRunning_CancelsHandlerAndRemovesUpload()
    {
        var started = new TaskCompletionSource();
        var cancelled = new TaskCompletionSource();

        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, async Task<Note> (UploadedFile file, CancellationToken cancellationToken) => {
                started.SetResult();

                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    cancelled.SetResult();
                    throw;
                }

                return new Note("never");
            }),
            options => options.CompletionPingInterval = TimeSpan.FromMilliseconds(50));

        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));
        string directory = host.GetUploadDirectory(session);

        using var completion = await host.CompleteAsync(session);
        await started.Task.WaitAsync(WaitTimeout);

        (await host.GetStatusAsync(session)).State.ShouldBe(UploadState.Processing);
        (await host.AbortAsync(session)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await cancelled.Task.WaitAsync(WaitTimeout);

        var lines = await UploadTestHost.ReadLinesAsync(completion);
        lines[^1].ShouldStartWith("""{"error":{"status":404,"code":"upload-not-found",""");

        await UploadTestHost.ShouldBeErrorAsync(await host.CompleteAsync(session), HttpStatusCode.NotFound, UploadErrorCodes.NotFound);
        Directory.Exists(directory).ShouldBeFalse();
    }

    [TestMethod]
    public async Task Chunk_WhileRunning_IsClosed()
    {
        var release = new TaskCompletionSource();

        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, async (UploadedFile file) => {
                await release.Task.WaitAsync(WaitTimeout);
                return new Note("done");
            }),
            options => options.CompletionPingInterval = TimeSpan.FromMilliseconds(50));

        byte[] file = UploadTestHost.CreateFile(10);
        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, file);

        using var completion = await host.CompleteAsync(session);

        await UploadTestHost.ShouldBeErrorAsync(await host.PutChunkAsync(session, file, 0), HttpStatusCode.Conflict, UploadErrorCodes.Closed);
        release.SetResult();
        (await UploadTestHost.ReadLinesAsync(completion))[^1].ShouldBe("""{"end":true}""");
    }

    [TestMethod]
    public async Task Complete_ParameterCannotBeBound_LeavesUploadToComplete()
    {
        int runs = 0;

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(
            Pattern, (UploadedFile file, [FromHeader(Name = "Copies")] int copies) => {
                Interlocked.Increment(ref runs);
                return Task.FromResult(new Note($"{copies} copies"));
            }));

        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        await UploadTestHost.ShouldBeErrorAsync(await CompleteWithCopiesAsync("many"), HttpStatusCode.BadRequest, null);
        runs.ShouldBe(0);
        (await host.GetStatusAsync(session)).State.ShouldBe(UploadState.Receiving);

        using var response = await CompleteWithCopiesAsync("3");
        (await UploadTestHost.ReadLinesAsync(response)).ShouldBe(["""{"item":{"text":"3 copies"}}""", """{"end":true}"""]);

        Task<HttpResponseMessage> CompleteWithCopiesAsync(string copies)
        {
            var request = host.CreateRequest(HttpMethod.Post, $"{UploadTestHost.Path}/uploads/{session.Id}/complete");
            request.Headers.Add("Copies", copies);

            return host.Client.SendAsync(request);
        }
    }

    [TestMethod]
    public async Task Complete_WithAnotherQueryString_IsQueryMismatch()
    {
        int runs = 0;

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(
            Pattern, (UploadRequest upload, string tag) => { }, (UploadedFile file, string tag, int? copies) => {
                Interlocked.Increment(ref runs);
                return Task.FromResult(new Note($"{tag} {copies}"));
            }));

        var session = await host.CreateUploadAsync("a", 10, query: "tag=ok&copies=2");
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        // The handler would bind other values than the validator did.
        foreach (string query in new[] { "tag=other&copies=2", "tag=ok", "tag=ok&copies=2&more=1", "tag=ok&copies=2&copies=3" })
            await UploadTestHost.ShouldBeErrorAsync(await host.CompleteAsync(session, query: query), HttpStatusCode.Conflict, UploadErrorCodes.QueryMismatch);

        runs.ShouldBe(0);

        // The order of the parameters and the case of their names don't matter, as for binding.
        (await host.CompleteAndReadAsync(session, query: "Copies=2&TAG=ok")).ShouldBe(["""{"item":{"text":"ok 2"}}""", """{"end":true}"""]);
    }

    [TestMethod]
    public async Task Handler_BindsRouteValuesQueryServicesAndSession()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(
            Pattern,
            (long folderId, string tag, UploadedFile file, TestSessionToken token, TimeProvider timeProvider) =>
                Task.FromResult(new Note($"{folderId} {tag} {file.Length} {token.UserId} {timeProvider is TestClock}"))));

        var (_, lines) = await host.UploadAsync(UploadTestHost.CreateFile(10), query: "tag=urgent");

        lines[0].ShouldBe("""{"item":{"text":"5 urgent 10 1 True"}}""");
    }

    [TestMethod]
    public async Task Validator_RunsAtCreationWithUploadRequest()
    {
        UploadRequest? seen = null;

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(
            Pattern,
            (long folderId, UploadRequest upload, TestSessionToken token) => {
                seen = upload;

                if (upload.FileName.EndsWith(".exe", StringComparison.Ordinal))
                    throw new ValidationApiException("This type of file is not supported.") { ErrorCode = "type-not-supported" };

                if (folderId is not 5 || token.UserId is not 1)
                    throw new ForbiddenApiException("No.");
            },
            (UploadedFile file) => Task.FromResult(new Note(file.FileName))));

        var session = await host.CreateUploadAsync("report.pdf", 123, contentType: "application/pdf");

        seen.ShouldNotBeNull();
        (seen.FileName, seen.Length, seen.ContentType).ShouldBe(("report.pdf", 123L, "application/pdf"));

        using var rejected = await host.SendCreateAsync(new { fileName = "setup.exe", length = 10 });

        await UploadTestHost.ShouldBeErrorAsync(rejected, HttpStatusCode.UnprocessableEntity, "type-not-supported");
        Directory.GetDirectories(Path.GetDirectoryName(host.GetUploadDirectory(session))!).ShouldHaveSingleItem();
    }

    [TestMethod]
    public async Task Command_IsReadByValidatorAndHandler()
    {
        FolderCommand? validated = null;

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(
            Pattern,
            (UploadRequest<FolderCommand> upload) => { validated = upload.Command; },
            (UploadedFile<FolderCommand> file) => Task.FromResult(new Note($"{file.Command.Title} {file.Command.Priority}"))));

        var (_, lines) = await host.UploadAsync(UploadTestHost.CreateFile(10), command: new FolderCommand("Invoices", 2));

        validated.ShouldBe(new FolderCommand("Invoices", 2));
        lines[0].ShouldBe("""{"item":{"text":"Invoices 2"}}""");
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("null")]
    [DataRow("""{"title":"Invoices","priority":"high"}""")]
    [DataRow("[1]")]
    public async Task Command_MissingOrNotValid_IsInvalidAtCreation(string? command)
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(
            Pattern, (UploadedFile<FolderCommand> file) => Task.FromResult(new Note(file.Command.Title))));

        string json = command is null ? """{"fileName":"a","length":10}""" : $$"""{"fileName":"a","length":10,"command":{{command}}}""";
        var request = host.CreateRequest(HttpMethod.Post, $"{UploadTestHost.Path}/uploads");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        await UploadTestHost.ShouldBeErrorAsync(await host.Client.SendAsync(request), HttpStatusCode.BadRequest, UploadErrorCodes.Invalid);
    }

    [TestMethod]
    public async Task Command_Nullable_CanBeMissing()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(
            Pattern, (UploadedFile<FolderCommand?> file) => Task.FromResult(new Note(file.Command?.Title ?? "none"))));

        var (_, lines) = await host.UploadAsync(UploadTestHost.CreateFile(10));

        lines[0].ShouldBe("""{"item":{"text":"none"}}""");
    }

    [TestMethod]
    public async Task UploadedFile_InDirectoryStore_HasFilePath()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, async (UploadedFile file) => {
            byte[] content = await File.ReadAllBytesAsync(file.FilePath!);

            await using var stream = file.OpenReadStream();
            return new Note($"{content.Length} {stream.CanSeek} {stream.CanWrite}");
        }));

        var (_, lines) = await host.UploadAsync(UploadTestHost.CreateFile(10));

        lines[0].ShouldBe("""{"item":{"text":"10 True False"}}""");
    }

    [TestMethod]
    public async Task UploadedFile_MovedByHandler_IsKept()
    {
        string destination = Path.Combine(Path.GetTempPath(), "httpapi-moved-" + Guid.NewGuid().ToString("N"));
        byte[] file = UploadTestHost.CreateFile(1000);

        try
        {
            await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, async (UploadedFile uploaded) => {
                await uploaded.MoveToAsync(destination);
                return new Note(uploaded.FilePath!);
            }));

            var (_, lines) = await host.UploadAsync(file);

            lines[0].ShouldBe(JsonSerializer.Serialize(new { item = new Note(destination) }, UploadTestHost.JsonOptions));
            UploadTestHost.Hash(await File.ReadAllBytesAsync(destination)).ShouldBe(UploadTestHost.Hash(file));
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [TestMethod]
    public async Task UploadedFile_MoveThatIsCancelled_LeavesTheFile()
    {
        string destination = Path.Combine(Path.GetTempPath(), "httpapi-moved-" + Guid.NewGuid().ToString("N"));

        try
        {
            await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, async (UploadedFile uploaded) => {
                string? path = uploaded.FilePath;
                await Should.ThrowAsync<OperationCanceledException>(() => uploaded.MoveToAsync(destination, new CancellationToken(canceled: true)));

                return new Note($"{uploaded.FilePath == path} {File.Exists(path)} {File.Exists(destination)}");
            }));

            var (_, lines) = await host.UploadAsync(UploadTestHost.CreateFile(10));

            lines[0].ShouldBe("""{"item":{"text":"True True False"}}""");
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [TestMethod]
    public void UploadedFile_MoveToAnotherFileSystem_IsFoundByTheMountPoints()
    {
        // A move on the same file system renames the file, and a move to another, which File.Move would copy synchronously, copies it asynchronously.
        if (OperatingSystem.IsWindows())
        {
            string[] roots = [@"C:\", @"D:\", @"\\server\share\"];

            DirectoryUploadStore.GetMountPoint(@"C:\data\uploads\data", roots).ShouldBe(@"C:\");
            DirectoryUploadStore.GetMountPoint(@"c:\files\a.bin", roots).ShouldBe(@"C:\");
            DirectoryUploadStore.GetMountPoint(@"D:\files\a.bin", roots).ShouldBe(@"D:\");
            DirectoryUploadStore.GetMountPoint(@"\\other\share\a.bin", roots).ShouldBe(@"\\other\share");
        }
        else
        {
            string[] roots = ["/", "/mnt/uploads", "/mnt/uploads-old/"];

            DirectoryUploadStore.GetMountPoint("/mnt/uploads/user/upload/data", roots).ShouldBe("/mnt/uploads");
            DirectoryUploadStore.GetMountPoint("/mnt/uploads", roots).ShouldBe("/mnt/uploads");
            DirectoryUploadStore.GetMountPoint("/mnt/uploads-old/data", roots).ShouldBe("/mnt/uploads-old/");
            DirectoryUploadStore.GetMountPoint("/mnt/uploadsx/data", roots).ShouldBe("/");
            DirectoryUploadStore.GetMountPoint("/var/data/a.bin", roots).ShouldBe("/");
        }
    }

    [TestMethod]
    public async Task Run_PastIdleExpiry_IsInterrupted()
    {
        var clock = new ManualClock(manualTimestamps: true);

        // The pings are further apart than the time limit, so that the response starts with the error.
        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, async Task<Note> (UploadedFile file, CancellationToken cancellationToken) => {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return new Note("never");
            }),
            options => {
                options.IdleExpiry = TimeSpan.FromHours(1);
                options.CompletionPingInterval = TimeSpan.FromHours(2);
            },
            timeProvider: clock);

        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        var completion = host.CompleteAsync(session);
        await WaitUntilAsync(() => clock.HasTimerDueIn(TimeSpan.FromHours(1)));
        clock.Advance(TimeSpan.FromHours(1));

        await UploadTestHost.ShouldBeErrorAsync(await completion, HttpStatusCode.InternalServerError, UploadErrorCodes.Interrupted);
        await UploadTestHost.ShouldBeErrorAsync(await host.CompleteAsync(session), HttpStatusCode.InternalServerError, UploadErrorCodes.Interrupted);
    }

    [TestMethod]
    public async Task UploadedFile_OutsideOfHandler_CannotBeBound()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapPost("/other", (UploadedFile file) => file.FileName));

        using var response = await host.Client.SendAsync(host.CreateRequest(HttpMethod.Post, "/other"));

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        host.Web.Logs.ShouldContain(l => l.Exception is InvalidOperationException && l.Exception.Message.Contains("only bound in the handlers"));
    }

    [TestMethod]
    public async Task Status_WhileRunning_IsProcessing()
    {
        var release = new TaskCompletionSource();

        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, async (UploadedFile file) => {
                await release.Task.WaitAsync(WaitTimeout);
                return new Note("done");
            }),
            options => options.IdleExpiry = TimeSpan.FromHours(3));

        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));
        using var completion = await host.CompleteAsync(session);

        var status = await host.GetStatusAsync(session);

        status.State.ShouldBe(UploadState.Processing);
        status.MissingChunks.ShouldBeEmpty();
        status.ExpiresUtc.ShouldBe(host.Clock.GetUtcNow().UtcDateTime.AddHours(3), TimeSpan.FromSeconds(5));

        release.SetResult();
        (await UploadTestHost.ReadLinesAsync(completion))[^1].ShouldBe("""{"end":true}""");
    }

    [TestMethod]
    public async Task Created_ResponseJson_IsTheToolkitContract()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, (UploadedFile file) => Task.FromResult(new Note("a"))));

        using var response = await host.SendCreateAsync(new { fileName = "a", length = 10 });
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        json.EnumerateObject().Select(p => p.Name).ShouldBe(["id", "chunkSize", "expiresUtc"]);
        json.GetProperty("expiresUtc").GetString().ShouldEndWith("Z");
    }

    /// <summary>
    /// Waits until a completion waits for its next ping, and advances the clock to it.
    /// </summary>
    private static async Task AdvanceToPingAsync(ManualClock clock, TimeSpan pingInterval)
    {
        await WaitUntilAsync(() => clock.HasTimerDueIn(pingInterval));
        clock.Advance(pingInterval);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}
