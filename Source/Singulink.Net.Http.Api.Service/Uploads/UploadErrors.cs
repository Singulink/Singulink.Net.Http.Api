using System.Globalization;
using System.Text.Json;

namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// Creates the errors that chunked uploads report, with their messages and error codes (see <see cref="UploadErrorCodes"/>).
/// </summary>
internal static class UploadErrors
{
    private const string InterruptedMessage = "Processing the upload was interrupted. Please upload the file again.";

    public static BadRequestApiException FileNameInvalid() => Invalid("fileName", "The file name is empty or longer than 255 characters.");

    public static BadRequestApiException LengthInvalid() => Invalid("length", "The length of the file is negative.");

    public static BadRequestApiException ContentTypeInvalid() => Invalid("contentType", "The content type of the file is longer than 255 characters.");

    public static BadRequestApiException ChunkSizeInvalid() => Invalid("chunkSize", "The chunk size is not positive.");

    public static BadRequestApiException IdInvalid() => Invalid("id", "The upload's ID is the empty GUID.");

    public static BadRequestApiException IdempotencyKeyInvalid() => Invalid("idempotencyKey", "The idempotency key is empty or longer than 128 characters.");

    public static ContentTooLargeApiException DirectTooLarge(int maxSize)
    {
        var exception = new ContentTooLargeApiException(
            $"The file is too large to upload directly. Files of up to {FormatSize(maxSize)} can be uploaded in one request, and larger ones in chunks.") {
            ErrorCode = UploadErrorCodes.DirectTooLarge,
        };

        exception.Data[UploadErrorData.MaxSizeKey] = (long)maxSize;
        return exception;
    }

    public static ConflictApiException IdConflict()
    {
        return new ConflictApiException("The upload's ID is the ID of another upload.") { ErrorCode = UploadErrorCodes.IdConflict };
    }

    public static ConflictApiException KeyConflict()
    {
        return new ConflictApiException("The idempotency key is the key of another upload.") { ErrorCode = UploadErrorCodes.KeyConflict };
    }

    public static ServiceUnavailableApiException CreationPending(TimeSpan retryAfter)
    {
        return new ServiceUnavailableApiException("An earlier upload with the same ID is still being removed. Please try again.") {
            ErrorCode = UploadErrorCodes.CreationPending,
            RetryAfter = retryAfter,
        };
    }

    public static BadRequestApiException CommandMissing() => Invalid("command", "The upload has no command.");

    public static BadRequestApiException CommandInvalid(JsonException exception) => Invalid("command", "The upload's command is not valid.", exception);

    public static ContentTooLargeApiException TooLarge(long maxSize)
    {
        var exception = new ContentTooLargeApiException($"The file is too large. Files of up to {FormatSize(maxSize)} can be uploaded.") {
            ErrorCode = UploadErrorCodes.TooLarge,
        };

        exception.Data[UploadErrorData.MaxSizeKey] = maxSize;
        return exception;
    }

    public static ContentTooLargeApiException ExceedsSizeLimit(long maxTotalSizePerUser)
    {
        string message = $"The file is too large. The uploads in progress can take up {FormatSize(maxTotalSizePerUser)} together.";
        var exception = new ContentTooLargeApiException(message) { ErrorCode = UploadErrorCodes.ExceedsSizeLimit };

        exception.Data[UploadErrorData.MaxTotalSizePerUserKey] = maxTotalSizePerUser;
        return exception;
    }

    /// <summary>
    /// Gets the error of a creation whose body is longer than the largest creation, or than the server's own request size limit if the length is
    /// <see langword="null"/>.
    /// </summary>
    public static ContentTooLargeApiException CreationTooLarge(int? maxLength, Exception? innerException = null)
    {
        string message = maxLength is { } length
            ? $"The creation of an upload is larger than {FormatSize(length)}."
            : "The creation of an upload is larger than the service accepts.";

        return new ContentTooLargeApiException(message, innerException) { ErrorCode = UploadErrorCodes.CreationTooLarge };
    }

    /// <summary>
    /// Gets the error of a direct upload whose creation header is longer than the longest one.
    /// </summary>
    public static HeadersTooLargeApiException CreationHeaderTooLarge(int maxLength)
    {
        return new HeadersTooLargeApiException($"The creation header of a direct upload is larger than {FormatSize(maxLength)}.") {
            ErrorCode = UploadErrorCodes.CreationHeaderTooLarge,
        };
    }

    public static BadRequestApiException CreationUnreadable(Exception? innerException = null)
    {
        return new BadRequestApiException("The creation of an upload cannot be read.", innerException) { ErrorCode = UploadErrorCodes.CreationUnreadable };
    }

    public static UnsupportedMediaTypeApiException CreationNotJson()
    {
        return new UnsupportedMediaTypeApiException("The creation of an upload must be JSON.") { ErrorCode = UploadErrorCodes.CreationNotJson };
    }

    public static ServerErrorApiException OutcomeUnreadable()
    {
        return new ServerErrorApiException("The outcome of the upload cannot be read.") { ErrorCode = UploadErrorCodes.OutcomeUnreadable };
    }

    public static TooManyRequestsApiException LimitReached(int maxUploads, TimeSpan retryAfter)
    {
        var exception = new TooManyRequestsApiException("Too many uploads are in progress. Please wait for one to finish and try again.") {
            ErrorCode = UploadErrorCodes.LimitReached,
            RetryAfter = retryAfter,
        };

        exception.Data[UploadErrorData.MaxUploadsPerUserKey] = maxUploads;
        return exception;
    }

    public static TooManyRequestsApiException SizeLimitReached(long maxTotalSizePerUser, TimeSpan retryAfter)
    {
        string message = $"The uploads in progress can take up {FormatSize(maxTotalSizePerUser)} together. Please wait for one to finish and try again.";
        var exception = new TooManyRequestsApiException(message) { ErrorCode = UploadErrorCodes.SizeLimitReached, RetryAfter = retryAfter };

        exception.Data[UploadErrorData.MaxTotalSizePerUserKey] = maxTotalSizePerUser;
        return exception;
    }

    public static ServiceUnavailableApiException StorageFull(TimeSpan retryAfter)
    {
        return new ServiceUnavailableApiException("There is no room for the upload at the moment. Please try again later.") {
            ErrorCode = UploadErrorCodes.StorageFull,
            RetryAfter = retryAfter,
        };
    }

    public static ServiceUnavailableApiException StoreUnavailable(TimeSpan retryAfter, Exception? innerException = null)
    {
        return new ServiceUnavailableApiException("The uploads can't be reached at the moment. Please try again.", innerException) {
            ErrorCode = UploadErrorCodes.StoreUnavailable,
            RetryAfter = retryAfter,
        };
    }

    public static NotFoundApiException NotFound()
    {
        return new NotFoundApiException("The upload does not exist or has expired.") { ErrorCode = UploadErrorCodes.NotFound };
    }

    public static BadRequestApiException ChunkRangeInvalid()
    {
        return new BadRequestApiException("The chunk's Content-Range header is missing, or does not cover exactly one chunk of the upload.") {
            ErrorCode = UploadErrorCodes.ChunkInvalid,
        };
    }

    public static BadRequestApiException BodyLengthInvalid(Exception? innerException = null)
    {
        return new BadRequestApiException("The length of the body differs from the length of the file.", innerException) {
            ErrorCode = UploadErrorCodes.ChunkInvalid,
        };
    }

    public static BadRequestApiException ChunkLengthInvalid(Exception? innerException = null)
    {
        return new BadRequestApiException("The length of the chunk differs from its Content-Range header.", innerException) {
            ErrorCode = UploadErrorCodes.ChunkInvalid,
        };
    }

    public static ConflictApiException Closed()
    {
        return new ConflictApiException("The upload has all its chunks and is processed or being processed.") { ErrorCode = UploadErrorCodes.Closed };
    }

    public static ConflictApiException Incomplete()
    {
        return new ConflictApiException("Chunks of the upload are missing.") { ErrorCode = UploadErrorCodes.Incomplete };
    }

    public static ConflictApiException QueryMismatch()
    {
        return new ConflictApiException("The query string of the completion differs from the one the upload was created with.") {
            ErrorCode = UploadErrorCodes.QueryMismatch,
        };
    }

    public static ConflictApiException Cancelled()
    {
        return new ConflictApiException("The upload was cancelled before its processing finished.") { ErrorCode = UploadErrorCodes.Cancelled };
    }

    public static ConflictApiException Finished()
    {
        return new ConflictApiException("The upload has already been processed, so it can no longer be cancelled.") {
            ErrorCode = UploadErrorCodes.Finished,
        };
    }

    public static ServiceUnavailableApiException CancellationUnavailable(TimeSpan retryAfter, Exception? innerException = null)
    {
        return new ServiceUnavailableApiException("The cancellation of the upload can't be reached at the moment. Please try again.", innerException) {
            ErrorCode = UploadErrorCodes.CancellationUnavailable,
            RetryAfter = retryAfter,
        };
    }

    public static ServiceUnavailableApiException ServerStopping(TimeSpan retryAfter)
    {
        return new ServiceUnavailableApiException("The service is restarting. Please try again.") {
            ErrorCode = UploadErrorCodes.ServerStopping,
            RetryAfter = retryAfter,
        };
    }

    /// <summary>
    /// Gets the error of an upload whose handler stopped before it finished, which is the upload's final outcome.
    /// </summary>
    public static StreamErrorInfo Interrupted() => new() {
        Status = StatusCodes.Status500InternalServerError,
        Code = UploadErrorCodes.Interrupted,
        Message = InterruptedMessage,
    };

    /// <summary>
    /// Gets the exception of an upload whose handler stopped before it finished, which a completion reports.
    /// </summary>
    public static ServerErrorApiException InterruptedException()
    {
        return new ServerErrorApiException(InterruptedMessage) { ErrorCode = UploadErrorCodes.Interrupted };
    }

    /// <summary>
    /// Formats a size in the largest binary unit that divides it: <c>1 GiB</c> for 2^30 bytes.
    /// </summary>
    internal static string FormatSize(long size)
    {
        (long unit, string name) = size switch {
            >= 1L << 30 when size % (1L << 30) is 0 => (1L << 30, "GiB"),
            >= 1L << 20 when size % (1L << 20) is 0 => (1L << 20, "MiB"),
            >= 1L << 10 when size % (1L << 10) is 0 => (1L << 10, "KiB"),
            _ => (1L, "bytes"),
        };

        return string.Create(CultureInfo.InvariantCulture, $"{size / unit:N0} {name}");
    }

    private static BadRequestApiException Invalid(string property, string message, Exception? innerException = null)
    {
        var exception = new BadRequestApiException(message, innerException) { ErrorCode = UploadErrorCodes.Invalid };
        exception.Data[UploadErrorData.PropertyKey] = property;

        return exception;
    }
}
