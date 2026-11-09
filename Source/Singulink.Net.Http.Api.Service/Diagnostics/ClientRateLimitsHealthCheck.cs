using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Data = Singulink.Net.Http.Api.Service.HttpApiHealthCheckData.RateLimiting;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Checks the per-client rate limits of the app's <see cref="RateLimiterOptions"/> on this server: degraded if the short-lived store couldn't count a
/// request of a limit within the last 5 minutes, which refuses the request, and healthy otherwise, also before any limit has counted a request.
/// </summary>
internal sealed class ClientRateLimitsHealthCheck(IOptions<RateLimiterOptions> options, TimeProvider timeProvider) : IHealthCheck
{
    internal static readonly TimeSpan RecentFailureAge = TimeSpan.FromMinutes(5);

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var policies = ClientRateLimits.Get(options.Value).Policies;
        var data = new Dictionary<string, object>();
        var nowUtc = timeProvider.GetUtcNow();
        bool hasRecentFailure = false;

        foreach (var policy in policies)
        {
            string prefix = policy.Policy is null ? string.Empty : policy.Policy + ".";
            data[prefix + Data.PermitLimit] = (long)policy.PermitLimit;
            data[prefix + Data.Window] = policy.Window.TotalSeconds;
            data[prefix + Data.QueuedRequests] = (long)policy.QueuedRequests;
            data[prefix + Data.Rejected] = policy.Rejected;
            data[prefix + Data.NotCounted] = policy.NotCounted;

            if (policy.LastNotCountedUtc is { } lastNotCounted)
            {
                data[prefix + Data.LastNotCounted] = lastNotCounted;
                hasRecentFailure |= nowUtc - lastNotCounted < RecentFailureAge;
            }
        }

        HealthCheckResult result;

        if (hasRecentFailure)
            result = HealthCheckResult.Degraded("The short-lived store couldn't count requests of a limit within the last 5 minutes.", data: data);
        else if (!policies.Any(p => p.HasCounted))
            result = HealthCheckResult.Healthy("No per-client limit has counted a request yet.", data);
        else
            result = HealthCheckResult.Healthy("The per-client limits count requests.", data);

        return Task.FromResult(result);
    }
}
