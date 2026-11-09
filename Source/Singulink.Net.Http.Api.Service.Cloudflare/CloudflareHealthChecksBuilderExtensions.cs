using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Adds the health check of Cloudflare forwarding to an app's health checks (see
/// <see cref="HealthCheckServiceCollectionExtensions.AddHealthChecks(IServiceCollection)"/>). It is not added unless the app adds it, with this method or
/// with <see cref="HttpApiHealthChecksBuilderExtensions.AddHttpApiChecks"/>.
/// </summary>
public static class CloudflareHealthChecksBuilderExtensions
{
    /// <summary>
    /// Adds a check of Cloudflare forwarding (see <see cref="CloudflareServiceCollectionExtensions.AddCloudflareForwarding"/>) on this server.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="name">The name of the check. Default is <c>httpapi-cloudflare</c>.</param>
    /// <param name="failureStatus">The status that a failure reports. Default is <see cref="HealthStatus.Unhealthy"/>.</param>
    /// <param name="tags">The tags of the check. Default is <c>httpapi</c> and <c>cloudflare</c>.</param>
    /// <remarks>
    /// It reports the failure status while no ranges are loaded, which refuses every request, <see cref="HealthStatus.Degraded"/> while the ranges were
    /// downloaded longer than <see cref="CloudflareForwardingOptions.StaleWarningAge"/> ago, and <see cref="HealthStatus.Healthy"/> otherwise. Its values
    /// are under the keys of <see cref="CloudflareHealthCheckData"/>.
    /// </remarks>
    public static IHealthChecksBuilder AddCloudflareForwardingCheck(
        this IHealthChecksBuilder builder, string? name = null, HealthStatus? failureStatus = null, IEnumerable<string>? tags = null)
    {
        return builder.Add(new HealthCheckRegistration(
            name ?? "httpapi-cloudflare",
            sp => new CloudflareHealthCheck(sp.GetRequiredService<CloudflareNetworks>(), sp.GetRequiredService<IOptions<CloudflareForwardingOptions>>()),
            failureStatus,
            tags ?? [HttpApiHealthChecksBuilderExtensions.Tag, "cloudflare"]));
    }
}
