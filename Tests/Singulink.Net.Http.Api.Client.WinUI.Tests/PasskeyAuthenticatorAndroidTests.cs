using Singulink.Net.Http.Api.DataContracts;
using Windows.Security.Authentication.Web;

namespace Singulink.Net.Http.Api.Client.Passkeys;

/// <summary>
/// Runs the passkey page hand-off on the Android path, which goes through Uno Platform's web authentication broker at run time in every build that an
/// Android app can load, with a fake broker in place of Uno's.
/// </summary>
[PrefixTestClass]
public sealed class PasskeyAuthenticatorAndroidTests
{
    private const string RequestOptionsJson = """{"challenge":"Y2hhbGxlbmdl","rpId":"example.com","allowCredentials":[],"userVerification":"required"}""";
    private const string CreationOptionsJson = """{"challenge":"Y2hhbGxlbmdl","rp":{"id":"example.com","name":"Example"},"user":{"id":"dTE","name":"zoë"}}""";
    private const string CredentialJson = """{"id":"Y3JlZA","rawId":"Y3JlZA","type":"public-key","authenticatorAttachment":"cross-platform"}""";

    private static readonly Uri PageUri = new("https://example.com/passkey");
    private static readonly Uri CallbackUri = new("myapp://passkey");
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task GetCredentialAsync_OpensPageWithBroker_ReturnsCredential()
    {
        var broker = new FakeBroker(query => $"state={query["state"]}&credential={EncodeBase64Url(CredentialJson)}");
        var authenticator = CreateAuthenticator(broker, new() { PageUri = PageUri, CallbackUri = CallbackUri });

        var credential = await authenticator.GetCredentialAsync(RequestOptionsJson, TestContext.CancellationToken).WaitAsync(TestTimeout);

        credential.Json.ShouldBe(CredentialJson);
        credential.Attachment.ShouldBe(PasskeyAttachment.CrossPlatform);

        var request = broker.Requests.ShouldHaveSingleItem();
        request.CallbackUri.ShouldBe(CallbackUri);

        var query = PasskeyPageProtocol.ParseQuery(request.RequestUri.OriginalString);
        query["op"].ShouldBe("get");
        query["redirect"].ShouldBe("myapp://passkey");
    }

    [TestMethod]
    public async Task CreateCredentialAsync_SendsCreateOperation()
    {
        var broker = new FakeBroker(query => $"state={query["state"]}&credential={EncodeBase64Url(CredentialJson)}");
        var authenticator = CreateAuthenticator(broker, new() { PageUri = PageUri, CallbackUri = CallbackUri });

        await authenticator.CreateCredentialAsync(CreationOptionsJson, TestContext.CancellationToken).WaitAsync(TestTimeout);

        PasskeyPageProtocol.ParseQuery(broker.Requests.ShouldHaveSingleItem().RequestUri.OriginalString)["op"].ShouldBe("create");
    }

    [TestMethod]
    public async Task NoCallbackUri_UsesTheBrokersCallbackUri()
    {
        var broker = new FakeBroker(query => $"state={query["state"]}&credential={EncodeBase64Url(CredentialJson)}");
        var authenticator = CreateAuthenticator(broker, new() { PageUri = PageUri });
        authenticator.GetDefaultCallbackUri = () => new Uri("myapp:/authentication-callback");

        await authenticator.GetCredentialAsync(RequestOptionsJson, TestContext.CancellationToken).WaitAsync(TestTimeout);

        broker.Requests.ShouldHaveSingleItem().CallbackUri.ShouldBe(new Uri("myapp:/authentication-callback"));
    }

    [TestMethod]
    public async Task NoCallbackUri_NoCallbackActivity_ThrowsInvalidOperation()
    {
        var broker = new FakeBroker(null);
        var authenticator = CreateAuthenticator(broker, new() { PageUri = PageUri });
        authenticator.GetDefaultCallbackUri = () => throw new InvalidOperationException("No custom scheme found for this application.");

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => authenticator.GetCredentialAsync(RequestOptionsJson, TestContext.CancellationToken));

        ex.Message.ShouldStartWith("PasskeyAuthenticatorOptions.CallbackUri must be set");
        broker.Requests.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task DefaultCallbackUriWithBrowserScheme_ThrowsInvalidOperation()
    {
        var broker = new FakeBroker(null);
        var authenticator = CreateAuthenticator(broker, new() { PageUri = PageUri });
        authenticator.GetDefaultCallbackUri = () => new Uri("https://example.com/callback");

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => authenticator.GetCredentialAsync(RequestOptionsJson, TestContext.CancellationToken));

        ex.Message.ShouldStartWith("The web authentication broker's callback address must have a custom scheme");
        broker.Requests.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task NoMatchingActivity_BrokersInvalidOperationPassesThrough()
    {
        // Uno Platform's Android broker throws this when no activity derives from WebAuthenticationBrokerActivityBase with a matching intent filter.
        var broker = new FakeBroker(null) { Exception = new InvalidOperationException("You must subclass the `WebAuthenticationBrokerActivityBase`.") };
        var authenticator = CreateAuthenticator(broker, new() { PageUri = PageUri, CallbackUri = CallbackUri });

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => authenticator.GetCredentialAsync(RequestOptionsJson, TestContext.CancellationToken).WaitAsync(TestTimeout));

        ex.Message.ShouldStartWith("You must subclass");
    }

    [TestMethod]
    public async Task CallerCancels_ThrowsOperationCanceled()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var broker = new FakeBroker(null) { WaitForCancellation = true };
        var authenticator = CreateAuthenticator(broker, new() { PageUri = PageUri, CallbackUri = CallbackUri });

        var task = authenticator.GetCredentialAsync(RequestOptionsJson, cts.Token);
        broker.Requests.ShouldHaveSingleItem();
        await cts.CancelAsync();

        var ex = await Should.ThrowAsync<OperationCanceledException>(() => task.WaitAsync(TestTimeout));
        ex.ShouldNotBeOfType<PasskeyAuthenticatorException>();
    }

    [TestMethod]
    public async Task HttpError_IncludesTheStatus()
    {
        var broker = new FakeBroker(null) { Result = new WebAuthenticationResult("Not Found", 404, WebAuthenticationStatus.ErrorHttp) };
        var authenticator = CreateAuthenticator(broker, new() { PageUri = PageUri, CallbackUri = CallbackUri });

        var ex = await Should.ThrowAsync<PasskeyAuthenticatorException>(
            () => authenticator.GetCredentialAsync(RequestOptionsJson, TestContext.CancellationToken).WaitAsync(TestTimeout));

        ex.Reason.ShouldBe(PasskeyErrorReason.PageNotOpened);
        ex.Message.ShouldBe("The passkey page could not be opened (HTTP 404, Not Found).");
    }

    [TestMethod]
    public async Task PageError_ThrowsPasskeyAuthenticatorException()
    {
        var broker = new FakeBroker(query => $"state={query["state"]}&error=NotAllowedError&message=From+the+page.");
        var authenticator = CreateAuthenticator(broker, new() { PageUri = PageUri, CallbackUri = CallbackUri });

        var ex = await Should.ThrowAsync<PasskeyAuthenticatorException>(
            () => authenticator.GetCredentialAsync(RequestOptionsJson, TestContext.CancellationToken).WaitAsync(TestTimeout));

        ex.Reason.ShouldBe(PasskeyErrorReason.NotAllowed);
        ex.Message.ShouldBe("From the page.");
    }

    [TestMethod]
    public async Task BrowserTabClosed_ThrowsCancelled()
    {
        var broker = new FakeBroker(null) { ThrowsCancellation = true };
        var authenticator = CreateAuthenticator(broker, new() { PageUri = PageUri, CallbackUri = CallbackUri });

        var ex = await Should.ThrowAsync<PasskeyAuthenticatorException>(
            () => authenticator.GetCredentialAsync(RequestOptionsJson, TestContext.CancellationToken).WaitAsync(TestTimeout));

        ex.Reason.ShouldBe(PasskeyErrorReason.Cancelled);
    }

    [TestMethod]
    [DataRow(WebAuthenticationStatus.UserCancel, PasskeyErrorReason.Cancelled)]
    [DataRow(WebAuthenticationStatus.ErrorHttp, PasskeyErrorReason.PageNotOpened)]
    public async Task BrokerResult_ThrowsPasskeyAuthenticatorException(WebAuthenticationStatus status, PasskeyErrorReason expectedReason)
    {
        var broker = new FakeBroker(null) { Result = new WebAuthenticationResult(null, 0, status) };
        var authenticator = CreateAuthenticator(broker, new() { PageUri = PageUri, CallbackUri = CallbackUri });

        var ex = await Should.ThrowAsync<PasskeyAuthenticatorException>(
            () => authenticator.GetCredentialAsync(RequestOptionsJson, TestContext.CancellationToken).WaitAsync(TestTimeout));

        ex.Reason.ShouldBe(expectedReason);
    }

    [TestMethod]
    public async Task RegisteredBrokerProvider_OnDesktop_UsesTheBrokersCallbackUri()
    {
        // The options' callback address is only used on iOS and Android.
        var broker = new FakeBroker(query => $"state={query["state"]}&credential={EncodeBase64Url(CredentialJson)}");
        var authenticator = CreateAuthenticator(broker, new() { PageUri = PageUri, CallbackUri = CallbackUri });
        authenticator.IsAndroid = false;
        authenticator.IsBrokerProviderRegistered = () => true;
        authenticator.GetDefaultCallbackUri = () => new Uri("http://127.0.0.1:5000/callback");

        await authenticator.GetCredentialAsync(RequestOptionsJson, TestContext.CancellationToken).WaitAsync(TestTimeout);

        broker.Requests.ShouldHaveSingleItem().CallbackUri.ShouldBe(new Uri("http://127.0.0.1:5000/callback"));
    }

    private static PasskeyAuthenticator CreateAuthenticator(FakeBroker broker, PasskeyAuthenticatorOptions options) => new(options) {
        IsAndroid = true,
        IsBrokerProviderRegistered = () => false,
        NativeAuthenticator = null,
        AuthenticateWithBroker = broker.AuthenticateAsync,
        CreateLoopbackFlow = () => throw new InvalidOperationException("The loopback flow is not used on Android."),
        GetDefaultCallbackUri = () => throw new InvalidOperationException("The default callback address is not used when the options have one."),
    };

    private static string EncodeBase64Url(string value) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(value));
}
