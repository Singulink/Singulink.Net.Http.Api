using System.Net;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// The content of a chunk's request: the chunk's bytes from its buffer, written to the request stream in slices of 64 KiB, each counted for the progress
/// once it is written, and each starting the request's inactivity timeout again.
/// </summary>
internal sealed class ChunkContent : HttpContent
{
    private const int SliceSize = 64 * 1024;

    private readonly byte[] _buffer;
    private readonly int _count;
    private readonly UploadProgressTracker.SendAttempt _attempt;
    private readonly InactivityTimeout _inactivity;

    public ChunkContent(byte[] buffer, int count, UploadProgressTracker.SendAttempt attempt, InactivityTimeout inactivity)
    {
        _buffer = buffer;
        _count = count;
        _attempt = attempt;
        _inactivity = inactivity;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

#if NET
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
#else
    private async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
#endif
    {
        // A handler can write the content more than once, such as when it sends the request again on another connection.
        _attempt.OnRestarted();

        for (int offset = 0; offset < _count; offset += SliceSize)
        {
            int size = Math.Min(SliceSize, _count - offset);

#if NET
            await stream.WriteAsync(_buffer.AsMemory(offset, size), cancellationToken).ConfigureAwait(false);
#else
            await stream.WriteAsync(_buffer, offset, size, cancellationToken).ConfigureAwait(false);
#endif
            _attempt.OnWritten(size);
            _inactivity.Reset();
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _count;
        return true;
    }
}
