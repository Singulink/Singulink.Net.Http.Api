using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service;

[PrefixTestClass]
public sealed class SignInRegistrationTests
{
    [TestMethod]
    public async Task AddDeviceLinks_RegistersSingletonServicesWithOptions()
    {
        var services = new ServiceCollection();

        services.AddSignInCodes(o => o.Lifetime = TimeSpan.FromMinutes(1));
        services.AddDeviceLinks(o => o.Lifetime = TimeSpan.FromMinutes(5));

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IShortLivedStore>().ShouldBeOfType<MemoryShortLivedStore>();
        provider.GetRequiredService<IShortLivedStore>().ShouldBeSameAs(provider.GetRequiredService<IShortLivedStore>());
        provider.GetRequiredService<SignInCodes>().ShouldBeSameAs(provider.GetRequiredService<SignInCodes>());
        provider.GetRequiredService<DeviceLinks>().ShouldBeSameAs(provider.GetRequiredService<DeviceLinks>());
        services.Count(d => d.ServiceType == typeof(SignInCodes)).ShouldBe(1);

        provider.GetRequiredService<IOptions<SignInCodeOptions>>().Value.Lifetime.ShouldBe(TimeSpan.FromMinutes(1));
        provider.GetRequiredService<IOptions<DeviceLinkOptions>>().Value.Lifetime.ShouldBe(TimeSpan.FromMinutes(5));

        var start = await provider.GetRequiredService<DeviceLinks>().StartAsync(Client.Pkce.Create().Challenge, "Device", null, null);
        start.Lifetime.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [TestMethod]
    public async Task AddShortLivedStore_UsesRegisteredTimeProvider()
    {
        var clock = new TestClock();
        var services = new ServiceCollection();

        services.AddSingleton<TimeProvider>(clock);
        services.AddDeviceLinks();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<TimeProvider>().ShouldBeSameAs(clock);
        var store = provider.GetRequiredService<IShortLivedStore>();
        (await store.TryAddAsync("test:key", "value", TimeSpan.FromMinutes(1))).ShouldBeTrue();

        clock.Advance(TimeSpan.FromMinutes(1));
        (await store.GetAsync("test:key")).ShouldBeNull();
    }

    [TestMethod]
    public void AddSignInCodes_KeepsRegisteredStore()
    {
        var store = new SharedShortLivedStore(TimeProvider.System);
        var services = new ServiceCollection();

        services.AddSingleton<IShortLivedStore>(store);
        services.AddSignInCodes();
        services.AddShortLivedStore();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IShortLivedStore>().ShouldBeSameAs(store);
        services.Count(d => d.ServiceType == typeof(IShortLivedStore)).ShouldBe(1);
    }
}
