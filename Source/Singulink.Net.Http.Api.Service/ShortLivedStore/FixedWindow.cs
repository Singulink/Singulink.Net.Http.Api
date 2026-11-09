using System.Globalization;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The current window of a fixed window count kept in the <see cref="IShortLivedStore"/>. Windows are aligned to the clock, so that every server counts the
/// same window under the same key.
/// </summary>
/// <param name="Key">The key the window's count is kept under: the key prefix followed by the window's number.</param>
/// <param name="Remaining">The time until the window ends.</param>
/// <param name="KeyLifetime">How long the window's count is kept.</param>
/// <param name="Index">The window's number: how many windows of its length came before it since the start of the clock.</param>
internal readonly record struct FixedWindow(string Key, TimeSpan Remaining, TimeSpan KeyLifetime, long Index)
{
    // Counts are kept a little past the end of their window, so that servers whose clocks are slightly behind still find them.
    private static readonly TimeSpan ClockSkewAllowance = TimeSpan.FromSeconds(10);

    public static FixedWindow Get(string keyPrefix, long windowTicks, TimeProvider timeProvider)
    {
        long nowTicks = timeProvider.GetUtcNow().UtcTicks;
        long index = GetIndex(nowTicks, windowTicks);
        var remaining = TimeSpan.FromTicks(windowTicks - (nowTicks % windowTicks));
        var keyLifetime = remaining < TimeSpan.MaxValue - ClockSkewAllowance ? remaining + ClockSkewAllowance : TimeSpan.MaxValue;

        return new FixedWindow(keyPrefix + index.ToString(CultureInfo.InvariantCulture), remaining, keyLifetime, index);
    }

    /// <summary>
    /// Gets the number of the window of the specified length that a time is in, in UTC ticks.
    /// </summary>
    public static long GetIndex(long utcTicks, long windowTicks) => utcTicks / windowTicks;
}
