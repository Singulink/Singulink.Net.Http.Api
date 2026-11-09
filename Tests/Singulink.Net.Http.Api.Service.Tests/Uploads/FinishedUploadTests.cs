using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Singulink.Net.Http.Api.DataContracts;
using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Finished uploads in the directory store: their outcomes apart from the open uploads, what a server that stops while it finishes an upload leaves, the
/// removal of outcomes and of the reservations of their keys, and the uploads that 4.x finished.
/// </summary>
[PrefixTestClass]
public sealed class FinishedUploadTests
{
    private const string Pattern = "/folders/{folderId}/files";

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task FinishedUpload_LeavesItsDirectory_AndIsReadFromItsOutcome()
    {
        await using var host = await StartAsync();
        string key = Guid.NewGuid().ToString("N");
        var session = await CreateAsync(host, key);
        string uploadDirectory = host.GetUploadDirectory(session);

        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));
        var lines = await host.CompleteAndReadAsync(session);

        Directory.Exists(uploadDirectory).ShouldBeFalse();
        Path.GetFileName(host.GetOutcomeFile(session)).ShouldStartWith(session.Id + ".");
        Path.GetFileName(Path.GetDirectoryName(host.GetOutcomeFile(session))).ShouldBe("outcomes");

        (await host.CompleteAndReadAsync(session)).ShouldBe(lines);
        (await host.GetStatusAsync(session)).State.ShouldBe(UploadState.Completed);

        // A creation made again with the key gets the finished upload.
        using var again = await host.SendCreateAsync(new { fileName = "a", length = 10, idempotencyKey = key });
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await again.Content.ReadFromJsonAsync<UploadSession>(UploadTestHost.JsonOptions))!.Id.ShouldBe(session.Id);
    }

    [TestMethod]
    public async Task ServerThatStoppedAfterItStoredTheOutcome_LeavesAnUploadThatReadsAsFinished_AndIsMovedOnByTheNextServer()
    {
        string directory = UploadTestHost.CreateDirectoryPath();
        var shortLivedStore = new DeclaredSharedShortLivedStore(new MemoryShortLivedStore(TimeProvider.System));

        try
        {
            await using var first = await StartAsync(directory, shortLivedStore: shortLivedStore);
            await using var second = await StartAsync(directory, shortLivedStore: shortLivedStore);

            var session = await CreateAsync(first, Guid.NewGuid().ToString("N"));
            await first.PutChunksAsync(session, UploadTestHost.CreateFile(10));
            string uploadDirectory = first.GetUploadDirectory(session);

            // The first server claimed the run, stored the outcome in the directory and stopped, so its claim lapsed.
            await WriteLapsedClaimAsync(uploadDirectory);
            await WriteFinishedAsync(uploadDirectory, """{"hasItem":true,"item":{"text":"stored"}}""");

            // The second server reads the outcome, also while the directory is left, and the run does not run again.
            var lines = UploadTestHost.WithoutPings(await second.CompleteAndReadAsync(session));
            lines.ShouldBe(["""{"item":{"text":"stored"}}""", """{"end":true}"""]);

            // Its cleanup moves the upload on.
            await second.Services.GetRequiredService<IUploadStore>().CleanUpAsync();

            Directory.Exists(uploadDirectory).ShouldBeFalse();
            UploadTestHost.WithoutPings(await second.CompleteAndReadAsync(session)).ShouldBe(lines);
        }
        finally
        {
            UploadTestHost.DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task OutcomeStoredAgain_MovesOnTheUploadThatAServerLeft_AndIsNotStored()
    {
        await using var host = await StartAsync();
        var session = await CreateAsync(host, Guid.NewGuid().ToString("N"));
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));
        string uploadDirectory = host.GetUploadDirectory(session);

        var store = host.Services.GetRequiredService<IUploadStore>();
        var record = (await store.GetAsync("1", session.Id)).ShouldNotBeNull().Record;
        await WriteLapsedClaimAsync(uploadDirectory);
        await WriteFinishedAsync(uploadDirectory, """{"hasItem":true,"item":{"text":"first"}}""");

        // As when another server ends the run whose claim lapsed.
        (await store.TrySetOutcomeAsync(record, """{"error":{"status":500,"code":"upload-interrupted","message":"m"}}""")).ShouldBeFalse();

        Directory.Exists(uploadDirectory).ShouldBeFalse();
        (await store.GetAsync("1", session.Id)).ShouldNotBeNull().Outcome.ShouldBe("""{"hasItem":true,"item":{"text":"first"}}""");
    }

    [TestMethod]
    public async Task CopyOfAnOutcomeThatAServerLeftHalfWritten_IsNotTheOutcome_AndTheCleanupCopiesItAgain()
    {
        await using var host = await StartAsync();
        var session = await CreateAsync(host, Guid.NewGuid().ToString("N"));
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));
        string uploadDirectory = host.GetUploadDirectory(session);
        string outcome = """{"hasItem":true,"item":{"text":"stored"}}""";

        // The server stored the outcome in the directory and stopped while it copied it to the user's outcomes, two hours ago.
        await WriteLapsedClaimAsync(uploadDirectory);
        await WriteFinishedAsync(uploadDirectory, outcome);

        string outcomesDirectory = Path.Combine(Path.GetDirectoryName(uploadDirectory)!, "outcomes");
        string partialCopy = Path.Combine(outcomesDirectory, $"{session.Id}.{DateTime.UtcNow.Ticks}.-.json.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(outcomesDirectory);
        await File.WriteAllTextAsync(partialCopy, """{"record":{"id":""");
        File.SetLastWriteTimeUtc(partialCopy, DateTime.UtcNow.AddHours(-2));

        var store = host.Services.GetRequiredService<IUploadStore>();
        await store.CleanUpAsync();

        Directory.Exists(uploadDirectory).ShouldBeFalse();
        File.Exists(partialCopy).ShouldBeFalse();
        (await store.GetAsync("1", session.Id)).ShouldNotBeNull().Outcome.ShouldBe(outcome);
        StoreWarnings(host).ShouldBeEmpty();
    }

    [TestMethod]
    public async Task OutcomesThatTwoServersStoreAtTheSameTime_StoreOne()
    {
        string directory = UploadTestHost.CreateDirectoryPath();
        var shortLivedStore = new DeclaredSharedShortLivedStore(new MemoryShortLivedStore(TimeProvider.System));

        try
        {
            await using var first = await StartAsync(directory, shortLivedStore: shortLivedStore);
            await using var second = await StartAsync(directory, shortLivedStore: shortLivedStore);
            IUploadStore[] stores = [first.Services.GetRequiredService<IUploadStore>(), second.Services.GetRequiredService<IUploadStore>()];

            for (int round = 0; round < 10; round++)
            {
                var session = await CreateAsync(first, Guid.NewGuid().ToString("N"));
                await first.PutChunksAsync(session, UploadTestHost.CreateFile(10));
                var record = (await stores[0].GetAsync("1", session.Id)).ShouldNotBeNull().Record;
                string uploadDirectory = first.GetUploadDirectory(session);

                // Both servers end the run whose claim lapsed, as interrupted, at the same moment.
                await WriteLapsedClaimAsync(uploadDirectory);
                bool[] stored = await Task.WhenAll(stores.Select((s, i) => Task.Run(() => s.TrySetOutcomeAsync(record, $"outcome {i}").AsTask())));

                stored.Count(s => s).ShouldBe(1);
                string outcome = $"outcome {Array.IndexOf(stored, true)}";

                foreach (var store in stores)
                    (await store.GetAsync("1", session.Id)).ShouldNotBeNull().Outcome.ShouldBe(outcome);

                Directory.Exists(uploadDirectory).ShouldBeFalse();
            }
        }
        finally
        {
            UploadTestHost.DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task OutcomesThatTwoServersRemoveAtTheSameTime_AreRemovedWithTheirKeys()
    {
        string directory = UploadTestHost.CreateDirectoryPath();
        var shortLivedStore = new DeclaredSharedShortLivedStore(new MemoryShortLivedStore(TimeProvider.System));

        try
        {
            await using var first = await StartAsync(directory, shortLivedStore: shortLivedStore);
            await using var second = await StartAsync(directory, shortLivedStore: shortLivedStore);
            IUploadStore[] stores = [first.Services.GetRequiredService<IUploadStore>(), second.Services.GetRequiredService<IUploadStore>()];
            List<(string Key, UploadSession Session)> uploads = [];

            for (int i = 0; i < 10; i++)
            {
                string key = Guid.NewGuid().ToString("N");
                uploads.Add((key, await UploadAsync(first, key)));
            }

            await Task.WhenAll(stores.Select(s => Task.Run(() => s.RemoveOutcomesAsync(DateTime.UtcNow.AddHours(1)).AsTask())));

            foreach (var (key, session) in uploads)
            {
                (await stores[1].GetAsync("1", session.Id)).ShouldBeNull();

                using var again = await second.SendCreateAsync(new { fileName = "a", length = 10, idempotencyKey = key });
                again.StatusCode.ShouldBe(HttpStatusCode.Created);
            }

            StoreWarnings(first).Concat(StoreWarnings(second)).ShouldBeEmpty();
        }
        finally
        {
            UploadTestHost.DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task OutcomeThatIsBeingWritten_IsLeftToItsWriter()
    {
        await using var host = await StartAsync();
        var session = await CreateAsync(host, Guid.NewGuid().ToString("N"));
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));
        string uploadDirectory = host.GetUploadDirectory(session);

        var store = host.Services.GetRequiredService<IUploadStore>();
        var record = (await store.GetAsync("1", session.Id)).ShouldNotBeNull().Record;

        // Another server has just created the outcome's file, and has not written its content yet.
        await WriteLapsedClaimAsync(uploadDirectory);
        await File.WriteAllBytesAsync(Path.Combine(uploadDirectory, "finished.json"), []);

        (await store.TrySetOutcomeAsync(record, "{}")).ShouldBeFalse();
        await store.CleanUpAsync();

        Directory.Exists(uploadDirectory).ShouldBeTrue();
        Directory.GetDirectories(Path.GetDirectoryName(uploadDirectory)!, "outcomes").ShouldBeEmpty();
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task RemovingOutcomes_RemovesTheOlderOnes_WithoutOpeningTheOthers()
    {
        await using var host = await StartAsync();
        var store = host.Services.GetRequiredService<IUploadStore>();
        string olderKey = Guid.NewGuid().ToString("N");
        string newerKey = Guid.NewGuid().ToString("N");

        var older = await UploadAsync(host, olderKey);
        host.Clock.Advance(TimeSpan.FromMinutes(10));
        var newer = await UploadAsync(host, newerKey);

        // The newer outcome can't be opened, as a file that another process holds.
        using (new FileStream(host.GetOutcomeFile(newer), FileMode.Open, FileAccess.Read, FileShare.None))
            await store.RemoveOutcomesAsync(host.Clock.GetUtcNow().UtcDateTime.AddMinutes(-5));

        StoreWarnings(host).ShouldBeEmpty();
        (await store.GetAsync("1", older.Id)).ShouldBeNull();
        (await store.GetAsync("1", newer.Id)).ShouldNotBeNull();

        // A creation made again gets the newer upload, and creates the older one again.
        (await host.SendCreateAsync(new { fileName = "a", length = 10, idempotencyKey = olderKey })).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await host.SendCreateAsync(new { fileName = "a", length = 10, idempotencyKey = newerKey })).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task Cleanup_RemovesExpiredOutcomes_AndAKeyIsFreeAgainOnAnotherServer()
    {
        string directory = UploadTestHost.CreateDirectoryPath();
        var shortLivedStore = new DeclaredSharedShortLivedStore(new MemoryShortLivedStore(TimeProvider.System));

        try
        {
            await using var first = await StartAsync(directory, shortLivedStore: shortLivedStore);
            await using var second = await StartAsync(directory, shortLivedStore: shortLivedStore);
            string key = Guid.NewGuid().ToString("N");
            var session = await UploadAsync(first, key);

            // The second server's cleanup, once the outcome is no longer kept.
            await second.Services.GetRequiredService<IUploadStore>().RemoveOutcomesAsync(DateTime.UtcNow.AddHours(1));

            await UploadTestHost.ShouldBeErrorAsync(await second.CompleteAsync(session), HttpStatusCode.NotFound, UploadErrorCodes.NotFound);

            // The upload is created again, with the ID that the key gives it.
            using var again = await second.SendCreateAsync(new { fileName = "a", length = 10, idempotencyKey = key });
            again.StatusCode.ShouldBe(HttpStatusCode.Created);
            (await again.Content.ReadFromJsonAsync<UploadSession>(UploadTestHost.JsonOptions))!.Id.ShouldBe(session.Id);
        }
        finally
        {
            UploadTestHost.DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task OutcomeOfAServerWhoseClockIsAhead_IsRemovedByItsOwnTime()
    {
        string directory = UploadTestHost.CreateDirectoryPath();
        var shortLivedStore = new DeclaredSharedShortLivedStore(new MemoryShortLivedStore(TimeProvider.System));
        var now = DateTimeOffset.UtcNow;

        try
        {
            await using var ahead = await StartAsync(directory, new TestClock(now.AddMinutes(1)), shortLivedStore);
            await using var other = await StartAsync(directory, new TestClock(now), shortLivedStore);
            var session = await UploadAsync(ahead, null);
            var store = other.Services.GetRequiredService<IUploadStore>();

            // With a retention of an hour, a cleanup by the other server's clock an hour and 30 seconds later keeps the outcome, which was stored a minute
            // later by the clock of the server that stored it.
            await store.RemoveOutcomesAsync(now.UtcDateTime.AddSeconds(30));
            (await store.GetAsync("1", session.Id)).ShouldNotBeNull();

            await store.RemoveOutcomesAsync(now.UtcDateTime.AddMinutes(2));
            (await store.GetAsync("1", session.Id)).ShouldBeNull();
        }
        finally
        {
            UploadTestHost.DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task Cleanup_KeepsTheDirectoryOfOutcomes_OnceItIsOld()
    {
        await using var host = await StartAsync();
        string key = Guid.NewGuid().ToString("N");
        var session = await UploadAsync(host, key);
        string userDirectory = Path.GetDirectoryName(Path.GetDirectoryName(host.GetOutcomeFile(session)))!;

        Directory.SetCreationTimeUtc(Path.Combine(userDirectory, "outcomes"), DateTime.UtcNow.AddHours(-2));

        var store = host.Services.GetRequiredService<IUploadStore>();
        await store.CleanUpAsync();

        (await store.GetAsync("1", session.Id)).ShouldNotBeNull();
        (await host.SendCreateAsync(new { fileName = "a", length = 10, idempotencyKey = key })).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task UploadThat4xFinished_IsNotAnswered_AndIsRemovedAtTheCleanup()
    {
        await using var host = await StartAsync();
        string key = Guid.NewGuid().ToString("N");
        var session = await CreateAsync(host, key);
        string uploadDirectory = host.GetUploadDirectory(session);

        // 4.x left a finished upload's record and outcome in its directory, and removed the rest.
        Directory.Delete(Path.Combine(uploadDirectory, "chunks"));
        File.Delete(Path.Combine(uploadDirectory, "data"));
        await File.WriteAllTextAsync(
            Path.Combine(uploadDirectory, "outcome.json"), $$"""{"outcome":"{\"hasItem\":false}","storedUtc":"{{DateTime.UtcNow:O}}"}""");

        var store = host.Services.GetRequiredService<IUploadStore>();
        (await store.GetAsync("1", session.Id)).ShouldBeNull();
        (await store.ListAsync().ToListAsync()).ShouldBeEmpty();

        await store.CleanUpAsync();

        Directory.Exists(uploadDirectory).ShouldBeFalse();
        (await host.SendCreateAsync(new { fileName = "a", length = 10, idempotencyKey = key })).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [TestMethod]
    public async Task CountOfOpenUploads_ListsOnlyTheOpenUploads_AndOnlyInTheCleanup()
    {
        var store = new MemoryUploadStore(TimeProvider.System);

        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, (UploadedFile file) => Task.FromResult(new Note("done"))),
            options => {
                options.MaxUploadsPerUser = 2;
            },
            configureServices: services => services.AddSingleton<IUploadStore>(store));

        for (int i = 0; i < 1000; i++)
        {
            var record = new UploadRecord(new string((char)('a' + (i % 26)), 3) + i.ToString("D40"), "1", 10, 5 << 20, DateTime.UtcNow, "{}");
            await store.TryCreateAsync(record);
            await store.TrySetOutcomeAsync(record, """{"hasItem":false}""");
        }

        // Creations within the limit check the counts of the user's open uploads without listing them.
        var open = await host.CreateUploadAsync("a", 10);
        await host.CreateUploadAsync("b", 10);
        store.ListedCount.ShouldBe(0);

        // A creation over the limit is rejected by the counts, without a listing either.
        using (var over = await host.SendCreateAsync(new { fileName = "c", length = 10 }))
            await UploadTestHost.ShouldBeErrorAsync(over, HttpStatusCode.TooManyRequests, UploadErrorCodes.LimitReached);

        store.ListedCount.ShouldBe(0);

        // The cleanup corrects the counts from a listing of the open uploads: the two open uploads, not the 1,000 finished ones.
        await host.Services.GetRequiredService<ChunkedUploads>().CleanUpAsync(CancellationToken.None);

        store.ListedCount.ShouldBe(2);
        (await store.GetAsync("1", open.Id)).ShouldNotBeNull();
    }

    private static Task<UploadTestHost> StartAsync(string? directory = null, TestClock? clock = null, IShortLivedStore? shortLivedStore = null)
    {
        return UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, (UploadedFile file) => Task.FromResult(new Note("done"))),
            options => options.CompletionPingInterval = TimeSpan.FromMilliseconds(50),
            directory,
            clock: clock,
            shortLivedStore: shortLivedStore);
    }

    private static async Task<UploadSession> CreateAsync(UploadTestHost host, string? key)
    {
        using var response = await host.SendCreateAsync(new { fileName = "a", length = 10, idempotencyKey = key });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<UploadSession>(UploadTestHost.JsonOptions)).ShouldNotBeNull();
    }

    private static async Task<UploadSession> UploadAsync(UploadTestHost host, string? key)
    {
        var session = await CreateAsync(host, key);
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));
        UploadTestHost.WithoutPings(await host.CompleteAndReadAsync(session))[^1].ShouldBe("""{"end":true}""");

        return session;
    }

    /// <summary>
    /// Writes the claim of a run of a server that stopped: not renewed for longer than its lease.
    /// </summary>
    private static Task WriteLapsedClaimAsync(string uploadDirectory)
    {
        string startedUtc = DateTime.UtcNow.AddMinutes(-5).ToString("O");
        string renewedUtc = DateTime.UtcNow.AddMinutes(-4).ToString("O");

        return File.WriteAllTextAsync(
            Path.Combine(uploadDirectory, "claim"),
            $$"""{"claimId":"stopped","leaseDuration":"00:01:00","startedUtc":"{{startedUtc}}","renewedUtc":"{{renewedUtc}}"}""");
    }

    /// <summary>
    /// Writes the outcome of a finished upload to its directory, as the server that finishes it does first.
    /// </summary>
    private static async Task WriteFinishedAsync(string uploadDirectory, string outcome)
    {
        var record = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(uploadDirectory, "upload.json")))!;
        var finished = new JsonObject { ["record"] = record, ["outcome"] = outcome, ["storedUtc"] = DateTime.UtcNow };

        await File.WriteAllTextAsync(Path.Combine(uploadDirectory, "finished.json"), finished.ToJsonString());
    }

    /// <summary>
    /// Gets the warnings of the directory store, such as of a file that it could not open.
    /// </summary>
    private static List<string> StoreWarnings(UploadTestHost host)
    {
        return [.. host.Web.WarningLogs.Where(l => l.Category.EndsWith(".DirectoryUploadStore", StringComparison.Ordinal)).Select(l => l.Message)];
    }
}
