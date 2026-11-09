namespace Singulink.Net.Http.Api;

/// <summary>
/// Contains the error codes (<see cref="ApiException.ErrorCode"/>) of the errors that the chunked uploads of the <c>Singulink.Net.Http.Api.Service</c>
/// package report, so that apps and their clients can tell the failures apart and word them in their own way.
/// </summary>
public static class UploadErrorCodes
{
    /// <summary>
    /// The creation of an upload is not valid: its file name is empty or longer than 255 characters, its length is negative, its content type is longer
    /// than 255 characters, its chunk size is not positive, its ID is the empty GUID, its idempotency key is empty or longer than 128 characters, or its
    /// command cannot be read or is missing where the endpoint's validator or handler requires one.
    /// </summary>
    /// <remarks>
    /// Thrown as a <see cref="BadRequestApiException"/>.
    /// </remarks>
    public const string Invalid = "upload-invalid";

    /// <summary>
    /// The body of a creation of an upload, or the creation header of a direct upload, can't be read as a creation: it is missing or not valid JSON, it is
    /// <c>null</c>, or it lacks a value that a creation requires or has a value of the wrong type, such as an ID that is not a GUID.
    /// </summary>
    /// <remarks>
    /// Thrown as a <see cref="BadRequestApiException"/>.
    /// </remarks>
    public const string CreationUnreadable = "upload-creation-unreadable";

    /// <summary>
    /// The body of a creation of an upload is not JSON, by its content type.
    /// </summary>
    /// <remarks>
    /// Thrown as an <see cref="UnsupportedMediaTypeApiException"/>.
    /// </remarks>
    public const string CreationNotJson = "upload-creation-not-json";

    /// <summary>
    /// The body of a creation of an upload is larger than 64 KiB, or than a smaller request size limit of the server.
    /// </summary>
    /// <remarks>
    /// Thrown as a <see cref="ContentTooLargeApiException"/>.
    /// </remarks>
    public const string CreationTooLarge = "upload-creation-too-large";

    /// <summary>
    /// The creation header of a direct upload is larger than 8 KiB.
    /// </summary>
    /// <remarks>
    /// Thrown as a <see cref="HeadersTooLargeApiException"/>.
    /// </remarks>
    public const string CreationHeaderTooLarge = "upload-creation-header-too-large";

    /// <summary>
    /// The ID of a creation (see <see cref="DataContracts.CreateUploadCommand.Id"/>) is the ID of an earlier upload of the user for another endpoint, other
    /// route values, another query string, or another file name, length, content type or command.
    /// </summary>
    /// <remarks>
    /// Thrown as a <see cref="ConflictApiException"/>.
    /// </remarks>
    public const string IdConflict = "upload-id-conflict";

    /// <summary>
    /// The idempotency key of a creation of a 4.x client (see <see cref="DataContracts.CreateUploadCommand.IdempotencyKey"/>) is the key of an earlier
    /// upload of the user for another endpoint, other route values, another query string, or another file name, length, content type or command.
    /// </summary>
    /// <remarks>
    /// Thrown as a <see cref="ConflictApiException"/>.
    /// </remarks>
    public const string KeyConflict = "upload-key-conflict";

    /// <summary>
    /// The user has an upload with the ID of a creation that the service is removing, such as one that was cancelled a moment ago, and it was not removed
    /// within the few seconds that the creation waits for it.
    /// </summary>
    /// <remarks>
    /// Thrown as a <see cref="ServiceUnavailableApiException"/>, with <see cref="ApiException.RetryAfter"/> set: the creation made again creates the upload
    /// once the earlier one is removed.
    /// </remarks>
    public const string CreationPending = "upload-creation-pending";

    /// <summary>
    /// The length of an upload is over the maximum size of its endpoint, or the upload would need more chunks than the service allows at the largest chunk
    /// size.
    /// </summary>
    /// <remarks>
    /// Thrown as a <see cref="ContentTooLargeApiException"/>.
    /// </remarks>
    public const string TooLarge = "upload-too-large";

    /// <summary>
    /// The length of a direct upload, which uploads a file in one request, is over the largest chunk size of the service, which is the most that one
    /// request can carry.
    /// </summary>
    /// <remarks>
    /// Thrown as a <see cref="ContentTooLargeApiException"/>: the file can still be uploaded in chunks.
    /// </remarks>
    public const string DirectTooLarge = "upload-direct-too-large";

    /// <summary>
    /// The length of an upload is over the most that the open uploads of a user can have together, so that the upload can never be created, whatever other
    /// uploads the user has.
    /// </summary>
    /// <remarks>
    /// Thrown as a <see cref="ContentTooLargeApiException"/>.
    /// </remarks>
    public const string ExceedsSizeLimit = "upload-exceeds-size-limit";

    /// <summary>
    /// The user already has the maximum number of open uploads, which are the uploads that are receiving chunks or whose handler is running.
    /// </summary>
    /// <remarks>
    /// Thrown as a <see cref="TooManyRequestsApiException"/>, with <see cref="ApiException.RetryAfter"/> set to a second: the creation made again succeeds
    /// once another upload of the user has ended.
    /// </remarks>
    public const string LimitReached = "upload-limit-reached";

    /// <summary>
    /// The open uploads of the user, which are the uploads that are receiving chunks or whose handler is running, would together be longer than the
    /// service allows for a user.
    /// </summary>
    /// <remarks>
    /// Thrown as a <see cref="TooManyRequestsApiException"/>, with <see cref="ApiException.RetryAfter"/> set to a second: the creation made again succeeds
    /// once other uploads of the user have ended.
    /// </remarks>
    public const string SizeLimitReached = "upload-size-limit-reached";

    /// <summary>
    /// The open uploads have reached their total size limit, or the upload store has no room for the upload.
    /// </summary>
    /// <remarks>
    /// Thrown as a <see cref="ServiceUnavailableApiException"/>, with <see cref="ApiException.RetryAfter"/> set.
    /// </remarks>
    public const string StorageFull = "upload-storage-full";

    /// <summary>
    /// The upload store failed, such as a share that the service could not reach for a moment.
    /// </summary>
    /// <remarks>
    /// Thrown as a <see cref="ServiceUnavailableApiException"/>, with <see cref="ApiException.RetryAfter"/> set.
    /// </remarks>
    public const string StoreUnavailable = "upload-store-unavailable";

    /// <summary>
    /// The upload is unknown, expired or cancelled, or it belongs to another user, another endpoint or other route values.
    /// </summary>
    /// <remarks>
    /// Thrown as a <see cref="NotFoundApiException"/>.
    /// </remarks>
    public const string NotFound = "upload-not-found";

    /// <summary>
    /// A chunk's <c>Content-Range</c> header is missing or not valid, does not cover exactly one chunk of the upload, or the length of the chunk's body
    /// differs from it, or the length of the body of a direct upload differs from the length of the file.
    /// </summary>
    /// <remarks>
    /// Thrown as a <see cref="BadRequestApiException"/>.
    /// </remarks>
    public const string ChunkInvalid = "upload-chunk-invalid";

    /// <summary>
    /// A chunk arrives for an upload whose handler is running or has finished.
    /// </summary>
    /// <remarks>
    /// Thrown as a <see cref="ConflictApiException"/>.
    /// </remarks>
    public const string Closed = "upload-closed";

    /// <summary>
    /// The completion of an upload arrives while chunks of the upload are missing.
    /// </summary>
    /// <remarks>
    /// Thrown as a <see cref="ConflictApiException"/>.
    /// </remarks>
    public const string Incomplete = "upload-incomplete";

    /// <summary>
    /// The query string of the completion of an upload differs from the one the upload was created with, other than the user ID precondition, so that the
    /// handler would bind other values than the validator did.
    /// </summary>
    /// <remarks>
    /// Thrown as a <see cref="ConflictApiException"/>.
    /// </remarks>
    public const string QueryMismatch = "upload-query-mismatch";

    /// <summary>
    /// The server that a completion of an upload reached is stopping, while the completion waits for the handler that runs on another server or before the
    /// completion starts the handler.
    /// </summary>
    /// <remarks>
    /// Thrown as a <see cref="ServiceUnavailableApiException"/>, with <see cref="ApiException.RetryAfter"/> set: the completion made again gets the
    /// handler's run from another server, or starts it there.
    /// </remarks>
    public const string ServerStopping = "upload-server-stopping";

    /// <summary>
    /// The upload was cancelled with <c>DELETE {path}/uploads/{id}</c>, and its handler did not finish: it was not started, or it stopped when its
    /// cancellation token was cancelled.
    /// </summary>
    /// <remarks>
    /// The upload is removed. Reported as a <see cref="ConflictApiException"/> by the completions of the upload that were attached to its run.
    /// </remarks>
    public const string Cancelled = "upload-cancelled";

    /// <summary>
    /// The upload that a <c>DELETE {path}/uploads/{id}</c> cancels has finished: its handler ended with an outcome before the cancellation, and the outcome
    /// stays until the service no longer keeps it.
    /// </summary>
    /// <remarks>
    /// Thrown as a <see cref="ConflictApiException"/>.
    /// </remarks>
    public const string Finished = "upload-finished";

    /// <summary>
    /// The service could not write or read the cancellation of an upload, which it keeps in its short-lived store, such as one that it could not reach for
    /// a moment: when it answers a <c>DELETE {path}/uploads/{id}</c>, or before it starts the upload's handler, which it then leaves unstarted.
    /// </summary>
    /// <remarks>
    /// Thrown as a <see cref="ServiceUnavailableApiException"/>, with <see cref="ApiException.RetryAfter"/> set: the request made again tries again.
    /// </remarks>
    public const string CancellationUnavailable = "upload-cancellation-unavailable";

    /// <summary>
    /// The handler of the upload stopped before it finished, because the service stopped or failed while it ran, because the server that ran it lost the
    /// claim of its run, or because it ran past its time limit.
    /// </summary>
    /// <remarks>
    /// Reported as a <see cref="ServerErrorApiException"/>. This is the upload's final outcome: the handler does not run again, so the file is uploaded again
    /// to retry.
    /// </remarks>
    public const string Interrupted = "upload-interrupted";

    /// <summary>
    /// The stored outcome of an upload can't be read, such as one that a server of the directory store left partly written when it stopped while it stored
    /// it.
    /// </summary>
    /// <remarks>
    /// Reported as a <see cref="ServerErrorApiException"/>. This is the upload's final outcome: the handler does not run again, so the file is uploaded again
    /// to retry. The directory store reads an outcome that was damaged once it was stored, whose file holds the upload's record too, as an upload that
    /// doesn't exist (<see cref="NotFound"/>).
    /// </remarks>
    public const string OutcomeUnreadable = "upload-outcome-unreadable";
}
