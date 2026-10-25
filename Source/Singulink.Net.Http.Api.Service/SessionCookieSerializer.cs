using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Singulink.Net.Http.Api.Service;

#pragma warning disable SA1402 // File may only contain a single type

/// <summary>
/// Serializes session cookies: the refresh info with source-generated metadata, and the session token with the JSON type info that session handling was
/// registered with. For example: <c>{"r":"2026-10-21T08:30:00Z","v":"30.00:00:00","g":3,"p":true,"t":{...}}</c>.
/// </summary>
internal sealed class SessionCookieSerializer<TSessionToken>(JsonTypeInfo<TSessionToken> tokenTypeInfo)
    where TSessionToken : class, ISessionToken
{
    public string Serialize(SessionCookie<TSessionToken> cookie)
    {
        var data = new SessionCookieData {
            RefreshedUtc = cookie.RefreshedUtc,
            ValidFor = cookie.ValidFor,
            Generation = cookie.Generation,
            IsPersistent = cookie.IsPersistent,
            Token = JsonSerializer.SerializeToElement(cookie.Token, tokenTypeInfo),
        };

        return JsonSerializer.Serialize(data, SessionCookieJsonContext.Default.SessionCookieData);
    }

    /// <summary>
    /// Reads a session cookie from serialized data, or returns <see langword="null"/> if it has no session token. Throws a <see cref="JsonException"/> if
    /// the data is not a valid session cookie.
    /// </summary>
    public SessionCookie<TSessionToken>? Deserialize(string data)
    {
        if (JsonSerializer.Deserialize(data, SessionCookieJsonContext.Default.SessionCookieData) is not { } cookieData)
            return null;

        if (cookieData.Token.Deserialize(tokenTypeInfo) is not { } token)
            return null;

        return new(token, cookieData.RefreshedUtc, cookieData.ValidFor, cookieData.Generation, cookieData.IsPersistent);
    }
}

internal sealed class SessionCookieData
{
    [JsonPropertyName("r")]
    public required DateTime RefreshedUtc { get; init; }

    [JsonPropertyName("v")]
    public required TimeSpan ValidFor { get; init; }

    [JsonPropertyName("g")]
    public required int Generation { get; init; }

    [JsonPropertyName("p")]
    public required bool IsPersistent { get; init; }

    [JsonPropertyName("t")]
    public required JsonElement Token { get; init; }
}

[JsonSerializable(typeof(SessionCookieData))]
internal sealed partial class SessionCookieJsonContext : JsonSerializerContext;
