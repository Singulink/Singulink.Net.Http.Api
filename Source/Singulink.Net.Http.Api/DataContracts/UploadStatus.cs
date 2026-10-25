namespace Singulink.Net.Http.Api.DataContracts;

/// <summary>
/// Represents the status of a chunked upload: its state, its length and chunk size, the chunks that have not arrived and when it expires.
/// </summary>
public sealed class UploadStatus
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UploadStatus"/> class.
    /// </summary>
    /// <param name="state">The state of the upload.</param>
    /// <param name="length">The length of the file in bytes.</param>
    /// <param name="chunkSize">The chunk size in bytes.</param>
    /// <param name="missingChunks">The ranges of the indexes of the chunks that have not arrived.</param>
    /// <param name="expiresUtc">The time the upload expires, in UTC.</param>
    public UploadStatus(UploadState state, long length, int chunkSize, IReadOnlyList<ChunkRange> missingChunks, DateTime expiresUtc)
    {
        State = state;
        Length = length;
        ChunkSize = chunkSize;
        MissingChunks = missingChunks;
        ExpiresUtc = expiresUtc;
    }

    /// <summary>
    /// Gets the state of the upload.
    /// </summary>
    public UploadState State { get; }

    /// <summary>
    /// Gets the length of the file in bytes.
    /// </summary>
    public long Length { get; }

    /// <summary>
    /// Gets the chunk size in bytes.
    /// </summary>
    public int ChunkSize { get; }

    /// <summary>
    /// Gets the ranges of the indexes of the chunks that have not arrived, in ascending order. Empty once every chunk has arrived.
    /// </summary>
    public IReadOnlyList<ChunkRange> MissingChunks { get; }

    /// <summary>
    /// Gets the time the upload expires, in UTC: while it is receiving chunks, the time it expires if no chunk arrives before it; while its handler runs,
    /// the time the handler is stopped if it is still running; and once it has finished, the time its outcome is no longer kept.
    /// </summary>
    public DateTime ExpiresUtc { get; }
}
