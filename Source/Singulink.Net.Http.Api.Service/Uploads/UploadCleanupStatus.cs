namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// The last cleanup of chunked uploads on this server, which the health check of chunked uploads reports.
/// </summary>
internal sealed record UploadCleanupStatus
{
    public required DateTimeOffset StartedUtc { get; init; }

    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// Gets the open uploads that the listing found and kept, the total of their lengths, and the number of users who have them, or <see langword="null"/>
    /// if the listing failed.
    /// </summary>
    public long? OpenUploads { get; init; }

    public long? OpenUploadsSize { get; init; }

    public int? UsersWithOpenUploads { get; init; }

    /// <summary>
    /// Gets the runs that the cleanup ended as interrupted, the expired uploads it removed, and the uploads it could not clean up.
    /// </summary>
    public int Interrupted { get; init; }

    public int Expired { get; init; }

    public int Failed { get; init; }

    /// <summary>
    /// Gets the exception that the cleanup ended with, if it did not finish.
    /// </summary>
    public Exception? Exception { get; init; }
}
