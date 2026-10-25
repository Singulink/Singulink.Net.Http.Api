using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Singulink.Net.Http.Api.DataContracts;
using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The requests of the chunked upload protocol that create an upload, send its chunks, get its status and abort it.
/// </summary>
[PrefixTestClass]
public sealed class ChunkedUploadProtocolTests
{
    private const int MiB = 1 << 20;

    public sealed record FileResult(string Name, long Length, string? ContentType, string Hash, long FolderId);

    [TestMethod]
    public async Task Upload_OneChunk_RunsHandlerWithFile()
    {
        await using var host = await StartAsync();
        byte[] file = UploadTestHost.CreateFile(1000);

        using var created = await host.SendCreateAsync(new { fileName = "notes.txt", length = file.Length, contentType = "text/plain" });

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var session = (await created.Content.ReadFromJsonAsync<UploadSession>(UploadTestHost.JsonOptions)).ShouldNotBeNull();
        created.Headers.Location.ShouldBe(new Uri($"/folders/5/files/uploads/{session.Id}", UriKind.Relative));

        ShortLivedId.IsWellFormed(session.Id).ShouldBeTrue();
        session.ChunkSize.ShouldBe(5 * MiB);
        session.ExpiresUtc.ShouldBe(host.Clock.GetUtcNow().UtcDateTime.AddMinutes(15), TimeSpan.FromSeconds(5));

        using (var chunk = await host.PutChunkAsync(session, file, 0))
            chunk.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var lines = await host.CompleteAndReadAsync(session);

        lines.ShouldBe([
            $$$"""{"item":{"name":"notes.txt","length":1000,"contentType":"text/plain","hash":"{{{UploadTestHost.Hash(file)}}}","folderId":5}}""",
            """{"end":true}""",
        ]);
    }

    [TestMethod]
    public async Task Upload_SeveralChunks_RunsHandlerWithWholeFile()
    {
        await using var host = await StartAsync();
        byte[] file = UploadTestHost.CreateFile((10 * MiB) + 100);

        var (session, lines) = await host.UploadAsync(file);

        session.ChunkSize.ShouldBe(5 * MiB);
        lines[0].ShouldContain(UploadTestHost.Hash(file));
        lines[^1].ShouldBe("""{"end":true}""");
    }

    [TestMethod]
    public async Task Upload_EmptyFile_HasNoChunks()
    {
        await using var host = await StartAsync();

        var session = await host.CreateUploadAsync("empty.txt", 0);
        (await host.GetStatusAsync(session)).MissingChunks.ShouldBeEmpty();

        var lines = await host.CompleteAndReadAsync(session);
        lines[0].ShouldContain($"\"hash\":\"{UploadTestHost.Hash([])}\"");
    }

    [TestMethod]
    public async Task Chunks_OutOfOrderAndRepeated_MakeTheWholeFile()
    {
        await using var host = await StartAsync();
        byte[] file = UploadTestHost.CreateFile((10 * MiB) + 100);
        var session = await host.CreateUploadAsync("file.bin", file.Length);

        foreach (int index in new[] { 2, 0, 0, 1, 2 })
        {
            using var response = await host.PutChunkAsync(session, file, index);
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await host.CompleteAndReadAsync(session))[0].ShouldContain(UploadTestHost.Hash(file));
    }

    [TestMethod]
    public async Task Chunks_InParallel_MakeTheWholeFile()
    {
        await using var host = await StartAsync();
        byte[] file = UploadTestHost.CreateFile((15 * MiB) + 7);
        var session = await host.CreateUploadAsync("file.bin", file.Length);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(index => host.PutChunkAsync(session, file, index)));

        foreach (var response in responses)
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await host.CompleteAndReadAsync(session))[0].ShouldContain(UploadTestHost.Hash(file));
    }

    [TestMethod]
    public async Task Status_ReportsMissingChunks()
    {
        await using var host = await StartAsync();
        byte[] file = UploadTestHost.CreateFile((15 * MiB) + 7);
        var session = await host.CreateUploadAsync("file.bin", file.Length);

        var status = await host.GetStatusAsync(session);

        status.State.ShouldBe(UploadState.Receiving);
        status.Length.ShouldBe(file.Length);
        status.ChunkSize.ShouldBe(5 * MiB);
        status.MissingChunks.ShouldBe([new ChunkRange(0, 3)]);

        (await host.PutChunkAsync(session, file, 1)).Dispose();
        (await host.PutChunkAsync(session, file, 3)).Dispose();

        (await host.GetStatusAsync(session)).MissingChunks.ShouldBe([new ChunkRange(0, 0), new ChunkRange(2, 2)]);
    }

    [TestMethod]
    public async Task Status_AfterCompletion_IsCompleted()
    {
        await using var host = await StartAsync(options => options.OutcomeRetention = TimeSpan.FromMinutes(30));
        var (session, _) = await host.UploadAsync(UploadTestHost.CreateFile(10));

        var status = await host.GetStatusAsync(session);

        status.State.ShouldBe(UploadState.Completed);
        status.MissingChunks.ShouldBeEmpty();
        status.ExpiresUtc.ShouldBe(host.Clock.GetUtcNow().UtcDateTime.AddMinutes(30), TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task Complete_WithChunksMissing_IsIncomplete()
    {
        await using var host = await StartAsync();
        byte[] file = UploadTestHost.CreateFile((5 * MiB) + 1);
        var session = await host.CreateUploadAsync("file.bin", file.Length);
        (await host.PutChunkAsync(session, file, 1)).Dispose();

        using var response = await host.CompleteAsync(session);

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.Conflict, UploadErrorCodes.Incomplete);
        (await host.GetStatusAsync(session)).MissingChunks.ShouldBe([new ChunkRange(0, 0)]);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("bytes 1-5242880/{0}")]
    [DataRow("bytes 0-5242879/{1}")]
    [DataRow("bytes 0-10485759/{0}")]
    [DataRow("bytes 10485760-10485761/{0}")]
    [DataRow("bytes 15728640-15728640/{0}")]
    [DataRow("items 0-5242879/{0}")]
    [DataRow("bytes */{0}")]
    public async Task Chunk_ContentRangeNotOneChunk_IsChunkInvalid(string? contentRange)
    {
        await using var host = await StartAsync();
        long length = (10 * MiB) + 100;
        var session = await host.CreateUploadAsync("file.bin", length);

        var request = host.CreateRequest(HttpMethod.Put, $"{UploadTestHost.Path}/uploads/{session.Id}");
        request.Content = new ByteArrayContent(new byte[10]);

        if (contentRange is not null)
            request.Content.Headers.TryAddWithoutValidation("Content-Range", string.Format(null, contentRange, length, length + 1)).ShouldBeTrue();

        using var response = await host.Client.SendAsync(request);

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.BadRequest, UploadErrorCodes.ChunkInvalid);
    }

    [TestMethod]
    public async Task Chunk_ContentLengthDiffersFromRange_IsChunkInvalid()
    {
        await using var host = await StartAsync();
        byte[] file = UploadTestHost.CreateFile(100);
        var session = await host.CreateUploadAsync("file.bin", file.Length);

        var request = host.CreateRequest(HttpMethod.Put, $"{UploadTestHost.Path}/uploads/{session.Id}");
        request.Content = new ByteArrayContent(file, 0, 99);
        request.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 99, 100);

        using var response = await host.Client.SendAsync(request);

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.BadRequest, UploadErrorCodes.ChunkInvalid);
    }

    [TestMethod]
    [DataRow(99)]
    [DataRow(101)]
    [DataRow(5000)]
    public async Task Chunk_BodyWithoutContentLengthOfOtherLength_IsChunkInvalidAndNotStored(int bodyLength)
    {
        await using var host = await StartAsync();
        var session = await host.CreateUploadAsync("file.bin", 100);

        var request = host.CreateRequest(HttpMethod.Put, $"{UploadTestHost.Path}/uploads/{session.Id}");
        request.Content = new StreamContent(new UnknownLengthStream(new byte[bodyLength]));
        request.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 99, 100);

        using var response = await host.Client.SendAsync(request);

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.BadRequest, UploadErrorCodes.ChunkInvalid);
        (await host.GetStatusAsync(session)).MissingChunks.ShouldBe([new ChunkRange(0, 0)]);
    }

    [TestMethod]
    public async Task Chunk_BodyWithoutContentLength_IsStored()
    {
        await using var host = await StartAsync();
        byte[] file = UploadTestHost.CreateFile(100);
        var session = await host.CreateUploadAsync("file.bin", file.Length);

        var request = host.CreateRequest(HttpMethod.Put, $"{UploadTestHost.Path}/uploads/{session.Id}");
        request.Content = new StreamContent(new UnknownLengthStream(file));
        request.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 99, 100);

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await host.CompleteAndReadAsync(session))[0].ShouldContain(UploadTestHost.Hash(file));
    }

    [TestMethod]
    public async Task Chunk_AfterCompletion_IsClosed()
    {
        await using var host = await StartAsync();
        byte[] file = UploadTestHost.CreateFile(10);
        var (session, _) = await host.UploadAsync(file);

        using var response = await host.PutChunkAsync(session, file, 0);

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.Conflict, UploadErrorCodes.Closed);
    }

    [TestMethod]
    [DataRow("""{"fileName":"","length":10}""")]
    [DataRow("""{"fileName":"   ","length":10}""")]
    [DataRow("""{"fileName":"a","length":-1}""")]
    [DataRow("""{"fileName":"a","length":10,"chunkSize":0}""")]
    [DataRow("""{"fileName":"a","length":10,"chunkSize":-5}""")]
    public async Task Create_FieldNotValid_IsInvalid(string json)
    {
        await using var host = await StartAsync();

        using var response = await SendRawCreateAsync(host, json);

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.BadRequest, UploadErrorCodes.Invalid);
    }

    [TestMethod]
    public async Task Create_NamesOverMaxLength_AreInvalid()
    {
        await using var host = await StartAsync();
        string longName = new('a', 256);

        using (var fileName = await host.SendCreateAsync(new { fileName = longName, length = 10 }))
            await UploadTestHost.ShouldBeErrorAsync(fileName, HttpStatusCode.BadRequest, UploadErrorCodes.Invalid);

        using (var contentType = await host.SendCreateAsync(new { fileName = "a", length = 10, contentType = longName }))
            await UploadTestHost.ShouldBeErrorAsync(contentType, HttpStatusCode.BadRequest, UploadErrorCodes.Invalid);

        using (var atMax = await host.SendCreateAsync(new { fileName = longName[..255], length = 10, contentType = longName[..255] }))
            atMax.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [TestMethod]
    [DataRow("""{"length":10}""")]
    [DataRow("""{"fileName":null,"length":10}""")]
    [DataRow("""{"fileName":"a"}""")]
    [DataRow("""{"fileName":"a","length":"ten"}""")]
    [DataRow("not json")]
    [DataRow("null")]
    public async Task Create_BodyCannotBeRead_IsCreationUnreadable(string json)
    {
        await using var host = await StartAsync();

        using var response = await SendRawCreateAsync(host, json);

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.BadRequest, UploadErrorCodes.CreationUnreadable);
    }

    [TestMethod]
    public async Task Create_NotJson_IsUnsupportedMediaType()
    {
        await using var host = await StartAsync();

        using var response = await SendRawCreateAsync(host, """{"fileName":"a","length":10}""", "text/plain");

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.UnsupportedMediaType, UploadErrorCodes.CreationNotJson);
        (await response.Content.ReadAsStringAsync()).ShouldBe("[upload-creation-not-json] The creation of an upload must be JSON.");
    }

    [TestMethod]
    public async Task Create_Null_IsBadRequest()
    {
        await using var host = await StartAsync();

        using var response = await SendRawCreateAsync(host, "null", "application/json");

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.BadRequest, UploadErrorCodes.CreationUnreadable);
        (await response.Content.ReadAsStringAsync()).ShouldBe("[upload-creation-unreadable] The creation of an upload cannot be read.");
    }

    [TestMethod]
    public async Task Create_LargerThan64KiB_IsContentTooLarge()
    {
        await using var host = await StartAsync();
        string command = new('a', 64 * 1024);

        using var response = await SendRawCreateAsync(host, $$"""{"fileName":"a","length":10,"command":"{{command}}"}""", "application/json");

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.RequestEntityTooLarge, UploadErrorCodes.CreationTooLarge);
        (await response.Content.ReadAsStringAsync()).ShouldBe("[upload-creation-too-large] The creation of an upload is larger than 64 KiB.");
    }

    [TestMethod]
    public async Task Create_LargerThanTheServersRequestSizeLimit_IsCreationTooLarge()
    {
        // A server with a request size limit, such as Kestrel, fails the read of a body that runs past it, which has no Content-Length.
        await using var host = await UploadTestHost.StartAsync(app => {
            app.Use((context, next) => {
                context.Request.Body = new LimitedBody(new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge));
                return next(context);
            });

            app.MapChunkedUpload("/folders/{folderId}/files", HandleAsync);
        });

        using var response = await SendRawCreateAsync(host, """{"fileName":"a","length":10}""", "application/json");

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.RequestEntityTooLarge, UploadErrorCodes.CreationTooLarge);
    }

    [TestMethod]
    public async Task Create_WithoutSession_IsSignInRequired()
    {
        await using var host = await StartAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, $"{UploadTestHost.Path}/uploads?if-userId=1") {
            Content = new StringContent("""{"fileName":"a","length":10}""", Encoding.UTF8, "application/json"),
        };

        request.Headers.Add("User-Agent", "TestClient/1.0");
        using var response = await host.Client.SendAsync(request);

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.Unauthorized, ApiErrorCodes.SignInRequired);
    }

    [TestMethod]
    public async Task Create_OverEndpointMaxSize_IsTooLarge()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload("/folders/{folderId}/files", HandleAsync, o => o.MaxSize = 1000));

        using (var atMax = await host.SendCreateAsync(new { fileName = "a", length = 1000 }))
            atMax.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var overMax = await host.SendCreateAsync(new { fileName = "a", length = 1001 });

        await UploadTestHost.ShouldBeErrorAsync(overMax, HttpStatusCode.RequestEntityTooLarge, UploadErrorCodes.TooLarge);
        (await overMax.Content.ReadAsStringAsync()).ShouldContain("Files of up to 1,000 bytes can be uploaded.");
    }

    [TestMethod]
    public async Task Create_OverMaxSize_IsTooLarge()
    {
        await using var host = await StartAsync(options => options.MaxSize = 1000 * MiB);

        using var response = await host.SendCreateAsync(new { fileName = "a", length = (1000L * MiB) + 1 });

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.RequestEntityTooLarge, UploadErrorCodes.TooLarge);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Files of up to 1,000 MiB can be uploaded.");
    }

    [TestMethod]
    [DataRow(null, 5 * MiB)]
    [DataRow(1, 5 * MiB)]
    [DataRow(6 * MiB, 6 * MiB)]
    [DataRow(100 * MiB, 64 * MiB)]
    [DataRow(int.MaxValue, 64 * MiB)]
    public async Task Create_ChunkSize_IsKeptWithinBounds(int? requested, int expected)
    {
        await using var host = await StartAsync();

        var session = await host.CreateUploadAsync("file.bin", 10, requested);

        session.ChunkSize.ShouldBe(expected);
    }

    [TestMethod]
    public async Task Create_ChunkSizeOptions_SetDefaultAndBounds()
    {
        await using var host = await StartAsync(options => {
            options.MinChunkSize = 6 * MiB;
            options.DefaultChunkSize = 8 * MiB;
            options.MaxChunkSize = 10 * MiB;
        });

        (await host.CreateUploadAsync("file.bin", 10)).ChunkSize.ShouldBe(8 * MiB);
        (await host.CreateUploadAsync("file.bin", 10, 5 * MiB)).ChunkSize.ShouldBe(6 * MiB);
        (await host.CreateUploadAsync("file.bin", 10, 20 * MiB)).ChunkSize.ShouldBe(10 * MiB);
    }

    [TestMethod]
    public async Task Create_MoreChunksThanAllowed_RaisesChunkSize()
    {
        await using var host = await StartAsync(options => {
            options.MaxChunkCount = 2;
            options.MaxSize = long.MaxValue;
        });

        (await host.CreateUploadAsync("file.bin", 20 * MiB)).ChunkSize.ShouldBe(10 * MiB);
        (await host.CreateUploadAsync("file.bin", (20 * MiB) + 1)).ChunkSize.ShouldBe((10 * MiB) + 1);
        (await host.CreateUploadAsync("file.bin", 128 * MiB)).ChunkSize.ShouldBe(64 * MiB);

        using var response = await host.SendCreateAsync(new { fileName = "a", length = (128L * MiB) + 1 });

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.RequestEntityTooLarge, UploadErrorCodes.TooLarge);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Files of up to 128 MiB can be uploaded.");
    }

    [TestMethod]
    public async Task Create_OverMaxUploadsPerUser_IsLimitReached()
    {
        await using var host = await StartAsync(options => options.MaxUploadsPerUser = 2);

        var first = await host.CreateUploadAsync("a", 10);
        await host.CreateUploadAsync("b", 10);

        using (var third = await host.SendCreateAsync(new { fileName = "c", length = 10 }))
            await UploadTestHost.ShouldBeErrorAsync(third, HttpStatusCode.TooManyRequests, UploadErrorCodes.LimitReached);

        // Other users have limits of their own, and an upload that finished is no longer open.
        await host.CreateUploadAsync("c", 10, userId: 2);

        await host.PutChunksAsync(first, UploadTestHost.CreateFile(10));
        await host.CompleteAndReadAsync(first);

        await host.CreateUploadAsync("c", 10);
    }

    [TestMethod]
    public async Task Create_ManyAtTheSameTime_StayWithinMaxUploadsPerUser()
    {
        await using var host = await StartAsync(options => options.MaxUploadsPerUser = 3);

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => host.SendCreateAsync(new { fileName = $"f{i}", length = 10 })));

        responses.Select(r => (int)r.StatusCode).Order().ShouldBe([201, 201, 201, .. Enumerable.Repeat(429, 9)]);
    }

    [TestMethod]
    public async Task Create_OverMaxSizeAndMaxTotalSizePerUser_FailsWithTheErrorOfTheSmallerLimit()
    {
        await using var host = await StartAsync(options => {
            options.MaxSize = 2000;
            options.MaxTotalSizePerUser = 1000;
        });

        using (var overPerUser = await host.SendCreateAsync(new { fileName = "a", length = 3000 }))
            await UploadTestHost.ShouldBeErrorAsync(overPerUser, HttpStatusCode.RequestEntityTooLarge, UploadErrorCodes.ExceedsSizeLimit);

        await using var other = await StartAsync(options => {
            options.MaxSize = 1000;
            options.MaxTotalSizePerUser = 2000;
        });

        using (var overMax = await other.SendCreateAsync(new { fileName = "a", length = 3000 }))
            await UploadTestHost.ShouldBeErrorAsync(overMax, HttpStatusCode.RequestEntityTooLarge, UploadErrorCodes.TooLarge);
    }

    [TestMethod]
    public async Task Create_OverMaxTotalSizePerUser_IsSizeLimitReached()
    {
        await using var host = await StartAsync(options => options.MaxTotalSizePerUser = 1000);

        var first = await host.CreateUploadAsync("a", 600);

        using (var second = await host.SendCreateAsync(new { fileName = "b", length = 600 }))
        {
            await UploadTestHost.ShouldBeErrorAsync(second, HttpStatusCode.TooManyRequests, UploadErrorCodes.SizeLimitReached);
            second.Headers.RetryAfter.ShouldBeNull();
        }

        // An upload that can never fit exceeds the limit, other users have limits of their own, and an upload that finished is no longer open.
        using (var tooLarge = await host.SendCreateAsync(new { fileName = "c", length = 1001 }))
        {
            await UploadTestHost.ShouldBeErrorAsync(tooLarge, HttpStatusCode.RequestEntityTooLarge, UploadErrorCodes.ExceedsSizeLimit);
            (await tooLarge.Content.ReadAsStringAsync()).ShouldContain("1,000 bytes");
        }

        await host.CreateUploadAsync("b", 600, userId: 2);

        await host.PutChunksAsync(first, UploadTestHost.CreateFile(600));
        await host.CompleteAndReadAsync(first);

        await host.CreateUploadAsync("b", 1000);
    }

    [TestMethod]
    public async Task Create_ManyAtTheSameTime_StayWithinMaxTotalSizePerUser()
    {
        await using var host = await StartAsync(options => options.MaxTotalSizePerUser = 30);

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => host.SendCreateAsync(new { fileName = $"f{i}", length = 10 })));

        responses.Select(r => (int)r.StatusCode).Order().ShouldBe([201, 201, 201, .. Enumerable.Repeat(429, 9)]);
    }

    [TestMethod]
    public async Task Create_ManyAtTheSameTime_StayWithinMaxTotalSize()
    {
        await using var host = await StartAsync(options => options.MaxTotalSize = 30);

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 12).Select(i => host.SendCreateAsync(new { fileName = $"f{i}", length = 10 }, userId: (i % 2) + 1)));

        responses.Select(r => (int)r.StatusCode).Order().ShouldBe([201, 201, 201, .. Enumerable.Repeat(503, 9)]);
    }

    [TestMethod]
    public async Task Create_WithoutMaxTotalSize_LogsAWarningAtStartup()
    {
        await using var host = await StartAsync(options => { });

        host.Web.WarningLogs.ShouldContain(l => l.Message.Contains("ChunkedUploadOptions.MaxTotalSize is not set", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Create_OverMaxTotalSize_IsStorageFull()
    {
        await using var host = await StartAsync(options => options.MaxTotalSize = 1000);

        await host.CreateUploadAsync("a", 600);
        using var response = await host.SendCreateAsync(new { fileName = "b", length = 600 });

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.ServiceUnavailable, UploadErrorCodes.StorageFull);
        response.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromMinutes(1));
    }

    [TestMethod]
    public async Task Create_AfterUploadsEnded_HasTheirLengthsBackInMaxTotalSize()
    {
        await using var host = await StartAsync(options => options.MaxTotalSize = 1000);

        // An upload that is completed, and one that is aborted, stop counting right away, rather than at the next cleanup.
        await host.UploadAsync(UploadTestHost.CreateFile(600));

        var aborted = await host.CreateUploadAsync("b", 600);
        (await host.AbortAsync(aborted)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await host.CreateUploadAsync("c", 600);
    }

    [TestMethod]
    public async Task Create_OfTheLargestLength_IsTooLarge()
    {
        await using var host = await StartAsync(options => options.MaxSize = long.MaxValue);

        using var response = await host.SendCreateAsync(new { fileName = "a", length = long.MaxValue });

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.RequestEntityTooLarge, UploadErrorCodes.TooLarge);
    }

    [TestMethod]
    public async Task Create_WithTrailingSlash_HasLocationOfTheUpload()
    {
        await using var host = await StartAsync();

        var request = host.CreateRequest(HttpMethod.Post, $"{UploadTestHost.Path}/uploads/");
        request.Content = JsonContent.Create(new { fileName = "a", length = 10 }, options: UploadTestHost.JsonOptions);
        using var response = await host.Client.SendAsync(request);

        var session = (await response.Content.ReadFromJsonAsync<UploadSession>(UploadTestHost.JsonOptions)).ShouldNotBeNull();
        response.Headers.Location!.OriginalString.ShouldBe($"{UploadTestHost.Path}/uploads/{session.Id}");
    }

    [TestMethod]
    public async Task Create_AtAPathWithCharactersThatAreEscaped_HasTheEscapedLocation()
    {
        await using var host = await StartAsync();

        // The route value is "Résumés?%41", whose percent sign stays one.
        var request = host.CreateRequest(HttpMethod.Post, "/folders/R%C3%A9sum%C3%A9s%3F%2541/files/uploads");
        request.Content = JsonContent.Create(new { fileName = "a", length = 10 }, options: UploadTestHost.JsonOptions);
        using var response = await host.Client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var session = (await response.Content.ReadFromJsonAsync<UploadSession>(UploadTestHost.JsonOptions)).ShouldNotBeNull();
        response.Headers.GetValues("Location").ShouldHaveSingleItem().ShouldBe($"/folders/R%C3%A9sum%C3%A9s%3F%2541/files/uploads/{session.Id}");
    }

    [TestMethod]
    public async Task Create_WhenTheItemTypeIsNotInTheJsonOptions_IsServerError()
    {
        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload("/folders/{folderId}/files", HandleAsync),
            configureServices: services => services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolver = IntPayloadJsonContext.Default));

        using var response = await host.SendCreateAsync(new { fileName = "a", length = 10 });

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.InternalServerError, null);
        Directory.Exists(host.Directory).ShouldBeFalse();
    }

    [TestMethod]
    public async Task Create_NoRoomInDirectory_IsStorageFull()
    {
        await using var host = await StartAsync(options => {
            options.MaxSize = long.MaxValue;
            options.MaxChunkCount = int.MaxValue;
        });

        // More than any disk has, so the space cannot be reserved.
        using var response = await host.SendCreateAsync(new { fileName = "a", length = 1L << 50 });

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.ServiceUnavailable, UploadErrorCodes.StorageFull);
        response.Headers.RetryAfter.ShouldNotBeNull();
        Directory.EnumerateFiles(host.Directory, "*", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [TestMethod]
    public async Task Create_RepeatedWithIdempotencyKey_GetsSameUpload()
    {
        int validations = 0;

        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload("/folders/{folderId}/files", (UploadRequest upload) => { validations++; }, HandleAsync),
            options => options.MaxUploadsPerUser = 1);

        var body = new { fileName = "a.txt", length = 10, contentType = "text/plain", command = new { tag = "x" }, idempotencyKey = "key-1" };
        using var first = await host.SendCreateAsync(body);
        using var second = await host.SendCreateAsync(body);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.Headers.Location.ShouldBe(first.Headers.Location);

        var firstSession = (await first.Content.ReadFromJsonAsync<UploadSession>(UploadTestHost.JsonOptions)).ShouldNotBeNull();
        var secondSession = (await second.Content.ReadFromJsonAsync<UploadSession>(UploadTestHost.JsonOptions)).ShouldNotBeNull();

        secondSession.Id.ShouldBe(firstSession.Id);
        secondSession.ChunkSize.ShouldBe(firstSession.ChunkSize);
        validations.ShouldBe(1);
    }

    [TestMethod]
    [DataRow("fileName", "b.txt")]
    [DataRow("length", 11)]
    [DataRow("contentType", "text/html")]
    [DataRow("command", "y")]
    public async Task Create_IdempotencyKeyOfOtherUpload_IsKeyConflict(string changed, object value)
    {
        await using var host = await StartAsync();
        await host.SendCreateAsync(new { fileName = "a.txt", length = 10, contentType = "text/plain", command = new { tag = "x" }, idempotencyKey = "key-1" });

        using var response = await host.SendCreateAsync(new {
            fileName = changed is "fileName" ? value : "a.txt",
            length = changed is "length" ? value : 10,
            contentType = changed is "contentType" ? value : "text/plain",
            command = new { tag = changed is "command" ? value : "x" },
            idempotencyKey = "key-1",
        });

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.Conflict, UploadErrorCodes.KeyConflict);
    }

    [TestMethod]
    public async Task Create_IdempotencyKeyOnOtherRouteValues_IsKeyConflict()
    {
        await using var host = await StartAsync();
        await host.SendCreateAsync(new { fileName = "a.txt", length = 10, idempotencyKey = "key-1" });

        using var response = await host.SendCreateAsync(new { fileName = "a.txt", length = 10, idempotencyKey = "key-1" }, "/folders/6/files");

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.Conflict, UploadErrorCodes.KeyConflict);
    }

    [TestMethod]
    public async Task Create_IdempotencyKeyWithOtherQueryString_IsKeyConflict()
    {
        await using var host = await StartAsync();
        var body = new { fileName = "a.txt", length = 10, idempotencyKey = "key-1" };

        using (var first = await host.SendCreateAsync(body, query: "mode=merge"))
            first.StatusCode.ShouldBe(HttpStatusCode.Created);

        using (var sameQuery = await host.SendCreateAsync(body, query: "MODE=merge"))
            sameQuery.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var response = await host.SendCreateAsync(body, query: "mode=replace");

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.Conflict, UploadErrorCodes.KeyConflict);
    }

    [TestMethod]
    public async Task Create_IdempotencyKeyOfOtherUserOrAbortedUpload_CreatesUpload()
    {
        await using var host = await StartAsync();
        var body = new { fileName = "a.txt", length = 10, idempotencyKey = "key-1" };

        using var first = await host.SendCreateAsync(body);
        var session = (await first.Content.ReadFromJsonAsync<UploadSession>(UploadTestHost.JsonOptions)).ShouldNotBeNull();

        using (var otherUser = await host.SendCreateAsync(body, userId: 2))
            otherUser.StatusCode.ShouldBe(HttpStatusCode.Created);

        (await host.AbortAsync(session)).Dispose();

        using var afterAbort = await host.SendCreateAsync(body);
        afterAbort.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await afterAbort.Content.ReadFromJsonAsync<UploadSession>(UploadTestHost.JsonOptions))!.Id.ShouldNotBe(session.Id);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(129)]
    public async Task Create_IdempotencyKeyOfWrongLength_IsInvalid(int length)
    {
        await using var host = await StartAsync();

        using var response = await host.SendCreateAsync(new { fileName = "a.txt", length = 10, idempotencyKey = new string('k', length) });

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.BadRequest, UploadErrorCodes.Invalid);
    }

    [TestMethod]
    public async Task Requests_OfOtherUser_AreNotFound()
    {
        await using var host = await StartAsync();
        byte[] file = UploadTestHost.CreateFile(10);
        var session = await host.CreateUploadAsync("a", file.Length);

        await UploadTestHost.ShouldBeErrorAsync(await host.PutChunkAsync(session, file, 0, userId: 2), HttpStatusCode.NotFound, UploadErrorCodes.NotFound);
        await UploadTestHost.ShouldBeErrorAsync(await host.GetStatusResponseAsync(session, userId: 2), HttpStatusCode.NotFound, UploadErrorCodes.NotFound);
        await UploadTestHost.ShouldBeErrorAsync(await host.CompleteAsync(session, userId: 2), HttpStatusCode.NotFound, UploadErrorCodes.NotFound);

        // Aborting another user's upload does nothing.
        (await host.AbortAsync(session, userId: 2)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await host.GetStatusAsync(session)).State.ShouldBe(UploadState.Receiving);
    }

    [TestMethod]
    public async Task Requests_ForOtherRouteValuesOrEndpoint_AreNotFound()
    {
        await using var host = await UploadTestHost.StartAsync(app => {
            app.MapChunkedUpload("/folders/{folderId}/files", HandleAsync);
            app.MapChunkedUpload("/folders/{folderId}/images", HandleAsync);
        });

        byte[] file = UploadTestHost.CreateFile(10);
        var session = await host.CreateUploadAsync("a", file.Length);

        foreach (string path in new[] { "/folders/6/files", "/folders/5/images" })
        {
            await UploadTestHost.ShouldBeErrorAsync(await host.PutChunkAsync(session, file, 0, path), HttpStatusCode.NotFound, UploadErrorCodes.NotFound);
            await UploadTestHost.ShouldBeErrorAsync(await host.GetStatusResponseAsync(session, path), HttpStatusCode.NotFound, UploadErrorCodes.NotFound);
            await UploadTestHost.ShouldBeErrorAsync(await host.CompleteAsync(session, path), HttpStatusCode.NotFound, UploadErrorCodes.NotFound);
            (await host.AbortAsync(session, path)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        await host.PutChunksAsync(session, file);
        (await host.CompleteAndReadAsync(session))[0].ShouldContain(UploadTestHost.Hash(file));
    }

    [TestMethod]
    [DataRow("not-an-id")]
    [DataRow("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Requests_ForUnknownId_AreNotFound(string id)
    {
        await using var host = await StartAsync();
        var session = new UploadSession(id, 5 * MiB, DateTime.UtcNow);

        await UploadTestHost.ShouldBeErrorAsync(await host.GetStatusResponseAsync(session), HttpStatusCode.NotFound, UploadErrorCodes.NotFound);
        await UploadTestHost.ShouldBeErrorAsync(await host.CompleteAsync(session), HttpStatusCode.NotFound, UploadErrorCodes.NotFound);
        (await host.AbortAsync(session)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [TestMethod]
    public async Task Requests_ForIdInOtherCase_AreNotFound()
    {
        await using var host = await StartAsync();
        var session = await host.CreateUploadAsync("a", 10);
        string otherCase = session.Id.Any(char.IsLower) ? session.Id.ToUpperInvariant() : session.Id.ToLowerInvariant();

        await UploadTestHost.ShouldBeErrorAsync(
            await host.GetStatusResponseAsync(new UploadSession(otherCase, session.ChunkSize, session.ExpiresUtc)),
            HttpStatusCode.NotFound,
            UploadErrorCodes.NotFound);
    }

    [TestMethod]
    public async Task Abort_RemovesUpload()
    {
        await using var host = await StartAsync();
        var session = await host.CreateUploadAsync("a", 10);
        string directory = host.GetUploadDirectory(session);

        (await host.AbortAsync(session)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await UploadTestHost.ShouldBeErrorAsync(await host.GetStatusResponseAsync(session), HttpStatusCode.NotFound, UploadErrorCodes.NotFound);
        Directory.Exists(directory).ShouldBeFalse();
        (await host.AbortAsync(session)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [TestMethod]
    public async Task Abort_AfterCompletion_RemovesOutcome()
    {
        await using var host = await StartAsync();
        var (session, _) = await host.UploadAsync(UploadTestHost.CreateFile(10));

        (await host.AbortAsync(session)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await UploadTestHost.ShouldBeErrorAsync(await host.CompleteAsync(session), HttpStatusCode.NotFound, UploadErrorCodes.NotFound);
    }

    [TestMethod]
    public async Task Upload_IdleForIdleExpiry_IsRemoved()
    {
        await using var host = await StartAsync(options => options.IdleExpiry = TimeSpan.FromMinutes(5));
        byte[] file = UploadTestHost.CreateFile((5 * MiB) + 1);
        var session = await host.CreateUploadAsync("a", file.Length);

        // Each chunk keeps the upload for another idle expiry.
        host.Clock.Advance(TimeSpan.FromMinutes(4));
        (await host.PutChunkAsync(session, file, 0)).Dispose();
        host.Clock.Advance(TimeSpan.FromMinutes(4));

        var status = await host.GetStatusAsync(session);
        status.ExpiresUtc.ShouldBe(host.Clock.GetUtcNow().UtcDateTime.AddMinutes(1), TimeSpan.FromSeconds(5));

        string directory = host.GetUploadDirectory(session);
        host.Clock.Advance(TimeSpan.FromMinutes(1));

        await UploadTestHost.ShouldBeErrorAsync(await host.GetStatusResponseAsync(session), HttpStatusCode.NotFound, UploadErrorCodes.NotFound);
        Directory.Exists(directory).ShouldBeFalse();
    }

    [TestMethod]
    public async Task Upload_WithoutAChunkForUnstartedExpiry_IsRemoved()
    {
        await using var host = await StartAsync();
        byte[] file = UploadTestHost.CreateFile(10);

        // An upload that receives no chunk is kept for 15 minutes, and one that does for the idle expiry after its last chunk.
        var unstarted = await host.CreateUploadAsync("a", file.Length);
        var started = await host.CreateUploadAsync("b", file.Length);
        unstarted.ExpiresUtc.ShouldBe(host.Clock.GetUtcNow().UtcDateTime.AddMinutes(15), TimeSpan.FromSeconds(5));

        (await host.PutChunkAsync(started, file, 0)).Dispose();
        (await host.GetStatusAsync(started)).ExpiresUtc.ShouldBe(host.Clock.GetUtcNow().UtcDateTime.AddDays(1), TimeSpan.FromSeconds(5));

        host.Clock.Advance(TimeSpan.FromMinutes(15));

        await UploadTestHost.ShouldBeErrorAsync(await host.GetStatusResponseAsync(unstarted), HttpStatusCode.NotFound, UploadErrorCodes.NotFound);
        (await host.GetStatusAsync(started)).State.ShouldBe(UploadState.Receiving);
    }

    [TestMethod]
    public async Task Upload_WithoutAChunkForUnstartedExpiry_StopsCountingTowardTheLimits()
    {
        await using var host = await StartAsync(options => {
            options.MaxTotalSize = 1000;
            options.MaxTotalSizePerUser = 1000;
            options.MaxUploadsPerUser = 1;
        });

        await host.CreateUploadAsync("a", 1000);

        // The upload stops counting as it expires, before the next cleanup removes it.
        host.Clock.Advance(TimeSpan.FromMinutes(15));
        await host.CreateUploadAsync("b", 1000);
    }

    [TestMethod]
    public async Task Outcome_PastRetention_IsRemoved()
    {
        await using var host = await StartAsync(options => options.OutcomeRetention = TimeSpan.FromMinutes(5));
        var (session, lines) = await host.UploadAsync(UploadTestHost.CreateFile(10));

        host.Clock.Advance(TimeSpan.FromMinutes(4));
        (await host.CompleteAndReadAsync(session)).ShouldBe(lines);

        host.Clock.Advance(TimeSpan.FromMinutes(1));
        await UploadTestHost.ShouldBeErrorAsync(await host.CompleteAsync(session), HttpStatusCode.NotFound, UploadErrorCodes.NotFound);
    }

    private static Task<UploadTestHost> StartAsync(Action<ChunkedUploadOptions>? configure = null)
    {
        return UploadTestHost.StartAsync(app => app.MapChunkedUpload("/folders/{folderId}/files", HandleAsync), configure);
    }

    private static async Task<FileResult> HandleAsync(long folderId, UploadedFile file, CancellationToken cancellationToken)
    {
        return new FileResult(file.FileName, file.Length, file.ContentType, await UploadTestHost.HashAsync(file, cancellationToken), folderId);
    }

    private static Task<HttpResponseMessage> SendRawCreateAsync(UploadTestHost host, string json, string mediaType = "application/json")
    {
        var request = host.CreateRequest(HttpMethod.Post, $"{UploadTestHost.Path}/uploads");
        request.Content = new StringContent(json, Encoding.UTF8, mediaType);

        return host.Client.SendAsync(request);
    }

    /// <summary>
    /// A stream whose length is not known, so that it is sent without a <c>Content-Length</c> header.
    /// </summary>
    private sealed class UnknownLengthStream(byte[] content) : MemoryStream(content)
    {
        public override bool CanSeek => false;
    }

    /// <summary>
    /// A request body whose reads fail, as a server's body does once it runs past the server's request size limit.
    /// </summary>
    private sealed class LimitedBody(Exception exception) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw exception;

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw exception;

        public override int Read(byte[] buffer, int offset, int count) => throw exception;

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
