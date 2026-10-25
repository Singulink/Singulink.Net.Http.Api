namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// Specifies the result of writing a chunk of an upload to an <see cref="IUploadStore"/> (see <see cref="IUploadStore.WriteChunkAsync"/>).
/// </summary>
public enum ChunkWriteResult
{
    /// <summary>
    /// The chunk was written and is reported as received.
    /// </summary>
    Written,

    /// <summary>
    /// The upload no longer exists.
    /// </summary>
    NotFound,

    /// <summary>
    /// The run of the upload's handler is claimed, or the upload has an outcome, so the chunk was not written.
    /// </summary>
    Closed,

    /// <summary>
    /// The store has no room for the chunk, such as on a file system that could not reserve the upload's space when it was created, so the chunk is
    /// missing. The service reports it as <see cref="UploadErrorCodes.StorageFull"/>.
    /// </summary>
    NoRoom,
}
