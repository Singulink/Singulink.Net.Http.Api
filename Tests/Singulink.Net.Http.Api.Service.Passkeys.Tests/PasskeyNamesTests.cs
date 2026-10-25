namespace Singulink.Net.Http.Api.Service.Passkeys;

[PrefixTestClass]
public sealed class PasskeyNamesTests
{
    private const string GooglePasswordManager = "ea9b8d66-4d01-1d21-3ce4-b6b48cb575d4";
    private const string ICloudKeychain = "fbfc3007-154e-4ecc-8c0b-6e020557d7bd";
    private const string WindowsHello = "08987058-cadc-4b81-b6e1-30de50dcbe96";
    private const string OnePassword = "bada5566-a7aa-401f-bd96-45619a55120d";
    private const string Unknown = "00000000-0000-0000-0000-000000000000";
    private const string YubiKey = "ee882879-721c-4913-9775-3dfcce97072a";

    [TestMethod]
    [DataRow(GooglePasswordManager, "Google Password Manager")]
    [DataRow(ICloudKeychain, "iCloud Keychain")]
    [DataRow("dd4ec289-e01d-41c9-bb89-70fa845d4bf2", "iCloud Keychain")]
    [DataRow(WindowsHello, "Windows Hello")]
    [DataRow("9ddd1817-af5a-4672-a2b9-3e3dd95000a9", "Windows Hello")]
    [DataRow("6028b017-b1d4-4c02-b4b3-afcdafc96bb2", "Windows Hello")]
    [DataRow("d3452668-01fd-4c12-926c-83a4204853aa", "Microsoft Password Manager")]
    [DataRow("adce0002-35bc-c60a-648b-0b25f1f05503", "Chrome on Mac")]
    [DataRow("771b48fd-d3d4-4f74-9232-fc157ab0507a", "Edge on Mac")]
    [DataRow("53414d53-554e-4700-0000-000000000000", "Samsung Pass")]
    [DataRow(OnePassword, "1Password")]
    [DataRow("d548826e-79b4-db40-a3d8-11116f7e8349", "Bitwarden")]
    [DataRow("531126d6-e717-415c-9320-3d9aa6981239", "Dashlane")]
    [DataRow("f3809540-7f14-49c1-a8b3-8f813b225541", "Enpass")]
    [DataRow("fdb141b2-5d84-443e-8a35-4698c205a502", "KeePassXC")]
    [DataRow("0ea242b4-43c4-4a1b-8b17-dd6d0b6baec6", "Keeper")]
    [DataRow("b78a0a55-6ef8-d246-a042-ba0f6d55050c", "LastPass")]
    [DataRow("b84e4048-15dc-4dd0-8640-f4f60813c8af", "NordPass")]
    [DataRow("50726f74-6f6e-5061-7373-50726f746f6e", "Proton Pass")]
    public void ProviderName_KnownProviders(string aaGuid, string expected)
    {
        PasskeyNames.GetProviderName(new Guid(aaGuid)).ShouldBe(expected);
    }

    [TestMethod]
    [DataRow(Unknown)]
    [DataRow(YubiKey)]
    public void ProviderName_UnknownProviders(string aaGuid)
    {
        PasskeyNames.GetProviderName(new Guid(aaGuid)).ShouldBeNull();
    }

    [TestMethod]
    [DataRow(ICloudKeychain, "internal,hybrid", "IOS (Safari 18.0)", "iCloud Keychain (iOS)")]
    [DataRow(ICloudKeychain, "internal,hybrid", "MacOS (Safari 18.0)", "iCloud Keychain (macOS)")]
    [DataRow(GooglePasswordManager, "hybrid,internal", "Android (Chrome 130.0)", "Google Password Manager (Android)")]
    [DataRow(WindowsHello, "internal", "Windows (Edge 130.0)", "Windows Hello (Windows)")]
    [DataRow(OnePassword, "internal,hybrid", "Linux (Firefox 131.0)", "1Password (Linux)")]
    [DataRow(OnePassword, "internal,hybrid", "ChromeOS (Chrome 130.0)", "1Password (ChromeOS)")]
    [DataRow(Unknown, "internal", "Windows (Chrome 130.0)", "Passkey (Windows)")]
    [DataRow(Unknown, "internal", "Windows", "Passkey (Windows)")]
    [DataRow(Unknown, "", "Android (Chrome 130.0)", "Passkey (Android)")]
    [DataRow(Unknown, "internal", null, "Passkey")]
    [DataRow(Unknown, "internal", "", "Passkey")]
    [DataRow(Unknown, "internal", "Generic (Chrome 130.0)", "Passkey")]
    [DataRow(Unknown, "internal", "Unknown (Chrome 130.0)", "Passkey")]
    [DataRow(Unknown, "internal", "Mozilla/5.0 (X11; Linux x86_64) Gecko/20100101", "Passkey")]
    [DataRow(YubiKey, "usb", "Windows (Chrome 130.0)", "Security key")]
    [DataRow(YubiKey, "usb,nfc", "IOS (Safari 18.0)", "Security key")]
    [DataRow(YubiKey, "nfc,ble,smart-card", null, "Security key")]
    [DataRow(YubiKey, "usb,hybrid", "Windows (Chrome 130.0)", "Passkey (Windows)")]
    [DataRow(WindowsHello, "usb", "Windows (Chrome 130.0)", "Windows Hello")]
    public void DefaultName(string aaGuid, string transports, string? device, string expected)
    {
        string[] transportList = transports.Length is 0 ? [] : transports.Split(',');

        PasskeyNames.GetDefaultName(new Guid(aaGuid), transportList, device).ShouldBe(expected);
    }

    [TestMethod]
    public void DefaultName_IsNeverLongerThanTheMaximum()
    {
        string[] devices = ["Android (x)", "BlackBerry (x)", "ChromeOS (x)", "IOS (x)", "Linux (x)", "MacOS (x)", "Symbian (x)", "Unix (x)", "Windows (x)"];

        var aaGuids = typeof(PasskeyNames)
            .GetField("ProviderNames", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)
            .ShouldBeOfType<Dictionary<Guid, string>>()
            .Keys;

        foreach (var aaGuid in aaGuids)
        {
            foreach (string device in devices)
                PasskeyNames.GetDefaultName(aaGuid, ["internal"], device).Length.ShouldBeLessThanOrEqualTo(PasskeyNames.MaxDefaultNameLength);
        }
    }
}
