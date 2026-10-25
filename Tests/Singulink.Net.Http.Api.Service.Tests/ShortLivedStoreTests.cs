using System.Buffers.Text;
using System.Collections;
using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The behavior every <see cref="IShortLivedStore"/> must have, checked against the memory store and against a store that stands in for shared storage.
/// </summary>
public abstract class ShortLivedStoreTests
{
    private const string Key = "test:key";

    protected static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    protected TestClock Clock { get; } = new();

    protected abstract IShortLivedStore CreateStore();

    [TestMethod]
    public async Task ShortLivedState_AddsGetsAndTakesStates()
    {
        var states = new ShortLivedState<TestState>(CreateStore(), "test:state:", TestJsonContext.Default.TestState, Lifetime);

        string id = await states.AddAsync(new TestState("user-1", 3));
        string other = await states.AddAsync(new TestState("user-2", 4));

        ShortLivedId.IsWellFormed(id).ShouldBeTrue();
        (await states.GetAsync(id)).ShouldBe(new TestState("user-1", 3));
        (await states.TakeAsync(id)).ShouldBe(new TestState("user-1", 3));
        (await states.TakeAsync(id)).ShouldBeNull();
        (await states.GetAsync(id)).ShouldBeNull();
        (await states.GetAsync(other)).ShouldBe(new TestState("user-2", 4));
    }

    [TestMethod]
    public async Task ShortLivedState_RemovesStates()
    {
        var states = new ShortLivedState<TestState>(CreateStore(), "test:state:", TestJsonContext.Default.TestState, Lifetime);

        string id = await states.AddAsync(new TestState("user-1", 3));
        string other = await states.AddAsync(new TestState("user-2", 4));

        await states.RemoveAsync(id);
        await states.RemoveAsync(id);

        (await states.GetAsync(id)).ShouldBeNull();
        (await states.GetAsync(other)).ShouldBe(new TestState("user-2", 4));
    }

    [TestMethod]
    public async Task ShortLivedState_ExpiresAfterLifetime()
    {
        var states = new ShortLivedState<TestState>(CreateStore(), "test:state:", TestJsonContext.Default.TestState, Lifetime);
        string id = await states.AddAsync(new TestState("user-1", 3));

        Clock.Advance(Lifetime - TimeSpan.FromSeconds(1));
        (await states.GetAsync(id)).ShouldNotBeNull();

        Clock.Advance(TimeSpan.FromSeconds(1));
        (await states.TakeAsync(id)).ShouldBeNull();
    }

    [TestMethod]
    public async Task ShortLivedState_IdThatIsNotAnId_IsNotLookedUp()
    {
        var store = CreateStore();
        var states = new ShortLivedState<TestState>(store, "test:state:", TestJsonContext.Default.TestState, Lifetime);
        await store.TryAddAsync("test:state:short", """{"userId":"user-1","count":3}""", Lifetime);

        (await states.GetAsync("short")).ShouldBeNull();
        (await states.TakeAsync("short")).ShouldBeNull();
        await states.RemoveAsync("short");
        (await store.GetAsync("test:state:short")).ShouldNotBeNull();
    }

    [TestMethod]
    public void ShortLivedState_InvalidArguments_Throw()
    {
        var store = CreateStore();
        var typeInfo = TestJsonContext.Default.TestState;

        Should.Throw<ArgumentException>(() => new ShortLivedState<TestState>(store, "httpapi:state:", typeInfo, Lifetime)).ParamName.ShouldBe("keyPrefix");
        Should.Throw<ArgumentException>(() => new ShortLivedState<TestState>(store, string.Empty, typeInfo, Lifetime));
        Should.Throw<ArgumentOutOfRangeException>(() => new ShortLivedState<TestState>(store, "test:state:", typeInfo, TimeSpan.Zero));
    }

    [TestMethod]
    public async Task TryAdd_AddsOnlyIfKeyHasNoValue()
    {
        var store = CreateStore();

        (await store.TryAddAsync(Key, "first", Lifetime)).ShouldBeTrue();
        (await store.TryAddAsync(Key, "second", Lifetime)).ShouldBeFalse();

        (await store.GetAsync(Key)).ShouldBe("first");
    }

    [TestMethod]
    public async Task TryAdd_AfterExpiryOrRemoval_Adds()
    {
        var store = CreateStore();

        await store.TryAddAsync(Key, "first", Lifetime);
        Clock.Advance(Lifetime);
        (await store.TryAddAsync(Key, "second", Lifetime)).ShouldBeTrue();
        (await store.GetAsync(Key)).ShouldBe("second");

        await store.RemoveAsync(Key);
        (await store.TryAddAsync(Key, "third", Lifetime)).ShouldBeTrue();
        (await store.GetAsync(Key)).ShouldBe("third");
    }

    [TestMethod]
    public void TryAdd_UnderConcurrency_SucceedsOnce()
    {
        var store = CreateStore();
        const int rounds = 100;

        var results = Concurrently.RunTasks(threads: 8, rounds, round => store.TryAddAsync($"test:{round}", "value", Lifetime).AsTask());

        foreach (var round in results)
            round.Count(added => added).ShouldBe(1);
    }

    [TestMethod]
    public async Task Get_DoesNotUseUpValue()
    {
        var store = CreateStore();
        await store.TryAddAsync(Key, "value", Lifetime);

        (await store.GetAsync(Key)).ShouldBe("value");
        (await store.GetAsync(Key)).ShouldBe("value");
        (await store.TakeAsync(Key)).ShouldBe("value");
    }

    [TestMethod]
    public async Task Take_ReturnsValueOnce()
    {
        var store = CreateStore();
        await store.TryAddAsync(Key, "value", Lifetime);

        (await store.TakeAsync(Key)).ShouldBe("value");
        (await store.TakeAsync(Key)).ShouldBeNull();
        (await store.GetAsync(Key)).ShouldBeNull();
    }

    [TestMethod]
    public async Task Take_UnderConcurrency_ReturnsValueAtMostOnce()
    {
        var store = CreateStore();
        const int rounds = 200;

        for (int round = 0; round < rounds; round++)
            await store.TryAddAsync($"test:{round}", $"value-{round}", Lifetime);

        var results = Concurrently.RunTasks(threads: 8, rounds, round => store.TakeAsync($"test:{round}").AsTask());

        for (int round = 0; round < rounds; round++)
            results[round].Where(v => v is not null).ShouldHaveSingleItem().ShouldBe($"value-{round}");
    }

    [TestMethod]
    public async Task Get_WhileTakenConcurrently_NeverReturnsTakenValue()
    {
        var store = CreateStore();
        const int rounds = 200;

        for (int round = 0; round < rounds; round++)
            await store.TryAddAsync($"test:{round}", "value", Lifetime);

        var results = Concurrently.RunTasks(threads: 4, rounds, async round => {
            // Every thread takes once and then gets: after its own take, a get must never see the value again.
            bool took = await store.TakeAsync($"test:{round}") is not null;
            bool gotAfterTake = await store.GetAsync($"test:{round}") is not null;
            return (took, gotAfterTake);
        });

        foreach (var round in results)
        {
            round.Count(r => r.took).ShouldBe(1);
            round.ShouldAllBe(r => !r.gotAfterTake);
        }
    }

    [TestMethod]
    public async Task Remove_RemovesValue()
    {
        var store = CreateStore();
        await store.TryAddAsync(Key, "value", Lifetime);

        await store.RemoveAsync(Key);
        await store.RemoveAsync("test:unknown");

        (await store.GetAsync(Key)).ShouldBeNull();
        (await store.TakeAsync(Key)).ShouldBeNull();
    }

    [TestMethod]
    public async Task UnknownKey_ReturnsNull()
    {
        var store = CreateStore();
        await store.TryAddAsync(Key, "value", Lifetime);

        (await store.GetAsync("test:unknown")).ShouldBeNull();
        (await store.TakeAsync("test:unknown")).ShouldBeNull();
        (await store.TakeAsync(string.Empty)).ShouldBeNull();
    }

    [TestMethod]
    public async Task Value_ExpiresAfterLifetime()
    {
        var store = CreateStore();
        await store.TryAddAsync("test:get", "value", Lifetime);
        await store.TryAddAsync("test:take", "value", Lifetime);

        Clock.Advance(Lifetime - TimeSpan.FromSeconds(1));
        (await store.GetAsync("test:get")).ShouldBe("value");

        Clock.Advance(TimeSpan.FromSeconds(1));
        (await store.GetAsync("test:get")).ShouldBeNull();
        (await store.TakeAsync("test:take")).ShouldBeNull();
    }

    [TestMethod]
    public async Task Increment_CountsFromZeroAndReadsAsDecimalText()
    {
        var store = CreateStore();

        (await store.IncrementAsync(Key, 1, Lifetime)).ShouldBe(1);
        (await store.IncrementAsync(Key, 5, Lifetime)).ShouldBe(6);
        (await store.IncrementAsync(Key, -2, Lifetime)).ShouldBe(4);
        (await store.GetAsync(Key)).ShouldBe("4");

        (await store.IncrementAsync("test:negative", -1, Lifetime)).ShouldBe(-1);
        (await store.GetAsync("test:negative")).ShouldBe("-1");
    }

    [TestMethod]
    public async Task Increment_RestartsExpiry()
    {
        var store = CreateStore();

        await store.IncrementAsync(Key, 1, Lifetime);
        Clock.Advance(Lifetime - TimeSpan.FromSeconds(1));
        await store.IncrementAsync(Key, 1, Lifetime);

        Clock.Advance(Lifetime - TimeSpan.FromSeconds(1));
        (await store.GetAsync(Key)).ShouldBe("2");

        Clock.Advance(TimeSpan.FromSeconds(1));
        (await store.GetAsync(Key)).ShouldBeNull();
        (await store.IncrementAsync(Key, 1, Lifetime)).ShouldBe(1);
    }

    [TestMethod]
    public void Increment_UnderConcurrency_CountsEveryIncrement()
    {
        var store = CreateStore();

        var results = Concurrently.RunTasks(threads: 8, rounds: 50, _ => store.IncrementAsync(Key, 1, Lifetime).AsTask());
        var counts = results.SelectMany(r => r).Order().ToList();

        counts.ShouldBe(Enumerable.Range(1, 400).Select(i => (long)i));
    }

    [TestMethod]
    public async Task Counter_CanBeTakenAndIsNotAddedOver()
    {
        var store = CreateStore();
        await store.IncrementAsync(Key, 3, Lifetime);

        (await store.TryAddAsync(Key, "value", Lifetime)).ShouldBeFalse();
        (await store.TakeAsync(Key)).ShouldBe("3");
        (await store.IncrementAsync(Key, 1, Lifetime)).ShouldBe(1);
    }

    [TestMethod]
    public async Task Increment_OnValueThatIsNotACount_Throws()
    {
        var store = CreateStore();
        await store.TryAddAsync(Key, "value", Lifetime);

        await Should.ThrowAsync<InvalidOperationException>(async () => await store.IncrementAsync(Key, 1, Lifetime));
        (await store.GetAsync(Key)).ShouldBe("value");
    }

    [TestMethod]
    public async Task NonPositiveLifetime_Throws()
    {
        var store = CreateStore();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await store.TryAddAsync(Key, "value", TimeSpan.Zero));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await store.IncrementAsync(Key, 1, TimeSpan.FromSeconds(-1)));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await store.AddAsync("test:", "value", TimeSpan.Zero));
    }

    [TestMethod]
    public async Task AddAsync_ReturnsUnique256BitKeysUnderPrefix()
    {
        var store = CreateStore();
        var keys = new List<string>();

        for (int i = 0; i < 100; i++)
            keys.Add(await store.AddAsync("test:prefix:", $"value-{i}", Lifetime));

        keys.Distinct().Count().ShouldBe(keys.Count);

        for (int i = 0; i < keys.Count; i++)
        {
            keys[i].Length.ShouldBe(43);
            Base64Url.IsValid(keys[i], out int decodedLength).ShouldBeTrue();
            decodedLength.ShouldBe(32);
            ShortLivedId.IsWellFormed(keys[i]).ShouldBeTrue();

            (await store.GetAsync(keys[i])).ShouldBeNull();
            (await store.GetAsync("test:prefix:" + keys[i])).ShouldBe($"value-{i}");
        }
    }

    [TestMethod]
    public async Task AcquirePermit_AcquiresUpToTheLimitInEachWindow()
    {
        var store = CreateStore();
        var window = TimeSpan.FromHours(1);
        var remaining = TimeSpan.FromTicks(window.Ticks - (Clock.GetUtcNow().UtcTicks % window.Ticks));

        for (int i = 0; i < 3; i++)
        {
            var acquired = await store.AcquirePermitAsync("test:emails:", "alex@example.com", 3, window, Clock);
            acquired.IsAcquired.ShouldBeTrue();
            acquired.RetryAfter.ShouldBeNull();
        }

        var refused = await store.AcquirePermitAsync("test:emails:", "alex@example.com", 3, window, Clock);
        refused.IsAcquired.ShouldBeFalse();
        refused.RetryAfter.ShouldBe(remaining);

        // Other keys and prefixes have counts of their own.
        (await store.AcquirePermitAsync("test:emails:", "sam@example.com", 3, window, Clock)).IsAcquired.ShouldBeTrue();
        (await store.AcquirePermitAsync("test:other:", "alex@example.com", 3, window, Clock)).IsAcquired.ShouldBeTrue();

        Clock.Advance(remaining - TimeSpan.FromSeconds(1));
        (await store.AcquirePermitAsync("test:emails:", "alex@example.com", 3, window, Clock)).RetryAfter.ShouldBe(TimeSpan.FromSeconds(1));

        Clock.Advance(TimeSpan.FromSeconds(1));
        (await store.AcquirePermitAsync("test:emails:", "alex@example.com", 3, window, Clock)).IsAcquired.ShouldBeTrue();
    }

    [TestMethod]
    public void AcquirePermit_UnderConcurrency_AcquiresExactlyTheLimit()
    {
        var store = CreateStore();

        // The clock only moves when a test advances it, so every attempt falls in the same window.
        var results = Concurrently.RunTasks(threads: 8, rounds: 25, _ => store.AcquirePermitAsync("test:", "key", 50, TimeSpan.FromMinutes(1), Clock).AsTask());

        results.Sum(r => r.Count(p => p.IsAcquired)).ShouldBe(50);
    }

    [TestMethod]
    public async Task AcquirePermit_InvalidArguments_Throw()
    {
        var store = CreateStore();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await store.AcquirePermitAsync("test:", "key", 0, TimeSpan.FromMinutes(1), Clock));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await store.AcquirePermitAsync("test:", "key", 1, TimeSpan.Zero, Clock));
    }

    [TestMethod]
    public async Task TypedStates_ThatCannotBeRead_AreTreatedAsMissing()
    {
        var store = CreateStore();
        await store.TryAddAsync("test:get", "not json", Lifetime);
        await store.TryAddAsync("test:take", """{"userId":"user-1","count":"many"}""", Lifetime);

        (await store.GetAsync("test:", "get", TestJsonContext.Default.TestState)).ShouldBeNull();
        (await store.TakeAsync("test:", "take", TestJsonContext.Default.TestState)).ShouldBeNull();

        // Taking the unreadable state still used it up.
        (await store.GetAsync("test:take")).ShouldBeNull();
    }

    [TestMethod]
    public async Task TypedStates_ThatCannotBeRead_AreLoggedToTheGivenLogger()
    {
        var store = CreateStore();
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var logger = loggerFactory.CreateLogger("Test");

        await store.TryAddAsync("test:get", "not json", Lifetime);
        await store.TryAddAsync("test:take", "not json", Lifetime);

        (await store.GetAsync("test:", "get", TestJsonContext.Default.TestState, logger)).ShouldBeNull();
        (await store.TakeAsync("test:", "take", TestJsonContext.Default.TestState, logger)).ShouldBeNull();

        logs.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("TestState could not be read", StringComparison.Ordinal)).ShouldBe(2);
    }

    [TestMethod]
    public async Task TypedStates_RoundTripAsJson()
    {
        var store = CreateStore();
        var state = new TestState("user-1", 42);

        string id = await store.AddAsync("test:typed:", state, TestJsonContext.Default.TestState, Lifetime);

        (await store.GetAsync("test:typed:" + id)).ShouldBe("""{"userId":"user-1","count":42}""");
        (await store.GetAsync("test:typed:", id, TestJsonContext.Default.TestState)).ShouldBe(state);
        (await store.TakeAsync("test:typed:", id, TestJsonContext.Default.TestState)).ShouldBe(state);
        (await store.TakeAsync("test:typed:", id, TestJsonContext.Default.TestState)).ShouldBeNull();
        (await store.GetAsync("test:typed:", id, TestJsonContext.Default.TestState)).ShouldBeNull();

        // A state under an ID of the app's choosing, which other prefixes do not see.
        (await store.TryAddAsync("test:chosen:", "user-1", state, TestJsonContext.Default.TestState, Lifetime)).ShouldBeTrue();
        (await store.TryAddAsync("test:chosen:", "user-1", state with { Count = 1 }, TestJsonContext.Default.TestState, Lifetime)).ShouldBeFalse();
        (await store.GetAsync("test:chosen:", "user-1", TestJsonContext.Default.TestState)).ShouldBe(state);
        (await store.GetAsync("test:other:", "user-1", TestJsonContext.Default.TestState)).ShouldBeNull();
    }

    [TestMethod]
    public async Task TypedStates_AreRemovedUnderTheirPrefix()
    {
        var store = CreateStore();
        var state = new TestState("user-1", 42);

        await store.TryAddAsync("test:chosen:", "user-1", state, TestJsonContext.Default.TestState, Lifetime);
        await store.TryAddAsync("test:other:", "user-1", state, TestJsonContext.Default.TestState, Lifetime);

        await store.RemoveAsync("test:chosen:", "user-1");
        await store.RemoveAsync("test:chosen:", "user-1");

        (await store.GetAsync("test:chosen:", "user-1", TestJsonContext.Default.TestState)).ShouldBeNull();
        (await store.GetAsync("test:other:", "user-1", TestJsonContext.Default.TestState)).ShouldBe(state);
    }
}

[PrefixTestClass]
public sealed class MemoryShortLivedStoreTests : ShortLivedStoreTests
{
    private const string SyncKey = "test:sync";

    [TestMethod]
    public async Task ExpiredEntries_AreSweptOutAsNewOnesAreWritten()
    {
        var store = new MemoryShortLivedStore(Clock);

        for (int i = 0; i < 10; i++)
            await store.TryAddAsync($"test:{i}", "value", TimeSpan.FromSeconds(30));

        await store.IncrementAsync("test:counter", 1, TimeSpan.FromSeconds(30));
        GetEntryCount(store).ShouldBe(11);

        Clock.Advance(TimeSpan.FromMinutes(2));
        await store.TryAddAsync("test:new", "value", TimeSpan.FromMinutes(5));

        GetEntryCount(store).ShouldBe(1);
    }

    [TestMethod]
    public void Operations_CompleteSynchronously()
    {
        var store = new MemoryShortLivedStore(Clock);

        store.TryAddAsync(SyncKey, "value", TimeSpan.FromMinutes(1)).IsCompletedSuccessfully.ShouldBeTrue();
        store.GetAsync(SyncKey).IsCompletedSuccessfully.ShouldBeTrue();
        store.IncrementAsync("test:counter", 1, TimeSpan.FromMinutes(1)).IsCompletedSuccessfully.ShouldBeTrue();
        store.TakeAsync(SyncKey).IsCompletedSuccessfully.ShouldBeTrue();
        store.RemoveAsync(SyncKey).IsCompletedSuccessfully.ShouldBeTrue();
    }

    // Each of these entries takes up 120 + 2 * (6 + 5) = 142 bytes, so 7 of them fit in 1000 bytes and an 8th does not.
    private const long SmallMaxSize = 1000;
    private const int EntriesThatFit = 7;

    [TestMethod]
    public async Task FullStore_RefusesNewValuesAndCounters()
    {
        var store = CreateStore(SmallMaxSize);
        await FillAsync(store);

        var ex = await Should.ThrowAsync<ServiceUnavailableApiException>(async () => await store.TryAddAsync("test:x", "value", Lifetime));
        ex.ErrorCode.ShouldBe(ApiErrorCodes.ShortLivedStoreFull);
        ex.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        ex.RetryAfter.ShouldBe(TimeSpan.FromMinutes(1));

        await Should.ThrowAsync<ServiceUnavailableApiException>(async () => await store.IncrementAsync("test:c", 1, Lifetime));
        await Should.ThrowAsync<ServiceUnavailableApiException>(async () => await store.AddAsync("test:", "value", Lifetime));
        (await store.GetAsync("test:x")).ShouldBeNull();
    }

    [TestMethod]
    public async Task FullStore_StillReadsTakesRemovesAndCounts()
    {
        var store = CreateStore(SmallMaxSize);
        await FillAsync(store, EntriesThatFit - 1);
        (await store.IncrementAsync("test:c", 1, Lifetime)).ShouldBe(1);

        (await store.GetAsync("test:0")).ShouldBe("value");
        (await store.IncrementAsync("test:c", 5, Lifetime)).ShouldBe(6);
        (await store.IncrementAsync("test:c", -1, Lifetime)).ShouldBe(5);

        // A key that already has a value is refused as usual rather than for lack of room.
        (await store.TryAddAsync("test:0", "value", Lifetime)).ShouldBeFalse();

        (await store.TakeAsync("test:0")).ShouldBe("value");
        await store.RemoveAsync("test:1");

        // The room they took up is free again.
        (await store.TryAddAsync("test:x", "value", Lifetime)).ShouldBeTrue();
        (await store.TryAddAsync("test:y", "value", Lifetime)).ShouldBeTrue();
        await Should.ThrowAsync<ServiceUnavailableApiException>(async () => await store.TryAddAsync("test:z", "value", Lifetime));
    }

    [TestMethod]
    public async Task FullStore_SweepsOutExpiredEntriesBeforeRefusing()
    {
        var store = CreateStore(SmallMaxSize);

        for (int i = 0; i < EntriesThatFit; i++)
            await store.TryAddAsync($"test:{i}", "value", TimeSpan.FromSeconds(30));

        await Should.ThrowAsync<ServiceUnavailableApiException>(async () => await store.TryAddAsync("test:x", "value", Lifetime));

        // Well before the regular sweep is due.
        Clock.Advance(TimeSpan.FromSeconds(30));

        for (int i = 0; i < EntriesThatFit; i++)
            (await store.TryAddAsync($"test:new-{i}", "v", Lifetime)).ShouldBeTrue();

        GetEntryCount(store).ShouldBe(EntriesThatFit);
    }

    [TestMethod]
    public async Task FullStore_CountsExpiredValuesItReplaces()
    {
        var store = CreateStore(SmallMaxSize);
        await FillAsync(store, EntriesThatFit, TimeSpan.FromSeconds(30));
        Clock.Advance(TimeSpan.FromSeconds(30));

        // Replacing expired values takes no more room than they did.
        for (int i = 0; i < EntriesThatFit; i++)
            (await store.TryAddAsync($"test:{i}", "other", Lifetime)).ShouldBeTrue();

        // And so does a counter that replaces an expired value.
        Clock.Advance(Lifetime);
        (await store.IncrementAsync("test:0", 1, Lifetime)).ShouldBe(1);
    }

    [TestMethod]
    public async Task ValueLargerThanTheStore_IsRefused()
    {
        var store = CreateStore(SmallMaxSize);

        await Should.ThrowAsync<ServiceUnavailableApiException>(async () => await store.TryAddAsync("test:big", new string('x', 500), Lifetime));
        (await store.TryAddAsync("test:small", "value", Lifetime)).ShouldBeTrue();
    }

    [TestMethod]
    public async Task FullStore_LogsAWarningAtMostOnceAMinute()
    {
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var store = CreateStore(SmallMaxSize, loggerFactory.CreateLogger<MemoryShortLivedStore>());
        await FillAsync(store, EntriesThatFit, TimeSpan.FromHours(1));

        for (int i = 0; i < 3; i++)
            await Should.ThrowAsync<ServiceUnavailableApiException>(async () => await store.TryAddAsync("test:x", "value", Lifetime));

        var warning = logs.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldStartWith("The short-lived store is full: its values and counters take up about 994 of the 1000 bytes allowed");

        Clock.Advance(TimeSpan.FromMinutes(1));
        await Should.ThrowAsync<ServiceUnavailableApiException>(async () => await store.IncrementAsync("test:c", 1, Lifetime));

        logs.Entries.Count.ShouldBe(2);
    }

    [TestMethod]
    public void FullStore_UnderConcurrency_KeepsWithinItsSize()
    {
        var store = CreateStore(100_000);

        var results = Concurrently.RunTasks(threads: 8, rounds: 200, async round => {
            try
            {
                return await store.TryAddAsync($"test:{round}:{Guid.NewGuid():N}", "value", Lifetime);
            }
            catch (ServiceUnavailableApiException)
            {
                return false;
            }
        });

        // Each entry takes up 120 + 2 * (~40 + 5) bytes, so about 470 of the 1600 fit.
        int added = results.Sum(r => r.Count(a => a));
        added.ShouldBeInRange(400, 520);
        GetEntryCount(store).ShouldBe(added);
    }

    [TestMethod]
    public async Task AcquirePermit_WithTheSystemClock_Acquires()
    {
        var store = new MemoryShortLivedStore(TimeProvider.System);
        var window = TimeSpan.FromDays(365);

        (await store.AcquirePermitAsync("test:", "key", 1, window, TimeProvider.System)).IsAcquired.ShouldBeTrue();
        (await store.AcquirePermitAsync("test:", "key", 1, window, TimeProvider.System)).IsAcquired.ShouldBeFalse();
    }

    [TestMethod]
    public void Options_DefaultsAndInvalidValues()
    {
        var options = new MemoryShortLivedStoreOptions();

        options.MaxSize.ShouldBe(256L * 1024 * 1024);
        MemoryShortLivedStoreOptions.DefaultMaxSize.ShouldBe(256L * 1024 * 1024);

        Should.Throw<ArgumentOutOfRangeException>(() => options.MaxSize = 0);
        Should.Throw<ArgumentOutOfRangeException>(() => options.MaxSize = -1);
    }

    [TestMethod]
    public async Task AddShortLivedStore_WithOptions_ConfiguresTheMemoryStore()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddShortLivedStore(o => o.MaxSize = SmallMaxSize);
        services.AddSignInCodes();

        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IShortLivedStore>().ShouldBeOfType<MemoryShortLivedStore>();

        await FillAsync(store);
        await Should.ThrowAsync<ServiceUnavailableApiException>(async () => await store.TryAddAsync("test:x", "value", Lifetime));
    }

    [TestMethod]
    public async Task AddShortLivedStore_WithoutOptions_UsesTheDefaultSize()
    {
        var services = new ServiceCollection();
        services.AddShortLivedStore();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<MemoryShortLivedStoreOptions>>().Value.MaxSize.ShouldBe(MemoryShortLivedStoreOptions.DefaultMaxSize);
        (await provider.GetRequiredService<IShortLivedStore>().TryAddAsync("test:x", new string('x', 100_000), Lifetime)).ShouldBeTrue();
    }

    protected override IShortLivedStore CreateStore() => new MemoryShortLivedStore(Clock);

    private MemoryShortLivedStore CreateStore(long maxSize, ILogger<MemoryShortLivedStore>? logger = null)
    {
        var options = Options.Create(new MemoryShortLivedStoreOptions { MaxSize = maxSize });
        return new MemoryShortLivedStore(Clock, options, logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MemoryShortLivedStore>.Instance);
    }

    private static async Task FillAsync(IShortLivedStore store, int count = EntriesThatFit, TimeSpan? lifetime = null)
    {
        for (int i = 0; i < count; i++)
            (await store.TryAddAsync($"test:{i}", "value", lifetime ?? Lifetime)).ShouldBeTrue();
    }

    // Counts the entries the store holds, including expired ones that were not swept out yet.
    private static int GetEntryCount(MemoryShortLivedStore store)
    {
        var field = typeof(MemoryShortLivedStore).GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic).ShouldNotBeNull();
        return ((ICollection)field.GetValue(store).ShouldNotBeNull()).Count;
    }
}

[PrefixTestClass]
public sealed class SharedShortLivedStoreTests : ShortLivedStoreTests
{
    [TestMethod]
    public async Task AcquirePermit_KeepsTheKeyOutOfTheStore()
    {
        var store = new SharedShortLivedStore(Clock);
        await store.AcquirePermitAsync("test:emails:", "alex@example.com", 3, TimeSpan.FromMinutes(10), Clock);

        string key = store.Keys.ShouldHaveSingleItem();
        key.ShouldStartWith("test:emails:");
        key.ShouldNotContain("alex");
        (await store.GetAsync(key)).ShouldBe("1");
    }

    protected override IShortLivedStore CreateStore() => new SharedShortLivedStore(Clock);
}

public sealed record TestState(string UserId, int Count);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TestState))]
internal sealed partial class TestJsonContext : JsonSerializerContext
{
}
