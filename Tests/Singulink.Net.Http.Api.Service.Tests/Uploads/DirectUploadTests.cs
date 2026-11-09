using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Singulink.Net.Http.Api.DataContracts;
using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Direct uploads (<c>POST {path}/uploads/direct</c>): their creation header, their body, the response that starts with the upload's path, requests made
/// again with the idempotency key, bodies that fail, and servers that share a directory.
/// </summary>
[PrefixTestClass]
public sealed class DirectUploadTests
{
    private const string Pattern = "/folders/{folderId}/files";
    private const int MiB = 1 << 20;

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    public sealed record FolderCommand(string Title);

    [TestMethod]
    public async Task DirectUpload_RunsTheValidatorAndTheHandler_AndStreamsTheResultAfterAPingWithTheUploadsPath()
    {
        byte[] file = UploadTestHost.CreateFile(1000);
        var validated = new List<string>();

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, ValidateAsync, HandleAsync));
        var request = host.CreateDirectRequest(
            new ByteArrayContent(file), "report.pdf", contentType: "application/pdf", command: new FolderCommand("Invoices"), query: "tag=q1");
        using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        string location = response.Headers.Location.ShouldNotBeNull().OriginalString;
        location.ShouldStartWith("/folders/5/files/uploads/");

        var lines = await UploadTestHost.ReadLinesAsync(response);
        string item = $$$"""{"item":{"text":"5 q1 Invoices report.pdf application/pdf {{{file.Length}}} {{{UploadTestHost.Hash(file)}}}"}}""";
        lines.ShouldBe(["""{"ping":true}""", item, """{"end":true}"""]);
        validated.ShouldBe([$"5 q1 Invoices report.pdf application/pdf {file.Length}"]);

        // The upload is an upload as any other, which the requests of the protocol find by its ID.
        var session = new UploadSession(location[(location.LastIndexOf('/') + 1)..], 5 * MiB, DateTime.UtcNow);
        (await host.GetStatusAsync(session)).State.ShouldBe(UploadState.Completed);
        (await host.CompleteAndReadAsync(session, query: "tag=q1")).ShouldBe([item, """{"end":true}"""]);

        Task ValidateAsync(long folderId, string tag, UploadRequest<FolderCommand> upload)
        {
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
    public async Task DirectUpload_OfAStreamingHandler_StreamsItsItems()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, StreamAsync));
        using var response = await SendDirectAsync(host, new ByteArrayContent(UploadTestHost.CreateFile(10)));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await UploadTestHost.ReadLinesAsync(response)).ShouldBe([
            """{"ping":true}""", """{"item":{"text":"one"}}""", """{"item":{"text":"two"}}""", """{"end":true}""",
        ]);

        static async IAsyncEnumerable<Note> StreamAsync(UploadedFile file, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new Note("one");
            yield return new Note("two");
        }
    }

    [TestMethod]
    public async Task DirectUpload_OfAnEmptyFile_RunsTheHandlerWithIt()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HashAsync));
        using var response = await SendDirectAsync(host, new ByteArrayContent([]));

        (await UploadTestHost.ReadLinesAsync(response))[1].ShouldContain(UploadTestHost.Hash([]));
    }

    [TestMethod]
    public async Task DirectUpload_RejectedByTheValidator_FailsWithItsError_AndCreatesNoUpload()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(
            Pattern,
            (Action<UploadRequest>)(_ => throw new ValidationApiException("Not this file.") { ErrorCode = "app-file-rejected" }),
            HashAsync));

        using var response = await SendDirectAsync(host, new ByteArrayContent(UploadTestHost.CreateFile(10)));

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.UnprocessableContent, "app-file-rejected");
        response.Headers.Location.ShouldBeNull();
        CountUploads(host).ShouldBe(0);
    }

    [TestMethod]
    public async Task DirectUpload_WithoutAContentLength_HasTheLengthOfItsCreation()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HashAsync));
        byte[] file = UploadTestHost.CreateFile(10);

        // A stream of unknown length is sent with chunked transfer encoding.
        var request = host.CreateDirectRequest(new StreamContent(new UnknownLengthStream(file)), length: file.Length);

        using (var response = await host.Client.SendAsync(request))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            (await UploadTestHost.ReadLinesAsync(response))[1].ShouldContain(UploadTestHost.Hash(file));
        }

        var shorter = host.CreateDirectRequest(new StreamContent(new UnknownLengthStream(file)), length: file.Length + 1);

        using (var response = await host.Client.SendAsync(shorter))
            await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.BadRequest, UploadErrorCodes.ChunkInvalid);
    }

    [TestMethod]
    public async Task DirectUpload_WhoseContentLengthDiffersFromItsCreation_IsInvalid()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HashAsync));
        var request = host.CreateDirectRequest(new ByteArrayContent(UploadTestHost.CreateFile(10)), length: 11);

        using var response = await host.Client.SendAsync(request);

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.BadRequest, UploadErrorCodes.ChunkInvalid);
        CountUploads(host).ShouldBe(0);
    }

    [TestMethod]
    public async Task ContentTypeOfTheRequest_IsIgnored()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, (UploadedFile file) => new Note($"[{file.ContentType}]")));

        // A platform can add a default content type to a request with a body.
        var request = host.CreateDirectRequest(new ByteArrayContent([1]));
        request.Content!.Headers.ContentType = MediaTypeHeaderValue.Parse("application/x-www-form-urlencoded");

        using var response = await host.Client.SendAsync(request);
        ItemText((await UploadTestHost.ReadLinesAsync(response))[1]).ShouldBe("[]");
    }

    [TestMethod]
    public async Task UploadInChunksWithTheKey_OfTheSameFile_GetsTheUpload_WhateverItsNameAndContentType()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HashAsync));
        byte[] file = UploadTestHost.CreateFile(10);

        var uploads = new (string FileName, string? ContentType)[] {
            ("photo.jpg", null), ("photo.jpg", string.Empty), ("photo.jpg", " text/plain "), ("photo.jpg", "text/plain; name=é"),
            ("  résumé 履歴書 😀 \"final\".pdf ", "a"),
        };

        foreach (var (fileName, contentType) in uploads)
        {
            string key = Guid.NewGuid().ToString("N");
            using var direct = await host.Client.SendAsync(host.CreateDirectRequest(new ByteArrayContent(file), fileName, key, contentType: contentType));
            direct.StatusCode.ShouldBe(HttpStatusCode.Created);

            using var creation = await host.SendCreateAsync(new { fileName, length = file.Length, contentType, idempotencyKey = key });
            creation.StatusCode.ShouldBe(HttpStatusCode.OK, await creation.Content.ReadAsStringAsync());
        }
    }

    [TestMethod]
    public async Task DirectUpload_LongerThanTheLargestChunk_IsDirectTooLarge()
    {
        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, HashAsync, endpoint => endpoint.MaxSize = 8 * MiB),
            options => options.MaxChunkSize = 5 * MiB);

        using var response = await SendDirectAsync(host, new ByteArrayContent(UploadTestHost.CreateFile((5 * MiB) + 1)));

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.RequestEntityTooLarge, UploadErrorCodes.DirectTooLarge);
        (await response.Content.ReadAsStringAsync()).ShouldContain("5 MiB");
    }

    [TestMethod]
    public async Task DirectUpload_LongerThanTheEndpointAllows_IsTooLarge()
    {
        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, HashAsync, endpoint => endpoint.MaxSize = 8 * MiB),
            options => options.MaxChunkSize = 5 * MiB);

        using var response = await SendDirectAsync(host, new ByteArrayContent(UploadTestHost.CreateFile((8 * MiB) + 1)));

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.RequestEntityTooLarge, UploadErrorCodes.TooLarge);
    }

    [TestMethod]
    public async Task FileName_OutsideOfAscii_IsTheFileName()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, (UploadedFile file) => new Note(file.FileName)));

        const string Name = "résumé 履歴書 😀 \"final\".pdf";
        using var response = await SendDirectAsync(host, new ByteArrayContent([1]), Name);
        ItemText((await UploadTestHost.ReadLinesAsync(response))[1]).ShouldBe(Name);
    }

    [TestMethod]
    public async Task CreationHeader_WithOrWithoutPadding_IsTheCreation()
    {
        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, (UploadedFile<FolderCommand> file) => new Note(file.Command.Title)));

        // The space at the end makes the base64 end with padding.
        var creation = new { fileName = "a.txt", length = 1, command = new FolderCommand("Tax1") };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(creation, UploadTestHost.JsonOptions);
        json = [.. json, .. new byte[(3 - (json.Length % 3) + 1) % 3], (byte)' '];
        string encoded = Convert.ToBase64String(json).Replace('+', '-').Replace('/', '_');
        encoded.ShouldEndWith("=");

        foreach (string header in new[] { encoded, encoded.TrimEnd('=') })
        {
            var request = host.CreateRequest(HttpMethod.Post, $"{UploadTestHost.Path}/uploads/direct");
            request.Content = new ByteArrayContent([1]);
            request.Headers.TryAddWithoutValidation("Upload-Creation", header);

            using var response = await host.Client.SendAsync(request);
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            (await UploadTestHost.ReadLinesAsync(response)).ShouldContain("""{"item":{"text":"Tax1"}}""");
        }
    }

    [TestMethod]
    public async Task CreationHeader_ThatCannotBeRead_IsUnreadable_AndValuesThatAreNotValid_AreInvalid()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, (UploadedFile<FolderCommand> file) => new Note("x")));

        string[][] headers = [
            [], ["not base64!"], [UploadTestHost.EncodeHeader("not json"u8.ToArray())], [UploadTestHost.EncodeHeader("null"u8.ToArray())],
            [UploadTestHost.EncodeHeader("""{"length":1}"""u8.ToArray())],
            [UploadTestHost.EncodeHeader("""{"fileName":"a","length":1}"""u8.ToArray()), "second"],
        ];

        foreach (string[] values in headers)
        {
            var request = host.CreateRequest(HttpMethod.Post, $"{UploadTestHost.Path}/uploads/direct");
            request.Content = new ByteArrayContent([1]);
            request.Headers.TryAddWithoutValidation("Upload-Creation", values);

            using var response = await host.Client.SendAsync(request);
            await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.BadRequest, UploadErrorCodes.CreationUnreadable);
        }

        var tooLong = host.CreateDirectRequest(new ByteArrayContent([1]), command: new FolderCommand(new string('a', 7000)));

        using (var response = await host.Client.SendAsync(tooLong))
            await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.RequestHeaderFieldsTooLarge, UploadErrorCodes.CreationHeaderTooLarge);

        var longKey = host.CreateDirectRequest(new ByteArrayContent([1]), key: new string('k', 129), command: new FolderCommand("a"));

        using (var response = await host.Client.SendAsync(longKey))
            await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.BadRequest, UploadErrorCodes.Invalid);

        // A handler whose command is required gets none without one.
        using (var response = await SendDirectAsync(host, new ByteArrayContent([1])))
            await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.BadRequest, UploadErrorCodes.Invalid);
    }

    [TestMethod]
    public async Task UploadMadeAgainWithTheKey_AfterItFinished_GetsTheOutcomeWithoutRunningTheHandler()
    {
        int runs = 0;
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, (UploadedFile file) => {
            Interlocked.Increment(ref runs);
            return new Note("done");
        }));

        byte[] file = UploadTestHost.CreateFile(100);
        string key = Guid.NewGuid().ToString("N");

        // The response of the first request starts before its upload has finished, which the end of the response shows.
        using var first = await SendDirectAsync(host, new ByteArrayContent(file), key: key);
        var firstLines = await UploadTestHost.ReadLinesAsync(first);
        using var again = await SendDirectAsync(host, new ByteArrayContent(file), key: key);

        (await UploadTestHost.ReadLinesAsync(again)).ShouldBe(firstLines);
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        again.Headers.Location.ShouldBe(first.Headers.Location);
        runs.ShouldBe(1);

        // A creation in chunks with the key gets the upload too.
        using var creation = await host.SendCreateAsync(new { fileName = "photo.jpg", length = file.Length, idempotencyKey = key });
        creation.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task UploadMadeAgainWithTheKey_WhileTheHandlerRuns_AttachesToTheRun()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int runs = 0;

        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, async (UploadedFile file) => {
                Interlocked.Increment(ref runs);
                started.TrySetResult();
                await release.Task.WaitAsync(WaitTimeout);

                return new Note("done");
            }),
            options => options.CompletionPingInterval = TimeSpan.FromMilliseconds(50));

        byte[] file = UploadTestHost.CreateFile(100);
        string key = Guid.NewGuid().ToString("N");

        using var first = await SendDirectAsync(host, new ByteArrayContent(file), key: key);
        await started.Task.WaitAsync(WaitTimeout);

        var again = SendDirectAsync(host, new ByteArrayContent(file), key: key);
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        release.SetResult();

        using var againResponse = await again.WaitAsync(WaitTimeout);
        UploadTestHost.WithoutPings(await UploadTestHost.ReadLinesAsync(againResponse)).ShouldBe(["""{"item":{"text":"done"}}""", """{"end":true}"""]);
        UploadTestHost.WithoutPings(await UploadTestHost.ReadLinesAsync(first)).ShouldBe(["""{"item":{"text":"done"}}""", """{"end":true}"""]);
        runs.ShouldBe(1);
    }

    [TestMethod]
    public async Task UploadMadeAgainWithTheKey_WhileTheFirstStillSendsItsBody_RunsTheHandlerOnce()
    {
        int runs = 0;
        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, async (UploadedFile file, CancellationToken cancellationToken) => {
                Interlocked.Increment(ref runs);
                return new Note(await UploadTestHost.HashAsync(file, cancellationToken));
            }),
            options => options.CompletionPingInterval = TimeSpan.FromMilliseconds(50));

        byte[] file = UploadTestHost.CreateFile(MiB);
        string key = Guid.NewGuid().ToString("N");

        // The first request's body stops halfway, as on a connection that stalls, and the client makes the request again.
        var held = new ClientHeldContent(file);
        var first = SendDirectAsync(host, held, key: key);
        await held.Halfway.WaitAsync(WaitTimeout);

        var again = SendDirectAsync(host, new ByteArrayContent(file), key: key);
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        held.Release();

        string item = $$$"""{"item":{"text":"{{{UploadTestHost.Hash(file)}}}"}}""";
        using var againResponse = await again.WaitAsync(WaitTimeout);
        using var firstResponse = await first.WaitAsync(WaitTimeout);

        UploadTestHost.WithoutPings(await UploadTestHost.ReadLinesAsync(againResponse)).ShouldBe([item, """{"end":true}"""]);
        UploadTestHost.WithoutPings(await UploadTestHost.ReadLinesAsync(firstResponse)).ShouldBe([item, """{"end":true}"""]);
        runs.ShouldBe(1);
    }

    [TestMethod]
    public async Task UploadMadeAgainWithTheKey_WhoseBodyIsStored_WhenTheFirstBodyFails_EndsWithTheItem()
    {
        int runs = 0;
        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, async (UploadedFile file, CancellationToken cancellationToken) => {
                Interlocked.Increment(ref runs);
                return new Note(await UploadTestHost.HashAsync(file, cancellationToken));
            }),
            options => options.CompletionPingInterval = TimeSpan.FromMilliseconds(50));

        byte[] file = UploadTestHost.CreateFile(MiB);
        string key = Guid.NewGuid().ToString("N");

        // The client finds the first connection dead before the server does, and makes the request again. The second request stores its body, starts its
        // response, and waits for the first write to end before it claims the run.
        using var firstConnection = new CancellationTokenSource();
        var held = new ClientHeldContent(file);
        var first = host.Client.SendAsync(host.CreateDirectRequest(held, key: key), HttpCompletionOption.ResponseHeadersRead, firstConnection.Token);
        await held.Halfway.WaitAsync(WaitTimeout);

        using var again = await SendDirectAsync(host, new ByteArrayContent(file), key: key).WaitAsync(WaitTimeout);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        // The server then gives up on the first request, while the second one waits to look at the upload again.
        await firstConnection.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => first);

        UploadTestHost.WithoutPings(await UploadTestHost.ReadLinesAsync(again).WaitAsync(WaitTimeout))
            .ShouldBe([$$$"""{"item":{"text":"{{{UploadTestHost.Hash(file)}}}"}}""", """{"end":true}"""]);

        runs.ShouldBe(1);
    }

    [TestMethod]
    public async Task UploadMadeAgainWithTheKey_ThatStillSendsItsBody_WhenTheFirstBodyFails_EndsWithTheItem()
    {
        int runs = 0;
        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, async (UploadedFile file, CancellationToken cancellationToken) => {
                Interlocked.Increment(ref runs);
                return new Note(await UploadTestHost.HashAsync(file, cancellationToken));
            }),
            options => options.CompletionPingInterval = TimeSpan.FromMilliseconds(50));

        byte[] file = UploadTestHost.CreateFile(MiB);
        string key = Guid.NewGuid().ToString("N");

        using var firstConnection = new CancellationTokenSource();
        var held = new ClientHeldContent(file);
        var first = host.Client.SendAsync(host.CreateDirectRequest(held, key: key), HttpCompletionOption.ResponseHeadersRead, firstConnection.Token);
        await held.Halfway.WaitAsync(WaitTimeout);

        var heldAgain = new ClientHeldContent(file);
        var again = SendDirectAsync(host, heldAgain, key: key);
        await heldAgain.Halfway.WaitAsync(WaitTimeout);
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        await firstConnection.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => first);
        heldAgain.Release();

        using var againResponse = await again.WaitAsync(WaitTimeout);
        UploadTestHost.WithoutPings(await UploadTestHost.ReadLinesAsync(againResponse))
            .ShouldBe([$$$"""{"item":{"text":"{{{UploadTestHost.Hash(file)}}}"}}""", """{"end":true}"""]);

        runs.ShouldBe(1);
    }

    [TestMethod]
    public async Task CreationMadeAgainWithTheKey_WhenTheFirstBodyFailsBeforeItsChunkStarts_TheChunkAndTheCompletionSucceed()
    {
        int runs = 0;
        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, async (UploadedFile file, CancellationToken cancellationToken) => {
                Interlocked.Increment(ref runs);
                return new Note(await UploadTestHost.HashAsync(file, cancellationToken));
            }));

        byte[] file = UploadTestHost.CreateFile(MiB);
        string key = Guid.NewGuid().ToString("N");

        // The client falls back to the three requests while the server has not noticed yet that the first connection is dead.
        using var firstConnection = new CancellationTokenSource();
        var held = new ClientHeldContent(file);
        var first = host.Client.SendAsync(host.CreateDirectRequest(held, key: key), HttpCompletionOption.ResponseHeadersRead, firstConnection.Token);
        await held.Halfway.WaitAsync(WaitTimeout);

        using var creation = await host.SendCreateAsync(new { fileName = "photo.jpg", length = file.Length, idempotencyKey = key });
        creation.StatusCode.ShouldBe(HttpStatusCode.OK, await creation.Content.ReadAsStringAsync());
        var session = (await creation.Content.ReadFromJsonAsync<UploadSession>(UploadTestHost.JsonOptions)).ShouldNotBeNull();

        // The server then gives up on the first request, before the chunk starts.
        await firstConnection.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => first);
        await WaitUntilAsync(() => !HasChunkWrites(host));

        using (var chunk = await host.PutChunkAsync(session, file, 0))
            chunk.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        UploadTestHost.WithoutPings(await host.CompleteAndReadAsync(session))
            .ShouldBe([$$$"""{"item":{"text":"{{{UploadTestHost.Hash(file)}}}"}}""", """{"end":true}"""]);
        runs.ShouldBe(1);
    }

    [TestMethod]
    public async Task UploadWhoseBodyFails_Stays_AndARequestMadeAgainWithTheKeyGetsIt_AndRunsTheHandlerOnce()
    {
        int runs = 0;
        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, async (UploadedFile file, CancellationToken cancellationToken) => {
                Interlocked.Increment(ref runs);
                return new Note(await UploadTestHost.HashAsync(file, cancellationToken));
            }));

        byte[] file = UploadTestHost.CreateFile(MiB);
        string key = Guid.NewGuid().ToString("N");

        // The client's connection fails halfway through the body.
        using var connection = new CancellationTokenSource();
        var held = new ClientHeldContent(file);
        var send = host.Client.SendAsync(host.CreateDirectRequest(held, key: key), HttpCompletionOption.ResponseHeadersRead, connection.Token);
        await held.Halfway.WaitAsync(WaitTimeout);

        await connection.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => send);
        await WaitUntilAsync(() => !HasChunkWrites(host));
        CountUploads(host).ShouldBe(1);

        using var again = await SendDirectAsync(host, new ByteArrayContent(file), key: key).WaitAsync(WaitTimeout);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        UploadTestHost.WithoutPings(await UploadTestHost.ReadLinesAsync(again))
            .ShouldBe([$$$"""{"item":{"text":"{{{UploadTestHost.Hash(file)}}}"}}""", """{"end":true}"""]);

        runs.ShouldBe(1);
    }

    [TestMethod]
    public async Task UploadWhoseBodyFails_ExpiresByItsLastWrite()
    {
        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, HashAsync), options => options.IdleExpiry = TimeSpan.FromHours(1));

        using var connection = new CancellationTokenSource();
        var held = new ClientHeldContent(UploadTestHost.CreateFile(MiB));
        var send = host.Client.SendAsync(host.CreateDirectRequest(held), HttpCompletionOption.ResponseHeadersRead, connection.Token);
        await held.Halfway.WaitAsync(WaitTimeout);

        await connection.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => send);
        await WaitUntilAsync(() => !HasChunkWrites(host));
        CountUploads(host).ShouldBe(1);

        var options = host.Services.GetRequiredService<IOptions<ChunkedUploadOptions>>().Value;
        var uploads = host.Services.GetRequiredService<ChunkedUploads>();
        host.Clock.Advance(options.UnstartedExpiry + TimeSpan.FromMinutes(1));
        await uploads.CleanUpAsync(CancellationToken.None);
        CountUploads(host).ShouldBe(1);

        host.Clock.Advance(options.IdleExpiry);
        await uploads.CleanUpAsync(CancellationToken.None);
        CountUploads(host).ShouldBe(0);
    }

    [TestMethod]
    public async Task UploadWhoseClientIsGoneOnceItsBodyHasArrived_InTheDirectoryStore_Stays_AndARequestMadeAgainGetsIt()
    {
        using var clientGone = new CancellationTokenSource();
        var filter = new CancelWhenBodyIsReadFilter(clientGone);

        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, HashAsync).AddEndpointFilter(filter.InvokeAsync),
            options => options.CompletionPingInterval = TimeSpan.FromMinutes(1));

        byte[] file = UploadTestHost.CreateFile(100);
        string key = Guid.NewGuid().ToString("N");
        var request = host.CreateDirectRequest(new ByteArrayContent(file), key: key);

        try
        {
            using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, clientGone.Token);
        }
        catch (OperationCanceledException)
        {
        }

        await WaitUntilAsync(() => !HasChunkWrites(host));
        CountUploads(host).ShouldBe(1);

        using var again = await SendDirectAsync(host, new ByteArrayContent(file), key: key).WaitAsync(WaitTimeout);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await UploadTestHost.ReadLinesAsync(again)).ShouldContain($$$"""{"item":{"text":"{{{UploadTestHost.Hash(file)}}}"}}""");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task UploadWhoseClientIsGoneOnceItsBodyIsStored_RunsNoHandler_AndStays_ForACompletionOrARequestMadeAgain(bool completes)
    {
        var store = new MemoryUploadStore(TimeProvider.System);
        using var clientGone = new CancellationTokenSource();
        int runs = 0;

        // The client is gone once the body is stored, before the response starts, as one whose connection breaks then.
        store.AfterOperation = operation => {
            if (operation is "WriteChunk")
                clientGone.Cancel();
        };

        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, (UploadedFile file) => {
                Interlocked.Increment(ref runs);
                return new Note("done");
            }),
            configureServices: services => services.AddSingleton<IUploadStore>(store));

        byte[] file = UploadTestHost.CreateFile(100);
        string key = Guid.NewGuid().ToString("N");
        var request = host.CreateDirectRequest(new ByteArrayContent(file), key: key);

        try
        {
            using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, clientGone.Token);
        }
        catch (OperationCanceledException)
        {
        }

        await WaitUntilAsync(() => clientGone.IsCancellationRequested);
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        runs.ShouldBe(0);
        store.Count.ShouldBe(1);

        List<string> lines;

        if (completes)
        {
            var upload = await store.ListAsync().SingleAsync();
            lines = await host.CompleteAndReadAsync(new UploadSession(upload.Record.Id, upload.Record.ChunkSize, DateTime.UtcNow));
        }
        else
        {
            using var again = await SendDirectAsync(host, new ByteArrayContent(file), key: key).WaitAsync(WaitTimeout);
            again.StatusCode.ShouldBe(HttpStatusCode.OK);
            lines = await UploadTestHost.ReadLinesAsync(again);
        }

        UploadTestHost.WithoutPings(lines).ShouldBe(["""{"item":{"text":"done"}}""", """{"end":true}"""]);
        runs.ShouldBe(1);
    }

    [TestMethod]
    public async Task AbortThroughACreationWithTheKey_WhileTheBodyStillArrives_DeletesTheUpload_FreesItsKey_AndTheRequestEndsAsNotFound()
    {
        int runs = 0;
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, (UploadedFile file) => {
            Interlocked.Increment(ref runs);
            return new Note("done");
        }));

        byte[] file = UploadTestHost.CreateFile(MiB);
        string key = Guid.NewGuid().ToString("N");

        var held = new ClientHeldContent(file);
        var send = SendDirectAsync(host, held, key: key);
        await held.Halfway.WaitAsync(WaitTimeout);

        using var creation = await host.SendCreateAsync(new { fileName = "photo.jpg", length = file.Length, idempotencyKey = key });
        creation.StatusCode.ShouldBe(HttpStatusCode.OK, await creation.Content.ReadAsStringAsync());
        var session = (await creation.Content.ReadFromJsonAsync<UploadSession>(UploadTestHost.JsonOptions)).ShouldNotBeNull();

        using (var abort = await host.AbortAsync(session))
            abort.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await WaitUntilAsync(() => CountUploads(host) is 0);

        held.Release();

        using var response = await send.WaitAsync(WaitTimeout);
        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.NotFound, UploadErrorCodes.NotFound);

        var store = host.Services.GetRequiredService<IUploadStore>();
        (await store.GetAsync("1", session.Id)).ShouldBeNull();
        CountUploads(host).ShouldBe(0);
        runs.ShouldBe(0);
    }

    [TestMethod]
    public async Task Rejection_WhoseBodyStalls_IsAnsweredAfterAMinute()
    {
        var clock = new ManualClock();
        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, (Action<UploadRequest>)(_ => throw new ValidationApiException("Not this file.")), HashAsync),
            timeProvider: clock);

        var held = new ClientHeldContent(UploadTestHost.CreateFile(1000));
        var send = SendDirectAsync(host, held);
        await held.Halfway.WaitAsync(WaitTimeout);

        await WaitUntilAsync(() => clock.HasTimerDueIn(TimeSpan.FromMinutes(1)));
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        send.IsCompleted.ShouldBeFalse();

        clock.Advance(TimeSpan.FromMinutes(1));

        using var response = await send.WaitAsync(WaitTimeout);
        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.UnprocessableContent, null);
        held.Release();
    }

    [TestMethod]
    public async Task RequestWithoutASession_WhoseBodyStalls_IsAnsweredAfterAMinute_AlsoForAChunk()
    {
        var clock = new ManualClock();
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HashAsync), timeProvider: clock);

        foreach (var (method, path) in new[] { (HttpMethod.Post, "uploads/direct"), (HttpMethod.Put, "uploads/abc") })
        {
            var held = new ClientHeldContent(UploadTestHost.CreateFile(1000));
            var request = new HttpRequestMessage(method, $"{UploadTestHost.Path}/{path}") { Content = held };
            var send = host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            await held.Halfway.WaitAsync(WaitTimeout);

            await WaitUntilAsync(() => clock.HasTimerDueIn(TimeSpan.FromMinutes(1)));
            clock.Advance(TimeSpan.FromMinutes(1));

            using var response = await send.WaitAsync(WaitTimeout);
            await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.Unauthorized, ApiErrorCodes.SignInRequired);
            held.Release();
        }
    }

    [TestMethod]
    public async Task Rejection_WhileTheClientStillSendsALongBody_ReachesTheClientOverAConnection()
    {
        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, (Action<UploadRequest>)(_ => throw new ValidationApiException("Not this file.")), HashAsync),
            kestrel: true);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            using var response = await SendDirectAsync(host, new ByteArrayContent(UploadTestHost.CreateFile(20 * MiB)));
            await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.UnprocessableContent, null);
        }
    }

    [TestMethod]
    public async Task Rejection_WhileASlowClientStillSendsItsBody_ReachesTheClientOverAConnection()
    {
        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, (Action<UploadRequest>)(_ => throw new ValidationApiException("Not this file.")), HashAsync),
            kestrel: true);

        // Kestrel closes the connection of a request whose unread body takes more than a few seconds to arrive after the response has been sent, so the
        // service reads the body of a rejected upload before it answers. The body takes about 7 seconds here.
        using var response = await SendDirectAsync(host, new PacedContent(UploadTestHost.CreateFile(6 * MiB), TimeSpan.FromMilliseconds(70)));
        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.UnprocessableContent, null);
    }

    [TestMethod]
    public async Task TwoServers_UploadMadeAgainOnTheOtherWhileTheHandlerRuns_WaitsForTheOutcome_AndTheOutcomeIsKeptForEither()
    {
        string directory = UploadTestHost.CreateDirectoryPath();
        var shortLivedStore = new DeclaredSharedShortLivedStore(new MemoryShortLivedStore(TimeProvider.System));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int runs = 0;

        try
        {
            await using var first = await StartAsync(directory);
            await using var second = await StartAsync(directory);
            byte[] file = UploadTestHost.CreateFile(100);
            string key = Guid.NewGuid().ToString("N");

            // The client of the first server is gone once the response starts, and the handler runs on.
            using var firstResponse = await SendDirectAsync(first, new ByteArrayContent(file), key: key);
            string location = firstResponse.Headers.Location!.OriginalString;
            await started.Task.WaitAsync(WaitTimeout);

            var again = SendDirectAsync(second, new ByteArrayContent(file), key: key);
            await Task.Delay(TimeSpan.FromMilliseconds(200));
            release.SetResult();

            using var againResponse = await again.WaitAsync(WaitTimeout);
            var lines = await UploadTestHost.ReadLinesAsync(againResponse);
            lines[0].ShouldBe("""{"ping":true}""");
            UploadTestHost.WithoutPings(lines).ShouldBe(["""{"item":{"text":"done"}}""", """{"end":true}"""]);

            var session = new UploadSession(location[(location.LastIndexOf('/') + 1)..], 5 * MiB, DateTime.UtcNow);
            UploadTestHost.WithoutPings(await second.CompleteAndReadAsync(session)).ShouldBe(UploadTestHost.WithoutPings(lines));
            UploadTestHost.WithoutPings(await first.CompleteAndReadAsync(session)).ShouldBe(UploadTestHost.WithoutPings(lines));
            runs.ShouldBe(1);
        }
        finally
        {
            UploadTestHost.DeleteDirectory(directory);
        }

        Task<UploadTestHost> StartAsync(string directory) => UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, async (UploadedFile file) => {
                Interlocked.Increment(ref runs);
                started.TrySetResult();
                await release.Task.WaitAsync(WaitTimeout);

                return new Note("done");
            }),
            options => options.CompletionPingInterval = TimeSpan.FromMilliseconds(50),
            directory,
            shortLivedStore: shortLivedStore);
    }

    private static Task<HttpResponseMessage> SendDirectAsync(UploadTestHost host, HttpContent content, string fileName = "photo.jpg", string? key = null)
    {
        return host.Client.SendAsync(host.CreateDirectRequest(content, fileName, key: key), HttpCompletionOption.ResponseHeadersRead);
    }

    private static string? ItemText(string line) => JsonDocument.Parse(line).RootElement.GetProperty("item").GetProperty("text").GetString();

    private static async Task<Note> HashAsync(UploadedFile file, CancellationToken cancellationToken)
    {
        return new Note(await UploadTestHost.HashAsync(file, cancellationToken));
    }

    private static int CountUploads(UploadTestHost host)
    {
        if (!Directory.Exists(host.Directory))
            return 0;

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        int open = Directory.GetFiles(host.Directory, "upload.json", options).Length;
        int finished = Directory.GetDirectories(host.Directory, "outcomes", options).Sum(d => Directory.GetFiles(d, "*.json").Length);

        return open + finished;
    }

    /// <summary>
    /// Gets a value indicating whether a write of a chunk is alive in the directory store.
    /// </summary>
    private static bool HasChunkWrites(UploadTestHost host)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        return Directory.GetFiles(host.Directory, "*.writing", options).Length > 0;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(WaitTimeout);

        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    /// <summary>
    /// Cancels the client's request once the body of the request is read to its end, so that the client is gone while, or once, the body is stored.
    /// </summary>
    private sealed class CancelWhenBodyIsReadFilter(CancellationTokenSource clientGone)
    {
        public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var request = context.HttpContext.Request;
            request.Body = new CancellingBody(request.Body, clientGone);

            return next(context);
        }

        private sealed class CancellingBody(Stream inner, CancellationTokenSource clientGone) : ReadOnlyBodyStream
        {
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                int read = await inner.ReadAsync(buffer, cancellationToken);

                if (read is 0)
                    await clientGone.CancelAsync();

                return read;
            }
        }
    }

    /// <summary>
    /// A body of a known length that is sent 64 KiB at a time with a pause after each, as over a slow connection.
    /// </summary>
    private sealed class PacedContent(byte[] bytes, TimeSpan pause) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            for (int offset = 0; offset < bytes.Length; offset += 64 * 1024)
            {
                await stream.WriteAsync(bytes.AsMemory(offset, Math.Min(64 * 1024, bytes.Length - offset)));
                await stream.FlushAsync();
                await Task.Delay(pause);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = bytes.Length;
            return true;
        }
    }

    /// <summary>
    /// A stream that does not tell its length, which is sent with chunked transfer encoding.
    /// </summary>
    private sealed class UnknownLengthStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    /// <summary>
    /// A body that sends half of its bytes and then waits until it is released, as over a connection that stalls.
    /// </summary>
    private sealed class ClientHeldContent(byte[] bytes) : HttpContent
    {
        private readonly TaskCompletionSource _halfway = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Halfway => _halfway.Task;

        public void Release() => _release.TrySetResult();

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(bytes.AsMemory(0, bytes.Length / 2));
            await stream.FlushAsync();
            _halfway.TrySetResult();

            await _release.Task.WaitAsync(WaitTimeout);
            await stream.WriteAsync(bytes.AsMemory(bytes.Length / 2));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = bytes.Length;
            return true;
        }
    }
}
