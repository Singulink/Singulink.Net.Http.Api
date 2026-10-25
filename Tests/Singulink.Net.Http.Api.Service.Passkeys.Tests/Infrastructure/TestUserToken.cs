namespace Singulink.Net.Http.Api.Service.Passkeys.Infrastructure;

/// <summary>
/// A session token of a signed-in user, for the overloads that take one.
/// </summary>
public sealed record TestUserToken(string UserId) : ISessionToken;
