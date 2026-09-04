using System.Globalization;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Session token used by the tests. <see cref="Stamp"/> mirrors a user security stamp (used by the store to detect stale tokens) and
/// <see cref="BuildCount"/> counts how many times the token has been rebuilt from store data.
/// </summary>
public sealed record TestSessionToken(
    long SessionId,
    int UserId,
    int Stamp,
    DateTime RefreshedUtc,
    TimeSpan ValidFor,
    int Generation,
    bool IsPersistent,
    int BuildCount) : ISessionToken
{
    public TimeSpan RefreshAfter { get; init; } = TimeSpan.FromMinutes(10);

    string ISessionToken.UserId => UserId.ToString(CultureInfo.InvariantCulture);
}
