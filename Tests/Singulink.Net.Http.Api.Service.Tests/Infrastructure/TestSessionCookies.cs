using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Writes and reads session cookies in the library's format, <c>{"r":refreshed,"v":validFor,"g":generation,"p":persistent,"t":token}</c>, with the
/// token serialized with reflection or with the specified JSON type info.
/// </summary>
public static class TestSessionCookies
{
    /// <summary>
    /// The default name of the session cookie.
    /// </summary>
    public const string CookieName = "__Host-session-token";

    /// <summary>
    /// The default data protection purpose of the session cookie.
    /// </summary>
    public static readonly string Purpose = $"Singulink/Session[{typeof(TestSessionToken).FullName}]";

    public static string ToJson(TestSessionCookie cookie, JsonTypeInfo<TestSessionToken>? tokenTypeInfo = null)
    {
        var json = new JsonObject {
            ["r"] = JsonSerializer.SerializeToNode(cookie.RefreshedUtc),
            ["v"] = JsonSerializer.SerializeToNode(cookie.ValidFor),
            ["g"] = cookie.Generation,
            ["p"] = cookie.IsPersistent,
            ["t"] = tokenTypeInfo is null ? JsonSerializer.SerializeToNode(cookie.Token) : JsonSerializer.SerializeToNode(cookie.Token, tokenTypeInfo),
        };

        return json.ToJsonString();
    }

    public static TestSessionCookie FromJson(string data, JsonTypeInfo<TestSessionToken>? tokenTypeInfo = null)
    {
        var json = JsonNode.Parse(data).ShouldNotBeNull().AsObject();
        json.Select(p => p.Key).ShouldBe(["r", "v", "g", "p", "t"]);

        var tokenJson = json["t"].ShouldNotBeNull();
        var token = tokenTypeInfo is null ? tokenJson.Deserialize<TestSessionToken>() : tokenJson.Deserialize(tokenTypeInfo);

        return new TestSessionCookie(
            token.ShouldNotBeNull(),
            json["r"].Deserialize<DateTime>(),
            json["v"].Deserialize<TimeSpan>(),
            json["g"].Deserialize<int>(),
            json["p"].Deserialize<bool>());
    }

    /// <summary>
    /// Gets the protected cookie value for the specified cookie contents, with the data protection of the specified services.
    /// </summary>
    public static string Protect(IServiceProvider services, TestSessionCookie cookie)
    {
        return services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect(ToJson(cookie));
    }
}
