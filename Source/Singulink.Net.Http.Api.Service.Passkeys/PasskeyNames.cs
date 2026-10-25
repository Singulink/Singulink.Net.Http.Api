namespace Singulink.Net.Http.Api.Service.Passkeys;

/// <summary>
/// Provides names for passkeys: the provider a passkey is stored in, and the name a new passkey starts with.
/// </summary>
public static class PasskeyNames
{
    /// <summary>
    /// The maximum length of the names returned by <see cref="GetDefaultName"/>.
    /// </summary>
    public const int MaxDefaultNameLength = 40;

    private static readonly Dictionary<Guid, string> ProviderNames = new() {
        [new("ea9b8d66-4d01-1d21-3ce4-b6b48cb575d4")] = "Google Password Manager",
        [new("fbfc3007-154e-4ecc-8c0b-6e020557d7bd")] = "iCloud Keychain",
        [new("dd4ec289-e01d-41c9-bb89-70fa845d4bf2")] = "iCloud Keychain",
        [new("08987058-cadc-4b81-b6e1-30de50dcbe96")] = "Windows Hello",
        [new("9ddd1817-af5a-4672-a2b9-3e3dd95000a9")] = "Windows Hello",
        [new("6028b017-b1d4-4c02-b4b3-afcdafc96bb2")] = "Windows Hello",
        [new("d3452668-01fd-4c12-926c-83a4204853aa")] = "Microsoft Password Manager",
        [new("adce0002-35bc-c60a-648b-0b25f1f05503")] = "Chrome on Mac",
        [new("771b48fd-d3d4-4f74-9232-fc157ab0507a")] = "Edge on Mac",
        [new("53414d53-554e-4700-0000-000000000000")] = "Samsung Pass",
        [new("bada5566-a7aa-401f-bd96-45619a55120d")] = "1Password",
        [new("d548826e-79b4-db40-a3d8-11116f7e8349")] = "Bitwarden",
        [new("531126d6-e717-415c-9320-3d9aa6981239")] = "Dashlane",
        [new("f3809540-7f14-49c1-a8b3-8f813b225541")] = "Enpass",
        [new("fdb141b2-5d84-443e-8a35-4698c205a502")] = "KeePassXC",
        [new("0ea242b4-43c4-4a1b-8b17-dd6d0b6baec6")] = "Keeper",
        [new("b78a0a55-6ef8-d246-a042-ba0f6d55050c")] = "LastPass",
        [new("b84e4048-15dc-4dd0-8640-f4f60813c8af")] = "NordPass",
        [new("50726f74-6f6e-5061-7373-50726f746f6e")] = "Proton Pass",
    };

    // The platforms a device description can start with (the platform names reported by the service's session context), and how they are shown.
    private static readonly Dictionary<string, string> PlatformNames = new(StringComparer.OrdinalIgnoreCase) {
        ["Android"] = "Android",
        ["BlackBerry"] = "BlackBerry",
        ["ChromeOS"] = "ChromeOS",
        ["iOS"] = "iOS",
        ["Linux"] = "Linux",
        ["macOS"] = "macOS",
        ["Symbian"] = "Symbian",
        ["Unix"] = "Unix",
        ["Windows"] = "Windows",
    };

    /// <summary>
    /// Gets the name of the passkey provider (password manager or platform) with the specified AAGUID, such as "iCloud Keychain", "Google Password
    /// Manager" or "Windows Hello", or <see langword="null"/> if it is not a known provider.
    /// </summary>
    /// <param name="aaGuid">The AAGUID of the passkey (see <see cref="PasskeyRecord.AaGuid"/>).</param>
    public static string? GetProviderName(Guid aaGuid) => ProviderNames.GetValueOrDefault(aaGuid);

    /// <summary>
    /// Gets the name a new passkey can start with until the user renames it: its provider, or "Security key" for one that only connects by USB, NFC,
    /// Bluetooth or smart card, or "Passkey" otherwise, followed by the platform it was created on if it is known, such as "iCloud Keychain (iOS)". The
    /// name is at most <see cref="MaxDefaultNameLength"/> characters long.
    /// </summary>
    /// <param name="aaGuid">The AAGUID of the passkey.</param>
    /// <param name="transports">The transports the authenticator reported.</param>
    /// <param name="device">The device the passkey was created from, in the format of <see cref="SessionContext{TSessionToken}.Device"/> (such as
    /// "iOS (Safari 18.0)"), or <see langword="null"/> if it is not known.</param>
    public static string GetDefaultName(Guid aaGuid, IEnumerable<string> transports, string? device)
    {
        bool isSecurityKey = false;

        foreach (string transport in transports)
        {
            if (transport is not ("usb" or "nfc" or "ble" or "smart-card"))
            {
                isSecurityKey = false;
                break;
            }

            isSecurityKey = true;
        }

        string provider = GetProviderName(aaGuid) ?? (isSecurityKey ? "Security key" : "Passkey");

        // A security key goes from device to device, so the device it was added from says nothing about it.
        if (isSecurityKey || GetPlatform(device) is not { } platform)
            return provider;

        return $"{provider} ({platform})";
    }

    private static string? GetPlatform(string? device)
    {
        if (device is null)
            return null;

        int end = device.IndexOf(" (", StringComparison.Ordinal);
        string platform = end > 0 ? device[..end] : device;

        return PlatformNames.GetValueOrDefault(platform);
    }
}
