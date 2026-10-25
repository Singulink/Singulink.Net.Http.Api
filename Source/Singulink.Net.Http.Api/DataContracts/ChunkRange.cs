using System.Text.Json.Serialization;

namespace Singulink.Net.Http.Api.DataContracts;

/// <summary>
/// Represents a range of chunk indexes of a chunked upload, from <see cref="First"/> to <see cref="Last"/>, both included.
/// </summary>
public readonly struct ChunkRange : IEquatable<ChunkRange>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ChunkRange"/> struct.
    /// </summary>
    /// <param name="first">The index of the first chunk in the range.</param>
    /// <param name="last">The index of the last chunk in the range.</param>
    [JsonConstructor]
    public ChunkRange(int first, int last)
    {
        First = first;
        Last = last;
    }

    /// <summary>
    /// Gets the index of the first chunk in the range.
    /// </summary>
    public int First { get; }

    /// <summary>
    /// Gets the index of the last chunk in the range, which is part of the range.
    /// </summary>
    public int Last { get; }

    /// <summary>
    /// Determines whether two ranges are equal.
    /// </summary>
    public static bool operator ==(ChunkRange left, ChunkRange right) => left.Equals(right);

    /// <summary>
    /// Determines whether two ranges are not equal.
    /// </summary>
    public static bool operator !=(ChunkRange left, ChunkRange right) => !left.Equals(right);

    /// <inheritdoc/>
    public bool Equals(ChunkRange other) => First == other.First && Last == other.Last;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ChunkRange other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => (First * 397) ^ Last;

    /// <summary>
    /// Returns the range as text, such as <c>3-7</c>.
    /// </summary>
    public override string ToString() => $"{First}-{Last}";
}
