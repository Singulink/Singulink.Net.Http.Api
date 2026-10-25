using System.Diagnostics.CodeAnalysis;

namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// Gets the limits and the property that the errors of chunked uploads (see <see cref="UploadErrorCodes"/>) name in their messages, of the errors that
/// its methods list, which the errors keep in their <see cref="Exception.Data"/>. An exception handler gets the errors with their values (see
/// <see cref="ApiExceptionHandler.MapApiExceptionAsync"/>).
/// </summary>
public static class UploadErrorData
{
    internal const string MaxSizeKey = "MaxSize";
    internal const string MaxUploadsPerUserKey = "MaxUploadsPerUser";
    internal const string MaxTotalSizePerUserKey = "MaxTotalSizePerUser";
    internal const string PropertyKey = "Property";

    /// <summary>
    /// Gets the largest length of an upload, in bytes, of an error with <see cref="UploadErrorCodes.TooLarge"/>.
    /// </summary>
    /// <returns><see langword="true"/> if the exception has the value; otherwise, <see langword="false"/>.</returns>
    public static bool TryGetMaxSize(Exception exception, out long maxSize) => TryGet(exception, MaxSizeKey, out maxSize);

    /// <summary>
    /// Gets the largest number of open uploads of a user, of an error with <see cref="UploadErrorCodes.LimitReached"/>.
    /// </summary>
    /// <returns><see langword="true"/> if the exception has the value; otherwise, <see langword="false"/>.</returns>
    public static bool TryGetMaxUploadsPerUser(Exception exception, out int maxUploadsPerUser)
    {
        return TryGet(exception, MaxUploadsPerUserKey, out maxUploadsPerUser);
    }

    /// <summary>
    /// Gets the largest total length of the open uploads of a user, in bytes, of an error with <see cref="UploadErrorCodes.SizeLimitReached"/> or
    /// <see cref="UploadErrorCodes.ExceedsSizeLimit"/>.
    /// </summary>
    /// <returns><see langword="true"/> if the exception has the value; otherwise, <see langword="false"/>.</returns>
    public static bool TryGetMaxTotalSizePerUser(Exception exception, out long maxTotalSizePerUser)
    {
        return TryGet(exception, MaxTotalSizePerUserKey, out maxTotalSizePerUser);
    }

    /// <summary>
    /// Gets the property of the creation that is not valid, of an error with <see cref="UploadErrorCodes.Invalid"/>: <c>fileName</c>, <c>length</c>,
    /// <c>contentType</c>, <c>chunkSize</c>, <c>idempotencyKey</c> or <c>command</c>.
    /// </summary>
    /// <returns><see langword="true"/> if the exception has the value; otherwise, <see langword="false"/>.</returns>
    public static bool TryGetProperty(Exception exception, [NotNullWhen(true)] out string? property) => TryGet(exception, PropertyKey, out property);

    private static bool TryGet<T>(Exception exception, string key, [MaybeNullWhen(false)] out T value)
    {
        if (exception.Data[key] is T data)
        {
            value = data;
            return true;
        }

        value = default;
        return false;
    }
}
