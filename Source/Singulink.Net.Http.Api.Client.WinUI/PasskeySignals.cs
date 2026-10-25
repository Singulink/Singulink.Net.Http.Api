using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Singulink.Net.Http.Api.Client.Passkeys;

/// <summary>
/// Reads the arguments of WebAuthn signals from the WebAuthn JSON of credentials and options.
/// </summary>
internal static class PasskeySignals
{
    /// <summary>
    /// Reads the credential ID from a credential and the relying party ID from request options, which is <see langword="null"/> if the options don't name
    /// one (the page's host name is then used). Returns <see langword="false"/> if the credential has no ID.
    /// </summary>
    public static bool TryGetUnknownCredentialArguments(
        string credentialJson, string requestOptionsJson, out string? rpId, [NotNullWhen(true)] out string? credentialId)
    {
        rpId = null;
        credentialId = ReadString(credentialJson, "id");

        if (string.IsNullOrEmpty(credentialId))
        {
            credentialId = null;
            return false;
        }

        rpId = ReadString(requestOptionsJson, "rpId") is { Length: > 0 } id ? id : null;
        return true;
    }

    private static string? ReadString(string json, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            return root.ValueKind is JsonValueKind.Object && root.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.String ?
                value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
