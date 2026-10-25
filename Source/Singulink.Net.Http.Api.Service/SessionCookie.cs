namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The contents of a session cookie: the application's session token and the session's refresh info.
/// </summary>
internal sealed record SessionCookie<TSessionToken>(TSessionToken Token, DateTime RefreshedUtc, TimeSpan ValidFor, int Generation, bool IsPersistent)
    where TSessionToken : class, ISessionToken;
