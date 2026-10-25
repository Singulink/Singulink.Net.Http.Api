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
    /// Creates an uploaded file with the specified content in a temporary file, which <see cref="UploadedFile.FilePath"/> has.
    /// </summary>
    /// <param name="content">The content of the file.</param>
    /// <param name="fileName">The name of the file.</param>
    /// <param name="contentType">The content type of the file, if any.</param>
    public static TestUploadedFile Create(byte[] content, string fileName, string? contentType = null)
    {
        return new TestUploadedFile(fileName, TemporaryFileContent.Create(content), contentType);
    }

    /// <summary>
    /// Creates an uploaded file with the specified content in a temporary file, which <see cref="UploadedFile.FilePath"/> has, and the specified command.
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
    private string _path;
    private bool _isMoved;

    private TemporaryFileContent(string path, long length)
    {
        _path = path;
        Length = length;
    }

    public long Length { get; }

    public override string? FilePath
    {
        get {
            lock (_lock)
                return _path;
        }
    }

    public static TemporaryFileContent Create(byte[] content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"upload-{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(path, content);

        return new TemporaryFileContent(path, content.Length);
    }

    public override Stream OpenReadStream()
    {
        // As with the directory store, whose file can be written to and removed while it is open.
        return new FileStream(FilePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
    }

    public override async Task MoveToAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        string fullPath = Path.GetFullPath(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();

        // As with the directory store: a file on the same file system is renamed, and one on another is copied, which can be cancelled.
        if (!DirectoryUploadStore.IsOnSameFileSystem(fullPath, FilePath!))
        {
            await base.MoveToAsync(fullPath, cancellationToken);

            lock (_lock)
            {
                File.Delete(_path);
                _path = fullPath;
                _isMoved = true;
            }

            return;
        }

        lock (_lock)
        {
            File.Move(_path, fullPath, overwrite: false);
            _path = fullPath;
            _isMoved = true;
        }
    }

    public void Delete()
    {
        lock (_lock)
        {
            if (!_isMoved)
                File.Delete(_path);
        }
    }
}
