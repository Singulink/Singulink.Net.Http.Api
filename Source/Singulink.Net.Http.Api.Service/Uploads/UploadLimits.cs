namespace Singulink.Net.Http.Api.Service.Uploads;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Represents the limits of the open uploads of a user, which an <see cref="IUploadLimitsProvider"/> returns. Open uploads are the uploads that are
/// receiving chunks or whose handler is running.
/// </summary>
public sealed class UploadLimits
{
    /// <summary>
    /// Gets the largest number of open uploads of the user, counted across all upload endpoints. Must be at least 1.
    /// </summary>
    /// <remarks>
    /// A creation over it fails with <see cref="UploadErrorCodes.LimitReached"/>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public required int MaxUploadsPerUser {
        get;
        init {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// Gets the largest total length of the open uploads of the user, in bytes, counted across all upload endpoints, or <see langword="null"/> for no limit.
    /// Must be at least zero. Default is <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// A creation over it fails with <see cref="UploadErrorCodes.SizeLimitReached"/>, and an upload longer than it can't be created, and fails with
    /// <see cref="UploadErrorCodes.ExceedsSizeLimit"/>, so 0 allows only empty files.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public long? MaxTotalSizePerUser {
        get;
        init {
            if (value is { } size)
                ArgumentOutOfRangeException.ThrowIfNegative(size, nameof(value));

            field = value;
        }
    }
}
