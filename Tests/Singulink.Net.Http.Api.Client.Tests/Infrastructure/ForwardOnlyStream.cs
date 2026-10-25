namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// A readable stream of the specified bytes that can't seek, such as a network stream, and records whether it was disposed.
/// </summary>
public sealed class ForwardOnlyStream(byte[] content) : Stream
{
    private int _position;

    public bool IsDisposed { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (IsDisposed)
            throw new ObjectDisposedException(nameof(ForwardOnlyStream));

        // Reads return at most 7 bytes, as streams may.
        int read = Math.Min(Math.Min(count, 7), content.Length - _position);
        Array.Copy(content, _position, buffer, offset, read);
        _position += read;

        return read;
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        IsDisposed = true;
        base.Dispose(disposing);
    }
}
