using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Singulink.Net.Http.Api.DataContracts;
using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The rule of the direct upload and chunk requests about their bodies: a body that isn't stored is read before the answer, unless it has failed or isn't as
/// long as it should be, or the client is gone.
/// </summary>
[PrefixTestClass]
public sealed class UploadBodyDiscardTests
{
    private const string Pattern = "/folders/{folderId}/files";
    private const int MiB = 1 << 20;

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task DirectUpload_RejectedByTheValidator_ReadsTheBody()
    {
        var bodies = new RecordedBodies();
        await using var host = await StartAsync(new MemoryUploadStore(TimeProvider.System), bodies, Reject);

        using var response = await SendDirectAsync(host);

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.UnprocessableContent, null);
        bodies.Last.ReachedEnd.ShouldBeTrue();
    }

    [TestMethod]
    public async Task DirectUpload_WhoseContentLengthDiffersFromItsCreation_ReadsTheBody()
    {
        var bodies = new RecordedBodies();
        await using var host = await StartAsync(new MemoryUploadStore(TimeProvider.System), bodies);

        var request = host.CreateDirectRequest(new ByteArrayContent(UploadTestHost.CreateFile(10)), length: 11);
        using var response = await host.Client.SendAsync(request);

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.BadRequest, UploadErrorCodes.ChunkInvalid);
        bodies.Last.ReachedEnd.ShouldBeTrue();
    }

    [TestMethod]
    public async Task DirectUpload_WhoseWriteFailsInTheStore_ReadsTheBody()
    {
        var bodies = new RecordedBodies();
        var store = new MemoryUploadStore(TimeProvider.System) { BeforeOperation = FailWriteOfChunk };
        await using var host = await StartAsync(store, bodies);

        using var response = await SendDirectAsync(host);

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.ServiceUnavailable, UploadErrorCodes.StoreUnavailable);
        bodies.Last.ReachedEnd.ShouldBeTrue();
    }

    [TestMethod]
    public async Task DirectUpload_OfAStoreWithoutRoom_ReadsTheBody()
    {
        var bodies = new RecordedBodies();
        var store = new MemoryUploadStore(TimeProvider.System) { IsFull = true };
        await using var host = await StartAsync(store, bodies);

        using var response = await SendDirectAsync(host);

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.ServiceUnavailable, UploadErrorCodes.StorageFull);
        bodies.Last.ReachedEnd.ShouldBeTrue();
    }

    [TestMethod]
    public async Task DirectUploadMadeAgain_OfAFinishedUpload_ReadsTheBody_AndGetsTheOutcome()
    {
        var bodies = new RecordedBodies();
        await using var host = await StartAsync(new MemoryUploadStore(TimeProvider.System), bodies);
        string key = Guid.NewGuid().ToString("N");

        using (var first = await SendDirectAsync(host, key))
            first.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var again = await SendDirectAsync(host, key);

        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        UploadTestHost.WithoutPings(await UploadTestHost.ReadLinesAsync(again)).ShouldBe(["""{"item":{"text":"done"}}""", """{"end":true}"""]);
        bodies.Last.ReachedEnd.ShouldBeTrue();
    }

    [TestMethod]
    public async Task DirectUpload_WithTheKeyOfAnUploadOfSeveralChunks_ReadsTheBody()
    {
        var bodies = new RecordedBodies();
        await using var host = await StartAsync(new MemoryUploadStore(TimeProvider.System), bodies);
        string key = Guid.NewGuid().ToString("N");

        using (var creation = await host.SendCreateAsync(new { fileName = "photo.jpg", length = 6 * MiB, idempotencyKey = key }))
            creation.StatusCode.ShouldBe(HttpStatusCode.Created);

        var request = host.CreateDirectRequest(new ByteArrayContent(UploadTestHost.CreateFile(6 * MiB)), key: key);
        using var response = await host.Client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await UploadTestHost.ReadLinesAsync(response))[^1].ShouldContain(UploadErrorCodes.Incomplete);
        bodies.Last.ReachedEnd.ShouldBeTrue();
    }

    [TestMethod]
    public async Task DirectUpload_WhoseBodyFails_IsNotReadAgain()
    {
        var bodies = new RecordedBodies { Failure = new BadHttpRequestException("The body failed.", StatusCodes.Status400BadRequest) };
        await using var host = await StartAsync(new MemoryUploadStore(TimeProvider.System), bodies);

        using var response = await SendDirectAsync(host);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        bodies.Last.Reads.ShouldBe(1);
    }

    [TestMethod]
    public async Task DirectUpload_WhoseBodyIsLongerThanItsLength_IsNotReadOn()
    {
        var bodies = new RecordedBodies { Replacement = new byte[MiB] };
        await using var host = await StartAsync(new MemoryUploadStore(TimeProvider.System), bodies);

        using var response = await SendDirectAsync(host);

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.BadRequest, UploadErrorCodes.ChunkInvalid);
        bodies.Last.ReachedEnd.ShouldBeFalse();
    }

    [TestMethod]
    public async Task DirectUpload_RejectedOnceTheClientIsGone_DoesNotReadTheBody()
    {
        using var clientGone = new CancellationTokenSource();
        var bodies = new RecordedBodies();

        await using var host = await StartAsync(new MemoryUploadStore(TimeProvider.System), bodies, (UploadRequest _) => {
            clientGone.Cancel();
            throw new ValidationApiException("Not this file.");
        });

        var request = host.CreateDirectRequest(new ByteArrayContent(UploadTestHost.CreateFile(10)));

        // The request ends with a failure, which the test server passes on as it is.
        await Should.ThrowAsync<Exception>(() => host.Client.SendAsync(request, clientGone.Token));
        await bodies.Ended.WaitAsync(WaitTimeout);

        bodies.Last.Reads.ShouldBe(0);
    }

    [TestMethod]
    public async Task Chunk_OfAFinishedUpload_ReadsTheBody()
    {
        var bodies = new RecordedBodies();
        await using var host = await StartAsync(new MemoryUploadStore(TimeProvider.System), bodies);
        byte[] file = UploadTestHost.CreateFile(10);

        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, file);
        await host.CompleteAndReadAsync(session);

        using var response = await host.PutChunkAsync(session, file, 0);

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.Conflict, UploadErrorCodes.Closed);
        bodies.Last.ReachedEnd.ShouldBeTrue();
    }

    [TestMethod]
    public async Task Chunk_WithoutARange_ReadsTheBody()
    {
        var bodies = new RecordedBodies();
        await using var host = await StartAsync(new MemoryUploadStore(TimeProvider.System), bodies);
        var session = await host.CreateUploadAsync("a", 10);

        var request = host.CreateRequest(HttpMethod.Put, $"{UploadTestHost.Path}/uploads/{session.Id}");
        request.Content = new ByteArrayContent(UploadTestHost.CreateFile(10));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var response = await host.Client.SendAsync(request);

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.BadRequest, UploadErrorCodes.ChunkInvalid);
        bodies.Last.ReachedEnd.ShouldBeTrue();
    }

    [TestMethod]
    public async Task Chunk_WhoseWriteFailsInTheStore_ReadsTheBody()
    {
        var bodies = new RecordedBodies();
        var store = new MemoryUploadStore(TimeProvider.System);
        await using var host = await StartAsync(store, bodies);
        var session = await host.CreateUploadAsync("a", 10);

        store.BeforeOperation = FailWriteOfChunk;
        using var response = await host.PutChunkAsync(session, UploadTestHost.CreateFile(10), 0);

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.ServiceUnavailable, UploadErrorCodes.StoreUnavailable);
        bodies.Last.ReachedEnd.ShouldBeTrue();
    }

    [TestMethod]
    public async Task Chunk_OfAStoreWithoutRoom_ReadsTheBody()
    {
        var bodies = new RecordedBodies();
        var store = new MemoryUploadStore(TimeProvider.System) { IsFull = true };
        await using var host = await StartAsync(store, bodies);
        var session = await host.CreateUploadAsync("a", 10);

        using var response = await host.PutChunkAsync(session, UploadTestHost.CreateFile(10), 0);

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.ServiceUnavailable, UploadErrorCodes.StorageFull);
        bodies.Last.ReachedEnd.ShouldBeTrue();
    }

    [TestMethod]
    public async Task Chunk_WhoseBodyFails_IsNotReadAgain()
    {
        var bodies = new RecordedBodies { Failure = new BadHttpRequestException("The body failed.", StatusCodes.Status400BadRequest) };
        await using var host = await StartAsync(new MemoryUploadStore(TimeProvider.System), bodies);
        var session = await host.CreateUploadAsync("a", 10);

        using var response = await host.PutChunkAsync(session, UploadTestHost.CreateFile(10), 0);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        bodies.Last.Reads.ShouldBe(1);
    }

    [TestMethod]
    public async Task Chunk_WhoseBodyIsLongerThanItsLength_IsNotReadOn()
    {
        var bodies = new RecordedBodies { Replacement = new byte[MiB] };
        await using var host = await StartAsync(new MemoryUploadStore(TimeProvider.System), bodies);
        var session = await host.CreateUploadAsync("a", 10);

        using var response = await host.PutChunkAsync(session, UploadTestHost.CreateFile(10), 0);

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.BadRequest, UploadErrorCodes.ChunkInvalid);
        bodies.Last.ReachedEnd.ShouldBeFalse();
    }

    [TestMethod]
    public async Task Chunk_RejectedOnceTheClientIsGone_DoesNotReadTheBody()
    {
        using var clientGone = new CancellationTokenSource();
        var bodies = new RecordedBodies();
        var store = new MemoryUploadStore(TimeProvider.System);
        await using var host = await StartAsync(store, bodies);
        var session = await host.CreateUploadAsync("a", 10);

        store.BeforeOperation = operation => {
            if (operation is "Get")
            {
                clientGone.Cancel();
                throw new IOException("The store failed.");
            }
        };

        var request = host.CreateRequest(HttpMethod.Put, $"{UploadTestHost.Path}/uploads/{session.Id}");
        request.Content = new ByteArrayContent(UploadTestHost.CreateFile(10));

        // The request ends with a failure, which the test server passes on as it is.
        await Should.ThrowAsync<Exception>(() => host.Client.SendAsync(request, clientGone.Token));
        await bodies.Ended.WaitAsync(WaitTimeout);

        bodies.Last.Reads.ShouldBe(0);
    }

    private static void FailWriteOfChunk(string operation)
    {
        if (operation is "WriteChunk")
            throw new IOException("The store failed.");
    }

    private static Note Handle(UploadedFile file) => new("done");

    private static void Reject(UploadRequest request) => throw new ValidationApiException("Not this file.");

    private static Task<HttpResponseMessage> SendDirectAsync(UploadTestHost host, string? key = null)
    {
        return host.Client.SendAsync(host.CreateDirectRequest(new ByteArrayContent(UploadTestHost.CreateFile(10)), key: key));
    }

    private static Task<UploadTestHost> StartAsync(MemoryUploadStore store, RecordedBodies bodies, Action<UploadRequest>? validator = null)
    {
        return UploadTestHost.StartAsync(
            app => {
                app.Use(bodies.InvokeAsync);

                if (validator is null)
                    app.MapChunkedUpload(Pattern, Handle);
                else
                    app.MapChunkedUpload(Pattern, validator, Handle);
            },
            configureServices: services => services.AddSingleton<IUploadStore>(store));
    }

    /// <summary>
    /// A middleware that records the bodies of the direct upload and chunk requests.
    /// </summary>
    private sealed class RecordedBodies
    {
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Gets the exception that reads of the bodies fail with.
        /// </summary>
        public Exception? Failure { get; init; }

        /// <summary>
        /// Gets the bytes that the bodies have in place of the bytes of their requests.
        /// </summary>
        public byte[]? Replacement { get; init; }

        public RecordedBody Last { get; private set; } = null!;

        /// <summary>
        /// Gets a task that ends once a request whose body is recorded has been answered.
        /// </summary>
        public Task Ended => _ended.Task;

        public async Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            var request = context.Request;

            if (!HttpMethods.IsPut(request.Method) && request.Path.Value?.EndsWith("/uploads/direct", StringComparison.Ordinal) is not true)
            {
                await next(context);
                return;
            }

            Last = new RecordedBody(Replacement is { } bytes ? new MemoryStream(bytes) : request.Body, Failure);
            request.Body = Last;

            try
            {
                await next(context);
            }
            finally
            {
                _ended.TrySetResult();
            }
        }
    }

    private sealed class RecordedBody(Stream inner, Exception? failure) : ReadOnlyBodyStream
    {
        private int _reads;
        private int _reachedEnd;

        public int Reads => Volatile.Read(ref _reads);

        public bool ReachedEnd => Volatile.Read(ref _reachedEnd) is 1;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _reads);

            if (failure is not null)
                throw failure;

            int read = await inner.ReadAsync(buffer, cancellationToken);

            if (read is 0)
                Volatile.Write(ref _reachedEnd, 1);

            return read;
        }
    }
}
