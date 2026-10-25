namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// A read-only wrapper of a response stream that calls a callback whenever a read returns bytes. Disposing the wrapper disposes the stream.
/// </summary>
internal sealed class ActivityReportingStream : Stream
{
    private readonly Stream _stream;
    private readonly Action _activity;

    public ActivityReportingStream(Stream stream, Action activity)
    {
        _stream = stream;
        _activity = activity;
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

    public override int Read(byte[] buffer, int offset, int count) => OnRead(_stream.Read(buffer, offset, count));

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        OnRead(await _stream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false));

#if NET
    public override int Read(Span<byte> buffer) => OnRead(_stream.Read(buffer));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        OnRead(await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override ValueTask DisposeAsync() => _stream.DisposeAsync();
#endif

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _stream.Dispose();

        base.Dispose(disposing);
    }

    private int OnRead(int read)
    {
        if (read > 0)
            _activity();

        return read;
    }
}
