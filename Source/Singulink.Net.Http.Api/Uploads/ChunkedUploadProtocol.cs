namespace Singulink.Net.Http.Api;

/// <summary>
/// The constants of the chunked upload protocol that the service and the client share. For an upload endpoint at <c>{path}</c>, <c>POST
/// {path}/uploads</c> creates an upload, <c>{path}/uploads/{id}</c> is the upload (<c>PUT</c> sends a chunk, <c>GET</c> gets its status and
/// <c>DELETE</c> aborts it), and <c>POST {path}/uploads/{id}/complete</c> completes it.
/// </summary>
internal static class ChunkedUploadProtocol
{
    /// <summary>
    /// The path segment of an upload endpoint's uploads.
    /// </summary>
    public const string UploadsSegment = "uploads";

    /// <summary>
    /// The path segment, under an upload's path, that completes the upload.
    /// </summary>
    public const string CompleteSegment = "complete";

    /// <summary>
    /// The content type of the body of a chunk.
    /// </summary>
    public const string ChunkContentType = "application/octet-stream";

    /// <summary>
    /// The smallest chunk size the service uses: 5 MiB, the smallest part size that object stores such as Amazon S3 accept for all but the last part.
    /// Stores and the service's options can only raise it.
    /// </summary>
    public const int MinChunkSize = 5 << 20;

    /// <summary>
    /// The chunk size the service uses by default: 5 MiB.
    /// </summary>
    public const int DefaultChunkSize = 5 << 20;

    /// <summary>
    /// The largest chunk size the service uses: 64 MiB, which keeps every request well under the smallest request body limit of Cloudflare's plans
    /// (100 MB). Stores and the service's options can only lower it.
    /// </summary>
    public const int MaxChunkSize = 64 << 20;
}
