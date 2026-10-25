namespace Singulink.Net.Http.Api.Service;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Provides options for <see cref="MemoryShortLivedStore"/>.
/// </summary>
public sealed class MemoryShortLivedStoreOptions
{
    /// <summary>
    /// The default value of <see cref="MaxSize"/>: 256 MiB.
    /// </summary>
    public const long DefaultMaxSize = 256L * 1024 * 1024;

    /// <summary>
    /// Gets or sets the approximate amount of memory, in bytes, that the store's values and counters can take up. Must be positive. Default is <see
    /// cref="DefaultMaxSize"/> (256 MiB).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The size of an entry is estimated as 2 bytes per character of its key and value, plus about 120 bytes. Counters take up the same room as values.
    /// </para>
    /// <para>
    /// When adding a value or a counter would exceed the size, expired entries are removed first. If that does not make enough room, the operation throws
    /// a <see cref="ServiceUnavailableApiException"/> (<see cref="ApiErrorCodes.ShortLivedStoreFull"/>) and a warning is logged, at most once a minute.
    /// Reading, taking and removing values, and changing existing counters, always work.
    /// </para>
    /// </remarks>
    public long MaxSize {
        get;
        set {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, 0);
            field = value;
        }
    } = DefaultMaxSize;
}
