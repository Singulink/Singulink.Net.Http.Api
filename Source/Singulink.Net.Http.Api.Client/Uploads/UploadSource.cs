namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// The content of a file to upload in chunks (see <see cref="ApiClientBase.UploadAsync{TResult}"/> and
/// <see cref="ApiClientBase.UploadStreamingAsync{TItem}"/>), with its name, length and content type. It reads any byte range of the content, so that each
/// chunk is read from where it starts.
/// </summary>
/// <remarks>
/// <para>
/// An upload reads one range at a time, in order, each into the buffer of a chunk, and sends a chunk whose request fails again from that buffer. It reads a
/// range again only for a chunk that the service still lacks when the upload completes.
/// </para>
/// <para>
/// <see cref="FromFile(string, string?, string?)"/>, <see cref="FromStream(Stream, string, string?)"/>,
/// <see cref="FromStreamFactory(string, long, Func{CancellationToken, Task{Stream}}, string?)"/> and
/// <see cref="Create(string, long, Func{long, Memory{byte}, CancellationToken, ValueTask{int}}, string?)"/> create sources. A derived class implements
/// <see cref="ReadAsync(long, Memory{byte}, CancellationToken)"/>, and <see cref="ReleaseAsync"/> if it keeps something open between reads.
/// </para>
/// </remarks>
public abstract class UploadSource
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UploadSource"/> class with the specified file name, length and content type.
    /// </summary>
    /// <param name="fileName">The name of the file, which the service receives.</param>
    /// <param name="length">The length of the content in bytes.</param>
    /// <param name="contentType">The content type of the file, such as <c>image/png</c>, which the service receives. Default is <see langword="null"/>,
    /// which sends none.</param>
    /// <exception cref="ArgumentException"><paramref name="fileName"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    protected UploadSource(string fileName, long length, string? contentType = null)
    {
        if (fileName.Length is 0)
            throw new ArgumentException("The file name cannot be empty.", nameof(fileName));

        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length), length, "The length cannot be negative.");

        FileName = fileName;
        Length = length;
        ContentType = contentType;
    }

    /// <summary>
    /// Gets the name of the file, which the service receives.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// Gets the length of the content in bytes.
    /// </summary>
    public long Length { get; }

    /// <summary>
    /// Gets the content type of the file, such as <c>image/png</c>, which the service receives, or <see langword="null"/> if it has none.
    /// </summary>
    public string? ContentType { get; }

    /// <summary>
    /// Reads bytes of the content, starting at the specified offset, into the buffer.
    /// </summary>
    /// <param name="offset">The offset in the content of the first byte to read.</param>
    /// <param name="buffer">The buffer to read into. An upload never reads past <see cref="Length"/>.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The number of bytes read, which can be less than the length of the buffer. Zero means that the content ends at the offset.</returns>
    /// <remarks>
    /// The same range can be read more than once. An exception that the read throws ends the upload, which aborts the upload on the service and throws it.
    /// </remarks>
    public abstract ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken);

    /// <summary>
    /// Releases what the source keeps open between reads, such as a stream. An upload calls it when it stops reading the source: once its chunks are sent,
    /// and when it fails or is cancelled. A later read opens what it needs again. An exception that it throws does not change how the upload goes on, and
    /// is traced as a warning (see <see cref="System.Diagnostics.Trace"/>). Does nothing by default.
    /// </summary>
    /// <remarks>
    /// <see cref="ReadAsync"/> and <see cref="ReleaseAsync"/> are both public, so a source can read and release another source.
    /// </remarks>
    public virtual ValueTask ReleaseAsync() => default;

    /// <summary>
    /// Creates a source that reads a file. Each read opens the file and checks that its length and last write time are still the ones it had when the
    /// source was created, so that a file that changes fails the read with an <see cref="IOException"/> instead of being uploaded with other content.
    /// Other processes can read, write and delete the file while a read has it open.
    /// </summary>
    /// <param name="path">The path of the file.</param>
    /// <param name="fileName">The file name that the service receives. Default is <see langword="null"/>, which uses the name in
    /// <paramref name="path"/>.</param>
    /// <param name="contentType">The content type of the file, such as <c>image/png</c>, which the service receives. Default is <see langword="null"/>,
    /// which sends none.</param>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public static UploadSource FromFile(string path, string? fileName = null, string? contentType = null)
    {
        var file = new FileInfo(path);

        if (!file.Exists)
            throw new FileNotFoundException($"The file '{path}' does not exist.", path);

        return new FileSource(file, fileName ?? file.Name, contentType);
    }

    /// <summary>
    /// Creates a source that reads a seekable stream by setting its position, one read at a time. The content is the whole stream, whose length is
    /// read now. A read of a stream whose length has changed since throws an <see cref="IOException"/>. The stream is not disposed.
    /// </summary>
    /// <param name="stream">The stream, which must be readable and seekable.</param>
    /// <param name="fileName">The name of the file, which the service receives.</param>
    /// <param name="contentType">The content type of the file, such as <c>image/png</c>, which the service receives. Default is <see langword="null"/>,
    /// which sends none.</param>
    /// <exception cref="ArgumentException"><paramref name="stream"/> is not readable or not seekable, or <paramref name="fileName"/> is empty.</exception>
    public static UploadSource FromStream(Stream stream, string fileName, string? contentType = null)
    {
        if (!stream.CanRead)
            throw new ArgumentException("The stream must be readable.", nameof(stream));

        if (!stream.CanSeek)
            throw new ArgumentException("The stream must be seekable. FromStreamFactory reads streams that are not.", nameof(stream));

        return new StreamSource(stream, fileName, contentType);
    }

    /// <summary>
    /// Creates a source that reads streams that a function opens, one read at a time. It keeps a stream open between reads and reads it forward, setting
    /// its position if it is seekable. It opens a new stream to go back in one that is not seekable, after a read fails, and after
    /// <see cref="ReleaseAsync"/>. A stream whose length differs from <paramref name="length"/> fails the read with an <see cref="IOException"/>.
    /// </summary>
    /// <param name="fileName">The name of the file, which the service receives.</param>
    /// <param name="length">The length of the content in bytes.</param>
    /// <param name="openAsync">A function that opens a readable stream of the content, positioned at its start. The source disposes the streams.</param>
    /// <param name="contentType">The content type of the file, such as <c>image/png</c>, which the service receives. Default is <see langword="null"/>,
    /// which sends none.</param>
    /// <exception cref="ArgumentException"><paramref name="fileName"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    public static UploadSource FromStreamFactory(string fileName, long length, Func<CancellationToken, Task<Stream>> openAsync, string? contentType = null)
    {
        return new StreamFactorySource(fileName, length, openAsync, contentType);
    }

    /// <summary>
    /// Creates a source whose reads call the specified function.
    /// </summary>
    /// <param name="fileName">The name of the file, which the service receives.</param>
    /// <param name="length">The length of the content in bytes.</param>
    /// <param name="readAsync">A function that reads bytes of the content from an offset into a buffer and returns how many it read, as
    /// <see cref="ReadAsync(long, Memory{byte}, CancellationToken)"/> does.</param>
    /// <param name="contentType">The content type of the file, such as <c>image/png</c>, which the service receives. Default is <see langword="null"/>,
    /// which sends none.</param>
    /// <exception cref="ArgumentException"><paramref name="fileName"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    public static UploadSource Create(
        string fileName, long length, Func<long, Memory<byte>, CancellationToken, ValueTask<int>> readAsync, string? contentType = null)
    {
        return new DelegateSource(fileName, length, readAsync, contentType);
    }

    /// <summary>
    /// Fills the buffer with the content from the specified offset, which the buffer must not reach past the end of.
    /// </summary>
    /// <exception cref="IOException">The content ended before the buffer was full.</exception>
    /// <exception cref="InvalidOperationException">A read returned a negative count or more bytes than it was given room for.</exception>
    internal async ValueTask ReadExactlyAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int filled = 0;

        while (filled < buffer.Length)
        {
            int read = await ReadAsync(offset + filled, buffer[filled..], cancellationToken).ConfigureAwait(false);

            if (read is 0)
                throw new IOException($"The content of the upload source ended at byte {offset + filled}, before its length of {Length} bytes.");

            if (read < 0 || read > buffer.Length - filled)
            {
                throw new InvalidOperationException(
                    $"A read of the upload source returned {read} for a buffer of {buffer.Length - filled} bytes at offset {offset + filled}.");
            }

            filled += read;
        }
    }

    private static ValueTask DisposeStreamAsync(Stream stream)
    {
#if NET
        return stream.DisposeAsync();
#else
        stream.Dispose();
        return default;
#endif
    }

    private sealed class FileSource(FileInfo file, string fileName, string? contentType) : UploadSource(fileName, file.Length, contentType)
    {
        private readonly string _path = file.FullName;
        private readonly DateTime _lastWriteTimeUtc = file.LastWriteTimeUtc;

        public override async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            const FileShare share = FileShare.ReadWrite | FileShare.Delete;

#if NET
            using var handle = File.OpenHandle(_path, FileMode.Open, FileAccess.Read, share, FileOptions.Asynchronous);
            CheckUnchanged(RandomAccess.GetLength(handle), File.GetLastWriteTimeUtc(handle));

            return await RandomAccess.ReadAsync(handle, buffer, offset, cancellationToken).ConfigureAwait(false);
#else
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, share, 4096, FileOptions.Asynchronous);
            CheckUnchanged(stream.Length, File.GetLastWriteTimeUtc(_path));
            stream.Position = offset;

            return await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
#endif
        }

        private void CheckUnchanged(long length, DateTime lastWriteTimeUtc)
        {
            if (length != Length || lastWriteTimeUtc != _lastWriteTimeUtc)
                throw new IOException($"The file '{_path}' has changed since the upload source was created.");
        }
    }

    private sealed class StreamSource(Stream stream, string fileName, string? contentType) : UploadSource(fileName, stream.Length, contentType)
    {
        private readonly SemaphoreSlim _lock = new(1, 1);

        public override async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (stream.Length != Length)
                    throw new IOException($"The length of the stream has changed from {Length} to {stream.Length} bytes since the upload source was created.");

                stream.Position = offset;
                return await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }
    }

    private sealed class StreamFactorySource(string fileName, long length, Func<CancellationToken, Task<Stream>> openAsync, string? contentType)
        : UploadSource(fileName, length, contentType)
    {
        private readonly SemaphoreSlim _lock = new(1, 1);
        private Stream? _stream;
        private long _position;

        public override async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var stream = await GetStreamAtAsync(offset, cancellationToken).ConfigureAwait(false);
                int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                _position += read;

                // A stream that can't seek is only known to be longer than the content once the content's end has been read.
                if (_position == Length && read > 0 && !stream.CanSeek && await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) > 0)
                    throw new IOException($"The stream that the upload source opened is longer than the content's length of {Length} bytes.");

                return read;
            }
            catch
            {
                // The stream's position is unknown after a failed read, so the next read opens a new stream.
                await CloseAsync().ConfigureAwait(false);
                throw;
            }
            finally
            {
                _lock.Release();
            }
        }

        public override async ValueTask ReleaseAsync()
        {
            await _lock.WaitAsync().ConfigureAwait(false);

            try
            {
                await CloseAsync().ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Gets the open stream positioned at the offset: seeks in a seekable stream, skips forward in one that is not, and opens a new stream if there is
        /// none or the offset is behind the position of one that is not seekable.
        /// </summary>
        private async ValueTask<Stream> GetStreamAtAsync(long offset, CancellationToken cancellationToken)
        {
            if (_stream is null || (!_stream.CanSeek && offset < _position))
            {
                await CloseAsync().ConfigureAwait(false);

                var stream = await openAsync(cancellationToken).ConfigureAwait(false);
                _stream = stream;
                _position = 0;

                if (!stream.CanRead)
                    throw new InvalidOperationException("The stream that the upload source opened is not readable.");
            }

            if (_stream.CanSeek)
            {
                long streamLength = _stream.Length;

                if (streamLength != Length)
                {
                    throw new IOException(
                        $"The stream that the upload source opened has a length of {streamLength} bytes instead of the content's {Length} bytes.");
                }

                _stream.Position = offset;
                _position = offset;

                return _stream;
            }

            if (offset > _position)
                await SkipAsync(_stream, offset - _position, cancellationToken).ConfigureAwait(false);

            return _stream;
        }

        private async ValueTask SkipAsync(Stream stream, long count, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[(int)Math.Min(count, 81920)];

            while (count > 0)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(count, buffer.Length)), cancellationToken).ConfigureAwait(false);

                if (read is 0)
                    throw new IOException($"The stream that the upload source opened ended at byte {_position}, before its length of {Length} bytes.");

                _position += read;
                count -= read;
            }
        }

        private async ValueTask CloseAsync()
        {
            if (_stream is { } stream)
            {
                _stream = null;
                await DisposeStreamAsync(stream).ConfigureAwait(false);
            }
        }
    }

    private sealed class DelegateSource(
        string fileName, long length, Func<long, Memory<byte>, CancellationToken, ValueTask<int>> readAsync, string? contentType)
        : UploadSource(fileName, length, contentType)
    {
        public override ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken) =>
            readAsync(offset, buffer, cancellationToken);
    }
}
