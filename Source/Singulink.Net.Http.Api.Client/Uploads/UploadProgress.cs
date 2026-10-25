namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// The progress of an upload in chunks.
/// </summary>
/// <param name="BytesUploaded">The number of bytes in the chunks that the service has acknowledged, and on platforms other than the browser also the
/// bytes that the requests of the chunks being sent have written so far (see <see cref="UploadOptions.Progress"/>).</param>
/// <param name="Length">The length of the content in bytes.</param>
/// <param name="IsProcessing">A value indicating whether the service has received every chunk and the upload is being completed.</param>
public readonly record struct UploadProgress(long BytesUploaded, long Length, bool IsProcessing);
