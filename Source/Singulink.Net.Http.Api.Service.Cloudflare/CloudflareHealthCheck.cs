using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Data = Singulink.Net.Http.Api.Service.CloudflareHealthCheckData;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Checks Cloudflare forwarding on this server: the registration's failure status while no ranges are loaded, which refuses every request, degraded when
/// the downloaded ranges are older than <see cref="CloudflareForwardingOptions.StaleWarningAge"/>, and healthy otherwise.
/// </summary>
internal sealed class CloudflareHealthCheck(CloudflareNetworks networks, IOptions<CloudflareForwardingOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var error = networks.LastRefreshError;

        var data = new Dictionary<string, object> {
            [Data.Ranges] = (long)networks.Count,
            [Data.DownloadRanges] = settings.DownloadRanges,
            [Data.RefusedRequests] = networks.RefusedRequests,
        };

        if (networks.RangesDate is { } rangesDate)
            data[Data.RangesDate] = rangesDate;

        if (networks.LastRefreshUtc is { } lastRefresh)
        {
            data[Data.LastRefresh] = lastRefresh;
            data[Data.LastRefreshSucceeded] = error is null;
        }

        if (error is not null)
            data[Data.LastRefreshError] = error.Message;

        HealthCheckResult result;

        if (!networks.IsAvailable)
        {
            if (error is not null)
                data[HttpApiHealthCheckData.Error] = error.Message;

            string message = "Cloudflare's IP ranges are not loaded, so every request is refused" + (error is null ? "." : $": {error.Message}");
            result = new HealthCheckResult(context.Registration.FailureStatus, message, error, data);
        }
        else if (settings.DownloadRanges && networks.RangesDate is { } date && DateTimeOffset.UtcNow - date > settings.StaleWarningAge)
        {
            result = HealthCheckResult.Degraded($"Cloudflare's IP ranges in use are from {date:O}, which is older than the stale warning age.", data: data);
        }
        else
        {
            result = HealthCheckResult.Healthy("Cloudflare's IP ranges are loaded.", data);
        }

        return Task.FromResult(result);
    }
}
