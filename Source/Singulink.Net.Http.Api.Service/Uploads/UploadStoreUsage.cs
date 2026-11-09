namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// How much room an <see cref="IUploadStore"/> has (see <see cref="IUploadStore.GetUsageAsync"/>). Each value is <see langword="null"/> when the store
/// can't tell it.
/// </summary>
public sealed class UploadStoreUsage
{
    /// <summary>
    /// Gets a usage whose values are all unknown.
    /// </summary>
    public static UploadStoreUsage Unknown { get; } = new();

    /// <summary>
    /// Gets where the store keeps its uploads, such as the directory of the directory store, for display, or <see langword="null"/> if the store doesn't
    /// report it.
    /// </summary>
    public string? Location { get; init; }

    /// <summary>
    /// Gets the space that is available to the store, in bytes, such as the free space of the directory store's volume that the process can use, or
    /// <see langword="null"/> if the store doesn't report it.
    /// </summary>
    public long? AvailableBytes { get; init; }

    /// <summary>
    /// Gets the total space of the storage the store keeps its uploads on, in bytes, or <see langword="null"/> if the store doesn't report it.
    /// </summary>
    public long? TotalBytes { get; init; }
}
