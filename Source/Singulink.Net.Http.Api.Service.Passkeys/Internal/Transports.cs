using System.Text.Json;
using System.Text.Json.Nodes;
using Fido2NetLib.Objects;

namespace Singulink.Net.Http.Api.Service.Passkeys.Internal;

/// <summary>
/// Converts between the transport names browsers report and the transports Fido2NetLib knows.
/// </summary>
internal static class Transports
{
    private const int MaxCount = 10;
    private const int MaxLength = 32;

    private static readonly Dictionary<string, AuthenticatorTransport> KnownTransports = new(StringComparer.Ordinal) {
        ["usb"] = AuthenticatorTransport.Usb,
        ["nfc"] = AuthenticatorTransport.Nfc,
        ["ble"] = AuthenticatorTransport.Ble,
        ["smart-card"] = AuthenticatorTransport.SmartCard,
        ["hybrid"] = AuthenticatorTransport.Hybrid,
        ["internal"] = AuthenticatorTransport.Internal,
    };

    /// <summary>
    /// Gets the known transports among the specified transport names, or <see langword="null"/> if there are none.
    /// </summary>
    public static AuthenticatorTransport[]? ToKnown(IEnumerable<string> names)
    {
        var known = names.Select(n => KnownTransports.TryGetValue(n, out var t) ? t : (AuthenticatorTransport?)null).OfType<AuthenticatorTransport>().ToArray();
        return known.Length > 0 ? known : null;
    }

    /// <summary>
    /// Reads the transport names in a JSON array, ignoring values that are not plausible transport names, and replaces the array's contents with the known
    /// transports only, so that a transport added to WebAuthn after this library does not fail the credential.
    /// </summary>
    public static List<string> ReadAndFilter(JsonArray array)
    {
        List<string> names = [];

        foreach (var item in array)
        {
            if (item is JsonValue value && value.GetValueKind() is JsonValueKind.String &&
                value.GetValue<string>() is { Length: > 0 and <= MaxLength } name &&
                name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-') &&
                !names.Contains(name) && names.Count < MaxCount)
            {
                names.Add(name);
            }
        }

        array.Clear();

        foreach (string name in names)
        {
            if (KnownTransports.ContainsKey(name))
                array.Add((JsonNode)JsonValue.Create(name)); // The JsonNode overload: Add<T> is not trim-safe.
        }

        return names;
    }
}
