#if !NET

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// A read-only wrapper of a .NET Framework response stream whose asynchronous reads end with an <see cref="OperationCanceledException"/> as soon as their
/// token is cancelled, by disposing the response (see <see cref="HttpContentPolyfills.WaitOrAbortAsync{T}(Task{T}, IDisposable, CancellationToken)"/>).
/// The response streams of .NET Framework ignore cancellation once a read has started. Disposing the wrapper does not dispose the stream, which the response
/// disposes.
/// </summary>
internal sealed class CancellableResponseStream : Stream
{
    private readonly Stream _stream;
    private readonly HttpResponseMessage _response;

    // Reads go into a buffer of the wrapper's own, since an aborted read can still write into its buffer, which the caller may have returned to a pool. The
    // buffer is reused only after a read completed.
    private byte[] _buffer = [];

    public CancellableResponseStream(Stream stream, HttpResponseMessage response)
    {
        _stream = stream;
        _response = response;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        byte[] readBuffer = _buffer.Length >= count ? _buffer : new byte[count];
        _buffer = [];

        int read = await _stream.ReadAsync(readBuffer, 0, count, cancellationToken).WaitOrAbortAsync(_response, cancellationToken).ConfigureAwait(false);
        Buffer.BlockCopy(readBuffer, 0, buffer, offset, read);
        _buffer = readBuffer;

        return read;
    }

    public override int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

#endif
