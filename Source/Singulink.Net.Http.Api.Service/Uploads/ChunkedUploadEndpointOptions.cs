namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// Provides the options of an upload endpoint mapped with <c>MapChunkedUpload</c>.
/// </summary>
public sealed class ChunkedUploadEndpointOptions
{
    /// <summary>
    /// Gets or sets the largest upload of the endpoint, in bytes, or <see langword="null"/> for <see cref="ChunkedUploadOptions.MaxSize"/>. Default is
    /// <see langword="null"/>.
    /// </summary>
    public long? MaxSize {
        get;
        set {
            if (value is { } size)
                ArgumentOutOfRangeException.ThrowIfNegative(size, nameof(value));

            field = value;
        }
    }
}
