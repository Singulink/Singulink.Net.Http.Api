namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// How much an <see cref="IShortLivedStore"/> holds and can hold (see <see cref="IShortLivedStore.GetUsageAsync"/>). Each value is <see langword="null"/>
/// when the store can't tell it.
/// </summary>
public sealed class ShortLivedStoreUsage
{
    /// <summary>
    /// Gets a usage whose values are all unknown.
    /// </summary>
    public static ShortLivedStoreUsage Unknown { get; } = new();

    /// <summary>
    /// Gets the number of entries, values and counters, including entries that have expired but have not been removed yet, or <see langword="null"/> if
    /// the store doesn't report it.
    /// </summary>
    public long? EntryCount { get; init; }

    /// <summary>
    /// Gets the memory that the entries take up, in bytes, as the store estimates it, or <see langword="null"/> if the store doesn't report it.
    /// </summary>
    public long? UsedBytes { get; init; }

    /// <summary>
    /// Gets the memory that the store can take up, in bytes, or <see langword="null"/> if it has no limit or doesn't know it.
    /// </summary>
    public long? MaxBytes { get; init; }

    /// <summary>
    /// Gets the number of writes that the store refused because it was full, since it was created, or <see langword="null"/> if the store doesn't report
    /// it.
    /// </summary>
    public long? RefusedWrites { get; init; }

    /// <summary>
    /// Gets when the store last refused a write because it was full, or <see langword="null"/> if it hasn't or can't tell.
    /// </summary>
    public DateTimeOffset? LastRefusedWriteUtc { get; init; }
}
