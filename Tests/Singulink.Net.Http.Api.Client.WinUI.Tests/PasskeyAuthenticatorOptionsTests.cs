namespace Singulink.Net.Http.Api.Client.Passkeys;

[PrefixTestClass]
public sealed class PasskeyAuthenticatorOptionsTests
{
    [TestMethod]
    public void Options_Defaults()
    {
        var options = new PasskeyAuthenticatorOptions();

        options.PageUri.ShouldBeNull();
        options.CallbackUri.ShouldBeNull();
        options.PrefersEphemeralWebBrowserSession.ShouldBeTrue();
        options.CompletedMessage.ShouldBe("You can close this tab and return to the app.");
        options.Timeout.ShouldBeNull();
        options.StartTimeout.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [TestMethod]
    public void Options_HandOffSettings_RejectInvalidValues()
    {
        var options = new PasskeyAuthenticatorOptions();

        Should.Throw<ArgumentException>(() => options.CompletedMessage = string.Empty);
        Should.Throw<ArgumentOutOfRangeException>(() => options.Timeout = TimeSpan.Zero);
        Should.Throw<ArgumentOutOfRangeException>(() => options.Timeout = TimeSpan.FromSeconds(-1));
        Should.Throw<ArgumentOutOfRangeException>(() => options.StartTimeout = TimeSpan.Zero);

        options.CompletedMessage = "Done.";
        options.Timeout = TimeSpan.FromMinutes(10);
        options.StartTimeout = TimeSpan.FromSeconds(5);

        options.CompletedMessage.ShouldBe("Done.");
        options.Timeout.ShouldBe(TimeSpan.FromMinutes(10));
        options.StartTimeout.ShouldBe(TimeSpan.FromSeconds(5));

        options.Timeout = null;
        options.Timeout.ShouldBeNull();
    }

    [TestMethod]
    [DataRow("https://example.com/passkey")]
    [DataRow("https://service.example.com:8443/passkey?lang=de")]
    [DataRow("http://localhost:5000/passkey")]
    [DataRow("http://127.0.0.1/passkey")]
    [DataRow("http://[::1]:5000/passkey")]
    public void Options_PageUri_AcceptsHttpsAndLoopbackHttp(string pageUri)
    {
        var options = new PasskeyAuthenticatorOptions { PageUri = new Uri(pageUri) };

        options.PageUri.ShouldBe(new Uri(pageUri));
    }

    [TestMethod]
    [DataRow("http://example.com/passkey")]
    [DataRow("ftp://example.com/passkey")]
    [DataRow("myapp://passkey")]
    public void Options_PageUri_RejectsOtherAddresses(string pageUri)
    {
        Should.Throw<ArgumentException>(() => new PasskeyAuthenticatorOptions { PageUri = new Uri(pageUri) });
    }

    [TestMethod]
    public void Options_PageUri_RejectsRelativeAddress()
    {
        Should.Throw<ArgumentException>(() => new PasskeyAuthenticatorOptions { PageUri = new Uri("/passkey", UriKind.Relative) });
    }

    [TestMethod]
    public void Options_CallbackUri_AcceptsAbsoluteAndRejectsRelative()
    {
        var options = new PasskeyAuthenticatorOptions { CallbackUri = new Uri("myapp://auth") };
        options.CallbackUri.ShouldBe(new Uri("myapp://auth"));

        Should.Throw<ArgumentException>(() => options.CallbackUri = new Uri("auth", UriKind.Relative));
    }

    [TestMethod]
    public void Options_AcceptNull()
    {
        var options = new PasskeyAuthenticatorOptions { PageUri = new Uri("https://example.com/passkey"), CallbackUri = new Uri("myapp://auth") };

        options.PageUri = null;
        options.CallbackUri = null;

        options.PageUri.ShouldBeNull();
        options.CallbackUri.ShouldBeNull();
    }
}
