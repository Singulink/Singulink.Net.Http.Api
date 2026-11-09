using Singulink.IO;
using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service.Testing;

#pragma warning disable SA1402 // File may only contain a single type

/// <summary>
/// An uploaded file for testing the handlers of chunked uploads without a request. Its content is in a temporary file, which is deleted when it is
/// disposed unless the handler moved it.
/// </summary>
public sealed class TestUploadedFile : UploadedFile, IDisposable
{
    private readonly TemporaryFileContent _content;

    private TestUploadedFile(string fileName, TemporaryFileContent content, string? contentType)
        : base(ShortLivedId.Create(), fileName, content.Length, contentType, content)
    {
        _content = content;
    }

    /// <summary>
    /// Creates an uploaded file with the specified content in a temporary file, which <see cref="UploadedFile.File"/> is.
    /// </summary>
    /// <param name="content">The content of the file.</param>
    /// <param name="fileName">The name of the file.</param>
    /// <param name="contentType">The content type of the file, if any.</param>
    public static TestUploadedFile Create(byte[] content, string fileName, string? contentType = null)
    {
        return new TestUploadedFile(fileName, TemporaryFileContent.Create(content), contentType);
    }

    /// <summary>
    /// Creates an uploaded file with the specified content in a temporary file, which <see cref="UploadedFile.File"/> is, and the specified command.
    /// </summary>
    /// <typeparam name="TCommand">The type of the command.</typeparam>
    /// <param name="content">The content of the file.</param>
    /// <param name="fileName">The name of the file.</param>
    /// <param name="command">The app's command.</param>
    /// <param name="contentType">The content type of the file, if any.</param>
    public static TestUploadedFile<TCommand> Create<TCommand>(byte[] content, string fileName, TCommand command, string? contentType = null)
    {
        return new TestUploadedFile<TCommand>(fileName, TemporaryFileContent.Create(content), command, contentType);
    }

    /// <summary>
    /// Deletes the temporary file, unless it was moved.
    /// </summary>
    public void Dispose() => _content.Delete();
}

/// <summary>
/// An uploaded file with the app's command for testing the handlers of chunked uploads without a request, which
/// <see cref="TestUploadedFile.Create{TCommand}(byte[], string, TCommand, string?)"/> creates. Its content is in a temporary file, which is deleted when it
/// is disposed unless the handler moved it.
/// </summary>
/// <typeparam name="TCommand">The type of the command.</typeparam>
public sealed class TestUploadedFile<TCommand> : UploadedFile<TCommand>, IDisposable
{
    private readonly TemporaryFileContent _content;

    internal TestUploadedFile(string fileName, TemporaryFileContent content, TCommand command, string? contentType)
        : base(ShortLivedId.Create(), fileName, content.Length, contentType, content, command)
    {
        _content = content;
    }

    /// <summary>
    /// Deletes the temporary file, unless it was moved.
    /// </summary>
    public void Dispose() => _content.Delete();
}

/// <summary>
/// The content of a test's uploaded file, in a temporary file until it is moved.
/// </summary>
internal sealed class TemporaryFileContent : UploadContent
{
    private readonly Lock _lock = new();
    private IAbsoluteFilePath _file;
    private bool _isMoved;

    private TemporaryFileContent(IAbsoluteFilePath file, long length)
    {
        _file = file;
        Length = length;
    }

    public long Length { get; }

    public override IAbsoluteFilePath? File
    {
        get {
            lock (_lock)
                return _file;
        }
    }

    public static TemporaryFileContent Create(byte[] content)
    {
        var file = DirectoryPath.GetTemp().CombineFile($"upload-{Guid.NewGuid():N}.tmp", PathOptions.None);

        using (var stream = file.OpenStream(FileMode.CreateNew, FileAccess.Write))
            stream.Write(content);

        return new TemporaryFileContent(file, content.Length);
    }

    public override Stream OpenReadStream()
    {
        // As with the directory store, whose file can be written to and removed while it is open.
        return File!.OpenAsyncStream(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }

    public override async Task MoveToAsync(IAbsoluteFilePath destination, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // As with the directory store: a file on the same file system is renamed, and one on another is copied, which can be cancelled.
        if (!DirectoryUploadStore.IsOnSameFileSystem(destination, File!))
        {
            await base.MoveToAsync(destination, cancellationToken);

            lock (_lock)
            {
                _file.Delete();
                _file = destination;
                _isMoved = true;
            }

            return;
        }

        lock (_lock)
        {
            _file.MoveTo(destination, overwrite: false);
            _file = destination;
            _isMoved = true;
        }
    }

    public void Delete()
    {
        lock (_lock)
        {
            if (!_isMoved)
                _file.Delete();
        }
    }
}
