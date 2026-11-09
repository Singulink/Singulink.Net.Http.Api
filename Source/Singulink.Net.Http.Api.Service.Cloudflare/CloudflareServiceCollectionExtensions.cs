using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Extension methods for registering Cloudflare forwarding services in an <see cref="IServiceCollection"/>.
/// </summary>
public static class CloudflareServiceCollectionExtensions
{
    /// <summary>
    /// Registers the services that trust the client address and scheme forwarded by Cloudflare, including a hosted service that keeps Cloudflare's IP
    /// ranges up to date.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="configure">An optional action to configure options. <see cref="CloudflareForwardingOptions.SavedRangesFile"/> must be set, here or
    /// through configuration.</param>
    /// <remarks>
    /// <para>
    /// Add the middleware with <see cref="CloudflareWebApplicationExtensions.UseCloudflareForwarding(IApplicationBuilder)"/>. The application fails to
    /// start if <see cref="CloudflareForwardingOptions.SavedRangesFile"/> is not set.
    /// </para>
    /// <para>
    /// Cloudflare's IP ranges are downloaded when the application starts (in <see cref="WebApplication"/> apps, before it accepts requests), saved to
    /// <see cref="CloudflareForwardingOptions.SavedRangesFile"/>, and downloaded again every <see cref="CloudflareForwardingOptions.RefreshInterval"/>. Startup
    /// waits for the ranges no longer than <see cref="CloudflareForwardingOptions.DownloadTimeout"/>. If they cannot be downloaded in time, the saved
    /// copy is used. Without a usable saved copy, the application still starts, and the middleware refuses every request with a <c>503 Service
    /// Unavailable</c> response until a download succeeds.
    /// </para>
    /// <para>
    /// If <see cref="CloudflareForwardingOptions.DownloadRanges"/> is <see langword="false"/>, the ranges are read from the saved copy instead, at startup
    /// and every <see cref="CloudflareForwardingOptions.RefreshInterval"/>. Until it can be read, the middleware refuses every request in the same way.
    /// </para>
    /// <para>
    /// Downloads use the <see cref="HttpClient"/> named <see cref="CloudflareForwardingOptions.HttpClientName"/>.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddCloudflareForwarding(this IServiceCollection services, Action<CloudflareForwardingOptions>? configure = null)
    {
        services.AddOptions<CloudflareForwardingOptions>().ValidateOnStart();

        if (configure is not null)
            services.Configure(configure);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CloudflareForwardingOptions>, CloudflareForwardingOptionsValidator>());

        services.AddHttpClient(CloudflareForwardingOptions.HttpClientName);
        services.AddMetrics();
        services.TryAddSingleton<CloudflareNetworks>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddHostedService<CloudflareRangeRefresher>();

        HttpApiHealthCheckSource.Register(services, "cloudflare", b => b.AddCloudflareForwardingCheck());
        return services;
    }
}
