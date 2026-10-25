using System.Reflection;

namespace Singulink.Net.Http.Api.Service.Uploads;

#pragma warning disable SA1402 // File may only contain a single type

/// <summary>
/// Represents the complete file of a chunked upload, which the handler of the upload takes as a parameter.
/// </summary>
/// <remarks>
/// The file is removed once the handler finishes, unless the handler moved it with <see cref="MoveToAsync"/> or failed with a <c>503</c>, which leaves the
/// file for the next run of the handler. A handler that moves the file and then fails with a <c>503</c> leaves no file for the next run, which then fails,
/// or, past <see cref="ChunkedUploadOptions.UnstartedExpiry"/> after the upload's creation, finds the upload expired, since without its file the upload
/// counts as one that received no chunk.
/// A handler that reads it with <see cref="OpenReadStream"/> works with any <see cref="IUploadStore"/>, while <see cref="FilePath"/> is only available from
/// stores that keep the file in a file system, such as the directory store.
/// </remarks>
public class UploadedFile : IBindableFromHttpContext<UploadedFile>
{
    private readonly UploadContent _content;

    /// <summary>
    /// Initializes a new instance of the <see cref="UploadedFile"/> class.
    /// </summary>
    /// <param name="id">The upload ID.</param>
    /// <param name="fileName">The name of the file.</param>
    /// <param name="length">The length of the file in bytes.</param>
    /// <param name="contentType">The content type of the file, if the client sent one.</param>
    /// <param name="content">The content of the file.</param>
    public UploadedFile(string id, string fileName, long length, string? contentType, UploadContent content)
    {
        Id = id;
        FileName = fileName;
        Length = length;
        ContentType = contentType;
        _content = content;
    }

    /// <summary>
    /// Gets the upload ID.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the name of the file as the client sent it.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// Gets the length of the file in bytes, which is the length that the upload was created with.
    /// </summary>
    public long Length { get; }

    /// <summary>
    /// Gets the content type of the file as the client sent it, or <see langword="null"/> if it sent none.
    /// </summary>
    public string? ContentType { get; }

    /// <summary>
    /// Gets the path of a file that has the complete file, or <see langword="null"/> if the upload store keeps none, as for stores that keep uploads in an
    /// object store. With the directory store, the file is in the upload's directory.
    /// </summary>
    public string? FilePath => _content.FilePath;

    /// <summary>
    /// Opens a read-only stream of the file. The stream of the directory store is seekable.
    /// </summary>
    public Stream OpenReadStream() => _content.OpenReadStream();

    /// <summary>
    /// Moves the file to a new file at the specified path, so that it is kept after the handler finishes. A store that keeps the file in a file system
    /// moves it, which copies nothing on the same volume, and other stores copy it there.
    /// </summary>
    /// <param name="destinationPath">The path of the new file, which must not exist.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <exception cref="IOException">The file at <paramref name="destinationPath"/> exists, or the file cannot be moved, such as while a stream of it is
    /// open.</exception>
    public Task MoveToAsync(string destinationPath, CancellationToken cancellationToken = default) => _content.MoveToAsync(destinationPath, cancellationToken);

    /// <summary>
    /// Binds the uploaded file parameter of the handler of a chunked upload.
    /// </summary>
    /// <exception cref="InvalidOperationException">The parameter is not a parameter of the handler of a chunked upload.</exception>
    static ValueTask<UploadedFile?> IBindableFromHttpContext<UploadedFile>.BindAsync(HttpContext context, ParameterInfo parameter)
    {
        return new(UploadedFileBinding.Get(context).File);
    }
}

/// <summary>
/// Represents the complete file of a chunked upload with the app's command from the upload's creation, which the handler of the upload takes as a
/// parameter.
/// </summary>
/// <typeparam name="TCommand">The type of the command.</typeparam>
/// <inheritdoc cref="UploadedFile" path="/remarks"/>
public class UploadedFile<TCommand> : UploadedFile, IBindableFromHttpContext<UploadedFile<TCommand>>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UploadedFile{TCommand}"/> class.
    /// </summary>
    /// <param name="id">The upload ID.</param>
    /// <param name="fileName">The name of the file.</param>
    /// <param name="length">The length of the file in bytes.</param>
    /// <param name="contentType">The content type of the file, if the client sent one.</param>
    /// <param name="content">The content of the file.</param>
    /// <param name="command">The app's command.</param>
    public UploadedFile(string id, string fileName, long length, string? contentType, UploadContent content, TCommand command)
        : base(id, fileName, length, contentType, content)
    {
        Command = command;
    }

    /// <summary>
    /// Gets the app's command from the upload's creation, read with the app's JSON options.
    /// </summary>
    public TCommand Command { get; }

    /// <summary>
    /// Binds the uploaded file parameter of the handler of a chunked upload, with the command from the upload's creation.
    /// </summary>
    /// <exception cref="InvalidOperationException">The parameter is not a parameter of the handler of a chunked upload.</exception>
    /// <exception cref="BadRequestApiException">The command is missing where the parameter does not allow <see langword="null"/>, or cannot be
    /// read.</exception>
    static ValueTask<UploadedFile<TCommand>?> IBindableFromHttpContext<UploadedFile<TCommand>>.BindAsync(HttpContext context, ParameterInfo parameter)
    {
        var binding = UploadedFileBinding.Get(context);
        var file = binding.File;
        var command = UploadCommands.Read<TCommand>(binding.Command, parameter, binding.SerializerOptions);

        return new(new UploadedFile<TCommand>(file.Id, file.FileName, file.Length, file.ContentType, binding.Content, command));
    }
}
