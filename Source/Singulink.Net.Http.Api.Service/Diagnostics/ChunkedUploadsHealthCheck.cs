using Microsoft.Extensions.Diagnostics.HealthChecks;
using Singulink.Net.Http.Api.Service.Uploads;
using Data = Singulink.Net.Http.Api.Service.HttpApiHealthCheckData.Uploads;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Checks chunked uploads on this server: the registration's failure status if the upload store can't be read (see
/// <see cref="IUploadStore.GetUsageAsync"/>) or the total length of the open uploads can't be read from the short-lived store, degraded if the last
/// cleanup failed, the open uploads have taken up <see cref="ChunkedUploadOptions.MaxTotalSize"/>, or the store has less space available than the open
/// uploads can still take up under it, or, without it, than the largest upload that the service accepts, and healthy otherwise.
/// </summary>
internal sealed class ChunkedUploadsHealthCheck(ChunkedUploads uploads) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var options = uploads.Options;
        var store = uploads.Store;
        var (storeFailures, lastStoreFailure) = uploads.StoreFailures;

        var data = new Dictionary<string, object> {
            [Data.RunningHandlers] = (long)uploads.RunningHandlers,
            [Data.PendingClaimReleases] = (long)uploads.PendingClaimReleases,
            [Data.CountedPerMachine] = uploads.IsCountedPerMachine,
            [Data.MaxUploadsPerUser] = (long)options.MaxUploadsPerUser,
            [Data.StoreType] = store.GetType().Name,
            [Data.StoreSharingMode] = store.SharingMode.ToString(),
            [Data.StoreFailures] = storeFailures,
        };

        ShortLivedStoreHealthCheck.AddIfKnown(data, Data.MaxTotalSize, options.MaxTotalSize);
        ShortLivedStoreHealthCheck.AddIfKnown(data, Data.MaxTotalSizePerUser, options.MaxTotalSizePerUser);
        ShortLivedStoreHealthCheck.AddIfKnown(data, Data.LastStoreFailure, lastStoreFailure);
        string? cleanupProblem = AddCleanup(data, uploads.LastCleanup);

        UploadStoreUsage usage;
        long openSize;

        try
        {
            usage = await store.GetUsageAsync(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(context, data, $"The upload store can't be read: {ex.Message}", ex);
        }

        if (usage.Location is { } location)
            data[Data.StoreLocation] = location;

        ShortLivedStoreHealthCheck.AddIfKnown(data, Data.StoreAvailableBytes, usage.AvailableBytes);
        ShortLivedStoreHealthCheck.AddIfKnown(data, Data.StoreTotalBytes, usage.TotalBytes);

        try
        {
            openSize = await uploads.ReadOpenUploadsSizeAsync(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(context, data, $"The counts of the open uploads can't be read from the short-lived store: {ex.Message}", ex);
        }

        data[Data.OpenUploadsSize] = openSize;

        if (cleanupProblem is not null)
            return HealthCheckResult.Degraded(cleanupProblem, uploads.LastCleanup?.Exception, data);

        if (options.MaxTotalSize is { } limit && openSize >= limit)
        {
            return HealthCheckResult.Degraded(
                $"The open uploads have taken up {nameof(ChunkedUploadOptions)}.{nameof(ChunkedUploadOptions.MaxTotalSize)}, so new uploads are refused.",
                data: data);
        }

        if (usage.AvailableBytes is not { } available)
            return HealthCheckResult.Healthy("The upload store can be read.", data);

        if (options.MaxTotalSize is { } maxTotalSize)
        {
            long remaining = maxTotalSize - Math.Max(openSize, 0);

            if (available < remaining)
            {
                return HealthCheckResult.Degraded(
                    $"The upload store has less space available ({available} bytes) than the open uploads can still take up under " +
                    $"{nameof(ChunkedUploadOptions)}.{nameof(ChunkedUploadOptions.MaxTotalSize)} ({remaining} bytes).",
                    data: data);
            }

            return HealthCheckResult.Healthy(
                "The upload store has room for what the open uploads can still take up under " +
                $"{nameof(ChunkedUploadOptions)}.{nameof(ChunkedUploadOptions.MaxTotalSize)}.",
                data);
        }

        // A store that reserves each upload's length when it creates it refuses creations once it has no room for them.
        long maxUploadSize = uploads.MaxUploadSize;

        if (available < maxUploadSize)
        {
            return HealthCheckResult.Degraded(
                $"The upload store has less space available ({available} bytes) than the largest upload that the service accepts ({maxUploadSize} bytes).",
                data: data);
        }

        return HealthCheckResult.Healthy("The upload store has room for the largest upload that the service accepts.", data);
    }

    private static HealthCheckResult Failure(HealthCheckContext context, Dictionary<string, object> data, string message, Exception exception)
    {
        data[HttpApiHealthCheckData.Error] = exception.Message;
        return new HealthCheckResult(context.Registration.FailureStatus, message, exception, data);
    }

    /// <summary>
    /// Adds the values of the last cleanup, if one has ended.
    /// </summary>
    /// <returns>The description of a cleanup that failed, or <see langword="null"/>.</returns>
    private static string? AddCleanup(Dictionary<string, object> data, UploadCleanupStatus? cleanup)
    {
        if (cleanup is null)
            return null;

        data[Data.LastCleanup] = cleanup.StartedUtc;
        data[Data.LastCleanupDuration] = cleanup.Duration.TotalSeconds;
        ShortLivedStoreHealthCheck.AddIfKnown(data, Data.LastCleanupOpenUploads, cleanup.OpenUploads);
        ShortLivedStoreHealthCheck.AddIfKnown(data, Data.LastCleanupOpenUploadsSize, cleanup.OpenUploadsSize);
        ShortLivedStoreHealthCheck.AddIfKnown(data, Data.LastCleanupUsers, (long?)cleanup.UsersWithOpenUploads);
        data[Data.LastCleanupInterrupted] = (long)cleanup.Interrupted;
        data[Data.LastCleanupExpired] = (long)cleanup.Expired;
        data[Data.LastCleanupFailedUploads] = (long)cleanup.Failed;

        if (cleanup.Exception is not { } exception)
            return null;

        data[Data.LastCleanupError] = exception.Message;
        return $"The last cleanup of uploads failed: {exception.Message}";
    }
}
