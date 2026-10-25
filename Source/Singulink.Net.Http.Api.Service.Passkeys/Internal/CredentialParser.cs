using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Fido2NetLib;
using Fido2NetLib.Serialization;
using Singulink.Net.Http.Api.DataContracts;

namespace Singulink.Net.Http.Api.Service.Passkeys.Internal;

/// <summary>
/// Reads credentials from the JSON browsers produce (the <c>PublicKeyCredential.toJSON()</c> form).
/// </summary>
internal static class CredentialParser
{
    public static ParsedCredential<AuthenticatorAssertionRawResponse> ParseAssertion(string json)
    {
        return Parse(json, FidoModelSerializerContext.Default.AuthenticatorAssertionRawResponse, r => r.RawId, r => r.Response?.ClientDataJson);
    }

    public static ParsedCredential<AuthenticatorAttestationRawResponse> ParseAttestation(string json)
    {
        return Parse(json, PasskeyJsonContext.Default.AuthenticatorAttestationRawResponse, r => r.RawId, r => r.Response?.ClientDataJson);
    }

    private static ParsedCredential<T> Parse<T>(string json, JsonTypeInfo<T> typeInfo, Func<T, byte[]?> getRawId, Func<T, byte[]?> getClientData)
        where T : class
    {
        T? response;
        PasskeyAttachment attachment;
        List<string> transports = [];

        try
        {
            if (JsonNode.Parse(json) is not JsonObject root)
                throw InvalidCredential();

            attachment = ReadAttachment(root["authenticatorAttachment"]);

            if (root["response"] is JsonObject responseNode && responseNode["transports"] is JsonArray transportsNode)
                transports = Transports.ReadAndFilter(transportsNode);

            response = root.Deserialize(typeInfo);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            throw InvalidCredential();
        }

        if (response is null || getRawId(response) is not { Length: > 0 } || getClientData(response) is not { } clientData)
            throw InvalidCredential();

        return new ParsedCredential<T>(response, attachment, ReadOrigin(clientData), transports);
    }

    private static PasskeyAttachment ReadAttachment(JsonNode? node)
    {
        if (node is JsonValue value && value.GetValueKind() is JsonValueKind.String)
        {
            return value.GetValue<string>() switch {
                "platform" => PasskeyAttachment.Platform,
                "cross-platform" => PasskeyAttachment.CrossPlatform,
                _ => PasskeyAttachment.Unknown,
            };
        }

        return PasskeyAttachment.Unknown;
    }

    // The origin is read ahead of verification so that development origins can be accepted. Verification checks it, and everything else, again.
    private static string? ReadOrigin(byte[] clientDataJson)
    {
        try
        {
            using var document = JsonDocument.Parse(clientDataJson);

            if (document.RootElement.ValueKind is JsonValueKind.Object &&
                document.RootElement.TryGetProperty("origin", out var origin) &&
                origin.ValueKind is JsonValueKind.String)
            {
                return origin.GetString();
            }
        }
        catch (JsonException)
        {
            // Verification reports the malformed client data.
        }

        return null;
    }

    private static BadRequestApiException InvalidCredential() => new("Invalid passkey credential.");
}
