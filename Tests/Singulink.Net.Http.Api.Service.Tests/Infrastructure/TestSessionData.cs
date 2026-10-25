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

    public DateTime ExpiresUtc { get; set; }

    /// <summary>
    /// Gets how long after the last refresh the session expires.
    /// </summary>
    public TimeSpan ValidFor => ExpiresUtc - RefreshedUtc;

    public int Generation { get; set; }

    public bool IsPersistent { get; set; }

    public required DateTime CreatedUtc { get; set; }
}
