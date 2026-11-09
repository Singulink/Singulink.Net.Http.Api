namespace Singulink.Net.Http.Api.DataContracts;

/// <summary>
/// Represents a created chunked upload: its ID, the chunk size the service chose and when it expires.
/// </summary>
public sealed class UploadSession
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UploadSession"/> class.
    /// </summary>
    /// <param name="id">The upload ID.</param>
    /// <param name="chunkSize">The chunk size in bytes.</param>
    /// <param name="expiresUtc">The time the upload expires if no chunk arrives before it, in UTC.</param>
    public UploadSession(string id, int chunkSize, DateTime expiresUtc)
    {
        Id = id;
        ChunkSize = chunkSize;
        ExpiresUtc = expiresUtc;
    }

    /// <summary>
    /// Gets the upload ID: the ID of the creation (see <see cref="CreateUploadCommand.Id"/>), or a GUID that the service chose for a creation without one,
    /// in its 36-character form.
    /// </summary>
    /// <remarks>
    /// An upload that a 4.x service created has an ID of 43 base64url characters, which a service of a later version still uses while the upload is in
    /// progress.
    /// </remarks>
    public string Id { get; }

    /// <summary>
    /// Gets the chunk size in bytes.
    /// </summary>
    /// <remarks>
    /// Every chunk of the upload has this length, except the last, which has the rest of the file.
    /// </remarks>
    public int ChunkSize { get; }

    /// <summary>
    /// Gets the time the upload expires, in UTC: while it is receiving chunks, the time it expires if no chunk arrives before it, which each chunk moves
    /// later.
    /// </summary>
    /// <remarks>
    /// A repeated creation (see <see cref="CreateUploadCommand.Id"/>) can get an upload whose handler is running, or has finished, whose expiry is the time
    /// its handler is stopped, or the time its outcome is no longer kept, as in its status (see <see cref="UploadStatus.ExpiresUtc"/>).
    /// </remarks>
    public DateTime ExpiresUtc { get; }
}
