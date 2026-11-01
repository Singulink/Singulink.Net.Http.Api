namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Tests the plain .NET build, which reports that it has no native sessions, so that callers fall back to their own.
/// </summary>
[PrefixTestClass]
public sealed class WebAuthenticationNativeTests
{
    private static readonly Uri RequestUri = new("https://example.com/passkey?op=get");
    private static readonly Uri CallbackUri = new("myapp://passkey");

    [TestMethod]
    public void IsSupported_IsFalse()
    {
        WebAuthenticationNative.IsSupported.ShouldBeFalse();
    }

    [TestMethod]
    public void AuthenticateAsync_ThrowsPlatformNotSupported()
    {
        Should.Throw<PlatformNotSupportedException>(() => WebAuthenticationNative.AuthenticateAsync(RequestUri, CallbackUri));
    }

    [TestMethod]
    [DataRow("myapp://passkey")]
    [DataRow("ftp://example.com/passkey")]
    public void RequestUriThatIsNotHttp_ThrowsArgumentException(string requestUri)
    {
        Should.Throw<ArgumentException>(() => WebAuthenticationNative.AuthenticateAsync(new Uri(requestUri), CallbackUri)).ParamName.ShouldBe("requestUri");
    }

    [TestMethod]
    public void RelativeRequestUri_ThrowsArgumentException()
    {
        Should.Throw<ArgumentException>(() => WebAuthenticationNative.AuthenticateAsync(new Uri("/passkey", UriKind.Relative), CallbackUri))
            .ParamName.ShouldBe("requestUri");
    }

    [TestMethod]
    public void RelativeCallbackUri_ThrowsArgumentException()
    {
        Should.Throw<ArgumentException>(() => WebAuthenticationNative.AuthenticateAsync(RequestUri, new Uri("callback", UriKind.Relative)))
            .ParamName.ShouldBe("callbackUri");
    }
}
