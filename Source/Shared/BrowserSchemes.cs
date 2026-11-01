namespace Singulink.Net.Http.Api;

/// <summary>
/// The URI schemes that browsers handle themselves rather than handing them to an app. Linked into the passkey page's service package, which only returns
/// to callback addresses with other (custom) schemes, and into the WinUI client package, which only opens the page with one, so that both reject the same
/// schemes.
/// </summary>
internal static class BrowserSchemes
{
    private static readonly HashSet<string> Schemes = new(StringComparer.OrdinalIgnoreCase) {
        "about", "blob", "chrome", "chrome-extension", "data", "edge", "file", "filesystem", "ftp", "http", "https", "intent", "javascript", "mailto",
        "sms", "tel", "vbscript", "view-source", "ws", "wss",
    };

    /// <summary>
    /// Gets a value indicating whether browsers handle the specified scheme themselves.
    /// </summary>
    public static bool Contains(string scheme) => Schemes.Contains(scheme);

    /// <summary>
    /// Gets a value indicating whether an address is absolute and has a custom scheme, which browsers hand to an app.
    /// </summary>
    public static bool HasCustomScheme(Uri uri) => uri.IsAbsoluteUri && !Contains(uri.Scheme);
}
