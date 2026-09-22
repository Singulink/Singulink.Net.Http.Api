namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Session entity stored by <see cref="InMemorySessionStore"/>, standing in for a database row.
/// </summary>
public sealed class TestSessionData
{
    public long Id { get; set; }

    public int UserId { get; set; }

    public string Device { get; set; } = string.Empty;

    public IPAddress? IpAddress { get; set; }

    public DateTime RefreshedUtc { get; set; }

    public TimeSpan ValidFor { get; set; }

    public int Generation { get; set; }

    public bool IsPersistent { get; set; }
}
