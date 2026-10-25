using System.Text.Json;

namespace Singulink.Net.Http.Api.DataContracts;

/// <summary>
/// Represents a request to create a chunked upload: the file's name, length and content type, the chunk size the client asks for, the app's command, and
/// a key that makes a repeated creation get the same upload.
/// </summary>
public sealed class CreateUploadCommand
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CreateUploadCommand"/> class.
    /// </summary>
    /// <param name="fileName">The name of the file.</param>
    /// <param name="length">The length of the file in bytes.</param>
    /// <param name="contentType">The content type of the file, if known.</param>
    /// <param name="chunkSize">The chunk size the client asks for in bytes, or <see langword="null"/> for the service's default.</param>
    /// <param name="command">The app's command as JSON, or <see langword="null"/> if there is none.</param>
    /// <param name="idempotencyKey">A key that the client chose for the upload, or <see langword="null"/> for none.</param>
    public CreateUploadCommand(
        string fileName, long length, string? contentType = null, int? chunkSize = null, JsonElement? command = null, string? idempotencyKey = null)
    {
        FileName = fileName;
        Length = length;
        ContentType = contentType;
        ChunkSize = chunkSize;
        Command = command;
        IdempotencyKey = idempotencyKey;
    }

    /// <summary>
    /// Gets the name of the file.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// Gets the length of the file in bytes. The upload has exactly this many bytes.
    /// </summary>
    public long Length { get; }

    /// <summary>
    /// Gets the content type of the file, or <see langword="null"/> if it is not known.
    /// </summary>
    public string? ContentType { get; }

    /// <summary>
    /// Gets the chunk size the client asks for in bytes, or <see langword="null"/> for the service's default. The service keeps the size within its
    /// bounds, and the created upload has the size it chose (see <see cref="UploadSession.ChunkSize"/>).
    /// </summary>
    public int? ChunkSize { get; }

    /// <summary>
    /// Gets the app's command as JSON, which the service reads with the app's JSON options, or <see langword="null"/> if there is none.
    /// </summary>
    public JsonElement? Command { get; }

    /// <summary>
    /// Gets a key that the client chose for the upload, of 1 to 128 characters, or <see langword="null"/> for none. A creation with the key of an earlier
    /// creation of the user, such as one repeated after its response was lost, gets the upload that the earlier one created if it is for the same
    /// endpoint, query string, file and command, and fails with <see cref="UploadErrorCodes.KeyConflict"/> if not.
    /// </summary>
    public string? IdempotencyKey { get; }
}
