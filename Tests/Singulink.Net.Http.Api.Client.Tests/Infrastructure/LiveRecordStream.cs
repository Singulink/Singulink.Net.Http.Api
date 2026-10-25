using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// The body of a streaming response that a test writes records into while the client reads it. Reads wait for the next record, and end when the test
/// ends the stream, which without an end record is a stream whose connection dropped.
/// </summary>
public sealed class LiveRecordStream : Stream
{
    private readonly ConcurrentQueue<byte[]?> _chunks = new();
    private readonly SemaphoreSlim _available = new(0);
    private byte[]? _current;
    private int _offset;
    private bool _ended;
    private int _waitingReads;
    private int _recordsRead;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Gets a value indicating whether a read is waiting for the next record.
    /// </summary>
    public bool IsWaiting => Volatile.Read(ref _waitingReads) > 0;

    /// <summary>
    /// Gets the number of records that reads have started to return.
    /// </summary>
    public int RecordsRead => Volatile.Read(ref _recordsRead);

    /// <summary>
    /// Creates a streaming response with this stream as its body.
    /// </summary>
    public HttpResponseMessage CreateResponse()
    {
        var content = new StreamContent(this);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(StreamingResponse.ContentType);

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    public void WriteRecord(string record)
    {
        _chunks.Enqueue(Encoding.UTF8.GetBytes(record + "\n"));
        _available.Release();
    }

    /// <summary>
    /// Ends the stream after the records written so far, without an end record unless one was written.
    /// </summary>
    public void End()
    {
        _chunks.Enqueue(null);
        _available.Release();
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        while (_current is null || _offset == _current.Length)
        {
            if (_ended)
                return 0;

            Interlocked.Increment(ref _waitingReads);

            try
            {
                await _available.WaitAsync(cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _waitingReads);
            }

            _chunks.TryDequeue(out var next);

            if (next is null)
            {
                _ended = true;
                return 0;
            }

            _current = next;
            _offset = 0;
            Interlocked.Increment(ref _recordsRead);
        }

        int read = Math.Min(count, _current.Length - _offset);
        Array.Copy(_current, _offset, buffer, offset, read);
        _offset += read;

        return read;
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
