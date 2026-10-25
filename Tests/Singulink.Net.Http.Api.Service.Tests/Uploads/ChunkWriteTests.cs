using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Singulink.Net.Http.Api.DataContracts;
using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Writes of chunks in the directory store that overlap the claim of the upload's run, and writes of received chunks that fail.
/// </summary>
[PrefixTestClass]
public sealed class ChunkWriteTests
{
    private const string Pattern = "/folders/{folderId}/files";

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    public sealed record Note(string Text);

    [TestMethod]
    [DataRow(StatusCodes.Status400BadRequest)]
    [DataRow(StatusCodes.Status408RequestTimeout)]
    public async Task ChunkWhoseBodyFails_IsAnsweredWithTheBodysError_NotAsAStoreFailure(int status)
    {
        // The server's body fails after a few bytes, as Kestrel's does for a client that drops the connection or sends too slowly.
        await using var host = await UploadTestHost.StartAsync(app => {
            app.Use((context, next) => {
                if (HttpMethods.IsPut(context.Request.Method))
                    context.Request.Body = new FailingBody(context.Request.Body, 3, new BadHttpRequestException("The body failed.", status));

                return next(context);
            });

            app.MapChunkedUpload(Pattern, HashAsync);
        });

        var session = await host.CreateUploadAsync("a", 10);
        using var response = await host.PutChunkAsync(session, UploadTestHost.CreateFile(10), 0);

        response.StatusCode.ShouldBe((HttpStatusCode)status);
        host.Web.Logs.ShouldNotContain(l => l.Message.Contains("upload store failed", StringComparison.Ordinal));
        (await host.GetStatusAsync(session)).MissingChunks.ShouldNotBeEmpty();
    }

    [TestMethod]
    public async Task CompletionWhileAReceivedChunkIsWrittenAgain_IsIncomplete_UntilTheWriteEnds()
    {
        byte[] file = UploadTestHost.CreateFile(10);
        byte[] rewritten = UploadTestHost.CreateFile(10, seed: 2);

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HashAsync));
        var session = await host.CreateUploadAsync("a", file.Length);
        await host.PutChunksAsync(session, file);

        // The chunk is sent again, with other bytes, and its body arrives slowly.
        var body = new SlowContent(rewritten, sentBeforeWait: 4);
        var rewrite = SendChunkAsync(host, session, body);
        string chunksDirectory = Path.Combine(host.GetUploadDirectory(session), "chunks");
        await WaitUntilAsync(() => Directory.EnumerateFiles(chunksDirectory, "*.writing").Any());

        await UploadTestHost.ShouldBeErrorAsync(await host.CompleteAsync(session), HttpStatusCode.Conflict, UploadErrorCodes.Incomplete);

        body.Release();
        (await rewrite.WaitAsync(WaitTimeout)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await host.CompleteAndReadAsync(session)).ShouldBe([$$$"""{"item":{"text":"{{{UploadTestHost.Hash(rewritten)}}}"}}""", """{"end":true}"""]);
    }

    [TestMethod]
    public async Task CompletionWhileAWriteOnAnotherServerIsAlive_WaitsForTheWriteToEnd()
    {
        var clock = new ManualClock(manualTimestamps: true);
        byte[] file = UploadTestHost.CreateFile(10);

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HashAsync), timeProvider: clock);
        var session = await host.CreateUploadAsync("a", file.Length);
        await host.PutChunksAsync(session, file);

        // Another server is writing the chunk again, and has not removed its marker yet.
        string marker = Path.Combine(host.GetUploadDirectory(session), "chunks", "0.other.writing");
        await File.WriteAllTextAsync(marker, $$"""{"aliveUtc":"{{clock.UtcNow:O}}"}""");

        // The completion can't claim the run while the write is alive, and looks again a second later.
        var completion = host.CompleteAndReadAsync(session);
        await WaitUntilAsync(() => clock.HasTimerDueIn(TimeSpan.FromSeconds(1)));

        File.Delete(marker);
        clock.Advance(TimeSpan.FromSeconds(1));

        (await completion.WaitAsync(WaitTimeout)).ShouldBe([$$$"""{"item":{"text":"{{{UploadTestHost.Hash(file)}}}"}}""", """{"end":true}"""]);
    }

    [TestMethod]
    public async Task CompletionWhileTheMarkerOfAWriteCannotBeRead_WaitsForTheWriteToEnd()
    {
        var clock = new ManualClock(manualTimestamps: true);
        byte[] file = UploadTestHost.CreateFile(10);

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HashAsync), timeProvider: clock);
        var session = await host.CreateUploadAsync("a", file.Length);
        await host.PutChunksAsync(session, file);

        // A write that ends is removing its marker, which Windows doesn't let other servers open meanwhile. The marker's time is long past, so that only its
        // being unreadable keeps the write alive.
        string directory = host.GetUploadDirectory(session);
        string marker = Path.Combine(directory, "chunks", "0.other.writing");
        await File.WriteAllTextAsync(marker, $$"""{"aliveUtc":"{{clock.UtcNow.AddMinutes(-5):O}}"}""");

        Task<List<string>> completion;

        using (AccessDenial.DenyReading(marker))
        {
            // The completion takes the write for alive, gives up its claim, and looks again a second later.
            completion = host.CompleteAndReadAsync(session);
            await WaitUntilAsync(() => clock.HasTimerDueIn(TimeSpan.FromSeconds(1)) || completion.IsCompleted);

            completion.IsCompleted.ShouldBeFalse();
            File.Exists(Path.Combine(directory, "claim")).ShouldBeFalse();
        }

        File.Delete(marker);
        clock.Advance(TimeSpan.FromSeconds(1));

        (await completion.WaitAsync(WaitTimeout)).ShouldBe([$$$"""{"item":{"text":"{{{UploadTestHost.Hash(file)}}}"}}""", """{"end":true}"""]);
    }

    [TestMethod]
    public async Task CompletionWithTheMarkerOfAWriteThatStopped_RemovesItAndRunsTheHandler()
    {
        var clock = new ManualClock(manualTimestamps: true);
        byte[] file = UploadTestHost.CreateFile(10);

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HashAsync), timeProvider: clock);
        var session = await host.CreateUploadAsync("a", file.Length);
        await host.PutChunksAsync(session, file);

        // A server that wrote the chunk again stopped a minute ago without removing its marker.
        string marker = Path.Combine(host.GetUploadDirectory(session), "chunks", "0.other.writing");
        await File.WriteAllTextAsync(marker, $$"""{"aliveUtc":"{{clock.UtcNow.AddMinutes(-1):O}}"}""");

        (await host.CompleteAndReadAsync(session)).ShouldBe([$$$"""{"item":{"text":"{{{UploadTestHost.Hash(file)}}}"}}""", """{"end":true}"""]);
        File.Exists(marker).ShouldBeFalse();
    }

    [TestMethod]
    public async Task WriteWhoseMarkerCannotBeRenewedOnce_RenewsItAtTheNextInterval()
    {
        var clock = new ManualClock(manualTimestamps: true);
        byte[] file = UploadTestHost.CreateFile(10);

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HashAsync), timeProvider: clock);
        var session = await host.CreateUploadAsync("a", file.Length);
        string chunksDirectory = Path.Combine(host.GetUploadDirectory(session), "chunks");

        // The chunk's body arrives slowly, and the write renews its marker every 15 seconds.
        var body = new SlowContent(file, sentBeforeWait: 4);
        var write = SendChunkAsync(host, session, body);
        await WaitUntilAsync(() => Directory.EnumerateFiles(chunksDirectory, "*.writing").Any() && clock.HasTimerDueIn(TimeSpan.FromSeconds(15)));
        string marker = Directory.EnumerateFiles(chunksDirectory, "*.writing").Single();

        // The first renewal can't create the marker's temporary file.
        using (AccessDenial.DenyCreatingFiles(chunksDirectory))
        {
            clock.Advance(TimeSpan.FromSeconds(15));
            await WaitUntilAsync(() => host.Web.Logs.Any(l => l.Message.Contains("could not be renewed", StringComparison.Ordinal)));
        }

        await WaitUntilAsync(() => clock.HasTimerDueIn(TimeSpan.FromSeconds(15)));
        clock.Advance(TimeSpan.FromSeconds(15));
        await WaitUntilAsync(() => ReadAliveUtc(marker) == clock.UtcNow);

        body.Release();
        (await write.WaitAsync(WaitTimeout)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        static DateTime? ReadAliveUtc(string marker)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(marker));
                return document.RootElement.GetProperty("aliveUtc").GetDateTime().ToUniversalTime();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }
    }

    [TestMethod]
    public async Task ChunkWhoseRunIsClaimedDuringTheWrite_IsClosed()
    {
        var store = new MemoryUploadStore(TimeProvider.System);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] file = UploadTestHost.CreateFile(10);

        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, HandleAsync),
            options => options.Directory = null,
            configureServices: services => services.AddSingleton<IUploadStore>(store));

        var session = await host.CreateUploadAsync("a", file.Length);
        await host.PutChunksAsync(session, file);

        // The chunk's body arrives slowly, and the run is claimed before it has.
        var body = new SlowContent(file, sentBeforeWait: 4);
        var rewrite = SendChunkAsync(host, session, body);
        await body.WaitUntilSentAsync();

        var completion = host.CompleteAsync(session);
        await started.Task.WaitAsync(WaitTimeout);

        body.Release();
        await UploadTestHost.ShouldBeErrorAsync(await rewrite.WaitAsync(WaitTimeout), HttpStatusCode.Conflict, UploadErrorCodes.Closed);

        release.SetResult();
        using var done = await completion;

        async Task<Note> HandleAsync(UploadedFile uploaded)
        {
            started.SetResult();
            await release.Task.WaitAsync(WaitTimeout);

            return new Note("done");
        }
    }

    [TestMethod]
    public async Task ReceivedChunkWhoseRewriteFails_IsMissing()
    {
        byte[] file = UploadTestHost.CreateFile(10);

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HashAsync));
        var session = await host.CreateUploadAsync("a", file.Length);
        await host.PutChunksAsync(session, file);

        // The body of the chunk sent again ends before its range does.
        using var response = await SendChunkAsync(host, session, new SlowContent(file[..4], sentBeforeWait: 4, isReleased: true));
        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.BadRequest, UploadErrorCodes.ChunkInvalid);

        (await host.GetStatusAsync(session)).MissingChunks.ShouldBe([new ChunkRange(0, 0)]);
        await UploadTestHost.ShouldBeErrorAsync(await host.CompleteAsync(session), HttpStatusCode.Conflict, UploadErrorCodes.Incomplete);
    }

    [TestMethod]
    public async Task WriteWhileTheUploadIsDeleted_IsNotFound()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HashAsync));
        var session = await host.CreateUploadAsync("a", 10);
        var store = host.Services.GetRequiredService<IUploadStore>();
        var record = (await store.GetAsync("1", session.Id)).ShouldNotBeNull().Record;
        string directory = host.GetUploadDirectory(session);

        // A deletion at the same time has removed the record, and Windows doesn't let files be created in a directory while the deletion removes it.
        File.Delete(Path.Combine(directory, "upload.json"));

        using (AccessDenial.DenyCreatingFiles(Path.Combine(directory, "chunks")))
            (await store.WriteChunkAsync(record, 0, new MemoryStream(new byte[10]))).ShouldBe(ChunkWriteResult.NotFound);
    }

    [TestMethod]
    public async Task WriteOfAnUploadThatHasAnOutcome_IsClosed()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HashAsync));
        var (session, _) = await host.UploadAsync(UploadTestHost.CreateFile(10));
        var store = host.Services.GetRequiredService<IUploadStore>();
        var record = (await store.GetAsync("1", session.Id)).ShouldNotBeNull().Record;

        // The outcome removed the chunks, as it does while a write of a chunk that started before it goes on.
        Directory.Exists(Path.Combine(host.GetUploadDirectory(session), "chunks")).ShouldBeFalse();
        (await store.WriteChunkAsync(record, 0, new MemoryStream(new byte[10]))).ShouldBe(ChunkWriteResult.Closed);
    }

    private static async Task<Note> HashAsync(UploadedFile file, CancellationToken cancellationToken)
    {
        return new Note(await UploadTestHost.HashAsync(file, cancellationToken));
    }

    /// <summary>
    /// Sends the first chunk of an upload of 10 bytes with the specified content, which has no length, so that its body can end before its range does.
    /// </summary>
    private static Task<HttpResponseMessage> SendChunkAsync(UploadTestHost host, UploadSession session, HttpContent content)
    {
        var request = host.CreateRequest(HttpMethod.Put, $"{UploadTestHost.Path}/uploads/{session.Id}");
        request.Content = content;
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentRange = new ContentRangeHeaderValue(0, 9, 10);

        return host.Client.SendAsync(request);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(WaitTimeout);

        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    /// <summary>
    /// Content without a length that sends its first bytes, and the rest once it is released.
    /// </summary>
    private sealed class SlowContent(byte[] bytes, int sentBeforeWait, bool isReleased = false) : HttpContent
    {
        private readonly TaskCompletionSource _sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitUntilSentAsync() => _sent.Task.WaitAsync(WaitTimeout);

        public void Release() => _release.SetResult();

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(bytes.AsMemory(0, sentBeforeWait));
            await stream.FlushAsync();
            _sent.SetResult();

            if (!isReleased)
                await _release.Task.WaitAsync(WaitTimeout);

            await stream.WriteAsync(bytes.AsMemory(sentBeforeWait));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>
    /// A request body that returns the first bytes of the body and then throws, as a server's body does for a client that drops the connection.
    /// </summary>
    private sealed class FailingBody(Stream body, int failsAfter, Exception exception) : Stream
    {
        private int _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read >= failsAfter)
                throw exception;

            int read = await body.ReadAsync(buffer[..Math.Min(buffer.Length, failsAfter - _read)], cancellationToken);
            _read += read;

            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
