using System.Text.Json.Serialization;

namespace Singulink.Net.Http.Api.Service.AotTestApp;

internal sealed record AppSessionToken(long SessionId, string UserId) : ISessionToken<AppSessionToken>;

/// <summary>
/// Serializes session tokens with default settings, which the reflection-based session handling registration also uses.
/// </summary>
[JsonSerializable(typeof(AppSessionToken))]
internal sealed partial class SessionTokenJsonContext : JsonSerializerContext;
