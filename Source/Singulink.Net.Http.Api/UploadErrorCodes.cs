namespace Singulink.Net.Http.Api;

/// <summary>
/// Contains the error codes (<see cref="ApiException.ErrorCode"/>) of the errors that the chunked uploads of the <c>Singulink.Net.Http.Api.Service</c>
/// package report, so that apps and their clients can tell the failures apart and word them in their own way.
/// </summary>
public static class UploadErrorCodes
{
    /// <summary>
    /// The creation of an upload is not valid: its file name is empty or longer than 255 characters, its length is negative, its content type is longer
    /// than 255 characters, its chunk size is not positive, its idempotency key is empty or longer than 128 characters, or its command cannot be read or
    /// is missing where the endpoint's validator or handler requires one.
    /// Thrown as a <see cref="BadRequestApiException"/>.
    /// </summary>
    public const string Invalid = "upload-invalid";

    /// <summary>
    /// The body of a creation of an upload can't be read as a creation: it is not valid JSON, it is <c>null</c>, or it lacks a value that a creation
    /// requires or has a value of the wrong type. Thrown as a <see cref="BadRequestApiException"/>.
    /// </summary>
    public const string CreationUnreadable = "upload-creation-unreadable";

    /// <summary>
    /// The body of a creation of an upload is not JSON, by its content type. Thrown as an <see cref="UnsupportedMediaTypeApiException"/>.
    /// </summary>
    public const string CreationNotJson = "upload-creation-not-json";

    /// <summary>
    /// The body of a creation of an upload is larger than 64 KiB, or than a smaller request size limit of the server. Thrown as a
    /// <see cref="ContentTooLargeApiException"/>.
    /// </summary>
    public const string CreationTooLarge = "upload-creation-too-large";

    /// <summary>
    /// The idempotency key of a creation (see <see cref="DataContracts.CreateUploadCommand.IdempotencyKey"/>) is the key of an earlier upload of the user
    /// for another endpoint, other route values, another query string, or another file name, length, content type or command. Thrown as a
    /// <see cref="ConflictApiException"/>.
    /// </summary>
    public const string KeyConflict = "upload-key-conflict";

    /// <summary>
    /// The idempotency key of a creation (see <see cref="DataContracts.CreateUploadCommand.IdempotencyKey"/>) is reserved by an earlier creation of the
    /// user that has not finished creating its upload yet, such as one whose response the client did not wait for, or the stale reservation of a creation
    /// that stopped can't be released yet. Thrown as a <see cref="ServiceUnavailableApiException"/>, with <see cref="ApiException.RetryAfter"/> set: the
    /// creation made again gets the upload once it is created, or creates its own once the stale reservation is released.
    /// </summary>
    public const string CreationPending = "upload-creation-pending";

    /// <summary>
    /// The length of an upload is over the maximum size of its endpoint, or the upload would need more chunks than the service allows at the largest chunk
    /// size. Thrown as a <see cref="ContentTooLargeApiException"/>.
    /// </summary>
    public const string TooLarge = "upload-too-large";

    /// <summary>
    /// The length of an upload is over the most that the open uploads of a user can have together, so that the upload can never be created, whatever other
    /// uploads the user has. Thrown as a <see cref="ContentTooLargeApiException"/>.
    /// </summary>
    public const string ExceedsSizeLimit = "upload-exceeds-size-limit";

    /// <summary>
    /// The user already has the maximum number of open uploads, which are the uploads that are receiving chunks or whose handler is running. Thrown as a
    /// <see cref="TooManyRequestsApiException"/>.
    /// </summary>
    public const string LimitReached = "upload-limit-reached";

    /// <summary>
    /// The open uploads of the user, which are the uploads that are receiving chunks or whose handler is running, would together be longer than the
    /// service allows for a user. Thrown as a <see cref="TooManyRequestsApiException"/>.
    /// </summary>
    public const string SizeLimitReached = "upload-size-limit-reached";

    /// <summary>
    /// The open uploads have reached their total size limit, or the upload store has no room for the upload. Thrown as a
    /// <see cref="ServiceUnavailableApiException"/>, with <see cref="ApiException.RetryAfter"/> set.
    /// </summary>
    public const string StorageFull = "upload-storage-full";

    /// <summary>
    /// The upload store failed, such as a share that the service could not reach for a moment. Thrown as a <see cref="ServiceUnavailableApiException"/>,
    /// with <see cref="ApiException.RetryAfter"/> set.
    /// </summary>
    public const string StoreUnavailable = "upload-store-unavailable";

    /// <summary>
    /// The upload is unknown, expired or aborted, or it belongs to another user, another endpoint or other route values. Thrown as a
    /// <see cref="NotFoundApiException"/>.
    /// </summary>
    public const string NotFound = "upload-not-found";

    /// <summary>
    /// A chunk's <c>Content-Range</c> header is missing or not valid, does not cover exactly one chunk of the upload, or the length of the chunk's body
    /// differs from it. Thrown as a <see cref="BadRequestApiException"/>.
    /// </summary>
    public const string ChunkInvalid = "upload-chunk-invalid";

    /// <summary>
    /// A chunk arrives for an upload whose handler is running or has finished. Thrown as a <see cref="ConflictApiException"/>.
    /// </summary>
    public const string Closed = "upload-closed";

    /// <summary>
    /// The completion of an upload arrives while chunks of the upload are missing. Thrown as a <see cref="ConflictApiException"/>.
    /// </summary>
    public const string Incomplete = "upload-incomplete";

    /// <summary>
    /// The query string of the completion of an upload differs from the one the upload was created with, other than the user ID precondition, so that the
    /// handler would bind other values than the validator did. Thrown as a <see cref="ConflictApiException"/>.
    /// </summary>
    public const string QueryMismatch = "upload-query-mismatch";

    /// <summary>
    /// The server that a completion of an upload reached is stopping, while the completion waits for the handler that runs on another server or before the
    /// completion starts the handler. Thrown as a <see cref="ServiceUnavailableApiException"/>, with <see cref="ApiException.RetryAfter"/> set: the
    /// completion made again gets the handler's run from another server, or starts it there.
    /// </summary>
    public const string ServerStopping = "upload-server-stopping";

    /// <summary>
    /// The handler of the upload stopped before it finished, because the service stopped or failed while it ran, because the server that ran it lost the
    /// claim of its run, or because it ran past its time limit. Reported as a <see cref="ServerErrorApiException"/>. This is the upload's final outcome:
    /// the handler does not run again, so the file is uploaded again to retry.
    /// </summary>
    public const string Interrupted = "upload-interrupted";

    /// <summary>
    /// The stored outcome of an upload can't be read, such as a file of the directory store that was damaged. Reported as a
    /// <see cref="ServerErrorApiException"/>. This is the upload's final outcome: the handler does not run again, so the file is uploaded again to retry.
    /// </summary>
    public const string OutcomeUnreadable = "upload-outcome-unreadable";
}
