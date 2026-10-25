namespace Singulink.Net.Http.Api.Service.Passkeys.Infrastructure;

/// <summary>
/// A time provider whose time only moves when a test advances it.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan amount) => _now += amount;
}
