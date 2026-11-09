using System.Text.Json;

namespace Singulink.Net.Http.Api.DataContracts;

/// <summary>
/// Represents a request to create a chunked upload: the upload's ID, the file's name, length and content type, the chunk size the client asks for, and the
/// app's command.
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
    /// <param name="idempotencyKey">The idempotency key of a 4.x client, or <see langword="null"/> for none.</param>
    /// <param name="id">The ID that the client chose for the upload, or <see langword="null"/> for one that the service chooses.</param>
    public CreateUploadCommand(
        string fileName,
        long length,
        string? contentType = null,
        int? chunkSize = null,
        JsonElement? command = null,
        string? idempotencyKey = null,
        Guid? id = null)
    {
        FileName = fileName;
        Length = length;
        ContentType = contentType;
        ChunkSize = chunkSize;
        Command = command;
        IdempotencyKey = idempotencyKey;
        Id = id;
    }

    /// <summary>
    /// Gets the ID that the client chose for the upload, which the paths of the upload's requests have, or <see langword="null"/> for one that the service
    /// chooses and returns (see <see cref="UploadSession.Id"/>).
    /// </summary>
    /// <remarks>
    /// The IDs are the user's: a creation with the ID of an earlier upload of the user, such as one made again after its response was lost, gets that upload
    /// while it is open and while its outcome is kept, if it is for the same endpoint, query string, file and command, and fails with
    /// <see cref="UploadErrorCodes.IdConflict"/> if not. The toolkit's client uses a random GUID (version 4).
    /// </remarks>
    public Guid? Id { get; }

    /// <summary>
    /// Gets the name of the file.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// Gets the length of the file in bytes.
    /// </summary>
    /// <remarks>
    /// The upload has exactly this many bytes.
    /// </remarks>
    public long Length { get; }

    /// <summary>
    /// Gets the content type of the file, or <see langword="null"/> if it is not known.
    /// </summary>
    public string? ContentType { get; }

    /// <summary>
    /// Gets the chunk size the client asks for in bytes, or <see langword="null"/> for the service's default.
    /// </summary>
    /// <remarks>
    /// The service keeps the size within its bounds, and the created upload has the size it chose (see <see cref="UploadSession.ChunkSize"/>).
    /// </remarks>
    public int? ChunkSize { get; }

    /// <summary>
    /// Gets the app's command as JSON, which the service reads with the app's JSON options, or <see langword="null"/> if there is none.
    /// </summary>
    public JsonElement? Command { get; }

    /// <summary>
    /// Gets the idempotency key of a 4.x client, of 1 to 128 characters, or <see langword="null"/> for none.
    /// </summary>
    /// <remarks>
    /// The service makes the upload's ID from the key of a creation that has no <see cref="Id"/>, so a creation with the key of an earlier creation of the
    /// user gets the upload that the earlier one created, as one with its ID does, and fails with <see cref="UploadErrorCodes.KeyConflict"/> where one with
    /// its ID fails with <see cref="UploadErrorCodes.IdConflict"/>. A creation with an <see cref="Id"/> ignores it.
    /// </remarks>
    public string? IdempotencyKey { get; }
}
