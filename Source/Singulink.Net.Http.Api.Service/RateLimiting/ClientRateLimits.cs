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
    private readonly List<RateLimitPolicyCounters> _policies = [];
    private LimiterServices? _services;

    public LimiterServices Services => Volatile.Read(ref _services)!;

    public static ClientRateLimits Get(RateLimiterOptions options) => All.GetValue(options, _ => new ClientRateLimits());

    /// <summary>
    /// Gets the counts of the per-client limits of the options, in the order they were added.
    /// </summary>
    public IReadOnlyList<RateLimitPolicyCounters> Policies
    {
        get {
            lock (_policies)
                return [.. _policies];
        }
    }

    public void AddPolicy(RateLimitPolicyCounters policy)
    {
        lock (_policies)
            _policies.Add(policy);
    }

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
        if (Volatile.Read(ref _services) is not null)
            return;

        var resolved = LimiterServices.Resolve(services);

        if (Interlocked.CompareExchange(ref _services, resolved, null) is null && resolved.Metrics is { } metrics)
        {
            foreach (var policy in Policies)
                metrics.AddPolicy(policy);
        }
    }

    internal sealed class LimiterServices(IShortLivedStore store, TimeProvider timeProvider, HttpApiMetrics? metrics)
    {
        public IShortLivedStore Store { get; } = store;

        public TimeProvider TimeProvider { get; } = timeProvider;

        public HttpApiMetrics? Metrics { get; } = metrics;

        public static LimiterServices Resolve(IServiceProvider services)
        {
            var timeProvider = services.GetService<TimeProvider>() ?? TimeProvider.System;
            var metrics = HttpApiMetrics.Get(services);
            var store = services.GetKeyedService<IShortLivedStore>(ShortLivedStoreKeys.RateLimiting) ?? services.GetService<IShortLivedStore>();

            if (store is null)
            {
                var memoryStore = new MemoryShortLivedStore(
                    timeProvider,
                    services.GetService<IOptions<MemoryShortLivedStoreOptions>>() ?? Options.Create(new MemoryShortLivedStoreOptions()),
                    services.GetService<ILogger<MemoryShortLivedStore>>() ?? NullLogger<MemoryShortLivedStore>.Instance);

                metrics?.AddStore(ShortLivedStoreKeys.RateLimiting, memoryStore);
                store = memoryStore;
            }

            return new LimiterServices(store, timeProvider, metrics);
        }
    }
}
