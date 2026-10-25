using System.Text.Json;
using Singulink.Net.Http.Api.DataContracts;

namespace Singulink.Net.Http.Api.Client.Passkeys;

/// <summary>
/// A passkey credential returned by an <see cref="IPasskeyAuthenticator"/>, for the app to post to its API.
/// </summary>
public sealed class PasskeyCredential
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PasskeyCredential"/> class.
    /// </summary>
    /// <param name="json">The credential as WebAuthn JSON.</param>
    /// <param name="attachment">Where the passkey came from.</param>
    public PasskeyCredential(string json, PasskeyAttachment attachment)
    {
        Json = json;
        Attachment = attachment;
    }

    /// <summary>
    /// Gets the credential as WebAuthn JSON (the <c>PublicKeyCredential.toJSON()</c> form), which the app posts to its API.
    /// </summary>
    public string Json { get; }

    /// <summary>
    /// Gets where the passkey came from, as reported by the browser: on this device (<see cref="PasskeyAttachment.Platform"/>) or on another one
    /// (<see cref="PasskeyAttachment.CrossPlatform"/>).
    /// </summary>
    public PasskeyAttachment Attachment { get; }

    /// <summary>
    /// Creates a credential from its WebAuthn JSON (the <c>PublicKeyCredential.toJSON()</c> form), with the attachment that its
    /// <c>authenticatorAttachment</c> property reports, or <see cref="PasskeyAttachment.Unknown"/> if it reports none or the JSON can't be read. The JSON
    /// is not validated; the service validates the credential.
    /// </summary>
    /// <param name="json">The credential as WebAuthn JSON.</param>
    public static PasskeyCredential FromJson(string json) => new(json, ReadAttachment(json));

    private static PasskeyAttachment ReadAttachment(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind is JsonValueKind.Object &&
                document.RootElement.TryGetProperty("authenticatorAttachment", out var attachment) &&
                attachment.ValueKind is JsonValueKind.String)
            {
                return attachment.GetString() switch {
                    "platform" => PasskeyAttachment.Platform,
                    "cross-platform" => PasskeyAttachment.CrossPlatform,
                    _ => PasskeyAttachment.Unknown,
                };
            }
        }
        catch (JsonException)
        {
            // The service reports the malformed credential.
        }

        return PasskeyAttachment.Unknown;
    }
}
