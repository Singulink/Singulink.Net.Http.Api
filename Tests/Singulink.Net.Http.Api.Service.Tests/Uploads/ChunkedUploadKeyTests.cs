using System.Buffers.Text;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Singulink.Net.Http.Api.DataContracts;
using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The reservation of the idempotency keys of upload creations, which makes creations with the same key get the same upload, also when they run at the
/// same time.
/// </summary>
[PrefixTestClass]
public sealed class ChunkedUploadKeyTests
{
    private const string Pattern = "/folders/{folderId}/files";

    public sealed record Note(string Text);

    private static readonly object Body = new { fileName = "a.txt", length = 10, idempotencyKey = "key-1" };

    private static readonly TimeSpan KeyPollInterval = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task Create_ConcurrentWithSameKey_GetSameUpload()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HandleAsync));

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => host.SendCreateAsync(Body)));

        responses.Count(r => r.StatusCode is HttpStatusCode.Created).ShouldBe(1);
        responses.Count(r => r.StatusCode is HttpStatusCode.OK).ShouldBe(7);

        var ids = await Task.WhenAll(responses.Select(async r => (await r.Content.ReadFromJsonAsync<UploadSession>(UploadTestHost.JsonOptions))!.Id));
        ids.Distinct().ShouldHaveSingleItem();

        string userDirectory = Directory.GetDirectories(host.Directory).ShouldHaveSingleItem();
        Directory.GetDirectories(userDirectory).Count(d => ShortLivedId.IsWellFormed(Path.GetFileName(d))).ShouldBe(1);
    }

    [TestMethod]
    public async Task Create_WhileEarlierCreationRuns_WaitsForItsUpload()
    {
        var clock = new ManualClock(manualTimestamps: true);
        var firstValidation = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        int validations = 0;

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, ValidateAsync, HandleAsync), timeProvider: clock);

        var first = host.SendCreateAsync(Body);
        await firstValidation.Task.WaitAsync(WaitTimeout);
        var second = host.SendCreateAsync(Body);

        // The second creation looks for the first one's upload every 100 milliseconds.
        await WaitForKeyPollAsync(clock);
        second.IsCompleted.ShouldBeFalse();
        release.SetResult();

        using var firstResponse = await first;
        clock.Advance(KeyPollInterval);
        using var secondResponse = await second;

        firstResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        secondResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        secondResponse.Headers.Location.ShouldBe(firstResponse.Headers.Location);
        validations.ShouldBe(1);

        async Task ValidateAsync(UploadRequest upload)
        {
            Interlocked.Increment(ref validations);
            firstValidation.TrySetResult();
            await release.Task.WaitAsync(WaitTimeout);
        }
    }

    [TestMethod]
    public async Task Create_WhileEarlierCreationRunsPastWait_IsCreationPending()
    {
        var clock = new ManualClock(manualTimestamps: true);
        var firstValidation = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, ValidateAsync, HandleAsync), timeProvider: clock);

        var first = host.SendCreateAsync(Body);
        await firstValidation.Task.WaitAsync(WaitTimeout);

        // The second creation waits for the first one's upload for 5 seconds.
        var second = host.SendCreateAsync(Body);
        await WaitForKeyPollAsync(clock);
        clock.Advance(TimeSpan.FromSeconds(5));

        using (var pending = await second)
        {
            await UploadTestHost.ShouldBeErrorAsync(pending, HttpStatusCode.ServiceUnavailable, UploadErrorCodes.CreationPending);
            pending.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromSeconds(1));
        }

        release.SetResult();
        (await first).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await host.SendCreateAsync(Body)).StatusCode.ShouldBe(HttpStatusCode.OK);

        async Task ValidateAsync(UploadRequest upload)
        {
            firstValidation.TrySetResult();
            await release.Task.WaitAsync(WaitTimeout);
        }
    }

    [TestMethod]
    public async Task Create_WhileEarlierCreationFails_RunsItself()
    {
        var clock = new ManualClock(manualTimestamps: true);
        var firstValidation = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        int validations = 0;

        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, ValidateAsync, HandleAsync), timeProvider: clock);

        var first = host.SendCreateAsync(Body);
        await firstValidation.Task.WaitAsync(WaitTimeout);
        var second = host.SendCreateAsync(Body);

        await WaitForKeyPollAsync(clock);
        release.SetResult();

        // The first creation releases the key, which the second one reserves when it looks again.
        await UploadTestHost.ShouldBeErrorAsync(await first, HttpStatusCode.ServiceUnavailable, null);
        clock.Advance(KeyPollInterval);
        (await second).StatusCode.ShouldBe(HttpStatusCode.Created);
        validations.ShouldBe(2);

        async Task ValidateAsync(UploadRequest upload)
        {
            if (Interlocked.Increment(ref validations) is not 1)
                return;

            firstValidation.SetResult();
            await release.Task.WaitAsync(WaitTimeout);

            throw new ServiceUnavailableApiException("The folder service is busy.");
        }
    }

    [TestMethod]
    public async Task Create_KeyReservedLongAgoForNoUpload_IsTakenOver()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HandleAsync));
        string keyPath = GetKeyPath(host, userId: "1", key: "key-1");
        string reservedUtc = (host.Clock.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(3)).ToString("O");

        await WriteKeyFileAsync(keyPath, $$"""{"reservationId":"r1","uploadId":"{{new string('A', 43)}}","reservedUtc":"{{reservedUtc}}"}""");

        using var response = await host.SendCreateAsync(Body);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var session = (await response.Content.ReadFromJsonAsync<UploadSession>(UploadTestHost.JsonOptions))!;
        (await File.ReadAllTextAsync(keyPath)).ShouldContain(session.Id);
    }

    [TestMethod]
    public async Task Create_WhileTheReservationOfTheKeyIsBeingWritten_IsUnavailable_UntilItIsBroken()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HandleAsync));
        string keyPath = GetKeyPath(host, userId: "1", key: "key-1");

        // Another server has just created the reservation, and has not written its content yet.
        await WriteKeyFileAsync(keyPath, string.Empty);

        using (var unavailable = await host.SendCreateAsync(Body))
            await UploadTestHost.ShouldBeErrorAsync(unavailable, HttpStatusCode.ServiceUnavailable, UploadErrorCodes.StoreUnavailable);

        // A reservation that is still empty a minute after it was created is broken, such as by a server that stopped while it wrote it, and is taken over.
        File.SetLastWriteTimeUtc(keyPath, DateTime.UtcNow.AddMinutes(-2));

        using var response = await host.SendCreateAsync(Body);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var session = (await response.Content.ReadFromJsonAsync<UploadSession>(UploadTestHost.JsonOptions))!;
        (await File.ReadAllTextAsync(keyPath)).ShouldContain(session.Id);
    }

    [TestMethod]
    public async Task Create_WithAStaleKeyThatCannotBeReleased_IsCreationPending()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HandleAsync));
        string keyPath = GetKeyPath(host, userId: "1", key: "key-1");
        string reservedUtc = (host.Clock.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(3)).ToString("O");

        // A release of a reservation long ago made its marker and stopped before it removed the reservation.
        await WriteKeyFileAsync(keyPath, $$"""{"reservationId":"r1","uploadId":"{{new string('A', 43)}}","reservedUtc":"{{reservedUtc}}"}""");
        await File.WriteAllBytesAsync(keyPath + ".r1.released", []);

        using var response = await host.SendCreateAsync(Body).WaitAsync(WaitTimeout);

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.ServiceUnavailable, UploadErrorCodes.CreationPending);
    }

    [TestMethod]
    public async Task Create_WithAStaleKeyThatCannotBeReleased_WaitsOnTheTimeProvider()
    {
        var clock = new ManualClock(manualTimestamps: true);
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HandleAsync), timeProvider: clock);
        string keyPath = GetKeyPath(host, userId: "1", key: "key-1");
        string reservedUtc = (clock.UtcNow - TimeSpan.FromMinutes(3)).ToString("O");

        await WriteKeyFileAsync(keyPath, $$"""{"reservationId":"r1","uploadId":"{{new string('A', 43)}}","reservedUtc":"{{reservedUtc}}"}""");
        await File.WriteAllBytesAsync(keyPath + ".r1.released", []);

        // The time that the creation waits for the key passes only when the clock is advanced, so the creation waits on the clock between its releases.
        var pending = host.SendCreateAsync(Body);
        await WaitForKeyPollAsync(clock);
        clock.Advance(TimeSpan.FromSeconds(5));

        using var response = await pending.WaitAsync(WaitTimeout);

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.ServiceUnavailable, UploadErrorCodes.CreationPending);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task ReleaseThatCannotRemoveTheReservationAtOnce_RemovesItLater()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HandleAsync));
        var store = host.Services.GetRequiredService<IUploadStore>();
        string uploadId = ShortLivedId.Create();
        string keyPath = GetKeyPath(host, userId: "1", key: "key-1");

        (await store.ReserveKeyAsync("1", "key-1", uploadId)).UploadId.ShouldBe(uploadId);

        // Another process has the reservation open and doesn't let it be removed for a while.
        using (new FileStream(keyPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await store.ReleaseKeyAsync("1", "key-1", uploadId);

        for (int i = 0; File.Exists(keyPath); i++)
        {
            i.ShouldBeLessThan(200, "The reservation was not removed in time.");
            await Task.Delay(100);
        }

        string otherId = ShortLivedId.Create();
        (await store.ReserveKeyAsync("1", "key-1", otherId)).UploadId.ShouldBe(otherId);
    }

    [TestMethod]
    public async Task Create_ConcurrentTakeoversOfAStaleKey_GetSameUpload()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HandleAsync), options => options.MaxUploadsPerUser = 1000);
        string reservedUtc = (host.Clock.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(3)).ToString("O");

        for (int round = 0; round < 20; round++)
        {
            // A key reserved long ago for an upload that was never created, which each creation takes over at the same time.
            string key = $"stale-{round}";
            string keyPath = GetKeyPath(host, userId: "1", key);
            await WriteKeyFileAsync(keyPath, $$"""{"reservationId":"r","uploadId":"{{new string('A', 43)}}","reservedUtc":"{{reservedUtc}}"}""");

            var body = new { fileName = "a.txt", length = 10, idempotencyKey = key };
            var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => host.SendCreateAsync(body)));

            responses.Select(r => (int)r.StatusCode).Order().ShouldBe([200, 200, 200, 200, 200, 200, 200, 201]);
        }
    }

    [TestMethod]
    public async Task Create_WithKeyOfAnAbortedUpload_CreatesAnotherUpload()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HandleAsync));

        using var first = await host.SendCreateAsync(Body);
        var aborted = (await first.Content.ReadFromJsonAsync<UploadSession>(UploadTestHost.JsonOptions))!;
        (await host.AbortAsync(aborted)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var second = await host.SendCreateAsync(Body);
        second.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await second.Content.ReadFromJsonAsync<UploadSession>(UploadTestHost.JsonOptions))!.Id.ShouldNotBe(aborted.Id);
    }

    [TestMethod]
    public async Task Create_WithKeyOfACompletedUpload_GetsThatUpload()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HandleAsync));

        using var first = await host.SendCreateAsync(Body);
        var completed = (await first.Content.ReadFromJsonAsync<UploadSession>(UploadTestHost.JsonOptions))!;
        await host.PutChunksAsync(completed, UploadTestHost.CreateFile(10));
        await host.CompleteAndReadAsync(completed);

        using var second = await host.SendCreateAsync(Body);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await second.Content.ReadFromJsonAsync<UploadSession>(UploadTestHost.JsonOptions))!.Id.ShouldBe(completed.Id);
    }

    [TestMethod]
    public async Task Abort_ReleasesKey()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HandleAsync));
        string keyPath = GetKeyPath(host, userId: "1", key: "key-1");

        using var created = await host.SendCreateAsync(Body);
        var session = (await created.Content.ReadFromJsonAsync<UploadSession>(UploadTestHost.JsonOptions))!;
        File.Exists(keyPath).ShouldBeTrue();

        (await host.AbortAsync(session)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        File.Exists(keyPath).ShouldBeFalse();
    }

    [TestMethod]
    public async Task Create_RejectedAfterReservation_ReleasesKey()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HandleAsync), options => options.MaxUploadsPerUser = 1);
        string keyPath = GetKeyPath(host, userId: "1", key: "key-2");

        await host.CreateUploadAsync("other.txt", 10);
        using var response = await host.SendCreateAsync(new { fileName = "a.txt", length = 10, idempotencyKey = "key-2" });

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.TooManyRequests, UploadErrorCodes.LimitReached);
        File.Exists(keyPath).ShouldBeFalse();
    }

    private static Task<Note> HandleAsync(UploadedFile file) => Task.FromResult(new Note(file.FileName));

    /// <summary>
    /// Waits until a creation waits to look for the upload of a key that an earlier creation reserved.
    /// </summary>
    private static async Task WaitForKeyPollAsync(ManualClock clock)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        while (!clock.HasTimerDueIn(KeyPollInterval))
            await Task.Delay(10, timeout.Token);
    }

    /// <summary>
    /// Writes the file of a key's reservation, again if the cleanup at startup removes its empty directory in the meantime.
    /// </summary>
    private static async Task WriteKeyFileAsync(string keyPath, string json)
    {
        while (true)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
                await File.WriteAllTextAsync(keyPath, json);

                return;
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    /// <summary>
    /// Gets the path of the file of a key's reservation in the directory store, <c>{Directory}/{userKey}/keys/{keyHash}</c>.
    /// </summary>
    private static string GetKeyPath(UploadTestHost host, string userId, string key)
    {
        static string Hash(string value) => Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        return Path.Combine(host.Directory, Hash(userId), "keys", Hash(key));
    }
}
