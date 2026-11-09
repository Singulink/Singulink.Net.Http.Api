using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Tests of the short-lived stores of the toolkit's features, which they resolve with their keys (see <see cref="ShortLivedStoreKeys"/>).
/// </summary>
[PrefixTestClass]
public sealed class ShortLivedStoreKeysTests
{
    private static readonly string[] Keys = [ShortLivedStoreKeys.SignIn, ShortLivedStoreKeys.RateLimiting, ShortLivedStoreKeys.Uploads];

    [TestMethod]
    public void EveryKey_GetsAMemoryStoreOfItsOwn_WhileTheAppsStoreIsTheDefault()
    {
        var services = new ServiceCollection();
        services.AddShortLivedStore();
        using var provider = services.BuildServiceProvider();

        var appStore = provider.GetRequiredService<IShortLivedStore>().ShouldBeOfType<MemoryShortLivedStore>();
        var stores = Keys.Select(k => provider.GetRequiredKeyedService<IShortLivedStore>(k).ShouldBeOfType<MemoryShortLivedStore>()).ToList();

        stores.Append(appStore).Distinct().Count().ShouldBe(Keys.Length + 1);

        // One instance for each key.
        foreach (string key in Keys)
            provider.GetRequiredKeyedService<IShortLivedStore>(key).ShouldBeSameAs(stores[Array.IndexOf(Keys, key)]);
    }

    [TestMethod]
    public void TheMemoryStoresOfTheKeys_Hold64MiB_AndTheAppsStore256MiB_UnlessTheAppSetsTheirSizes_BeforeOrAfter()
    {
        var services = new ServiceCollection();
        services.Configure<MemoryShortLivedStoreOptions>(ShortLivedStoreKeys.SignIn, o => o.MaxSize = 1000);
        services.AddShortLivedStore();
        services.Configure<MemoryShortLivedStoreOptions>(ShortLivedStoreKeys.Uploads, o => o.MaxSize = 2000);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptionsMonitor<MemoryShortLivedStoreOptions>>();
        options.Get(ShortLivedStoreKeys.SignIn).MaxSize.ShouldBe(1000);
        options.Get(ShortLivedStoreKeys.Uploads).MaxSize.ShouldBe(2000);
        options.Get(ShortLivedStoreKeys.RateLimiting).MaxSize.ShouldBe(64L * 1024 * 1024);
        MemoryShortLivedStoreOptions.DefaultKeyedMaxSize.ShouldBe(64L * 1024 * 1024);
        options.CurrentValue.MaxSize.ShouldBe(256L * 1024 * 1024);
        options.Get("app:key").MaxSize.ShouldBe(256L * 1024 * 1024);
    }

    [TestMethod]
    public async Task TheStoreOfAKey_HasTheNamedOptionsOfItsKey_AndFillingItLeavesTheOtherStoresWorking()
    {
        var services = new ServiceCollection();
        services.AddSignInCodes();
        services.Configure<MemoryShortLivedStoreOptions>(ShortLivedStoreKeys.Uploads, o => o.MaxSize = 1000);
        using var provider = services.BuildServiceProvider();

        var uploads = provider.GetRequiredKeyedService<IShortLivedStore>(ShortLivedStoreKeys.Uploads);
        (await uploads.TryAddAsync("test:a", new string('a', 100), TimeSpan.FromMinutes(1))).ShouldBeTrue();

        var ex = await Should.ThrowAsync<ServiceUnavailableApiException>(
            () => uploads.TryAddAsync("test:b", new string('b', 1000), TimeSpan.FromMinutes(1)).AsTask());
        ex.ErrorCode.ShouldBe(ApiErrorCodes.ShortLivedStoreFull);

        // Sign-in and the app's own values have stores of their own, with the default size.
        string code = await provider.GetRequiredService<SignInCodes>().CreateAsync("user-1", Client.Pkce.Create().Challenge);
        code.ShouldNotBeNullOrEmpty();
        (await provider.GetRequiredService<IShortLivedStore>().TryAddAsync("test:b", new string('b', 1000), TimeSpan.FromMinutes(1))).ShouldBeTrue();
    }

    [TestMethod]
    public void AStoreOfTheAppsOwn_IsUsedForEveryKey()
    {
        var shared = new SharedShortLivedStore(TimeProvider.System);
        var services = new ServiceCollection();
        services.AddSingleton<IShortLivedStore>(shared);
        services.AddSignInCodes();
        using var provider = services.BuildServiceProvider();

        foreach (string key in Keys)
            provider.GetRequiredKeyedService<IShortLivedStore>(key).ShouldBeOfType<AppShortLivedStore>().Store.ShouldBeSameAs(shared);
    }

    [TestMethod]
    public async Task AStoreOfTheAppsOwn_IsNotDisposedByTheStoresOfTheKeys()
    {
        // The container never disposes a store that the app registered as an instance, and the stores of the keys don't either.
        var store = new DisposableStore(new SharedShortLivedStore(TimeProvider.System));
        var services = new ServiceCollection();
        services.AddSingleton<IShortLivedStore>(store);
        services.AddShortLivedStore();

        await using (var provider = services.BuildServiceProvider())
        {
            foreach (string key in Keys)
                (await provider.GetRequiredKeyedService<IShortLivedStore>(key).TryAddAsync("test:" + key, "1", TimeSpan.FromMinutes(1))).ShouldBeTrue();

            (await store.GetAsync("test:" + ShortLivedStoreKeys.Uploads)).ShouldBe("1");
        }

        store.DisposeCount.ShouldBe(0);
    }

    [TestMethod]
    public void AMemoryStoreThatTheAppRegisters_IsUsedForEveryKey()
    {
        // The app registered the store, so it is the app's own, even though it is a memory store too.
        var memory = new MemoryShortLivedStore(TimeProvider.System);
        var services = new ServiceCollection();
        services.AddShortLivedStore();
        services.AddSingleton<IShortLivedStore>(memory);
        using var provider = services.BuildServiceProvider();

        foreach (string key in Keys)
            provider.GetRequiredKeyedService<IShortLivedStore>(key).ShouldBeOfType<AppShortLivedStore>().Store.ShouldBeSameAs(memory);
    }

    [TestMethod]
    public void AStoreRegisteredForAKey_TakesThePlaceOfThatKeysStoreOnly()
    {
        var shared = new SharedShortLivedStore(TimeProvider.System);
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IShortLivedStore>(ShortLivedStoreKeys.Uploads, shared);
        services.AddShortLivedStore();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredKeyedService<IShortLivedStore>(ShortLivedStoreKeys.Uploads).ShouldBeSameAs(shared);
        provider.GetRequiredKeyedService<IShortLivedStore>(ShortLivedStoreKeys.SignIn).ShouldBeOfType<MemoryShortLivedStore>();
        provider.GetRequiredService<IShortLivedStore>().ShouldBeOfType<MemoryShortLivedStore>();
    }

    [TestMethod]
    public void AStoreRegisteredForAnyKey_TakesThePlaceOfEveryKeysStore()
    {
        var shared = new SharedShortLivedStore(TimeProvider.System);
        var services = new ServiceCollection();
        services.AddShortLivedStore();
        services.AddKeyedSingleton<IShortLivedStore>(KeyedService.AnyKey, shared);
        using var provider = services.BuildServiceProvider();

        foreach (string key in Keys)
            provider.GetRequiredKeyedService<IShortLivedStore>(key).ShouldBeSameAs(shared);

        provider.GetRequiredService<IShortLivedStore>().ShouldBeOfType<MemoryShortLivedStore>();
    }

    [TestMethod]
    [DataRow("app")]
    [DataRow(ShortLivedStoreKeys.RateLimiting)]
    [DataRow(ShortLivedStoreKeys.Uploads)]
    [DataRow("upload store")]
    public async Task ProcessLocalSignInStore_WhileAnotherStoreIsShared_IsWarnedAboutWhenTheAppStarts(string sharedStore)
    {
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(logs));
        services.AddKeyedSingleton<IShortLivedStore>(ShortLivedStoreKeys.SignIn, new MemoryShortLivedStore(TimeProvider.System));

        if (sharedStore is "app")
            services.AddSingleton<IShortLivedStore>(new SharedShortLivedStore(TimeProvider.System));
        else if (sharedStore is "upload store")
            services.AddSingleton<IUploadStore>(new MemoryUploadStore(TimeProvider.System) { SharingMode = StoreSharingMode.Shared });
        else
            services.AddKeyedSingleton<IShortLivedStore>(sharedStore, new SharedShortLivedStore(TimeProvider.System));

        services.AddSignInCodes();
        await StartAsync(services);

        string expectedStore = sharedStore switch {
            "app" => $"the app's {nameof(IShortLivedStore)} ({nameof(SharedShortLivedStore)})",
            "upload store" => $"the {nameof(IUploadStore)} ({nameof(MemoryUploadStore)})",
            _ => $"the {nameof(IShortLivedStore)} of '{sharedStore}' ({nameof(SharedShortLivedStore)})",
        };

        // The app's store is also the store of the other keys, which are listed after it.
        logs.Entries.Where(e => e.Level is LogLevel.Warning).ShouldHaveSingleItem().Message.ShouldContain(
            $"keeps its values in this process, but other stores are shared with other processes: {expectedStore}");
    }

    [TestMethod]
    public async Task SignInStores_ThatAreAllProcessLocal_OrShared_AreNotWarnedAbout()
    {
        var logs = new CapturingLoggerProvider();

        var local = new ServiceCollection();
        local.AddLogging(builder => builder.AddProvider(logs));
        local.AddSignInCodes();
        await StartAsync(local);

        var shared = new ServiceCollection();
        shared.AddLogging(builder => builder.AddProvider(logs));
        shared.AddSingleton<IShortLivedStore>(new SharedShortLivedStore(TimeProvider.System));
        shared.AddSignInCodes();
        await StartAsync(shared);

        logs.Entries.ShouldNotContain(e => e.Level >= LogLevel.Warning);
    }

    private sealed class DisposableStore(IShortLivedStore inner) : IShortLivedStore, IDisposable
    {
        public int DisposeCount { get; private set; }

        public StoreSharingMode SharingMode => inner.SharingMode;

        public void Dispose() => DisposeCount++;

        public ValueTask<ShortLivedStoreUsage> GetUsageAsync(CancellationToken cancellationToken = default) => inner.GetUsageAsync(cancellationToken);

        public ValueTask<bool> TryAddAsync(string key, string value, TimeSpan lifetime, CancellationToken cancellationToken = default) =>
            inner.TryAddAsync(key, value, lifetime, cancellationToken);

        public ValueTask SetAsync(string key, string value, TimeSpan lifetime, CancellationToken cancellationToken = default) =>
            inner.SetAsync(key, value, lifetime, cancellationToken);

        public ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default) => inner.GetAsync(key, cancellationToken);

        public ValueTask<string?> TakeAsync(string key, CancellationToken cancellationToken = default) => inner.TakeAsync(key, cancellationToken);

        public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) => inner.RemoveAsync(key, cancellationToken);

        public ValueTask<long> IncrementAsync(string key, long amount, TimeSpan lifetime, CancellationToken cancellationToken = default) =>
            inner.IncrementAsync(key, amount, lifetime, cancellationToken);
    }

    private static async Task StartAsync(ServiceCollection services)
    {
        await using var provider = services.BuildServiceProvider();

        foreach (var service in provider.GetServices<IHostedService>())
            await service.StartAsync(CancellationToken.None);
    }
}
