using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Adds the health checks of the toolkit's features to an app's health checks (see
/// <see cref="HealthCheckServiceCollectionExtensions.AddHealthChecks(IServiceCollection)"/>). None is added unless the app adds it. Each check reports
/// its values in <see cref="HealthCheckResult.Data"/> under the keys of <see cref="HttpApiHealthCheckData"/>, and is tagged <c>httpapi</c> and with its
/// feature by default, so that apps can filter them, such as in <c>MapHealthChecks</c>.
/// </summary>
/// <remarks>
/// A check that fails, such as for a store that can't be read, reports the failure status of its registration, which is
/// <see cref="HealthStatus.Unhealthy"/> unless the app passes another, with the exception in <see cref="HealthCheckResult.Exception"/> and its message in
/// the description and under <see cref="HttpApiHealthCheckData.Error"/>. The checks report this server's state: with several servers, each reports its own.
/// </remarks>
public static class HttpApiHealthChecksBuilderExtensions
{
    /// <summary>
    /// The tag of every check of the toolkit.
    /// </summary>
    public const string Tag = "httpapi";

    // The default name of the check of the app's store.
    private const string StoreCheckName = "httpapi-short-lived-store";

    /// <summary>
    /// Adds the checks of the toolkit's features that are registered in the service collection when it is called, with their default names and tags:
    /// the store of the app's own values when a short-lived store is registered (<see cref="AddShortLivedStoreCheck"/>), the store of
    /// <see cref="ShortLivedStoreKeys.SignIn"/> with sign-in codes, device links or passkeys, chunked uploads (<see cref="AddChunkedUploadsCheck"/>) and the
    /// store of <see cref="ShortLivedStoreKeys.Uploads"/>, hub connections with a session with session handling (<see cref="AddHubSessionsCheck"/>), the
    /// per-client rate limits with ASP.NET Core's rate limiting (<see cref="AddClientRateLimitsCheck"/>) and the store of
    /// <see cref="ShortLivedStoreKeys.RateLimiting"/> if a short-lived store is registered, and Cloudflare forwarding.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <remarks>
    /// <para>
    /// Call it once, after the registration methods of the features. A check that it adds twice, or that the app also adds with the same name, makes the
    /// health check service fail when it starts, as any two checks of the same name do.
    /// </para>
    /// <para>
    /// The checks that it adds of the keys' stores report <see cref="HealthStatus.Healthy"/> for a key whose store is the app's store, with the type and
    /// sharing mode of the store, and don't read it again: the check of the app's store reads it. A key with a store of its own is read by its check, and a
    /// check that the app adds with <see cref="AddShortLivedStoreCheck"/> always reads its store.
    /// </para>
    /// </remarks>
    public static IHealthChecksBuilder AddHttpApiChecks(this IHealthChecksBuilder builder)
    {
        // Adding checks adds services, so the services are read first.
        var services = builder.Services;
        var sources = services.Select(d => d.ImplementationInstance).OfType<HttpApiHealthCheckSource>().DistinctBy(s => s.Feature).ToList();
        bool hasRateLimiting = services.Any(d => d.ServiceType == typeof(IConfigureOptions<RateLimiterOptions>));
        bool hasKeyedStores = services.Any(d => d.ServiceType == typeof(IShortLivedStore) && d.IsKeyedService);

        foreach (var source in sources)
            source.Add(builder);

        if (hasRateLimiting)
        {
            builder.AddClientRateLimitsCheck();

            if (hasKeyedStores)
                builder.AddFeatureStoreCheck(ShortLivedStoreKeys.RateLimiting);
        }

        return builder;
    }

    /// <summary>
    /// Adds a check of a short-lived store: the store of the app's own values, or the store of a key of <see cref="ShortLivedStoreKeys"/> (see
    /// <see cref="IShortLivedStore.GetUsageAsync"/>).
    /// </summary>
    /// <remarks>
    /// It reports the failure status if the store can't be read, <see cref="HealthStatus.Degraded"/> if it refused a write because it was full within the
    /// last minute, whatever the failure status, or is more than 90% full, and <see cref="HealthStatus.Healthy"/> otherwise. Its values are under the keys
    /// of <see cref="HttpApiHealthCheckData.ShortLivedStore"/>.
    /// </remarks>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="key">The key of the store, or <see langword="null"/> for the store registered without a key.</param>
    /// <param name="name">The name of the check. Default is <c>httpapi-short-lived-store</c>, followed by the key without its <c>httpapi:</c> prefix after a
    /// dash, such as <c>httpapi-short-lived-store-sign-in</c>.</param>
    /// <param name="failureStatus">The status that a failure reports. Default is <see cref="HealthStatus.Unhealthy"/>.</param>
    /// <param name="tags">The tags of the check. Default is <c>httpapi</c> and <c>short-lived-store</c>.</param>
    public static IHealthChecksBuilder AddShortLivedStoreCheck(
        this IHealthChecksBuilder builder, string? key = null, string? name = null, HealthStatus? failureStatus = null, IEnumerable<string>? tags = null)
    {
        return builder.AddStoreCheck(key, name, failureStatus, tags, appStoreCheckName: null);
    }

    /// <summary>
    /// Adds the check of a feature's key for <see cref="AddHttpApiChecks"/>, which adds the check of the app's store too, so that a key whose store is the
    /// app's store isn't read twice.
    /// </summary>
    internal static IHealthChecksBuilder AddFeatureStoreCheck(this IHealthChecksBuilder builder, string key) =>
        builder.AddStoreCheck(key, null, null, null, StoreCheckName);

    private static IHealthChecksBuilder AddStoreCheck(
        this IHealthChecksBuilder builder, string? key, string? name, HealthStatus? failureStatus, IEnumerable<string>? tags, string? appStoreCheckName)
    {
        if (name is null)
        {
            string? suffix = key?.StartsWith(ShortLivedKeys.Prefix, StringComparison.Ordinal) is true ? key[ShortLivedKeys.Prefix.Length..] : key;
            name = suffix is null ? StoreCheckName : $"{StoreCheckName}-{suffix}";
        }

        return builder.Add(new HealthCheckRegistration(
            name,
            sp => new ShortLivedStoreHealthCheck(
                key is null ? sp.GetRequiredService<IShortLivedStore>() : sp.GetRequiredKeyedService<IShortLivedStore>(key),
                sp.GetService<TimeProvider>() ?? TimeProvider.System,
                appStoreCheckName),
            failureStatus,
            tags ?? [Tag, "short-lived-store"]));
    }

    /// <summary>
    /// Adds a check of chunked uploads (see <see cref="ServiceCollectionExtensions.AddChunkedUploads"/>) on this server: the upload store (see
    /// <see cref="IUploadStore.GetUsageAsync"/>), the counts of the open uploads, the last cleanup and the handlers that run.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="name">The name of the check. Default is <c>httpapi-uploads</c>.</param>
    /// <param name="failureStatus">The status that a failure reports. Default is <see cref="HealthStatus.Unhealthy"/>.</param>
    /// <param name="tags">The tags of the check. Default is <c>httpapi</c> and <c>uploads</c>.</param>
    /// <remarks>
    /// <para>
    /// It reports the failure status if the upload store can't be read, or the total length of the open uploads can't be read from the short-lived store,
    /// <see cref="HealthStatus.Degraded"/> if the last cleanup failed, the open uploads have taken up <see cref="ChunkedUploadOptions.MaxTotalSize"/>, or
    /// the upload store has less space available than the open uploads can still take up under it, if it is set, or otherwise than the largest upload that
    /// the service accepts (the largest <see cref="ChunkedUploadEndpointOptions.MaxSize"/> of the mapped endpoints, with
    /// <see cref="ChunkedUploadOptions.MaxSize"/> for those without one), and <see cref="HealthStatus.Healthy"/> otherwise. A store that can't tell its
    /// available space has no space rule. Its values are under the keys of <see cref="HttpApiHealthCheckData.Uploads"/>.
    /// </para>
    /// <para>
    /// Each run reads the upload store's usage, which for the directory store is the space of its volume, and one counter of the short-lived store.
    /// </para>
    /// </remarks>
    public static IHealthChecksBuilder AddChunkedUploadsCheck(
        this IHealthChecksBuilder builder, string? name = null, HealthStatus? failureStatus = null, IEnumerable<string>? tags = null)
    {
        return builder.Add(new HealthCheckRegistration(
            name ?? "httpapi-uploads", sp => new ChunkedUploadsHealthCheck(sp.GetRequiredService<ChunkedUploads>()), failureStatus, tags ?? [Tag, "uploads"]));
    }

    /// <summary>
    /// Adds a check of the per-client rate limits (see <see cref="RateLimiterOptionsExtensions"/>) of the app's <see cref="RateLimiterOptions"/> on this
    /// server.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="name">The name of the check. Default is <c>httpapi-rate-limiting</c>.</param>
    /// <param name="failureStatus">The status that a failure reports. Default is <see cref="HealthStatus.Unhealthy"/>.</param>
    /// <param name="tags">The tags of the check. Default is <c>httpapi</c> and <c>rate-limiting</c>.</param>
    /// <remarks>
    /// It reports <see cref="HealthStatus.Degraded"/> if the short-lived store couldn't count a request of a limit within the last 5 minutes, which refuses
    /// the request, and <see cref="HealthStatus.Healthy"/> otherwise, also before a limit has counted a request, which its description says. Its values are
    /// under the keys of <see cref="HttpApiHealthCheckData.RateLimiting"/>, for each limit.
    /// </remarks>
    public static IHealthChecksBuilder AddClientRateLimitsCheck(
        this IHealthChecksBuilder builder, string? name = null, HealthStatus? failureStatus = null, IEnumerable<string>? tags = null)
    {
        return builder.Add(new HealthCheckRegistration(
            name ?? "httpapi-rate-limiting",
            sp => new ClientRateLimitsHealthCheck(sp.GetRequiredService<IOptions<RateLimiterOptions>>(), sp.GetService<TimeProvider>() ?? TimeProvider.System),
            failureStatus,
            tags ?? [Tag, "rate-limiting"]));
    }

    /// <summary>
    /// Adds a check of the hub connections with a session (see the SignalR hubs guide) on this server, for each session token type.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="name">The name of the check. Default is <c>httpapi-hub-sessions</c>.</param>
    /// <param name="failureStatus">The status that a failure reports. Default is <see cref="HealthStatus.Unhealthy"/>.</param>
    /// <param name="tags">The tags of the check. Default is <c>httpapi</c> and <c>hub-sessions</c>.</param>
    /// <remarks>
    /// It reports <see cref="HealthStatus.Degraded"/> if a recheck of the sessions in the last round failed, such as when the session store couldn't be
    /// reached, and <see cref="HealthStatus.Healthy"/> otherwise. Its values are under the keys of <see cref="HttpApiHealthCheckData.HubSessions"/>, for
    /// each session token type.
    /// </remarks>
    public static IHealthChecksBuilder AddHubSessionsCheck(
        this IHealthChecksBuilder builder, string? name = null, HealthStatus? failureStatus = null, IEnumerable<string>? tags = null)
    {
        return builder.Add(new HealthCheckRegistration(
            name ?? "httpapi-hub-sessions",
            sp => new HubSessionsHealthCheck(sp.GetServices<ISessionConnectionTracker>()),
            failureStatus,
            tags ?? [Tag, "hub-sessions"]));
    }
}
