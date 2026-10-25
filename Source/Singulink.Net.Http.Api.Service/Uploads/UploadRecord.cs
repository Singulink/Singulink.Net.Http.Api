namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// Represents what an <see cref="IUploadStore"/> keeps of an upload from its creation: its ID, its owner, its length and chunk size, its creation time and
/// the service's metadata.
/// </summary>
public sealed class UploadRecord
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UploadRecord"/> class.
    /// </summary>
    /// <param name="id">The upload ID.</param>
    /// <param name="userId">The ID of the user the upload belongs to.</param>
    /// <param name="length">The length of the file in bytes.</param>
    /// <param name="chunkSize">The chunk size in bytes.</param>
    /// <param name="createdUtc">The time the upload was created, in UTC.</param>
    /// <param name="metadata">The service's metadata of the upload.</param>
    /// <param name="idempotencyKey">The idempotency key that is reserved for the upload, if its creation has one.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative or <paramref name="chunkSize"/> is not positive.</exception>
    public UploadRecord(string id, string userId, long length, int chunkSize, DateTime createdUtc, string metadata, string? idempotencyKey = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);

        Id = id;
        UserId = userId;
        Length = length;
        ChunkSize = chunkSize;
        CreatedUtc = createdUtc;
        Metadata = metadata;
        IdempotencyKey = idempotencyKey;
    }

    /// <summary>
    /// Gets the upload ID: 256 random bits as 43 base64url characters, unique across users.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the ID of the user the upload belongs to (<see cref="ISessionToken.UserId"/>).
    /// </summary>
    public string UserId { get; }

    /// <summary>
    /// Gets the length of the file in bytes.
    /// </summary>
    public long Length { get; }

    /// <summary>
    /// Gets the chunk size in bytes. Every chunk has this length, except the last, which has the rest of the file.
    /// </summary>
    public int ChunkSize { get; }

    /// <summary>
    /// Gets the number of chunks, which is 0 for an empty file.
    /// </summary>
    public int ChunkCount => (int)((Length / ChunkSize) + (Length % ChunkSize > 0 ? 1 : 0));

    /// <summary>
    /// Gets the time the upload was created, in UTC.
    /// </summary>
    public DateTime CreatedUtc { get; }

    /// <summary>
    /// Gets the service's metadata of the upload, such as the file's name and the endpoint the upload belongs to, as text in a format of the service's
    /// own.
    /// </summary>
    public string Metadata { get; }

    /// <summary>
    /// Gets the idempotency key that is reserved for the upload (see <see cref="IUploadStore.ReserveKeyAsync"/>), or <see langword="null"/> if its
    /// creation has none. Deleting the upload releases it.
    /// </summary>
    public string? IdempotencyKey { get; }

    /// <summary>
    /// Gets the offset of a chunk in the file.
    /// </summary>
    /// <param name="index">The index of the chunk.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative or not less than <see cref="ChunkCount"/>.</exception>
    public long GetChunkOffset(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, ChunkCount);

        return (long)index * ChunkSize;
    }

    /// <summary>
    /// Gets the length of a chunk in bytes: <see cref="ChunkSize"/>, or the rest of the file for the last chunk.
    /// </summary>
    /// <param name="index">The index of the chunk.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative or not less than <see cref="ChunkCount"/>.</exception>
    public int GetChunkLength(int index) => (int)Math.Min(ChunkSize, Length - GetChunkOffset(index));
}
