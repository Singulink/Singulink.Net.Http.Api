namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// The progress of an upload in chunks.
/// </summary>
public readonly record struct UploadProgress
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UploadProgress"/> struct.
    /// </summary>
    /// <param name="bytesUploaded">The number of bytes in the chunks that the service has acknowledged, and on platforms other than the browser also the
    /// bytes that the requests of the chunks being sent have written so far (see <see cref="UploadOptions.Progress"/>).</param>
    /// <param name="length">The length of the content in bytes.</param>
    /// <param name="isProcessing">A value indicating whether the service has received every chunk and the upload is being completed.</param>
    public UploadProgress(long bytesUploaded, long length, bool isProcessing)
    {
        BytesUploaded = bytesUploaded;
        Length = length;
        IsProcessing = isProcessing;
    }

    /// <summary>
    /// Gets the number of bytes in the chunks that the service has acknowledged, and on platforms other than the browser also the bytes that the requests
    /// of the chunks being sent have written so far (see <see cref="UploadOptions.Progress"/>).
    /// </summary>
    public long BytesUploaded { get; }

    /// <summary>
    /// Gets the length of the content in bytes.
    /// </summary>
    public long Length { get; }

    /// <summary>
    /// Gets a value indicating whether the service has received every chunk and the upload is being completed.
    /// </summary>
    public bool IsProcessing { get; }
}
