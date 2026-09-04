namespace Singulink.Net.Http.Api.Service;

public sealed class TestSessionData : ISessionData
{
    public long Id { get; set; }

    public int UserId { get; set; }

    public string Device { get; set; } = string.Empty;

    public IPAddress? IpAddress { get; set; }

    public DateTime RefreshedUtc { get; set; }

    public TimeSpan ValidFor { get; set; }

    public int Generation { get; set; }

    public bool IsPersistent { get; set; }

    public TestSessionData Clone() => (TestSessionData)MemberwiseClone();
}
