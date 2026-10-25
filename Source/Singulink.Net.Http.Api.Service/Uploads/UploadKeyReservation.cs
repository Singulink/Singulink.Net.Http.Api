namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// Represents the reservation of an idempotency key of a user for an upload in an <see cref="IUploadStore"/>, which
/// <see cref="IUploadStore.ReserveKeyAsync"/> returns.
/// </summary>
public sealed class UploadKeyReservation
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UploadKeyReservation"/> class.
    /// </summary>
    /// <param name="uploadId">The ID of the upload that the key is reserved for.</param>
    /// <param name="reservedUtc">The time the key was reserved, in UTC.</param>
    public UploadKeyReservation(string uploadId, DateTime reservedUtc)
    {
        UploadId = uploadId;
        ReservedUtc = reservedUtc;
    }

    /// <summary>
    /// Gets the ID of the upload that the key is reserved for, which may not be created yet.
    /// </summary>
    public string UploadId { get; }

    /// <summary>
    /// Gets the time the key was reserved, in UTC.
    /// </summary>
    public DateTime ReservedUtc { get; }
}
