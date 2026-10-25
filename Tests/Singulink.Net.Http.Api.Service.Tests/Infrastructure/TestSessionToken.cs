using System.Globalization;
using System.Text.Json.Serialization;

namespace Singulink.Net.Http.Api.Service;

#pragma warning disable SA1402 // File may only contain a single type

/// <summary>
/// Session token used by the tests. <see cref="Stamp"/> mirrors a user security stamp (the store replaces tokens whose stamp is out of date) and
/// <see cref="BuildCount"/> counts how many times the token has been replaced by the store.
/// </summary>
public sealed record TestSessionToken(long SessionId, int UserId, int Stamp, int BuildCount = 0) : ISessionToken<TestSessionToken>
{
    /// <summary>
    /// Gets data that makes the token larger, for cookie size tests.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Padding { get; init; }

    string ISessionToken.UserId => UserId.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The contents of a session cookie: the token and the session's refresh info.
/// </summary>
public sealed record TestSessionCookie(TestSessionToken Token, DateTime RefreshedUtc, TimeSpan ValidFor, int Generation, bool IsPersistent);
