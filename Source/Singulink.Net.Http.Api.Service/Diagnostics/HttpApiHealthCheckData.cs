namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The keys of the values in <see cref="Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Data"/> of the toolkit's health checks (see
/// <see cref="HttpApiHealthChecksBuilderExtensions"/>). A value that a check can't tell is left out. Times are <see cref="DateTimeOffset"/> values in
/// UTC, durations and windows are seconds as <see cref="double"/> values, and counts, sizes (in bytes) and limits are <see cref="long"/> values.
/// </summary>
public static class HttpApiHealthCheckData
{
    /// <summary>
    /// The message of the failure that a check reports, which is also in its description, and whose exception is in
    /// <see cref="Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Exception"/>.
    /// </summary>
    public const string Error = "error";

    /// <summary>
    /// The keys of the check of a short-lived store (see
    /// <see cref="HttpApiHealthChecksBuilderExtensions.AddShortLivedStoreCheck"/>),
    /// which come from <see cref="IShortLivedStore.SharingMode"/> and <see cref="IShortLivedStore.GetUsageAsync"/>.
    /// </summary>
    public static class ShortLivedStore
    {
        /// <summary>The name of the store's type, which is the type of the app's store when it serves a key.</summary>
        public const string StoreType = "store_type";

        /// <summary>The store's <see cref="StoreSharingMode"/>, as its name.</summary>
        public const string SharingMode = "sharing_mode";

        /// <summary>The number of entries (see <see cref="ShortLivedStoreUsage.EntryCount"/>).</summary>
        public const string Entries = "entries";

        /// <summary>The memory that the entries take up (see <see cref="ShortLivedStoreUsage.UsedBytes"/>).</summary>
        public const string UsedBytes = "used_bytes";

        /// <summary>The memory that the store can take up (see <see cref="ShortLivedStoreUsage.MaxBytes"/>).</summary>
        public const string MaxBytes = "max_bytes";

        /// <summary>The writes that the store refused because it was full (see <see cref="ShortLivedStoreUsage.RefusedWrites"/>).</summary>
        public const string RefusedWrites = "refused_writes";

        /// <summary>When the store last refused a write (see <see cref="ShortLivedStoreUsage.LastRefusedWriteUtc"/>).</summary>
        public const string LastRefusedWrite = "last_refused_write";
    }

    /// <summary>
    /// The keys of the check of chunked uploads (see
    /// <see cref="HttpApiHealthChecksBuilderExtensions.AddChunkedUploadsCheck"/>).
    /// </summary>
    public static class Uploads
    {
        /// <summary>The upload handlers running in this process.</summary>
        public const string RunningHandlers = "running_handlers";

        /// <summary>The claims of runs that this server could not release yet, and releases in the background.</summary>
        public const string PendingClaimReleases = "pending_claim_releases";

        /// <summary>The total length of the open uploads, as the limit of all users counts it in the short-lived store.</summary>
        public const string OpenUploadsSize = "open_uploads_size";

        /// <summary>Whether the counts of the open uploads are this machine's rather than those of all servers (see the uploads guide).</summary>
        public const string CountedPerMachine = "counted_per_machine";

        /// <summary>The value of <see cref="Service.Uploads.ChunkedUploadOptions.MaxTotalSize"/>, if it is set.</summary>
        public const string MaxTotalSize = "max_total_size";

        /// <summary>The value of <see cref="Service.Uploads.ChunkedUploadOptions.MaxUploadsPerUser"/>.</summary>
        public const string MaxUploadsPerUser = "max_uploads_per_user";

        /// <summary>The value of <see cref="Service.Uploads.ChunkedUploadOptions.MaxTotalSizePerUser"/>, if it is set.</summary>
        public const string MaxTotalSizePerUser = "max_total_size_per_user";

        /// <summary>The name of the upload store's type.</summary>
        public const string StoreType = "store_type";

        /// <summary>The upload store's <see cref="StoreSharingMode"/>, as its name.</summary>
        public const string StoreSharingMode = "store_sharing_mode";

        /// <summary>Where the upload store keeps its uploads (see <see cref="Service.Uploads.UploadStoreUsage.Location"/>).</summary>
        public const string StoreLocation = "store_location";

        /// <summary>The space available to the upload store (see <see cref="Service.Uploads.UploadStoreUsage.AvailableBytes"/>).</summary>
        public const string StoreAvailableBytes = "store_available_bytes";

        /// <summary>The total space of the upload store's storage (see <see cref="Service.Uploads.UploadStoreUsage.TotalBytes"/>).</summary>
        public const string StoreTotalBytes = "store_total_bytes";

        /// <summary>The operations of the upload store that failed and were reported as unavailable since the app started.</summary>
        public const string StoreFailures = "store_failures";

        /// <summary>When an operation of the upload store last failed.</summary>
        public const string LastStoreFailure = "last_store_failure";

        /// <summary>When the last cleanup on this server started.</summary>
        public const string LastCleanup = "last_cleanup";

        /// <summary>How long the last cleanup took.</summary>
        public const string LastCleanupDuration = "last_cleanup_duration";

        /// <summary>The open uploads that the listing of the last cleanup found.</summary>
        public const string LastCleanupOpenUploads = "last_cleanup_open_uploads";

        /// <summary>The total length of the open uploads that the listing of the last cleanup found.</summary>
        public const string LastCleanupOpenUploadsSize = "last_cleanup_open_uploads_size";

        /// <summary>The number of users with open uploads that the listing of the last cleanup found.</summary>
        public const string LastCleanupUsers = "last_cleanup_users";

        /// <summary>The runs that the last cleanup ended as interrupted.</summary>
        public const string LastCleanupInterrupted = "last_cleanup_interrupted";

        /// <summary>The expired uploads that the last cleanup removed.</summary>
        public const string LastCleanupExpired = "last_cleanup_expired";

        /// <summary>The uploads that the last cleanup could not clean up.</summary>
        public const string LastCleanupFailedUploads = "last_cleanup_failed_uploads";

        /// <summary>The message of the exception that the last cleanup ended with, if it did not finish.</summary>
        public const string LastCleanupError = "last_cleanup_error";
    }

    /// <summary>
    /// The keys of the check of the per-client rate limits (see
    /// <see cref="HttpApiHealthChecksBuilderExtensions.AddClientRateLimitsCheck"/>), one of
    /// each for each limit: those of the global limiter as they are, and those of a policy after the policy's name and a dot, such as
    /// <c>uploads.queued_requests</c>. The keys have no dot, so the name of a policy, which can have one, is what comes before the last dot.
    /// </summary>
    public static class RateLimiting
    {
        /// <summary>The limit's <see cref="ClientRateLimitOptions.PermitLimit"/>.</summary>
        public const string PermitLimit = "permit_limit";

        /// <summary>The limit's <see cref="ClientRateLimitOptions.Window"/>.</summary>
        public const string Window = "window";

        /// <summary>The requests waiting for the next window on this server.</summary>
        public const string QueuedRequests = "queued_requests";

        /// <summary>The requests that the limit refused on this server since the app started.</summary>
        public const string Rejected = "rejected";

        /// <summary>The requests refused on this server since the app started because the short-lived store could not count them.</summary>
        public const string NotCounted = "not_counted";

        /// <summary>When the short-lived store last could not count a request of the limit.</summary>
        public const string LastNotCounted = "last_not_counted";
    }

    /// <summary>
    /// The keys of the check of hub connections with a session (see
    /// <see cref="HttpApiHealthChecksBuilderExtensions.AddHubSessionsCheck"/>), one of
    /// each for each session token type, after the name of the type and a dot, such as <c>SessionToken.tracked_connections</c>.
    /// </summary>
    public static class HubSessions
    {
        /// <summary>The hub connections with a session that this server tracks.</summary>
        public const string TrackedConnections = "tracked_connections";

        /// <summary>The connections that rechecks ended because their session ended, since the app started.</summary>
        public const string ConnectionsEnded = "connections_ended";

        /// <summary>When the last round of rechecks ended.</summary>
        public const string LastCheck = "last_check";

        /// <summary>The connections that the last round rechecked.</summary>
        public const string LastCheckDue = "last_check_due";

        /// <summary>The rechecks of the last round that failed, such as when the session store couldn't be reached.</summary>
        public const string LastCheckFailures = "last_check_failures";
    }
}
