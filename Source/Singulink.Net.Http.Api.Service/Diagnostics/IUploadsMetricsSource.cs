namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The values of chunked uploads that the observable instruments of <see cref="HttpApiMetrics"/> read. A value is <see langword="null"/> while it is not
/// known, such as the open uploads before the first cleanup.
/// </summary>
internal interface IUploadsMetricsSource
{
    long? RunningHandlers { get; }

    long? PendingClaimReleases { get; }

    long? OpenUploads { get; }

    long? OpenUploadsSize { get; }

    long? StoreAvailableBytes { get; }
}
