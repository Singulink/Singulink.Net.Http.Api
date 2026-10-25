using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// What the per-client limits of one <see cref="RateLimiterOptions"/> share: the configuration that applies to all of them, and the store and clock they
/// count requests with, which includes the memory store they share if no store is registered.
/// </summary>
internal sealed class ClientRateLimits
{
    private static readonly ConditionalWeakTable<RateLimiterOptions, ClientRateLimits> All = [];

    private readonly List<Action<ClientRateLimitOptions>> _sharedConfiguration = [];
    private LimiterServices? _services;

    public LimiterServices Services => Volatile.Read(ref _services)!;

    public static ClientRateLimits Get(RateLimiterOptions options) => All.GetValue(options, _ => new ClientRateLimits());

    public void AddSharedConfiguration(Action<ClientRateLimitOptions> configure)
    {
        lock (_sharedConfiguration)
            _sharedConfiguration.Add(configure);
    }

    public void ConfigureShared(ClientRateLimitOptions options)
    {
        Action<ClientRateLimitOptions>[] configurations;

        lock (_sharedConfiguration)
            configurations = [.. _sharedConfiguration];

        foreach (var configure in configurations)
            configure(options);
    }

    public void EnsureServices(IServiceProvider services)
    {
        if (Volatile.Read(ref _services) is null)
            Interlocked.CompareExchange(ref _services, LimiterServices.Resolve(services), null);
    }

    internal sealed class LimiterServices(IShortLivedStore store, TimeProvider timeProvider)
    {
        public IShortLivedStore Store { get; } = store;

        public TimeProvider TimeProvider { get; } = timeProvider;

        public static LimiterServices Resolve(IServiceProvider services)
        {
            var timeProvider = services.GetService<TimeProvider>() ?? TimeProvider.System;
            var store = services.GetService<IShortLivedStore>() ?? new MemoryShortLivedStore(
                timeProvider,
                services.GetService<IOptions<MemoryShortLivedStoreOptions>>() ?? Options.Create(new MemoryShortLivedStoreOptions()),
                services.GetService<ILogger<MemoryShortLivedStore>>() ?? NullLogger<MemoryShortLivedStore>.Instance);

            return new LimiterServices(store, timeProvider);
        }
    }
}
