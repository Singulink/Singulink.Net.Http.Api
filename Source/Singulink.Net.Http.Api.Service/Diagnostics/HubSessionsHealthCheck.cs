using Microsoft.Extensions.Diagnostics.HealthChecks;
using Data = Singulink.Net.Http.Api.Service.HttpApiHealthCheckData.HubSessions;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Checks the hub connections with a session on this server: degraded if a recheck of the last round failed, such as when the session store couldn't be
/// reached, and healthy otherwise.
/// </summary>
internal sealed class HubSessionsHealthCheck(IEnumerable<ISessionConnectionTracker> trackers) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>();
        int failures = 0;

        foreach (var tracker in trackers)
        {
            var status = tracker.GetStatus();
            string prefix = tracker.TokenTypeName + ".";

            data[prefix + Data.TrackedConnections] = (long)status.TrackedConnections;
            data[prefix + Data.ConnectionsEnded] = status.ConnectionsEnded;

            if (status.LastCheckUtc is { } lastCheck)
            {
                data[prefix + Data.LastCheck] = lastCheck;
                data[prefix + Data.LastCheckDue] = (long)status.LastCheckDue;
                data[prefix + Data.LastCheckFailures] = (long)status.LastCheckFailures;
                failures += status.LastCheckFailures;
            }
        }

        var result = failures > 0
            ? HealthCheckResult.Degraded($"{failures} rechecks of the sessions of hub connections failed in the last round.", data: data)
            : HealthCheckResult.Healthy("The sessions of hub connections are rechecked.", data);

        return Task.FromResult(result);
    }
}
