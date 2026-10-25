using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The stream of a chunk's body that a store reads.
/// </summary>
[PrefixTestClass]
public sealed class ChunkContentStreamTests
{
    [TestMethod]
    public void SynchronousRead_ReadsTheBodySynchronously()
    {
        using var stream = new ChunkContentStream(new SynchronousOnlyStream([1, 2, 3, 4]), length: 4);
        byte[] buffer = new byte[8];

        stream.Read(buffer, 0, 8).ShouldBe(4);
        buffer[..4].ShouldBe([1, 2, 3, 4]);
        stream.Read(buffer, 0, 8).ShouldBe(0);
    }

    [TestMethod]
    public void SynchronousRead_OfABodyThatIsLongerThanTheChunk_Throws()
    {
        using var stream = new ChunkContentStream(new SynchronousOnlyStream([1, 2, 3, 4, 5]), length: 4);

        Should.Throw<InvalidDataException>(() => stream.Read(new byte[8], 0, 8));
        stream.IsLengthMismatch.ShouldBeTrue();
    }

    /// <summary>
    /// A body that can only be read synchronously.
    /// </summary>
    private sealed class SynchronousOnlyStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The body is read asynchronously.");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The body is read asynchronously.");
    }
}
