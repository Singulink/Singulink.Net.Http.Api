namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The keys of the values in <see cref="Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Data"/> of the check of Cloudflare forwarding
/// (see <see cref="CloudflareHealthChecksBuilderExtensions.AddCloudflareForwardingCheck"/>), whose values follow the conventions of
/// <see cref="HttpApiHealthCheckData"/>. While the check fails, the message of the last refresh's failure is also under
/// <see cref="HttpApiHealthCheckData.Error"/>.
/// </summary>
public static class CloudflareHealthCheckData
{
    /// <summary>The number of Cloudflare's IP ranges in use.</summary>
    public const string Ranges = "ranges";

    /// <summary>When the ranges in use were downloaded, or when the saved copy in use was written.</summary>
    public const string RangesDate = "ranges_date";

    /// <summary>The value of <see cref="CloudflareForwardingOptions.DownloadRanges"/>.</summary>
    public const string DownloadRanges = "download_ranges";

    /// <summary>When the last refresh of the ranges ended.</summary>
    public const string LastRefresh = "last_refresh";

    /// <summary>Whether the last refresh of the ranges succeeded.</summary>
    public const string LastRefreshSucceeded = "last_refresh_succeeded";

    /// <summary>The message of the exception of the last refresh of the ranges, if it failed, whatever the check's status.</summary>
    public const string LastRefreshError = "last_refresh_error";

    /// <summary>The requests refused because no ranges were loaded, since the app started.</summary>
    public const string RefusedRequests = "refused_requests";
}
