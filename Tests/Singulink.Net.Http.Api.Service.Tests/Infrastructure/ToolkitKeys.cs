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

    public const string UploadCount = "httpapi:upload-count:";

    public const string UploadSize = "httpapi:upload-size:";

    public const string UploadTotalSize = "httpapi:upload-total-size";

    public const string UploadCancel = "httpapi:upload-cancel:";
}
