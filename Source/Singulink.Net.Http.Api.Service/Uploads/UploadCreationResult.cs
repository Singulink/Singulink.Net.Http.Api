namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// Specifies the result of creating an upload in an <see cref="IUploadStore"/> (see <see cref="IUploadStore.TryCreateAsync"/>).
/// </summary>
public enum UploadCreationResult
{
    /// <summary>
    /// The upload was created.
    /// </summary>
    Created,

    /// <summary>
    /// The user has an upload with the ID, open or finished, or one that is being created or removed, so the upload was not created.
    /// </summary>
    Exists,

    /// <summary>
    /// The store has no room for the upload, so it was not created. The service reports it as <see cref="UploadErrorCodes.StorageFull"/>.
    /// </summary>
    NoRoom,
}
