using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Singulink.Net.Http.Api.Service.Testing;
using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The upload helpers of the <c>Singulink.Net.Http.Api.Service.Testing</c> package: <see cref="TestUploadedFile"/> and
/// <see cref="FaultInjectionHandler"/>.
/// </summary>
[PrefixTestClass]
public sealed class UploadTestingHelperTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    public sealed record Note(string Text);

    public sealed record FolderCommand(string Title);

    [TestMethod]
    public async Task TestUploadedFile_HasContentInTemporaryFileUntilDisposed()
    {
        byte[] content = UploadTestHost.CreateFile(100);
        string path;

        using (var file = TestUploadedFile.Create(content, "notes.txt", "text/plain"))
        {
            (file.FileName, file.Length, file.ContentType).ShouldBe(("notes.txt", 100L, "text/plain"));
            ShortLivedId.IsWellFormed(file.Id).ShouldBeTrue();

            path = file.FilePath.ShouldNotBeNull();
            (await File.ReadAllBytesAsync(path)).ShouldBe(content);
            (await HandleAsync(file)).ShouldBe(new Note($"notes.txt {UploadTestHost.Hash(content)}"));
        }

        File.Exists(path).ShouldBeFalse();
    }

    [TestMethod]
    public async Task TestUploadedFile_WithCommand_HasCommand()
    {
        using var file = TestUploadedFile.Create(UploadTestHost.CreateFile(10), "a.txt", new FolderCommand("Invoices"));

        UploadedFile<FolderCommand> uploaded = file;
        uploaded.Command.ShouldBe(new FolderCommand("Invoices"));
        (await HandleAsync(file)).Text.ShouldStartWith("a.txt ");
    }

    [TestMethod]
    public async Task TestUploadedFile_Moved_IsKeptAfterDispose()
    {
        string destination = Path.Combine(Path.GetTempPath(), "httpapi-moved-" + Guid.NewGuid().ToString("N"));

        try
        {
            using (var file = TestUploadedFile.Create(UploadTestHost.CreateFile(10), "a.txt"))
            {
                await file.MoveToAsync(destination);
                file.FilePath.ShouldBe(destination);
            }

            File.Exists(destination).ShouldBeTrue();
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [TestMethod]
    public async Task FaultInjectionHandler_FailBeforeSending_DoesNotReachService()
    {
        int hits = 0;
        await using var host = await TestWebHost.StartAsync(app => app.MapPost("/items", () => Interlocked.Increment(ref hits)));
        var faults = new FaultInjectionHandler(host.App.GetTestServer().CreateHandler()).FailBeforeSending(r => r.Method == HttpMethod.Post);
        using var client = new HttpClient(faults) { BaseAddress = new Uri("http://localhost") };

        var ex = await Should.ThrowAsync<HttpRequestException>(() => client.PostAsync("/items", null));
        ex.HttpRequestError.ShouldBe(HttpRequestError.ConnectionError);
        hits.ShouldBe(0);

        (await client.PostAsync("/items", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        hits.ShouldBe(1);
        faults.Requests.Count.ShouldBe(2);
    }

    [TestMethod]
    public async Task TestUploadedFile_MoveThatIsCancelled_LeavesTheFile()
    {
        string destination = Path.Combine(Path.GetTempPath(), "httpapi-moved-" + Guid.NewGuid().ToString("N"));
        using var file = TestUploadedFile.Create(UploadTestHost.CreateFile(10), "a.txt");
        string? path = file.FilePath;

        await Should.ThrowAsync<OperationCanceledException>(() => file.MoveToAsync(destination, new CancellationToken(canceled: true)));

        file.FilePath.ShouldBe(path);
        File.Exists(path).ShouldBeTrue();
        File.Exists(destination).ShouldBeFalse();
    }

    [TestMethod]
    public async Task FaultInjectionHandler_FailAfterSending_ReachesServiceButLosesResponse()
    {
        int hits = 0;
        await using var host = await TestWebHost.StartAsync(app => app.MapPost("/items", () => Interlocked.Increment(ref hits)));
        var faults = new FaultInjectionHandler(host.App.GetTestServer().CreateHandler()).FailAfterSending(_ => true, count: 2);
        using var client = new HttpClient(faults) { BaseAddress = new Uri("http://localhost") };

        await Should.ThrowAsync<HttpRequestException>(() => client.PostAsync("/items", null));
        await Should.ThrowAsync<HttpRequestException>(() => client.PostAsync("/items", null));
        (await client.PostAsync("/items", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        hits.ShouldBe(3);
    }

    [TestMethod]
    public async Task FaultInjectionHandler_Requests_AreRecordedAsReceived()
    {
        await using var host = await TestWebHost.StartAsync(app => app.MapPut("/items/{id}", () => Results.NoContent()));
        var faults = new FaultInjectionHandler(host.App.GetTestServer().CreateHandler()).FailBeforeSending(_ => true);
        using var client = new HttpClient(faults) { BaseAddress = new Uri("http://localhost") };

        foreach (int id in new[] { 1, 2 })
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"/items/{id}?q=a") { Content = new ByteArrayContent(new byte[10]) };
            request.Content.Headers.ContentRange = new ContentRangeHeaderValue(10, 19, 100);

            try
            {
                using var response = await client.SendAsync(request);
            }
            catch (HttpRequestException)
            {
            }
        }

        var requests = faults.Requests;
        requests.Select(r => (r.Method, r.RequestUri!.PathAndQuery)).ShouldBe([(HttpMethod.Put, "/items/1?q=a"), (HttpMethod.Put, "/items/2?q=a")]);
        requests.ShouldAllBe(r => r.ContentRange!.ToString() == "bytes 10-19/100");
    }

    [TestMethod]
    public async Task FaultInjectionHandler_Respond_ReturnsResponseOfTest()
    {
        int hits = 0;
        await using var host = await TestWebHost.StartAsync(app => app.MapGet("/items", () => Interlocked.Increment(ref hits)));

        var faults = new FaultInjectionHandler(host.App.GetTestServer().CreateHandler()).Respond(_ => true, _ => {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3));

            return response;
        });

        using var client = new HttpClient(faults) { BaseAddress = new Uri("http://localhost") };

        using var busy = await client.GetAsync("/items");

        busy.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        busy.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(3));
        hits.ShouldBe(0);
    }

    [TestMethod]
    public async Task FaultInjectionHandler_CutResponseAfter_EndsStreamAndCompletionCanBeRepeated()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int runs = 0;

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload("/folders/{folderId}/files", StreamAsync));
        var session = await host.CreateUploadAsync("a", 10);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));

        var faults = new FaultInjectionHandler(host.Web.App.GetTestServer().CreateHandler())
            .CutResponseAfter(r => r.RequestUri!.AbsolutePath.EndsWith("/complete", StringComparison.Ordinal), records: 1);

        using var client = new HttpClient(faults) { BaseAddress = new Uri("http://localhost") };

        var request = host.CreateRequest(HttpMethod.Post, $"{UploadTestHost.Path}/uploads/{session.Id}/complete");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        var items = StreamingResponse.ReadItemsAsync<Note>(await response.Content.ReadAsStreamAsync(), UploadTestHost.JsonOptions);
        await using var enumerator = items.GetAsyncEnumerator();

        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        enumerator.Current.ShouldBe(new Note("working"));
        await Should.ThrowAsync<HttpIOException>(async () => await enumerator.MoveNextAsync());

        // The handler goes on without the client, which gets the outcome when it completes the upload again.
        release.SetResult();

        var again = host.CreateRequest(HttpMethod.Post, $"{UploadTestHost.Path}/uploads/{session.Id}/complete");
        using var repeated = await client.SendAsync(again);

        (await repeated.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l != """{"ping":true}""")
            .ShouldBe(["""{"item":{"text":"done"}}""", """{"end":true}"""]);

        runs.ShouldBe(1);

        async IAsyncEnumerable<Note> StreamAsync(UploadedFile file)
        {
            Interlocked.Increment(ref runs);
            yield return new Note("working");
            await release.Task.WaitAsync(WaitTimeout);
            yield return new Note("done");
        }
    }

    private static async Task<Note> HandleAsync(UploadedFile file)
    {
        return new Note($"{file.FileName} {await UploadTestHost.HashAsync(file)}");
    }
}
