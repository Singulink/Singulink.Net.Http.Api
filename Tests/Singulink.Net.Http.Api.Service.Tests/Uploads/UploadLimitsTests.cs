using System.Globalization;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Singulink.Net.Http.Api.DataContracts;
using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The limits of the open uploads of users that an app's <see cref="IUploadLimitsProvider"/> sets in place of the options' limits, and how the open uploads
/// of users are counted for them in the short-lived store, and corrected by the cleanup.
/// </summary>
[PrefixTestClass]
public sealed class UploadLimitsTests
{
    private const string Pattern = "/folders/{folderId}/files";

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task LimitsOfTheProvider_ApplyToTheCountOfTheUsersUploads_AndNullIsTheOptions()
    {
        var provider = new LimitsProvider { ["1"] = new UploadLimits { MaxUploadsPerUser = 3 }, ["2"] = null };
        await using var host = await StartAsync(provider, options => options.MaxUploadsPerUser = 1);

        for (int i = 0; i < 3; i++)
            await host.CreateUploadAsync($"f{i}", 10);

        using (var overProvider = await host.SendCreateAsync(new { fileName = "x", length = 10 }))
        {
            await UploadTestHost.ShouldBeErrorAsync(overProvider, HttpStatusCode.TooManyRequests, UploadErrorCodes.LimitReached);
            overProvider.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromSeconds(1));
        }

        await host.CreateUploadAsync("f", 10, userId: 2);

        using (var overOptions = await host.SendCreateAsync(new { fileName = "x", length = 10 }, userId: 2))
            await UploadTestHost.ShouldBeErrorAsync(overOptions, HttpStatusCode.TooManyRequests, UploadErrorCodes.LimitReached);

        provider.Calls.ShouldBe(["1", "1", "1", "1", "2", "2"]);
    }

    [TestMethod]
    public async Task LimitsOfTheProvider_ApplyToTheTotalLengthAndToTheLengthOfAnUpload_AndNullIsTheOptions()
    {
        var provider = new LimitsProvider { ["1"] = new UploadLimits { MaxUploadsPerUser = 5, MaxTotalSizePerUser = 1000 }, ["2"] = null };

        await using var host = await StartAsync(provider, options => {
            options.MaxSize = 5000;
            options.MaxTotalSizePerUser = 100;
        });

        // An upload over the options' total can be created by a user whose total is larger, and one over the user's total can never be.
        await host.CreateUploadAsync("a", 600);

        using (var overTotal = await host.SendCreateAsync(new { fileName = "b", length = 600 }))
            await UploadTestHost.ShouldBeErrorAsync(overTotal, HttpStatusCode.TooManyRequests, UploadErrorCodes.SizeLimitReached);

        using (var tooLong = await host.SendCreateAsync(new { fileName = "c", length = 1001 }))
            await UploadTestHost.ShouldBeErrorAsync(tooLong, HttpStatusCode.RequestEntityTooLarge, UploadErrorCodes.ExceedsSizeLimit);

        using (var ofOptions = await host.SendCreateAsync(new { fileName = "d", length = 600 }, userId: 2))
            await UploadTestHost.ShouldBeErrorAsync(ofOptions, HttpStatusCode.RequestEntityTooLarge, UploadErrorCodes.ExceedsSizeLimit);
    }

    [TestMethod]
    public async Task TotalOfTheProviderThatIsNull_IsNoLimit_WhateverTheOptionIs()
    {
        var provider = new LimitsProvider { ["1"] = new UploadLimits { MaxUploadsPerUser = 5 } };

        await using var host = await StartAsync(provider, options => {
            options.MaxSize = 5000;
            options.MaxTotalSizePerUser = 100;
        });

        await host.CreateUploadAsync("a", 600);
        await host.CreateUploadAsync("b", 600);
    }

    [TestMethod]
    public async Task CreationWithAnId_OfAFileThatCanNeverFitTheUsersTotal_GetsTheFinalError()
    {
        var store = new MemoryUploadStore(TimeProvider.System);
        var provider = new LimitsProvider { ["1"] = new UploadLimits { MaxUploadsPerUser = 1, MaxTotalSizePerUser = 100 } };
        await using var host = await StartAsync(provider, store: store);

        // The user is at the limit of uploads too, which uploads that end would lift.
        await host.CreateUploadAsync("a", 10);

        using var response = await host.SendCreateAsync(new { id = Guid.NewGuid(), fileName = "b", length = 101 });

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.RequestEntityTooLarge, UploadErrorCodes.ExceedsSizeLimit);
        response.Headers.RetryAfter.ShouldBeNull();
        store.Count.ShouldBe(1);
    }

    [TestMethod]
    public async Task CreationWithAnId_OfAFileThatCanNeverFitTheTotalOfAllUsers_GetsTheFinalError()
    {
        await using var host = await StartAsync(new LimitsProvider { ["1"] = null }, options => options.MaxTotalSize = 100);

        using var response = await host.SendCreateAsync(new { id = Guid.NewGuid(), fileName = "a", length = 101 });

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.RequestEntityTooLarge, UploadErrorCodes.TooLarge);
        response.Headers.RetryAfter.ShouldBeNull();
    }

    [TestMethod]
    public async Task LimitsOfTheProvider_ApplyToDirectUploads_BeforeTheirBodyIsStored()
    {
        var provider = new LimitsProvider { ["1"] = new UploadLimits { MaxUploadsPerUser = 1 } };
        await using var host = await StartAsync(provider);
        await host.CreateUploadAsync("a", 10);

        using var response = await host.Client.SendAsync(host.CreateDirectRequest(new ByteArrayContent(UploadTestHost.CreateFile(10)), "b"));

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.TooManyRequests, UploadErrorCodes.LimitReached);
        response.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromSeconds(1));
        provider.Calls.ShouldBe(["1", "1"]);
    }

    [TestMethod]
    public async Task CreationOverTheLimit_IsRejectedWithoutStoringAnything()
    {
        var store = new MemoryUploadStore(TimeProvider.System);
        var clock = new ManualClock(manualTimestamps: true);
        await using var host = await StartAsync(new LimitsProvider { ["1"] = new UploadLimits { MaxUploadsPerUser = 1 } }, store: store, clock: clock);
        await host.CreateUploadAsync("a", 10);
        int listed = store.ListedCount;

        string key = Guid.NewGuid().ToString("N");

        using (var over = await host.SendCreateAsync(new { fileName = "b", length = 10, idempotencyKey = key }))
            await UploadTestHost.ShouldBeErrorAsync(over, HttpStatusCode.TooManyRequests, UploadErrorCodes.LimitReached);

        store.Count.ShouldBe(1);

        // The rejection reads the counts, and doesn't list the user's open uploads.
        store.ListedCount.ShouldBe(listed);
    }

    [TestMethod]
    public async Task CreationMadeAgainWithTheKeyOfAnUpload_GetsTheUpload_AlsoOverLimitsThatShrankMeanwhile()
    {
        var provider = new LimitsProvider { ["1"] = new UploadLimits { MaxUploadsPerUser = 2 } };
        await using var host = await StartAsync(provider);
        string openKey = Guid.NewGuid().ToString("N");
        string finishedKey = Guid.NewGuid().ToString("N");

        var open = await CreateAsync(host, openKey, 100);
        var finished = await CreateAsync(host, finishedKey, 10);
        await host.PutChunksAsync(finished, UploadTestHost.CreateFile(10));
        await host.CompleteAndReadAsync(finished);

        // The user's limits shrink: one open upload, of up to 10 bytes in all, which the open upload is over.
        provider["1"] = new UploadLimits { MaxUploadsPerUser = 1, MaxTotalSizePerUser = 10 };

        using (var again = await host.SendCreateAsync(new { fileName = "a", length = 100, idempotencyKey = openKey }))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await again.Content.ReadFromJsonAsync<UploadSession>(UploadTestHost.JsonOptions)).ShouldNotBeNull().Id.ShouldBe(open.Id);
        }

        using (var again = await host.SendCreateAsync(new { fileName = "a", length = 10, idempotencyKey = finishedKey }))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await again.Content.ReadFromJsonAsync<UploadSession>(UploadTestHost.JsonOptions)).ShouldNotBeNull().Id.ShouldBe(finished.Id);
        }

        using (var other = await host.SendCreateAsync(new { fileName = "b", length = 100, idempotencyKey = Guid.NewGuid().ToString("N") }))
            await UploadTestHost.ShouldBeErrorAsync(other, HttpStatusCode.RequestEntityTooLarge, UploadErrorCodes.ExceedsSizeLimit);
    }

    [TestMethod]
    public async Task CreationsAtTheSameTimeOnOneServer_StayWithinTheLimit()
    {
        await using var host = await StartAsync(new LimitsProvider { ["1"] = new UploadLimits { MaxUploadsPerUser = 5 } });

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() => host.SendCreateAsync(new { fileName = $"f{i}", length = 10 }))));
        var statuses = responses.Select(r => r.StatusCode).ToList();

        foreach (var response in responses)
            response.Dispose();

        statuses.Count(s => s is HttpStatusCode.Created).ShouldBe(5);
        statuses.Count(s => s is HttpStatusCode.TooManyRequests).ShouldBe(15);
    }

    [TestMethod]
    public async Task CreationsAtTheSameTimeOnTwoServers_ForTheLastUploadOfTheUser_CreateOne()
    {
        var store = new MemoryUploadStore(TimeProvider.System) { SharingMode = StoreSharingMode.Shared };
        var shortLivedStore = new DeclaredSharedShortLivedStore(new MemoryShortLivedStore(TimeProvider.System));
        var provider = new LimitsProvider { ["1"] = new UploadLimits { MaxUploadsPerUser = 5 } };

        await using var first = await StartAsync(provider, store: store, shortLivedStore: shortLivedStore);
        await using var second = await StartAsync(provider, store: store, shortLivedStore: shortLivedStore);

        for (int i = 0; i < 4; i++)
            await first.CreateUploadAsync($"f{i}", 10);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(
            i => Task.Run(() => (i % 2 is 0 ? first : second).SendCreateAsync(new { fileName = $"g{i}", length = 10 }))));

        var statuses = responses.Select(r => r.StatusCode).ToList();

        foreach (var response in responses)
            response.Dispose();

        statuses.Count(s => s is HttpStatusCode.Created).ShouldBe(1);
        statuses.Count(s => s is HttpStatusCode.TooManyRequests).ShouldBe(9);
        store.Count.ShouldBe(5);
    }

    [TestMethod]
    public async Task UploadsThatEndedOnAnotherServer_StopCountingAtOnce()
    {
        var store = new MemoryUploadStore(TimeProvider.System) { SharingMode = StoreSharingMode.Shared };
        var shortLivedStore = new DeclaredSharedShortLivedStore(new MemoryShortLivedStore(TimeProvider.System));
        var provider = new LimitsProvider { ["1"] = new UploadLimits { MaxUploadsPerUser = 3 } };

        await using var first = await StartAsync(provider, store: store, shortLivedStore: shortLivedStore);
        await using var second = await StartAsync(provider, store: store, shortLivedStore: shortLivedStore);

        var a = await first.CreateUploadAsync("a", 10);
        var b = await first.CreateUploadAsync("b", 10);
        await first.CreateUploadAsync("c", 10);
        (await second.AbortAsync(a)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await second.AbortAsync(b)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // The first server's creations are within the limit by the counts.
        await first.CreateUploadAsync("d", 10);
        await first.CreateUploadAsync("e", 10);
    }

    [TestMethod]
    public async Task OpenUploadsFromBeforeARestart_Count_OnceTheFirstCleanupHasCountedThem()
    {
        string directory = UploadTestHost.CreateDirectoryPath();
        var provider = new LimitsProvider { ["1"] = new UploadLimits { MaxUploadsPerUser = 2 } };

        try
        {
            await using (var before = await StartAsync(provider, directory: directory))
            {
                await before.CreateUploadAsync("a", 10);
                await before.CreateUploadAsync("b", 10);
            }

            // The host that starts next keeps the counts in a memory store of its own, which starts empty, and its first cleanup counts the uploads.
            await using var after = await StartAsync(provider, directory: directory, backgroundCleanup: true);
            var shortLivedStore = after.Services.GetRequiredKeyedService<IShortLivedStore>(ShortLivedStoreKeys.Uploads);
            await WaitUntilAsync(async () => (await ReadCountsAsync(shortLivedStore)).Uploads is 2);

            using var over = await after.SendCreateAsync(new { fileName = "c", length = 10 });
            await UploadTestHost.ShouldBeErrorAsync(over, HttpStatusCode.TooManyRequests, UploadErrorCodes.LimitReached);
        }
        finally
        {
            UploadTestHost.DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task CountsInAShortLivedStoreThatIsKept_CountTheOpenUploadsAfterARestart()
    {
        var store = new MemoryUploadStore(TimeProvider.System);
        var shortLivedStore = new MemoryShortLivedStore(TimeProvider.System);
        var provider = new LimitsProvider { ["1"] = new UploadLimits { MaxUploadsPerUser = 2 } };

        await using (var before = await StartAsync(provider, store: store, shortLivedStore: shortLivedStore))
        {
            await before.CreateUploadAsync("a", 10);
            await before.CreateUploadAsync("b", 10);
        }

        // No cleanup runs: the counts that the store kept reject the creation.
        await using var after = await StartAsync(provider, store: store, shortLivedStore: shortLivedStore);

        using var over = await after.SendCreateAsync(new { fileName = "c", length = 10 });
        await UploadTestHost.ShouldBeErrorAsync(over, HttpStatusCode.TooManyRequests, UploadErrorCodes.LimitReached);
        store.ListedCount.ShouldBe(0);
    }

    [TestMethod]
    public async Task FileLongerThanTheTotalOfAllOpenUploads_CanNeverBeCreated_AndIsTooLarge()
    {
        await using var host = await StartAsync(new LimitsProvider { ["1"] = null }, configure: options => options.MaxTotalSize = 100);

        using var over = await host.SendCreateAsync(new { fileName = "a", length = 101 });
        await UploadTestHost.ShouldBeErrorAsync(over, HttpStatusCode.RequestEntityTooLarge, UploadErrorCodes.TooLarge);

        // One that fits once other uploads have ended can be retried.
        await host.CreateUploadAsync("b", 100);

        using var full = await host.SendCreateAsync(new { fileName = "c", length = 1 });
        await UploadTestHost.ShouldBeErrorAsync(full, HttpStatusCode.ServiceUnavailable, UploadErrorCodes.StorageFull);
    }

    [TestMethod]
    public async Task ExpiredUpload_CountsTowardTheTotalUntilTheCleanupRemovesIt()
    {
        var clock = new ManualClock();
        await using var host = await StartAsync(
            new LimitsProvider { ["1"] = null }, clock: clock, configure: options => options.MaxTotalSize = 100);

        var uploads = host.Services.GetRequiredService<ChunkedUploads>();
        await host.CreateUploadAsync("a", 100);

        // The upload expires after 15 minutes, and a cleanup before then keeps its counts.
        clock.Advance(TimeSpan.FromMinutes(10));
        await uploads.CleanUpAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(10));

        using (var full = await host.SendCreateAsync(new { fileName = "b", length = 1 }))
            await UploadTestHost.ShouldBeErrorAsync(full, HttpStatusCode.ServiceUnavailable, UploadErrorCodes.StorageFull);

        await uploads.CleanUpAsync(CancellationToken.None);
        await host.CreateUploadAsync("b", 1);
    }

    [TestMethod]
    public async Task Cleanup_ThatRemovesExpiredUploads_LeavesTheCountsOfTheOthers()
    {
        var clock = new ManualClock();
        await using var host = await StartAsync(new LimitsProvider { ["1"] = null, ["2"] = null }, clock: clock);
        var shortLivedStore = host.Services.GetRequiredKeyedService<IShortLivedStore>(ShortLivedStoreKeys.Uploads);

        await host.CreateUploadAsync("a", 100);
        clock.Advance(TimeSpan.FromMinutes(10));
        await host.CreateUploadAsync("b", 10);
        await host.CreateUploadAsync("c", 20, userId: 2);
        clock.Advance(TimeSpan.FromMinutes(6));

        // The cleanup takes the expired upload out of the counts as it removes it, and its correction counts the others once.
        await host.Services.GetRequiredService<ChunkedUploads>().CleanUpAsync(CancellationToken.None);

        (await ReadCountsAsync(shortLivedStore)).ShouldBe((1, 10, 30));
        (await ReadCountsAsync(shortLivedStore, userId: 2)).ShouldBe((1, 20, 30));
    }

    [TestMethod]
    public async Task ExpiredUpload_CountsTowardTheUsersLimitUntilTheCleanupRemovesIt()
    {
        var clock = new ManualClock();
        var store = new MemoryUploadStore(clock);
        await using var host = await StartAsync(new LimitsProvider { ["1"] = new UploadLimits { MaxUploadsPerUser = 2 } }, store: store, clock: clock);
        var shortLivedStore = host.Services.GetRequiredKeyedService<IShortLivedStore>(ShortLivedStoreKeys.Uploads);

        await host.CreateUploadAsync("a", 100);
        clock.Advance(TimeSpan.FromMinutes(10));
        await host.CreateUploadAsync("b", 10);
        clock.Advance(TimeSpan.FromMinutes(6));

        using (var over = await host.SendCreateAsync(new { fileName = "c", length = 20 }))
            await UploadTestHost.ShouldBeErrorAsync(over, HttpStatusCode.TooManyRequests, UploadErrorCodes.LimitReached);

        // The cleanup removes the expired upload, which makes room for the creation.
        await host.Services.GetRequiredService<ChunkedUploads>().CleanUpAsync(CancellationToken.None);
        (await ReadCountsAsync(shortLivedStore)).ShouldBe((1, 10, 10));

        await host.CreateUploadAsync("c", 20);

        store.Count.ShouldBe(2);
        (await ReadCountsAsync(shortLivedStore)).ShouldBe((2, 30, 30));
    }

    [TestMethod]
    public async Task CountsThatAreTooLow_AreCorrectedByTheCleanup()
    {
        await using var host = await StartAsync(new LimitsProvider { ["1"] = new UploadLimits { MaxUploadsPerUser = 2 } });
        var shortLivedStore = host.Services.GetRequiredKeyedService<IShortLivedStore>(ShortLivedStoreKeys.Uploads);

        await host.CreateUploadAsync("a", 10);
        await host.CreateUploadAsync("b", 10);

        // An upload that two endings took out of the counts.
        await shortLivedStore.IncrementAsync(ToolkitKeys.UploadCount + "1", -1, TimeSpan.FromHours(1));
        await shortLivedStore.IncrementAsync(ToolkitKeys.UploadSize + "1", -10, TimeSpan.FromHours(1));
        await shortLivedStore.IncrementAsync(ToolkitKeys.UploadTotalSize, -10, TimeSpan.FromHours(1));

        await host.Services.GetRequiredService<ChunkedUploads>().CleanUpAsync(CancellationToken.None);

        (await ReadCountsAsync(shortLivedStore)).ShouldBe((2, 20, 20));
    }

    [TestMethod]
    public async Task CountsThatLapsed_GoNegativeAsUploadsEnd_AndAreCorrectedByTheCleanup()
    {
        var clock = new ManualClock();

        await using var host = await StartAsync(
            new LimitsProvider { ["1"] = new UploadLimits { MaxUploadsPerUser = 2 } },
            options => options.UnstartedExpiry = options.IdleExpiry = TimeSpan.FromHours(1),
            clock: clock);

        var shortLivedStore = host.Services.GetRequiredKeyedService<IShortLivedStore>(ShortLivedStoreKeys.Uploads);
        var a = await host.CreateUploadAsync("a", 10);
        await host.CreateUploadAsync("b", 10);

        // No cleanup kept the counts for 15 minutes.
        clock.Advance(TimeSpan.FromMinutes(16));
        (await host.AbortAsync(a)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ReadCountsAsync(shortLivedStore)).ShouldBe((-1, -10, -10));

        await host.Services.GetRequiredService<ChunkedUploads>().CleanUpAsync(CancellationToken.None);
        (await ReadCountsAsync(shortLivedStore)).ShouldBe((1, 10, 10));

        await host.CreateUploadAsync("c", 10);

        using var over = await host.SendCreateAsync(new { fileName = "d", length = 10 });
        await UploadTestHost.ShouldBeErrorAsync(over, HttpStatusCode.TooManyRequests, UploadErrorCodes.LimitReached);
    }

    [TestMethod]
    public async Task SubtractionThatFails_IsLogged_AndCorrectedByTheCleanup()
    {
        var shortLivedStore = new InterceptingShortLivedStore(new MemoryShortLivedStore(TimeProvider.System));
        await using var host = await StartAsync(new LimitsProvider { ["1"] = new UploadLimits { MaxUploadsPerUser = 2 } }, shortLivedStore: shortLivedStore);

        var a = await host.CreateUploadAsync("a", 10);
        await host.CreateUploadAsync("b", 10);

        // The abort succeeds, and the counts stay too high.
        shortLivedStore.FailOnce("Increment", ToolkitKeys.UploadCount + "1");
        (await host.AbortAsync(a)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        host.Web.WarningLogs.ShouldContain(l => l.Message.Contains("could not be taken out of the counts of open uploads"));
        (await ReadCountsAsync(shortLivedStore)).ShouldBe((2, 20, 20));

        // The upload holds its place until the cleanup corrects the counts.
        using (var over = await host.SendCreateAsync(new { fileName = "c", length = 10 }))
            await UploadTestHost.ShouldBeErrorAsync(over, HttpStatusCode.TooManyRequests, UploadErrorCodes.LimitReached);

        await host.Services.GetRequiredService<ChunkedUploads>().CleanUpAsync(CancellationToken.None);
        (await ReadCountsAsync(shortLivedStore)).ShouldBe((1, 10, 10));

        await host.CreateUploadAsync("c", 10);
        (await ReadCountsAsync(shortLivedStore)).ShouldBe((2, 20, 20));
    }

    [TestMethod]
    public async Task CleanupWhoseCorrectionFails_LogsIt_AndRemovesTheOutcomesAllTheSame()
    {
        var clock = new ManualClock();
        var shortLivedStore = new InterceptingShortLivedStore(new MemoryShortLivedStore(clock));

        await using var host = await StartAsync(
            new LimitsProvider { ["1"] = null, ["2"] = null },
            options => options.UnstartedExpiry = options.IdleExpiry = TimeSpan.FromHours(2),
            clock: clock,
            shortLivedStore: shortLivedStore);

        var (finished, _) = await host.UploadAsync(UploadTestHost.CreateFile(10));
        await host.CreateUploadAsync("a", 10);
        await host.CreateUploadAsync("b", 20, userId: 2);

        // The outcome is no longer kept, the counts lapsed, and the counts of the users' uploads are too high.
        clock.Advance(TimeSpan.FromMinutes(61));
        await shortLivedStore.IncrementAsync(ToolkitKeys.UploadCount + "1", 5, TimeSpan.FromHours(1));
        await shortLivedStore.IncrementAsync(ToolkitKeys.UploadCount + "2", 5, TimeSpan.FromHours(1));

        // The correction of the first user's count fails, and the other counts are corrected.
        shortLivedStore.FailOnce("Increment", ToolkitKeys.UploadCount + "1");
        await host.Services.GetRequiredService<ChunkedUploads>().CleanUpAsync(CancellationToken.None);

        host.Web.WarningLogs.ShouldContain(l => l.Message.Contains("Counters of open uploads could not be corrected"));
        (await ReadCountsAsync(shortLivedStore)).ShouldBe((5, 10, 30));
        (await ReadCountsAsync(shortLivedStore, userId: 2)).ShouldBe((1, 20, 30));
        (await host.Services.GetRequiredService<IUploadStore>().GetAsync("1", finished.Id)).ShouldBeNull();

        await host.Services.GetRequiredService<ChunkedUploads>().CleanUpAsync(CancellationToken.None);
        (await ReadCountsAsync(shortLivedStore)).ShouldBe((1, 10, 30));
    }

    [TestMethod]
    public async Task Creation_ReadsOnlyTheCountsOfTheLimitsThatAreSet()
    {
        var reads = new List<string>();
        var shortLivedStore = new InterceptingShortLivedStore(new MemoryShortLivedStore(TimeProvider.System));

        shortLivedStore.After = (operation, key) => {
            if (operation is "Get")
            {
                lock (reads)
                    reads.Add(key);
            }
        };

        var provider = new LimitsProvider { ["1"] = null, ["2"] = new UploadLimits { MaxUploadsPerUser = 5, MaxTotalSizePerUser = 100 } };
        await using var host = await StartAsync(provider, shortLivedStore: shortLivedStore);

        await host.CreateUploadAsync("a", 10);
        reads.ShouldBe([ToolkitKeys.UploadCount + "1"]);

        reads.Clear();
        await host.CreateUploadAsync("b", 10, userId: 2);
        reads.ShouldBe([ToolkitKeys.UploadCount + "2", ToolkitKeys.UploadSize + "2"]);
    }

    [TestMethod]
    public async Task CountsOfAProcessLocalUploadStoreInASharedShortLivedStore_AreKeptForTheMachine_ApartFromTheCountsOfASharedUploadStore()
    {
        var shortLivedStore = new DeclaredSharedShortLivedStore(new MemoryShortLivedStore(TimeProvider.System));
        var provider = new LimitsProvider { ["1"] = new UploadLimits { MaxUploadsPerUser = 2 } };

        // Two servers share the short-lived store: one with an upload store that other servers could share, and one with an upload store of its own, which
        // counts for its machine.
        await using var shared = await StartAsync(
            provider, store: new MemoryUploadStore(TimeProvider.System) { SharingMode = StoreSharingMode.Shared }, shortLivedStore: shortLivedStore);

        await using var perMachine = await StartAsync(provider, store: new MemoryUploadStore(TimeProvider.System), shortLivedStore: shortLivedStore);

        await shared.CreateUploadAsync("a", 10);
        await shared.CreateUploadAsync("b", 10);
        await perMachine.CreateUploadAsync("c", 30);
        await perMachine.CreateUploadAsync("d", 30);

        // Each server's cleanup corrects only its own counts.
        await shared.Services.GetRequiredService<ChunkedUploads>().CleanUpAsync(CancellationToken.None);
        await perMachine.Services.GetRequiredService<ChunkedUploads>().CleanUpAsync(CancellationToken.None);

        (await ReadCountsAsync(shortLivedStore)).ShouldBe((2, 20, 20));

        // The per-machine keys are written out here rather than built from ToolkitKeys, to pin their format.
        string machine = "@" + Environment.MachineName;
        (await shortLivedStore.GetAsync("httpapi:upload-count" + machine + ":1")).ShouldBe("2");
        (await shortLivedStore.GetAsync("httpapi:upload-size" + machine + ":1")).ShouldBe("60");
        (await shortLivedStore.GetAsync("httpapi:upload-total-size" + machine)).ShouldBe("60");

        foreach (var host in new[] { shared, perMachine })
        {
            using var over = await host.SendCreateAsync(new { fileName = "e", length = 10 });
            await UploadTestHost.ShouldBeErrorAsync(over, HttpStatusCode.TooManyRequests, UploadErrorCodes.LimitReached);
        }
    }

    [TestMethod]
    [DataRow(ToolkitKeys.UploadCount + "1")]
    [DataRow(ToolkitKeys.UploadSize + "1")]
    [DataRow(ToolkitKeys.UploadTotalSize)]
    public async Task ShortLivedStoreThatFailsToCountACreation_FailsIt_AndLeavesTheCountsAsTheyWere(string failingKey)
    {
        var store = new MemoryUploadStore(TimeProvider.System);
        var shortLivedStore = new InterceptingShortLivedStore(new MemoryShortLivedStore(TimeProvider.System));
        await using var host = await StartAsync(new LimitsProvider { ["1"] = null }, store: store, shortLivedStore: shortLivedStore);
        await host.CreateUploadAsync("a", 10);

        shortLivedStore.FailOnce("Increment", failingKey);
        string key = Guid.NewGuid().ToString("N");

        using (var failed = await host.SendCreateAsync(new { fileName = "b", length = 10, idempotencyKey = key }))
            await UploadTestHost.ShouldBeErrorAsync(failed, HttpStatusCode.InternalServerError, null);

        (await ReadCountsAsync(shortLivedStore)).ShouldBe((1, 10, 10));
        store.Count.ShouldBe(1);
    }

    [TestMethod]
    public async Task CreationThatIsCancelled_LeavesTheCountsAsTheyWere()
    {
        var store = new MemoryUploadStore(TimeProvider.System);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        await using var host = await UploadTestHost.StartAsync(
            app => {
                app.Use(async (context, next) => {
                    try
                    {
                        await next(context);
                    }
                    finally
                    {
                        if (cancellation.IsCancellationRequested)
                            ended.TrySetResult();
                    }
                });

                // The validator cancels the request of the second upload, which goes on until it would count the upload.
                app.MapChunkedUpload(
                    Pattern,
                    (UploadRequest request, HttpContext context) => {
                        if (request.FileName is "b")
                        {
                            cancellation.Cancel();
                            SpinWait.SpinUntil(() => context.RequestAborted.IsCancellationRequested, WaitTimeout).ShouldBeTrue();
                        }
                    },
                    (UploadedFile file) => new Note("done"));
            },
            configureServices: services => services.AddSingleton<IUploadStore>(store));

        var shortLivedStore = host.Services.GetRequiredKeyedService<IShortLivedStore>(ShortLivedStoreKeys.Uploads);
        await host.CreateUploadAsync("a", 10);

        var request = host.CreateRequest(HttpMethod.Post, $"{UploadTestHost.Path}/uploads");
        request.Content = JsonContent.Create(new { fileName = "b", length = 10 }, options: UploadTestHost.JsonOptions);
        await Should.ThrowAsync<OperationCanceledException>(() => host.Client.SendAsync(request, cancellation.Token));
        await ended.Task.WaitAsync(WaitTimeout);

        (await ReadCountsAsync(shortLivedStore)).ShouldBe((1, 10, 10));
        store.Count.ShouldBe(1);
    }

    [TestMethod]
    public async Task ShortLivedStoreThatIsFull_FailsTheCreationWithItsError()
    {
        var store = new MemoryUploadStore(TimeProvider.System);

        await using var host = await StartAsync(
            new LimitsProvider { ["1"] = null },
            store: store,
            configureServices: services => services.Configure<MemoryShortLivedStoreOptions>(ShortLivedStoreKeys.Uploads, options => options.MaxSize = 1));

        using var full = await host.SendCreateAsync(new { fileName = "a", length = 10 });

        await UploadTestHost.ShouldBeErrorAsync(full, HttpStatusCode.ServiceUnavailable, ApiErrorCodes.ShortLivedStoreFull);
        store.Count.ShouldBe(0);
    }

    [TestMethod]
    public async Task BackgroundCleanup_CorrectsTheCountsAgainAfterItsInterval_AlsoAfterACleanupThatFailed()
    {
        var clock = new ManualClock();
        var store = new MemoryUploadStore(clock);

        await using var host = await StartAsync(
            new LimitsProvider { ["1"] = null },
            options => options.UnstartedExpiry = options.IdleExpiry = TimeSpan.FromHours(1),
            store: store,
            clock: clock,
            backgroundCleanup: true);

        var shortLivedStore = host.Services.GetRequiredKeyedService<IShortLivedStore>(ShortLivedStoreKeys.Uploads);

        // The cleanup at the start corrects the total, which no creation has counted yet.
        await WaitUntilAsync(async () => await shortLivedStore.GetAsync(ToolkitKeys.UploadTotalSize) is not null);
        await host.CreateUploadAsync("a", 10);

        await shortLivedStore.IncrementAsync(ToolkitKeys.UploadCount + "1", 5, TimeSpan.FromHours(1));
        clock.Advance(ChunkedUploads.CleanupInterval);
        await WaitUntilAsync(async () => (await ReadCountsAsync(shortLivedStore)).Uploads is 1);

        // A cleanup that fails is logged, and the next one corrects the counts.
        store.ListWaitsFor = Task.FromException(new IOException("The store failed."));
        await shortLivedStore.IncrementAsync(ToolkitKeys.UploadCount + "1", 5, TimeSpan.FromHours(1));
        clock.Advance(ChunkedUploads.CleanupInterval);
        await WaitUntilAsync(
            () => Task.FromResult(host.Web.WarningLogs.Any(l => l.Message.StartsWith("Chunked uploads could not be cleaned up.", StringComparison.Ordinal))));

        store.ListWaitsFor = null;
        (await ReadCountsAsync(shortLivedStore)).Uploads.ShouldBe(6);
        clock.Advance(ChunkedUploads.CleanupInterval);
        await WaitUntilAsync(async () => (await ReadCountsAsync(shortLivedStore)).Uploads is 1);
    }

    [TestMethod]
    public async Task ProviderThatThrows_FailsTheCreationWithItsError()
    {
        var provider = new LimitsProvider { Failure = new ForbiddenApiException("No uploads for you.") };
        await using var host = await StartAsync(provider);

        using var response = await host.SendCreateAsync(new { fileName = "a", length = 10 });

        await UploadTestHost.ShouldBeErrorAsync(response, HttpStatusCode.Forbidden, null);
        (await host.Services.GetRequiredService<IUploadStore>().ListAsync().ToListAsync()).ShouldBeEmpty();
    }

    [TestMethod]
    public void Limits_ThatAreNotValid_Throw()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new UploadLimits { MaxUploadsPerUser = 0 });
        Should.Throw<ArgumentOutOfRangeException>(() => new UploadLimits { MaxUploadsPerUser = 1, MaxTotalSizePerUser = -1 });

        new UploadLimits { MaxTotalSizePerUser = 0, MaxUploadsPerUser = 1 }.MaxTotalSizePerUser.ShouldBe(0);
    }

    private static Task<UploadTestHost> StartAsync(
        LimitsProvider provider,
        Action<ChunkedUploadOptions>? configure = null,
        string? directory = null,
        MemoryUploadStore? store = null,
        ManualClock? clock = null,
        bool backgroundCleanup = false,
        IShortLivedStore? shortLivedStore = null,
        Action<IServiceCollection>? configureServices = null)
    {
        return UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, (UploadedFile file) => new Note("done")),
            options => {
                configure?.Invoke(options);
            },
            directory,
            configureServices: services => {
                services.AddSingleton<IUploadLimitsProvider>(provider);

                if (store is not null)
                    services.AddSingleton<IUploadStore>(store);

                configureServices?.Invoke(services);
            },
            timeProvider: clock,
            backgroundCleanup: backgroundCleanup,
            shortLivedStore: shortLivedStore);
    }

    /// <summary>
    /// Reads the counts of a user's open uploads, and the total length of all open uploads, from the short-lived store.
    /// </summary>
    private static async Task<(long Uploads, long Size, long TotalSize)> ReadCountsAsync(IShortLivedStore store, int userId = 1)
    {
        long uploads = await ReadAsync(ToolkitKeys.UploadCount + userId);
        long size = await ReadAsync(ToolkitKeys.UploadSize + userId);

        return (uploads, size, await ReadAsync(ToolkitKeys.UploadTotalSize));

        async Task<long> ReadAsync(string key) => long.Parse(await store.GetAsync(key) ?? "0", CultureInfo.InvariantCulture);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(WaitTimeout);

        while (!await condition())
            await Task.Delay(10, timeout.Token);
    }

    private static async Task<UploadSession> CreateAsync(UploadTestHost host, string key, long length)
    {
        using var response = await host.SendCreateAsync(new { fileName = "a", length, idempotencyKey = key });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<UploadSession>(UploadTestHost.JsonOptions)).ShouldNotBeNull();
    }

    /// <summary>
    /// A provider of limits by user ID, which records the users that it was called for.
    /// </summary>
    private sealed class LimitsProvider : IUploadLimitsProvider
    {
        private readonly Dictionary<string, UploadLimits?> _limits = [];
        private readonly List<string> _calls = [];

        public ApiException? Failure { get; init; }

        public List<string> Calls
        {
            get {
                lock (_calls)
                    return [.. _calls];
            }
        }

        public UploadLimits? this[string userId]
        {
            set {
                lock (_limits)
                    _limits[userId] = value;
            }
        }

        public ValueTask<UploadLimits?> GetLimitsAsync(HttpContext context, string userId, CancellationToken cancellationToken)
        {
            context.Request.Path.Value.ShouldNotBeNull().ShouldContain("/uploads");

            lock (_calls)
                _calls.Add(userId);

            if (Failure is not null)
                throw Failure;

            lock (_limits)
                return new(_limits.GetValueOrDefault(userId));
        }
    }
}
