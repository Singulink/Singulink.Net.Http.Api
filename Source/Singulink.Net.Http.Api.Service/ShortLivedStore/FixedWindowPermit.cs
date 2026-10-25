namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The result of an attempt to acquire a permit from a fixed window count in the short-lived store, returned by
/// <see cref="ShortLivedStoreExtensions.AcquirePermitAsync"/>.
/// </summary>
public readonly struct FixedWindowPermit
{
    internal FixedWindowPermit(bool isAcquired, TimeSpan? retryAfter)
    {
        IsAcquired = isAcquired;
        RetryAfter = retryAfter;
    }

    /// <summary>
    /// Gets a value indicating whether a permit was acquired.
    /// </summary>
    public bool IsAcquired { get; }

    /// <summary>
    /// Gets the time until the window ends and permits can be acquired again if no permit was acquired, otherwise <see langword="null"/>.
    /// </summary>
    public TimeSpan? RetryAfter { get; }
}
