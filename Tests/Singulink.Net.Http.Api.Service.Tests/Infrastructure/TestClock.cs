namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// A manually advanced clock, so that expiry can be tested without waiting.
/// </summary>
public sealed class TestClock(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public TestClock() : this(DateTimeOffset.UtcNow)
    {
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan time) => _utcNow += time;
}
