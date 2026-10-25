#if !NET

using System.Buffers;
using System.Runtime.InteropServices;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Provides the <see cref="Stream"/> read overload with a <see cref="Memory{T}"/> buffer that .NET Standard 2.0 lacks, so the same source compiles for both
/// builds.
/// </summary>
internal static class StreamPolyfills
{
    /// <summary>
    /// Reads bytes from the stream into the buffer, directly if the buffer is backed by an array.
    /// </summary>
    public static async ValueTask<int> ReadAsync(this Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (MemoryMarshal.TryGetArray<byte>(buffer, out var segment))
            return await stream.ReadAsync(segment.Array!, segment.Offset, segment.Count, cancellationToken).ConfigureAwait(false);

        byte[] array = ArrayPool<byte>.Shared.Rent(buffer.Length);

        try
        {
            int read = await stream.ReadAsync(array, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            array.AsSpan(0, read).CopyTo(buffer.Span);

            return read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(array);
        }
    }
}

#endif
