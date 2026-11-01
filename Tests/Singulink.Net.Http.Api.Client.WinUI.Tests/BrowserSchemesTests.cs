namespace Singulink.Net.Http.Api;

/// <summary>
/// Tests the schemes that callback addresses can't have, which the WinUI client shares with the passkey page.
/// </summary>
[PrefixTestClass]
public sealed class BrowserSchemesTests
{
    [TestMethod]
    [DataRow("myapp://passkey", true)]
    [DataRow("myapp:/authentication-callback", true)]
    [DataRow("com.example.app:/callback", true)]
    [DataRow("https://example.com/callback", false)]
    [DataRow("HTTP://example.com/callback", false)]
    [DataRow("intent://callback", false)]
    [DataRow("chrome-extension://callback", false)]
    [DataRow("file:///callback", false)]
    [DataRow("data:text/plain,callback", false)]
    [DataRow("javascript:alert(1)", false)]
    public void HasCustomScheme_RejectsSchemesThatBrowsersHandle(string uri, bool hasCustomScheme)
    {
        BrowserSchemes.HasCustomScheme(new Uri(uri)).ShouldBe(hasCustomScheme);
    }

    [TestMethod]
    public void HasCustomScheme_RejectsRelativeAddresses()
    {
        BrowserSchemes.HasCustomScheme(new Uri("/callback", UriKind.Relative)).ShouldBeFalse();
    }
}
