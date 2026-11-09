using Microsoft.Extensions.Diagnostics.HealthChecks;
using Data = Singulink.Net.Http.Api.Service.HttpApiHealthCheckData.ShortLivedStore;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Checks a short-lived store with <see cref="IShortLivedStore.GetUsageAsync"/>: the registration's failure status if the store can't be read, degraded if
/// it refused a write because it was full within the last minute or is more than 90% full, and healthy otherwise. With the name of the check of the app's
/// store, a store of a key that is the app's store isn't read again, and the check reports healthy with the name of the check that reads it.
/// </summary>
internal sealed class ShortLivedStoreHealthCheck(IShortLivedStore store, TimeProvider timeProvider, string? appStoreCheckName = null) : IHealthCheck
{
    internal const double FullRatio = 0.9;

    // A memory store refuses writes while it is full, and sweeps again at most every second, so a refusal within this time means it may still refuse them.
    internal static readonly TimeSpan RecentRefusalAge = TimeSpan.FromMinutes(1);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object> {
            [Data.StoreType] = AppShortLivedStore.GetTypeName(store),
            [Data.SharingMode] = store.SharingMode.ToString(),
        };

        if (appStoreCheckName is not null && store is AppShortLivedStore)
            return HealthCheckResult.Healthy($"The store of this key is the app's store, which the check '{appStoreCheckName}' reads.", data);

        ShortLivedStoreUsage usage;

        try
        {
            usage = await store.GetUsageAsync(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            string message = $"The short-lived store can't be read: {ex.Message}";
            data[HttpApiHealthCheckData.Error] = ex.Message;
            return new HealthCheckResult(context.Registration.FailureStatus, message, ex, data);
        }

        AddIfKnown(data, Data.Entries, usage.EntryCount);
        AddIfKnown(data, Data.UsedBytes, usage.UsedBytes);
        AddIfKnown(data, Data.MaxBytes, usage.MaxBytes);
        AddIfKnown(data, Data.RefusedWrites, usage.RefusedWrites);

        if (usage.LastRefusedWriteUtc is { } lastRefused)
        {
            data[Data.LastRefusedWrite] = lastRefused;

            // The store protects itself: the writes it refuses fail, and everything else works.
            if (timeProvider.GetUtcNow() - lastRefused < RecentRefusalAge)
                return HealthCheckResult.Degraded("The short-lived store is full and refuses writes.", data: data);
        }

        if (usage is not { UsedBytes: { } used, MaxBytes: { } max and > 0 })
            return HealthCheckResult.Healthy("The short-lived store can be read.", data);

        if (used > max * FullRatio)
            return HealthCheckResult.Degraded("The short-lived store is more than 90% full.", data: data);

        return HealthCheckResult.Healthy("The short-lived store can be read and has room.", data);
    }

    internal static void AddIfKnown<T>(Dictionary<string, object> data, string key, T? value)
        where T : struct
    {
        if (value is { } known)
            data[key] = known;
    }
}
