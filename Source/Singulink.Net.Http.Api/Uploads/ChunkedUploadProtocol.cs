namespace Singulink.Net.Http.Api;

/// <summary>
/// The constants of the chunked upload protocol that the service and the client share. For an upload endpoint at <c>{path}</c>, <c>POST
/// {path}/uploads</c> creates an upload, <c>{path}/uploads/{id}</c> is the upload (<c>PUT</c> sends a chunk, <c>GET</c> gets its status and
/// <c>DELETE</c> aborts it), and <c>POST {path}/uploads/{id}/complete</c> completes it. <c>POST {path}/uploads/direct</c> uploads a file directly, in
/// one request: it creates an upload with the file as its body and completes it.
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
    /// The path segment, under the path of an upload endpoint's uploads, that uploads a file directly, in one request.
    /// </summary>
    public const string DirectSegment = "direct";

    /// <summary>
    /// The header of a direct upload with the values of its creation: the JSON of a <see cref="DataContracts.CreateUploadCommand"/>, as the body of a
    /// creation has it, in UTF-8, base64url-encoded, which the toolkit's client sends without padding.
    /// </summary>
    public const string CreationHeaderName = "Upload-Creation";

    /// <summary>
    /// The longest value of the creation header of a direct upload: 8 KiB, which keeps the request's headers well within the limits of servers and proxies
    /// (IIS and http.sys take up to 16 KB in the request line and all headers together, and Kestrel up to 32 KB in all headers).
    /// </summary>
    public const int MaxCreationHeaderLength = 8 * 1024;

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
