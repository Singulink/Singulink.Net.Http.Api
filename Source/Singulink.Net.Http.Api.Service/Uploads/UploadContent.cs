using Singulink.IO;

namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// Represents the complete file of an upload as an <see cref="IUploadStore"/> provides it: a stream that reads it, and a file that has it, if the store
/// keeps one in a file system.
/// </summary>
public abstract class UploadContent
{
    /// <summary>
    /// Gets a file that has the content, or <see langword="null"/> if the store keeps none. Default is <see langword="null"/>.
    /// </summary>
    public virtual IAbsoluteFilePath? File => null;

    /// <summary>
    /// Opens a read-only stream of the content.
    /// </summary>
    /// <remarks>
    /// A store that keeps the content in a file system returns a seekable stream.
    /// </remarks>
    public abstract Stream OpenReadStream();

    /// <summary>
    /// Moves the content to a new file, so that the store no longer removes it. Default copies the content from <see cref="OpenReadStream"/> to the new
    /// file.
    /// </summary>
    /// <param name="destination">The new file, which must not exist.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <exception cref="IOException"><paramref name="destination"/> exists, or the content cannot be moved.</exception>
    public virtual async Task MoveToAsync(IAbsoluteFilePath destination, CancellationToken cancellationToken = default)
    {
        await using var source = OpenReadStream();
        await using var stream = destination.OpenAsyncStream(FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 0);

        try
        {
            await source.CopyToAsync(stream, cancellationToken);
        }
        catch
        {
            await stream.DisposeAsync();
            destination.Delete();
            throw;
        }
    }
}
