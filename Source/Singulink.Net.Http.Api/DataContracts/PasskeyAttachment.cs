using System.Text.Json.Serialization;

namespace Singulink.Net.Http.Api.DataContracts;

/// <summary>
/// Specifies where the passkey used in a ceremony came from, as reported by the browser. Serialized to JSON as the name of the value.
/// </summary>
/// <remarks>
/// The value is reported by the browser and not signed by the authenticator, so it is not proof of where the passkey is.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<PasskeyAttachment>))]
public enum PasskeyAttachment
{
    /// <summary>
    /// The browser did not say.
    /// </summary>
    Unknown,

    /// <summary>
    /// A passkey on the device itself, including one synced to it by a password manager.
    /// </summary>
    Platform,

    /// <summary>
    /// A passkey on another device, such as a phone used by scanning a QR code, or a security key.
    /// </summary>
    CrossPlatform,
}
