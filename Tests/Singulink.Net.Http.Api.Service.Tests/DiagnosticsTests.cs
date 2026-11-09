using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Tests of the toolkit's health checks, metrics and the usage of its stores.
/// </summary>
[PrefixTestClass]
public sealed class DiagnosticsTests
{
    private const string Pattern = "/folders/{folderId}/files";

    [TestMethod]
    public void NoCheck_IsAdded_UnlessTheAppAddsIt()
    {
        var services = new ServiceCollection();
        services.AddSignInCodes();
        services.AddHealthChecks();

        GetCheckNames(services).ShouldBeEmpty();
    }

    [TestMethod]
    public void AddHttpApiChecks_AddsTheChecksOfTheRegisteredFeatures()
    {
        var services = new ServiceCollection();
        services.AddShortLivedStore();
        services.AddHealthChecks().AddHttpApiChecks();

        GetCheckNames(services).ShouldBe(["httpapi-short-lived-store"]);

        services = new ServiceCollection();
        services.AddDeviceLinks();
        services.AddHttpSessionHandling<TestSessionToken, ScopedInMemorySessionStore>();
        services.AddChunkedUploads<TestSessionToken>();
        services.AddRateLimiter(o => o.AddClientFixedWindowLimiter("api", 10, TimeSpan.FromMinutes(1)));
        services.AddHealthChecks().AddHttpApiChecks();

        GetCheckNames(services).ShouldBe([
            "httpapi-short-lived-store",
            "httpapi-short-lived-store-sign-in",
            "httpapi-hub-sessions",
            "httpapi-uploads",
            "httpapi-short-lived-store-uploads",
            "httpapi-rate-limiting",
            "httpapi-short-lived-store-rate-limiting",
        ]);
    }

    [TestMethod]
    public void FeatureChecks_HaveTheirOwnNamesTagsAndFailureStatus_WhenGiven()
    {
        var services = new ServiceCollection();
        services.AddShortLivedStore();
        services.AddHealthChecks().AddShortLivedStoreCheck(ShortLivedStoreKeys.Uploads, "uploads-store", HealthStatus.Degraded, ["ready"]);

        var registration = GetRegistrations(services).ShouldHaveSingleItem();
        (registration.Name, registration.FailureStatus).ShouldBe(("uploads-store", HealthStatus.Degraded));
        registration.Tags.ShouldBe(["ready"]);

        services = new ServiceCollection();
        services.AddHealthChecks().AddClientRateLimitsCheck();
        GetRegistrations(services).ShouldHaveSingleItem().Tags.ShouldBe(["httpapi", "rate-limiting"], ignoreOrder: true);
    }

    [TestMethod]
    public async Task MemoryStore_ReportsItsUsage_AndRefusedWrites()
    {
        var clock = new TestClock();
        var store = new MemoryShortLivedStore(clock, Options.Create(new MemoryShortLivedStoreOptions { MaxSize = 1000 }), NullLogger());

        (await store.TryAddAsync("test:a", new string('a', 100), TimeSpan.FromMinutes(1))).ShouldBeTrue();
        await Should.ThrowAsync<ServiceUnavailableApiException>(async () => await store.TryAddAsync("test:b", new string('b', 1000), TimeSpan.FromMinutes(1)));

        var usage = await store.GetUsageAsync();
        (usage.EntryCount, usage.MaxBytes, usage.RefusedWrites).ShouldBe((1L, 1000L, 1L));
        usage.UsedBytes.ShouldBe(120 + (2 * ("test:a".Length + 100)));
        usage.LastRefusedWriteUtc.ShouldBe(clock.GetUtcNow());
    }

    [TestMethod]
    public async Task StoreCheck_IsHealthy_AndDegradedWhenNearlyFull_OrWhileItRefusesWrites()
    {
        var clock = new TestClock();
        var store = new MemoryShortLivedStore(clock, Options.Create(new MemoryShortLivedStoreOptions { MaxSize = 1000 }), NullLogger());

        var healthy = await CheckStoreAsync(store, clock);
        healthy.Status.ShouldBe(HealthStatus.Healthy);
        healthy.Data[HttpApiHealthCheckData.ShortLivedStore.StoreType].ShouldBe(nameof(MemoryShortLivedStore));
        healthy.Data[HttpApiHealthCheckData.ShortLivedStore.SharingMode].ShouldBe("ProcessLocal");
        healthy.Data[HttpApiHealthCheckData.ShortLivedStore.MaxBytes].ShouldBe(1000L);

        // 120 bytes and 2 per character: 400 characters of key and value take up 920 bytes.
        await store.TryAddAsync("test:a", new string('a', 400 - "test:a".Length), TimeSpan.FromMinutes(1));
        (await CheckStoreAsync(store, clock)).Status.ShouldBe(HealthStatus.Degraded);

        // A store that refuses writes protects itself, which is degraded whatever the failure status.
        await Should.ThrowAsync<ServiceUnavailableApiException>(async () => await store.TryAddAsync("test:b", "b", TimeSpan.FromMinutes(1)));
        var full = await CheckStoreAsync(store, clock);
        (full.Status, full.Description).ShouldBe((HealthStatus.Degraded, "The short-lived store is full and refuses writes."));
        full.Data[HttpApiHealthCheckData.ShortLivedStore.RefusedWrites].ShouldBe(1L);
        (await CheckStoreAsync(store, clock, HealthStatus.Degraded)).Status.ShouldBe(HealthStatus.Degraded);

        // A minute after the last refusal, the store is no longer taken as refusing writes.
        clock.Advance(TimeSpan.FromMinutes(1));
        (await CheckStoreAsync(store, clock)).Description.ShouldBe("The short-lived store is more than 90% full.");
    }

    [TestMethod]
    public async Task StoreCheck_OfAStoreThatCantTellItsSize_IsHealthy_WithoutSayingItHasRoom()
    {
        var result = await CheckStoreAsync(new SharedShortLivedStore(TimeProvider.System), TimeProvider.System);

        (result.Status, result.Description).ShouldBe((HealthStatus.Healthy, "The short-lived store can be read."));
    }

    [TestMethod]
    public async Task AddHttpApiChecks_ReadsTheAppsStoreOnce_AndAKeyWithAStoreOfItsOwn()
    {
        var appStore = new InterceptingShortLivedStore(new SharedShortLivedStore(TimeProvider.System));
        var signInStore = new InterceptingShortLivedStore(new SharedShortLivedStore(TimeProvider.System));

        // The checks run concurrently.
        ConcurrentQueue<string> reads = [];
        appStore.Before = (operation, _) => reads.Enqueue("app " + operation);
        signInStore.Before = (operation, _) => reads.Enqueue("sign-in " + operation);

        var services = new ServiceCollection();
        services.AddSingleton<IShortLivedStore>(appStore);
        services.AddKeyedSingleton<IShortLivedStore>(ShortLivedStoreKeys.SignIn, signInStore);
        services.AddSignInCodes();
        services.AddHttpSessionHandling<TestSessionToken, ScopedInMemorySessionStore>();
        services.AddChunkedUploads<TestSessionToken>();
        services.AddHealthChecks().AddHttpApiChecks().AddShortLivedStoreCheck(ShortLivedStoreKeys.Uploads, "app-uploads-store");

        await using var provider = services.BuildServiceProvider();
        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(r => r.Tags.Contains("short-lived-store"));

        // The check of the app's store reads it, and so do a check that the app adds itself and the check of a key with a store of its own.
        reads.ShouldBe(["app GetUsage", "app GetUsage", "sign-in GetUsage"], ignoreOrder: true);

        var uploads = report.Entries["httpapi-short-lived-store-uploads"];
        uploads.Status.ShouldBe(HealthStatus.Healthy);
        uploads.Description.ShouldBe("The store of this key is the app's store, which the check 'httpapi-short-lived-store' reads.");
        uploads.Data[HttpApiHealthCheckData.ShortLivedStore.StoreType].ShouldBe(nameof(InterceptingShortLivedStore));
        uploads.Data[HttpApiHealthCheckData.ShortLivedStore.SharingMode].ShouldBe("Shared");
        uploads.Data.ContainsKey(HttpApiHealthCheckData.ShortLivedStore.RefusedWrites).ShouldBeFalse();
        report.Entries["app-uploads-store"].Status.ShouldBe(HealthStatus.Healthy);
        report.Entries["httpapi-short-lived-store-sign-in"].Status.ShouldBe(HealthStatus.Healthy);
    }

    [TestMethod]
    public async Task StoreCheck_OfAStoreThatCantBeRead_ReportsItsFailureStatus_WithTheException()
    {
        var store = new InterceptingShortLivedStore(new MemoryShortLivedStore(TimeProvider.System));
        store.FailOnce("GetUsage", string.Empty);

        var result = await CheckStoreAsync(store, TimeProvider.System, HealthStatus.Degraded);

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Exception.ShouldBeOfType<InvalidOperationException>();
        result.Description.ShouldNotBeNull().ShouldContain(result.Exception.Message);
        result.Data[HttpApiHealthCheckData.Error].ShouldBe(result.Exception.Message);
    }

    [TestMethod]
    public async Task UploadsCheck_ReportsTheStoreTheCountsAndTheLastCleanup()
    {
        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, (UploadedFile file) => new Note("done")),
            configureServices: services => services.AddHealthChecks().AddHttpApiChecks());

        var session = await host.CreateUploadAsync("a", 10);
        await host.Services.GetRequiredService<ChunkedUploads>().CleanUpAsync(CancellationToken.None);

        var data = (await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync()).Entries["httpapi-uploads"].Data;

        data[HttpApiHealthCheckData.Uploads.OpenUploadsSize].ShouldBe(10L);
        data[HttpApiHealthCheckData.Uploads.LastCleanupOpenUploads].ShouldBe(1L);
        data[HttpApiHealthCheckData.Uploads.LastCleanupUsers].ShouldBe(1L);
        data[HttpApiHealthCheckData.Uploads.StoreType].ShouldBe("DirectoryUploadStore");
        data[HttpApiHealthCheckData.Uploads.StoreLocation].ShouldBe(host.Directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        ((long)data[HttpApiHealthCheckData.Uploads.StoreAvailableBytes]).ShouldBePositive();
        data[HttpApiHealthCheckData.Uploads.RunningHandlers].ShouldBe(0L);
        session.Id.ShouldNotBeNull();
    }

    [TestMethod]
    public async Task UploadsCheck_IsHealthy_WithRoomForTheLimit_DegradedWithLess_AndFails_WhileTheStoreCantBeRead()
    {
        var store = new MemoryUploadStore(TimeProvider.System) { Usage = new UploadStoreUsage { AvailableBytes = 1000, TotalBytes = 2000 } };
        bool fails = false;
        store.BeforeOperation = operation => {
            if (fails && operation is "GetUsage")
                throw new IOException("The share can't be reached.");
        };

        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, (UploadedFile file) => new Note("done")),
            options => options.MaxTotalSize = 1000,
            configureServices: services => {
                services.AddSingleton<IUploadStore>(store);
                services.AddHealthChecks().AddChunkedUploadsCheck();
            });

        var health = host.Services.GetRequiredService<HealthCheckService>();
        var healthy = (await health.CheckHealthAsync()).Entries["httpapi-uploads"];
        healthy.Status.ShouldBe(HealthStatus.Healthy);
        healthy.Data[HttpApiHealthCheckData.Uploads.MaxTotalSize].ShouldBe(1000L);
        healthy.Data[HttpApiHealthCheckData.Uploads.StoreTotalBytes].ShouldBe(2000L);
        healthy.Data.ContainsKey(HttpApiHealthCheckData.Uploads.StoreLocation).ShouldBeFalse();

        await host.CreateUploadAsync("a", 10);
        store.Usage = new UploadStoreUsage { AvailableBytes = 100 };
        var degraded = (await health.CheckHealthAsync()).Entries["httpapi-uploads"];
        degraded.Status.ShouldBe(HealthStatus.Degraded);
        degraded.Description.ShouldBe(
            "The upload store has less space available (100 bytes) than the open uploads can still take up under " +
            "ChunkedUploadOptions.MaxTotalSize (990 bytes).");

        fails = true;
        var failed = (await health.CheckHealthAsync()).Entries["httpapi-uploads"];
        failed.Status.ShouldBe(HealthStatus.Unhealthy);
        failed.Exception.ShouldBeOfType<IOException>();
        failed.Description.ShouldNotBeNull().ShouldContain("The share can't be reached.");
    }

    [TestMethod]
    public async Task UploadsCheck_IsDegraded_WhileTheOpenUploadsHaveTakenUpTheMaxTotalSize()
    {
        var store = new MemoryUploadStore(TimeProvider.System) { Usage = new UploadStoreUsage { AvailableBytes = 1000 } };

        await using var host = await UploadTestHost.StartAsync(
            app => app.MapChunkedUpload(Pattern, (UploadedFile file) => new Note("done")),
            options => options.MaxTotalSize = 100,
            configureServices: services => {
                services.AddSingleton<IUploadStore>(store);
                services.AddHealthChecks().AddChunkedUploadsCheck();
            });

        var health = host.Services.GetRequiredService<HealthCheckService>();
        await host.CreateUploadAsync("a", 99);
        (await health.CheckHealthAsync()).Entries["httpapi-uploads"].Status.ShouldBe(HealthStatus.Healthy);

        await host.CreateUploadAsync("b", 1);
        var degraded = (await health.CheckHealthAsync()).Entries["httpapi-uploads"];
        degraded.Status.ShouldBe(HealthStatus.Degraded);
        degraded.Description.ShouldBe("The open uploads have taken up ChunkedUploadOptions.MaxTotalSize, so new uploads are refused.");
        degraded.Data[HttpApiHealthCheckData.Uploads.OpenUploadsSize].ShouldBe(100L);
    }

    [TestMethod]
    public async Task UploadsCheck_WithoutAMaxTotalSize_IsDegraded_WithLessRoomThanTheLargestUploadOfTheEndpoints()
    {
        var store = new MemoryUploadStore(TimeProvider.System) { Usage = new UploadStoreUsage { AvailableBytes = 1000 } };

        await using var host = await UploadTestHost.StartAsync(
            app => {
                app.MapChunkedUpload(Pattern, (UploadedFile file) => new Note("done"));
                app.MapChunkedUpload("/large", (UploadedFile file) => new Note("done"), o => o.MaxSize = 1500);
            },
            options => options.MaxSize = 500,
            configureServices: services => {
                services.AddSingleton<IUploadStore>(store);
                services.AddHealthChecks().AddChunkedUploadsCheck();
            });

        var health = host.Services.GetRequiredService<HealthCheckService>();

        var degraded = (await health.CheckHealthAsync()).Entries["httpapi-uploads"];
        degraded.Status.ShouldBe(HealthStatus.Degraded);
        degraded.Description.ShouldBe(
            "The upload store has less space available (1000 bytes) than the largest upload that the service accepts (1500 bytes).");

        store.Usage = new UploadStoreUsage { AvailableBytes = 1500 };
        var healthy = (await health.CheckHealthAsync()).Entries["httpapi-uploads"];
        (healthy.Status, healthy.Description).ShouldBe((HealthStatus.Healthy, "The upload store has room for the largest upload that the service accepts."));

        // Without its available space, the store has no space rule.
        store.Usage = UploadStoreUsage.Unknown;
        var unknown = (await health.CheckHealthAsync()).Entries["httpapi-uploads"];
        (unknown.Status, unknown.Description).ShouldBe((HealthStatus.Healthy, "The upload store can be read."));
    }

    [TestMethod]
    public async Task DirectoryStore_ReadsTheSpaceOfTheNearestDirectory_WhileASharedDirectoryDoesntExist()
    {
        string parent = Path.Combine(Path.GetTempPath(), "httpapi-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);

        try
        {
            var root = TestPaths.ToDirectory(parent).CombineDirectory("uploads").CombineDirectory("shared");
            using var store = new DirectoryUploadStore(new(root, StoreSharingMode.Shared), TimeProvider.System, NullLoggerFactory.Instance);

            var usage = await store.GetUsageAsync();
            usage.Location.ShouldBe(root.PathDisplay);
            usage.AvailableBytes.ShouldNotBeNull().ShouldBePositive();
            usage.TotalBytes.ShouldNotBeNull().ShouldBePositive();
            store.TryGetAvailableBytes().ShouldNotBeNull().ShouldBePositive();
            root.Exists.ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [TestMethod]
    public async Task RateLimitsCheck_SaysWhenNoLimitHasCounted_AndReportsEachPolicy()
    {
        await using var host = await TestWebHost.StartAsync(
            app => {
                app.UseRateLimiter();
                app.MapGet("/limited", () => "ok").RequireRateLimiting("api");
            },
            configureServices: services => {
                services.AddRateLimiter(o => o.AddClientFixedWindowLimiter("api", 1, TimeSpan.FromMinutes(1)));
                services.AddHealthChecks().AddHttpApiChecks();
            });

        var health = host.App.Services.GetRequiredService<HealthCheckService>();
        var before = (await health.CheckHealthAsync()).Entries["httpapi-rate-limiting"];
        (before.Status, before.Description).ShouldBe((HealthStatus.Healthy, "No per-client limit has counted a request yet."));

        (await host.Client.GetAsync("/limited")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await host.Client.GetAsync("/limited")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        var after = (await health.CheckHealthAsync()).Entries["httpapi-rate-limiting"];
        after.Status.ShouldBe(HealthStatus.Healthy);
        after.Data["api." + HttpApiHealthCheckData.RateLimiting.PermitLimit].ShouldBe(1L);
        after.Data["api." + HttpApiHealthCheckData.RateLimiting.Rejected].ShouldBe(1L);
        after.Data["api." + HttpApiHealthCheckData.RateLimiting.NotCounted].ShouldBe(0L);
    }

    [TestMethod]
    public async Task RateLimitsCheck_IsDegraded_WhenTheStoreCouldntCountARequestLately()
    {
        var clock = new TestClock();
        var store = new MemoryShortLivedStore(clock, Options.Create(new MemoryShortLivedStoreOptions { MaxSize = 1 }), NullLogger());

        await using var host = await TestWebHost.StartAsync(
            app => {
                app.UseRateLimiter();
                app.MapGet("/limited", () => "ok").RequireRateLimiting("api");
            },
            configureServices: services => {
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<IShortLivedStore>(store);
                services.AddRateLimiter(o => o.AddClientFixedWindowLimiter("api", 5, TimeSpan.FromMinutes(1)));
                services.AddHealthChecks().AddClientRateLimitsCheck();
            });

        (await host.Client.GetAsync("/limited")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        var health = host.App.Services.GetRequiredService<HealthCheckService>();
        var result = (await health.CheckHealthAsync()).Entries["httpapi-rate-limiting"];
        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Data["api." + HttpApiHealthCheckData.RateLimiting.NotCounted].ShouldBe(1L);

        clock.Advance(TimeSpan.FromMinutes(5));
        (await health.CheckHealthAsync()).Entries["httpapi-rate-limiting"].Status.ShouldBe(HealthStatus.Healthy);
    }

    [TestMethod]
    public async Task HubSessionsCheck_ReportsEachTokenType()
    {
        await using var host = await TestWebHost.StartAsync(
            app => { },
            configureServices: services => {
                services.AddAllowedOrigins("example.com");
                services.AddHttpSessionHandling<TestSessionToken, ScopedInMemorySessionStore>();
                services.AddHealthChecks().AddHubSessionsCheck();
            });

        var result = (await host.App.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync()).Entries["httpapi-hub-sessions"];

        result.Status.ShouldBe(HealthStatus.Healthy);
        result.Data[nameof(TestSessionToken) + "." + HttpApiHealthCheckData.HubSessions.TrackedConnections].ShouldBe(0L);
        result.Data.ContainsKey(nameof(TestSessionToken) + "." + HttpApiHealthCheckData.HubSessions.LastCheck).ShouldBeFalse();
    }

    [TestMethod]
    public async Task Metrics_CountUploadsAndCancellations()
    {
        await using var host = await UploadTestHost.StartAsync(app => app.MapChunkedUpload(Pattern, (UploadedFile file) => new Note("done")));
        using var created = new MetricCollector<long>(host.Services.GetRequiredService<IMeterFactory>(), HttpApiMetrics.MeterName, "httpapi.upload.created");
        using var ended = new MetricCollector<long>(host.Services.GetRequiredService<IMeterFactory>(), HttpApiMetrics.MeterName, "httpapi.upload.ended");
        using var cancellations = new MetricCollector<long>(
            host.Services.GetRequiredService<IMeterFactory>(), HttpApiMetrics.MeterName, "httpapi.upload.cancellations");

        await host.UploadAsync(UploadTestHost.CreateFile(10));
        var aborted = await host.CreateUploadAsync("b", 10);
        (await host.AbortAsync(aborted)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        created.GetMeasurementSnapshot().Select(m => (m.Value, m.Tags[HttpApiMetrics.UploadKindTag])).ShouldBe([(1L, "chunked"), (1L, "chunked")]);
        ended.GetMeasurementSnapshot().Select(m => m.Tags[HttpApiMetrics.UploadEndTag]).ShouldBe(["succeeded", "deleted"]);
        cancellations.GetMeasurementSnapshot().ShouldHaveSingleItem().Tags[HttpApiMetrics.UploadCancellationTag].ShouldBe("deleted");
    }

    [TestMethod]
    public async Task Metrics_ReportTheMemoryStoresOfTheKeys_AndRateLimitRejections()
    {
        await using var host = await TestWebHost.StartAsync(
            app => {
                app.UseRateLimiter();
                app.MapGet("/limited", () => "ok").RequireRateLimiting("api");
            },
            configureServices: services => {
                services.AddShortLivedStore();
                services.AddRateLimiter(o => o.AddClientFixedWindowLimiter("api", 1, TimeSpan.FromMinutes(1)));
            });

        var meterFactory = host.App.Services.GetRequiredService<IMeterFactory>();
        using var rejections = new MetricCollector<long>(meterFactory, HttpApiMetrics.MeterName, "httpapi.rate_limiting.rejections");
        using var entries = new MetricCollector<long>(meterFactory, HttpApiMetrics.MeterName, "httpapi.short_lived_store.entry.count");

        (await host.Client.GetAsync("/limited")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await host.Client.GetAsync("/limited")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        var rejection = rejections.GetMeasurementSnapshot().ShouldHaveSingleItem();
        (rejection.Tags[HttpApiMetrics.PolicyTag], rejection.Tags[HttpApiMetrics.RateLimitReasonTag]).ShouldBe(("api", "limit"));

        entries.RecordObservableInstruments();
        var store = entries.GetMeasurementSnapshot().Where(m => (string?)m.Tags[HttpApiMetrics.StoreKeyTag] == ShortLivedStoreKeys.RateLimiting);
        store.ShouldHaveSingleItem().Value.ShouldBe(1);
    }

    [TestMethod]
    public async Task GlobalLimiter_HasNoPolicy_InTheMetricsAndTheCheck_SoAPolicyNamedGlobalIsApart()
    {
        await using var host = await TestWebHost.StartAsync(
            app => {
                app.UseRateLimiter();
                app.MapGet("/limited", () => "ok").RequireRateLimiting("global");
            },
            configureServices: services => {
                services.AddRateLimiter(o => o
                    .SetClientFixedWindowGlobalLimiter(1, TimeSpan.FromMinutes(1))
                    .AddClientFixedWindowLimiter("global", 5, TimeSpan.FromMinutes(1)));
                services.AddHealthChecks().AddClientRateLimitsCheck();
            });

        using var rejections = new MetricCollector<long>(
            host.App.Services.GetRequiredService<IMeterFactory>(), HttpApiMetrics.MeterName, "httpapi.rate_limiting.rejections");

        (await host.Client.GetAsync("/limited")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await host.Client.GetAsync("/limited")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        var rejection = rejections.GetMeasurementSnapshot().ShouldHaveSingleItem();
        rejection.Tags.ContainsKey(HttpApiMetrics.PolicyTag).ShouldBeFalse();
        rejection.Tags[HttpApiMetrics.RateLimitReasonTag].ShouldBe("limit");

        var data = (await host.App.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync()).Entries["httpapi-rate-limiting"].Data;
        data[HttpApiHealthCheckData.RateLimiting.PermitLimit].ShouldBe(1L);
        data[HttpApiHealthCheckData.RateLimiting.Rejected].ShouldBe(1L);
        data["global." + HttpApiHealthCheckData.RateLimiting.PermitLimit].ShouldBe(5L);
        data["global." + HttpApiHealthCheckData.RateLimiting.Rejected].ShouldBe(0L);
    }

    [TestMethod]
    public async Task ClientLimiter_ReportsItsStatistics_WithoutTheStore()
    {
        var clock = new TestClock();
        var store = new MemoryShortLivedStore(clock);
        var counters = new RateLimitPolicyCounters("api", 2, TimeSpan.FromMinutes(1));
        var limiter = new ClientFixedWindowRateLimiter(
            store, clock, "test:", 2, TimeSpan.FromMinutes(1), 0, rejectionMessage: null, countSynchronously: true, counters, metrics: null);

        limiter.GetStatistics().ShouldNotBeNull().CurrentAvailablePermits.ShouldBe(2);

        limiter.AttemptAcquire().IsAcquired.ShouldBeTrue();
        limiter.AttemptAcquire().IsAcquired.ShouldBeTrue();
        limiter.AttemptAcquire().IsAcquired.ShouldBeFalse();

        var statistics = limiter.GetStatistics().ShouldNotBeNull();
        (statistics.CurrentAvailablePermits, statistics.TotalSuccessfulLeases, statistics.TotalFailedLeases).ShouldBe((0L, 2L, 1L));

        // A synchronous attempt that fails is made again asynchronously, which decides, so only that refusal is counted for the policy.
        (counters.Rejected, counters.HasCounted).ShouldBe((0L, true));
        (await limiter.AcquireAsync()).IsAcquired.ShouldBeFalse();
        counters.Rejected.ShouldBe(1L);

        // A window that the limiter hasn't counted in has the whole limit.
        limiter.GetStatistics().ShouldNotBeNull().TotalFailedLeases.ShouldBe(2);
        clock.Advance(TimeSpan.FromMinutes(1));
        limiter.GetStatistics().ShouldNotBeNull().CurrentAvailablePermits.ShouldBe(2);
    }

    private static Microsoft.Extensions.Logging.ILogger<MemoryShortLivedStore> NullLogger() =>
        NullLogger<MemoryShortLivedStore>.Instance;

    private static List<HealthCheckRegistration> GetRegistrations(ServiceCollection services)
    {
        using var provider = services.BuildServiceProvider();
        return [.. provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations];
    }

    private static List<string> GetCheckNames(ServiceCollection services) => [.. GetRegistrations(services).Select(r => r.Name)];

    private static async Task<HealthCheckResult> CheckStoreAsync(
        IShortLivedStore store, TimeProvider timeProvider, HealthStatus failureStatus = HealthStatus.Unhealthy)
    {
        var check = new ShortLivedStoreHealthCheck(store, timeProvider);
        var registration = new HealthCheckRegistration("store", check, failureStatus, null);

        return await check.CheckHealthAsync(new HealthCheckContext { Registration = registration });
    }
}
