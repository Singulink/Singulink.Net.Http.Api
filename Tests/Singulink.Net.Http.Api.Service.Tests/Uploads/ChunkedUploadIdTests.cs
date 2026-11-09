using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Singulink.Net.Http.Api.DataContracts;
using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The IDs of uploads, which clients choose: a creation with the ID of an upload of its user gets that upload, also when creations run at the same time,
/// and the IDs of different users don't meet. 4.x clients send no ID, and get one from the service, or one from their idempotency key.
/// </summary>
[PrefixTestClass]
public sealed class ChunkedUploadIdTests
{
    private const string Pattern = "/folders/{folderId}/files";

    private static readonly TimeSpan CreationPollInterval = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task Create_WithAnId_CreatesTheUploadWithIt()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HandleAsync));
        var id = Guid.NewGuid();

        using var response = await host.SendCreateAsync(Body(id));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await ReadSessionAsync(response)).Id.ShouldBe(id.ToString("D"));
        response.Headers.Location!.OriginalString.ShouldBe($"/folders/5/files/uploads/{id:D}");
    }

    [TestMethod]
    public async Task Create_ConcurrentWithSameId_GetSameUpload()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HandleAsync));
        var body = Body(Guid.NewGuid());

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => host.SendCreateAsync(body)));

        responses.Count(r => r.StatusCode is HttpStatusCode.Created).ShouldBe(1);
        responses.Count(r => r.StatusCode is HttpStatusCode.OK).ShouldBe(7);

        var ids = await Task.WhenAll(responses.Select(async r => (await ReadSessionAsync(r)).Id));
        ids.Distinct().ShouldHaveSingleItem();

        string userDirectory = Directory.GetDirectories(host.Directory).ShouldHaveSingleItem();
        Directory.GetDirectories(userDirectory).ShouldHaveSingleItem().ShouldEndWith(ids[0]);
    }

    [TestMethod]
    public async Task Create_AgainWhileTheUploadIsOpen_GetsItWithoutRunningTheValidator()
    {
        int validations = 0;
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, ValidateAsync, HandleAsync));
        var body = Body(Guid.NewGuid());

        using var first = await host.SendCreateAsync(body);
        using var second = await host.SendCreateAsync(body);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.Headers.Location.ShouldBe(first.Headers.Location);
        validations.ShouldBe(1);

        Task ValidateAsync(UploadRequest upload)
        {
            Interlocked.Increment(ref validations);
            return Task.CompletedTask;
        }
    }

    [TestMethod]
    public async Task Create_AgainAfterTheUploadFinished_GetsThatUpload()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HandleAsync));
        var body = Body(Guid.NewGuid());

        using var first = await host.SendCreateAsync(body);
        var completed = await ReadSessionAsync(first);
        await host.PutChunksAsync(completed, UploadTestHost.CreateFile(10));
        await host.CompleteAndReadAsync(completed);

        using var second = await host.SendCreateAsync(body);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadSessionAsync(second)).Id.ShouldBe(completed.Id);

        // Its completion gets the outcome.
        UploadTestHost.WithoutPings(await host.CompleteAndReadAsync(completed)).ShouldBe(["""{"item":{"text":"a.txt"}}""", """{"end":true}"""]);
    }

    [TestMethod]
    public async Task Create_WithTheIdOfAnotherFile_IsIdConflict()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HandleAsync));
        var id = Guid.NewGuid();

        (await host.SendCreateAsync(Body(id))).StatusCode.ShouldBe(HttpStatusCode.Created);
        using var conflict = await host.SendCreateAsync(new { id, fileName = "other.txt", length = 10 });

        await UploadTestHost.ShouldBeErrorAsync(conflict, HttpStatusCode.Conflict, UploadErrorCodes.IdConflict);
    }

    [TestMethod]
    [DataRow("not-a-guid")]
    [DataRow("12345")]
    [DataRow("")]
    public async Task Create_WithAnIdThatIsNotAGuid_IsUnreadable(string id)
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HandleAsync));

        using var response = await host.SendCreateAsync(new { id, fileName = "a.txt", length = 10 });

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.BadRequest, UploadErrorCodes.CreationUnreadable);
    }

    [TestMethod]
    public async Task Create_WithTheEmptyGuid_IsInvalid()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HandleAsync));

        using var response = await host.SendCreateAsync(Body(Guid.Empty));

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.BadRequest, UploadErrorCodes.Invalid);
    }

    [TestMethod]
    public async Task Create_WithoutAnIdOrKey_GetsANewIdFromTheService_AsFor4xClients()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HandleAsync));
        var body = new { fileName = "a.txt", length = 10 };

        using var first = await host.SendCreateAsync(body);
        using var second = await host.SendCreateAsync(body);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.StatusCode.ShouldBe(HttpStatusCode.Created);

        var session = await ReadSessionAsync(first);
        Guid.TryParseExact(session.Id, "D", out var id).ShouldBeTrue();
        id.Version.ShouldBe(4);
        (await ReadSessionAsync(second)).Id.ShouldNotBe(session.Id);

        // The three requests of a 4.x client work with it.
        await host.PutChunksAsync(session, UploadTestHost.CreateFile(10));
        UploadTestHost.WithoutPings(await host.CompleteAndReadAsync(session)).ShouldBe(["""{"item":{"text":"a.txt"}}""", """{"end":true}"""]);
    }

    [TestMethod]
    public async Task Create_WithTheIdempotencyKeyOfA4xClient_GetsTheSameUpload_OrAKeyConflict()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HandleAsync));
        var body = new { fileName = "a.txt", length = 10, idempotencyKey = "key-1" };

        using var first = await host.SendCreateAsync(body);
        using var second = await host.SendCreateAsync(body);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadSessionAsync(second)).Id.ShouldBe((await ReadSessionAsync(first)).Id);

        using var conflict = await host.SendCreateAsync(new { fileName = "other.txt", length = 10, idempotencyKey = "key-1" });
        await UploadTestHost.ShouldBeErrorAsync(conflict, HttpStatusCode.Conflict, UploadErrorCodes.KeyConflict);

        // Another key is another upload.
        using var other = await host.SendCreateAsync(new { fileName = "a.txt", length = 10, idempotencyKey = "key-2" });
        other.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [TestMethod]
    public async Task UploadsOfTwoUsersWithTheSameId_DoNotInterfere()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HandleAsync));
        var id = Guid.NewGuid();

        using var first = await host.SendCreateAsync(Body(id), userId: 1);
        using var second = await host.SendCreateAsync(new { id, fileName = "b.txt", length = 10 }, userId: 2);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.StatusCode.ShouldBe(HttpStatusCode.Created);

        var firstSession = await ReadSessionAsync(first);
        var secondSession = await ReadSessionAsync(second);
        secondSession.Id.ShouldBe(firstSession.Id);

        // The second user's cancellation deletes the second user's upload, and leaves the first user's to run.
        await host.PutChunksAsync(firstSession, UploadTestHost.CreateFile(10), userId: 1);
        await host.PutChunksAsync(secondSession, UploadTestHost.CreateFile(10, seed: 2), userId: 2);
        (await host.AbortAsync(secondSession, userId: 2)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        UploadTestHost.WithoutPings(await host.CompleteAndReadAsync(firstSession, userId: 1)).ShouldBe(["""{"item":{"text":"a.txt"}}""", """{"end":true}"""]);

        using var deleted = await host.CompleteAsync(secondSession, userId: 2);
        (await UploadTestHost.ReadLinesAsync(deleted)).ShouldContain(l => l.Contains(UploadErrorCodes.NotFound));

        // The first user's finished upload doesn't keep the second user from creating one with the ID again.
        using var again = await host.SendCreateAsync(new { id, fileName = "b.txt", length = 10 }, userId: 2);
        again.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [TestMethod]
    public async Task Create_WhileTheUploadWithTheIdIsBeingRemoved_WaitsForTheRemoval()
    {
        var clock = new ManualClock(manualTimestamps: true);
        var store = new MemoryUploadStore(clock);
        await using var host = await StartWithStoreAsync(store, clock);
        bool isBeingRemoved = true;
        store.IsBeingRemoved = _ => Volatile.Read(ref isBeingRemoved);

        // The creation tries again every 100 milliseconds.
        var creation = host.SendCreateAsync(Body(Guid.NewGuid()));
        await WaitForPollAsync(clock);
        creation.IsCompleted.ShouldBeFalse();

        Volatile.Write(ref isBeingRemoved, false);
        clock.Advance(CreationPollInterval);

        using var response = await creation.WaitAsync(WaitTimeout);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [TestMethod]
    public async Task Create_WhileTheUploadWithTheIdIsBeingRemovedPastTheWait_IsCreationPending()
    {
        var clock = new ManualClock(manualTimestamps: true);
        var store = new MemoryUploadStore(clock) { IsBeingRemoved = _ => true };
        await using var host = await StartWithStoreAsync(store, clock);

        // The creation waits for 5 seconds.
        var creation = host.SendCreateAsync(Body(Guid.NewGuid()));
        await WaitForPollAsync(clock);
        clock.Advance(TimeSpan.FromSeconds(5));

        using var pending = await creation.WaitAsync(WaitTimeout);

        await UploadTestHost.ShouldBeErrorAsync(pending, HttpStatusCode.ServiceUnavailable, UploadErrorCodes.CreationPending);
        pending.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromSeconds(1));
        store.Count.ShouldBe(0);
    }

    [TestMethod]
    public async Task Create_WhoseStoreFailsToCreate_LeavesNothing_AndCanBeMadeAgain()
    {
        var store = new MemoryUploadStore(TimeProvider.System);
        await using var host = await StartWithStoreAsync(store);
        var body = Body(Guid.NewGuid());

        store.BeforeOperation = operation => {
            if (operation is "Create")
                throw new IOException("The store failed.");
        };

        using (var failed = await host.SendCreateAsync(body))
            await UploadTestHost.ShouldBeErrorAsync(failed, HttpStatusCode.ServiceUnavailable, UploadErrorCodes.StoreUnavailable);

        store.BeforeOperation = null;
        using var again = await host.SendCreateAsync(body);

        again.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [TestMethod]
    public async Task Create_RejectedAtALimit_LeavesTheIdFree()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HandleAsync), options => options.MaxUploadsPerUser = 1);
        var other = await host.CreateUploadAsync("other.txt", 10);
        var body = Body(Guid.NewGuid());

        using (var response = await host.SendCreateAsync(body))
            await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.TooManyRequests, UploadErrorCodes.LimitReached);

        (await host.AbortAsync(other)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await host.SendCreateAsync(body)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [TestMethod]
    public async Task DirectoryStore_CreationLeftUnfinished_DoesNotKeepTheIdFromBeingCreated_AndIsRemovedOnceStale()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, HandleAsync));
        var id = Guid.NewGuid();

        // A creation that stopped before it moved its directory to the upload's name, and the reservations of idempotency keys of 4.x.
        using (var first = await host.SendCreateAsync(Body(Guid.NewGuid())))
            first.StatusCode.ShouldBe(HttpStatusCode.Created);

        string userDirectory = Directory.GetDirectories(host.Directory).ShouldHaveSingleItem();
        string leftover = Path.Combine(userDirectory, $"{id:D}.{Guid.NewGuid():N}.new");
        string legacyKeys = Path.Combine(userDirectory, "keys");
        Directory.CreateDirectory(leftover);
        Directory.CreateDirectory(legacyKeys);
        await File.WriteAllTextAsync(Path.Combine(legacyKeys, "key"), "{}");

        using (var response = await host.SendCreateAsync(Body(id)))
            response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var store = host.Services.GetRequiredService<IUploadStore>();
        await store.CleanUpAsync();

        Directory.Exists(leftover).ShouldBeTrue();
        Directory.Exists(legacyKeys).ShouldBeFalse();

        Directory.SetCreationTimeUtc(leftover, DateTime.UtcNow.AddHours(-2));
        await store.CleanUpAsync();

        Directory.Exists(leftover).ShouldBeFalse();
        (await store.GetAsync("1", id.ToString("D"))).ShouldNotBeNull();
    }

    private static object Body(Guid id) => new { id, fileName = "a.txt", length = 10 };

    private static async Task<UploadSession> ReadSessionAsync(HttpResponseMessage response)
    {
        return (await response.Content.ReadFromJsonAsync<UploadSession>(UploadTestHost.JsonOptions)).ShouldNotBeNull();
    }

    private static Task<Note> HandleAsync(UploadedFile file) => Task.FromResult(new Note(file.FileName));

    private static Task<UploadTestHost> StartWithStoreAsync(MemoryUploadStore store, TimeProvider? timeProvider = null)
    {
        return UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, HandleAsync),
            configureServices: services => services.AddSingleton<IUploadStore>(store),
            timeProvider: timeProvider);
    }

    /// <summary>
    /// Waits until a creation waits to try again while the upload with its ID is being removed.
    /// </summary>
    private static async Task WaitForPollAsync(ManualClock clock)
    {
        using var timeout = new CancellationTokenSource(WaitTimeout);

        while (!clock.HasTimerDueIn(CreationPollInterval))
            await Task.Delay(10, timeout.Token);
    }
}
