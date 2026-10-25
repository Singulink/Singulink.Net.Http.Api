namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// Represents the complete file of an upload as an <see cref="IUploadStore"/> provides it: a stream that reads it, and the path of a file that has it, if
/// the store keeps one in a file system.
/// </summary>
public abstract class UploadContent
{
    /// <summary>
    /// Gets the path of a file that has the content, or <see langword="null"/> if the store keeps none. Default is <see langword="null"/>.
    /// </summary>
    public virtual string? FilePath => null;

    /// <summary>
    /// Opens a read-only stream of the content. A store that keeps the content in a file system returns a seekable stream.
    /// </summary>
    public abstract Stream OpenReadStream();

    /// <summary>
    /// Moves the content to a new file at the specified path, so that the store no longer removes it. Default copies the content from
    /// <see cref="OpenReadStream"/> to the new file.
    /// </summary>
    /// <param name="destinationPath">The path of the new file, which must not exist.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <exception cref="IOException">The file at <paramref name="destinationPath"/> exists, or the content cannot be moved.</exception>
    public virtual async Task MoveToAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        await using var source = OpenReadStream();
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 0, useAsync: true);

        try
        {
            await source.CopyToAsync(destination, cancellationToken);
        }
        catch
        {
            await destination.DisposeAsync();
            File.Delete(destinationPath);
            throw;
        }
    }
}
