namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// A read-only stream of the body of a chunk request, which a store reads the chunk from (see <see cref="IUploadStore.WriteChunkAsync"/>). It has exactly
/// the chunk's length: reading throws if the body ends before it, and before the last bytes are returned it checks that the body ends with them, so that
/// a store never stores a chunk from a body of another length. It records the exception of a read of the body that fails, such as of a client that drops
/// the connection or sends too slowly, so that the failure is reported as the body's rather than the store's.
/// </summary>
internal sealed class ChunkContentStream(Stream body, int length) : Stream
{
    private long _remaining = length;

    /// <summary>
    /// Gets a value indicating whether reading found that the body is not as long as the chunk.
    /// </summary>
    public bool IsLengthMismatch { get; private set; }

    /// <summary>
    /// Gets the exception of a read of the body that failed, other than for a body that is longer than the chunk, or <see langword="null"/> if none failed.
    /// </summary>
    public Exception? BodyException { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_remaining is 0 || buffer.Length is 0)
            return 0;

        int read;

        try
        {
            read = await body.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken);

            if (read is 0)
                throw Mismatch();

            _remaining -= read;

            if (_remaining is 0 && await body.ReadAsync(new byte[1], cancellationToken) is not 0)
                throw Mismatch();
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode is StatusCodes.Status413PayloadTooLarge)
        {
            // The body is longer than the request size limit, which is the chunk's length.
            throw Mismatch(ex);
        }
        catch (Exception ex) when (ex is not InvalidDataException || !IsLengthMismatch)
        {
            BodyException = ex;
            throw;
        }

        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <summary>
    /// Reads the body synchronously, which the server allows only if its <c>AllowSynchronousIO</c> option is set, as for any body.
    /// </summary>
    public override int Read(Span<byte> buffer)
    {
        if (_remaining is 0 || buffer.Length is 0)
            return 0;

        int read;

        try
        {
            read = body.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);

            if (read is 0)
                throw Mismatch();

            _remaining -= read;

            if (_remaining is 0 && body.Read(stackalloc byte[1]) is not 0)
                throw Mismatch();
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode is StatusCodes.Status413PayloadTooLarge)
        {
            throw Mismatch(ex);
        }
        catch (Exception ex) when (ex is not InvalidDataException || !IsLengthMismatch)
        {
            BodyException = ex;
            throw;
        }

        return read;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private InvalidDataException Mismatch(Exception? innerException = null)
    {
        IsLengthMismatch = true;
        return new InvalidDataException("The body of the chunk request is not as long as the chunk.", innerException);
    }
}
