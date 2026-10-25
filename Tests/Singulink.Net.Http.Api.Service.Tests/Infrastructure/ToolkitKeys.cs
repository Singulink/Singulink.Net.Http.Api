namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The prefixes of keys that the toolkit keeps in the short-lived store, for tests that look at or tamper with stored state.
/// </summary>
public static class ToolkitKeys
{
    public const string SignInCode = "httpapi:sign-in-code:";

    public const string DeviceLink = "httpapi:device-link:";

    public const string DeviceLinkCode = "httpapi:device-link-code:";

    public const string PasskeyCeremony = "httpapi:passkey-ceremony:";
}
