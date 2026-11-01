using Singulink.Net.Http.Api.DataContracts;

namespace Singulink.Net.Http.Api.Client.Passkeys;

/// <summary>
/// Runs the passkey page hand-off on the iOS path, with a fake web authentication session in place of the platform's, which runs in every build that an iOS
/// app can load.
/// </summary>
[PrefixTestClass]
public sealed class PasskeyAuthenticatorNativeTests
{
    private const string RequestOptionsJson = """{"challenge":"Y2hhbGxlbmdl","rpId":"example.com","allowCredentials":[],"userVerification":"required"}""";
    private const string CreationOptionsJson = """{"challenge":"Y2hhbGxlbmdl","rp":{"id":"example.com","name":"Example"},"user":{"id":"dTE","name":"zoë"}}""";
    private const string CredentialJson = """{"id":"Y3JlZA","rawId":"Y3JlZA","type":"public-key","authenticatorAttachment":"platform"}""";

    private static readonly Uri PageUri = new("https://example.com/passkey?lang=en");
    private static readonly Uri CallbackUri = new("myapp://passkey");
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void NoNativeSessions_OnThisPlatform()
    {
        // The tests run on the plain .NET build, which Uno Platform apps with the Skia renderer also load on iOS, where it finds them.
        WebAuthenticationNative.IsSupported.ShouldBeFalse();

        var authenticator = new PasskeyAuthenticator();
        authenticator.NativeAuthenticator.ShouldBeNull();
        authenticator.IsAndroid.ShouldBeFalse();
        authenticator.IsBrokerProviderRegistered().ShouldBeFalse();
    }

    [TestMethod]
    public async Task GetCredentialAsync_OpensPageInNativeSession_ReturnsCredential()
    {
        var session = new FakeSession(query => $"state={query["state"]}&credential={EncodeBase64Url(CredentialJson)}");
        var authenticator = session.CreateAuthenticator(new() { PageUri = PageUri, CallbackUri = CallbackUri });

        var credential = await authenticator.GetCredentialAsync(RequestOptionsJson, TestContext.CancellationToken).WaitAsync(TestTimeout);

        credential.Json.ShouldBe(CredentialJson);
        credential.Attachment.ShouldBe(PasskeyAttachment.Platform);

        var request = session.Requests.ShouldHaveSingleItem();
        request.CallbackUri.ShouldBe(CallbackUri);
        request.PrefersEphemeralWebBrowserSession.ShouldBeTrue();
        request.RequestUri.GetLeftPart(UriPartial.Path).ShouldBe("https://example.com/passkey");

        var query = PasskeyPageProtocol.ParseQuery(request.RequestUri.OriginalString);
        query["lang"].ShouldBe("en");
        query["op"].ShouldBe("get");
        DecodeBase64Url(query["options"]).ShouldBe(RequestOptionsJson);
        query["redirect"].ShouldBe("myapp://passkey");
        query["state"].ShouldMatch("^[A-Za-z0-9_-]{43}$");
    }

    [TestMethod]
    public async Task CreateCredentialAsync_SendsCreateOperation()
    {
        var session = new FakeSession(query => $"state={query["state"]}&credential={EncodeBase64Url(CredentialJson)}");
        var authenticator = session.CreateAuthenticator(new() { PageUri = PageUri, CallbackUri = CallbackUri });

        var credential = await authenticator.CreateCredentialAsync(CreationOptionsJson, TestContext.CancellationToken).WaitAsync(TestTimeout);

        credential.Json.ShouldBe(CredentialJson);

        var query = PasskeyPageProtocol.ParseQuery(session.Requests.ShouldHaveSingleItem().RequestUri.OriginalString);
        query["op"].ShouldBe("create");
        DecodeBase64Url(query["options"]).ShouldBe(CreationOptionsJson);
    }

    [TestMethod]
    public async Task PrefersEphemeralWebBrowserSession_IsPassedOn()
    {
        var session = new FakeSession(query => $"state={query["state"]}&credential={EncodeBase64Url(CredentialJson)}");
        var authenticator = session.CreateAuthenticator(new() { PageUri = PageUri, CallbackUri = CallbackUri, PrefersEphemeralWebBrowserSession = false });

        await authenticator.GetCredentialAsync(RequestOptionsJson, TestContext.CancellationToken).WaitAsync(TestTimeout);

        session.Requests.ShouldHaveSingleItem().PrefersEphemeralWebBrowserSession.ShouldBeFalse();
    }

    [TestMethod]
    public async Task NoCallbackUri_UsesDefaultCallbackUri()
    {
        var session = new FakeSession(query => $"state={query["state"]}&credential={EncodeBase64Url(CredentialJson)}");
        var authenticator = session.CreateAuthenticator(new() { PageUri = PageUri });
        authenticator.GetDefaultCallbackUri = () => new Uri("myapp:/authentication-callback");

        await authenticator.GetCredentialAsync(RequestOptionsJson, TestContext.CancellationToken).WaitAsync(TestTimeout);

        var request = session.Requests.ShouldHaveSingleItem();
        request.CallbackUri.ShouldBe(new Uri("myapp:/authentication-callback"));
        PasskeyPageProtocol.ParseQuery(request.RequestUri.OriginalString)["redirect"].ShouldBe("myapp:/authentication-callback");
    }

    [TestMethod]
    public async Task NoCallbackUri_NoDefault_ThrowsInvalidOperation()
    {
        var session = new FakeSession(null);
        var authenticator = session.CreateAuthenticator(new() { PageUri = PageUri });
        authenticator.GetDefaultCallbackUri = () => throw new InvalidOperationException("No custom scheme found for this application.");

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => authenticator.GetCredentialAsync(RequestOptionsJson, TestContext.CancellationToken));

        ex.Message.ShouldStartWith("PasskeyAuthenticatorOptions.CallbackUri must be set");
        session.Requests.ShouldBeEmpty();
    }

    [TestMethod]
    [DataRow("InvalidStateError", PasskeyErrorReason.AlreadyOnAuthenticator)]
    [DataRow("NotAllowedError", PasskeyErrorReason.NotAllowed)]
    [DataRow("UnknownError", PasskeyErrorReason.Unknown)]
    public async Task PageError_ThrowsPasskeyAuthenticatorException(string errorName, PasskeyErrorReason expectedReason)
    {
        var session = new FakeSession(query => $"state={query["state"]}&error={errorName}&message=From+the+page.");
        var authenticator = session.CreateAuthenticator(new() { PageUri = PageUri, CallbackUri = CallbackUri });

        var ex = await Should.ThrowAsync<PasskeyAuthenticatorException>(
            () => authenticator.GetCredentialAsync(RequestOptionsJson, TestContext.CancellationToken).WaitAsync(TestTimeout));

        ex.Reason.ShouldBe(expectedReason);
        ex.Message.ShouldEndWith("From the page.");
    }

    [TestMethod]
    public async Task ResponseForOtherRequest_Throws()
    {
        var session = new FakeSession(_ => $"state=forged&credential={EncodeBase64Url(CredentialJson)}");
        var authenticator = session.CreateAuthenticator(new() { PageUri = PageUri, CallbackUri = CallbackUri });

        var ex = await Should.ThrowAsync<PasskeyAuthenticatorException>(
            () => authenticator.GetCredentialAsync(RequestOptionsJson, TestContext.CancellationToken).WaitAsync(TestTimeout));

        ex.Reason.ShouldBe(PasskeyErrorReason.Unknown);
    }

    [TestMethod]
    public async Task BrowserClosed_ThrowsCancelled()
    {
        var session = new FakeSession(null) { Result = new(WebAuthenticationNativeStatus.UserCancel) };
        var authenticator = session.CreateAuthenticator(new() { PageUri = PageUri, CallbackUri = CallbackUri });

        var ex = await Should.ThrowAsync<PasskeyAuthenticatorException>(
            () => authenticator.GetCredentialAsync(RequestOptionsJson, TestContext.CancellationToken).WaitAsync(TestTimeout));

        ex.Reason.ShouldBe(PasskeyErrorReason.Cancelled);
        ex.IsCancellation.ShouldBeTrue();
    }

    [TestMethod]
    public async Task SessionNotOpened_ThrowsPageNotOpened()
    {
        var session = new FakeSession(null) {
            Result = new(WebAuthenticationNativeStatus.NotOpened, ErrorMessage: "The web authentication session could not be started."),
        };

        var authenticator = session.CreateAuthenticator(new() { PageUri = PageUri, CallbackUri = CallbackUri });

        var ex = await Should.ThrowAsync<PasskeyAuthenticatorException>(
            () => authenticator.CreateCredentialAsync(CreationOptionsJson, TestContext.CancellationToken).WaitAsync(TestTimeout));

        ex.Reason.ShouldBe(PasskeyErrorReason.PageNotOpened);
        ex.Message.ShouldBe("The passkey page could not be opened (The web authentication session could not be started.).");
    }

    [TestMethod]
    public async Task SessionError_ThrowsUnknown()
    {
        var session = new FakeSession(null) {
            Result = new(WebAuthenticationNativeStatus.Error, ErrorMessage: "The web authentication session ended without a callback address."),
        };

        var authenticator = session.CreateAuthenticator(new() { PageUri = PageUri, CallbackUri = CallbackUri });

        var ex = await Should.ThrowAsync<PasskeyAuthenticatorException>(
            () => authenticator.GetCredentialAsync(RequestOptionsJson, TestContext.CancellationToken).WaitAsync(TestTimeout));

        ex.Reason.ShouldBe(PasskeyErrorReason.Unknown);
        ex.Message.ShouldBe("The passkey page could not be completed (The web authentication session ended without a callback address.).");
    }

    [TestMethod]
    public async Task DefaultCallbackUriWithBrowserScheme_ThrowsInvalidOperation()
    {
        var session = new FakeSession(null);
        var authenticator = session.CreateAuthenticator(new() { PageUri = PageUri });
        authenticator.GetDefaultCallbackUri = () => new Uri("https://example.com/callback");

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => authenticator.GetCredentialAsync(RequestOptionsJson, TestContext.CancellationToken));

        ex.Message.ShouldStartWith("The web authentication broker's callback address must have a custom scheme");
        session.Requests.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task RegisteredBrokerProvider_RunsTheOperationInstead()
    {
        var session = new FakeSession(null);
        var broker = new FakeBroker(query => $"state={query["state"]}&credential={EncodeBase64Url(CredentialJson)}");
        var authenticator = session.CreateAuthenticator(new() { PageUri = PageUri, CallbackUri = CallbackUri });
        authenticator.IsBrokerProviderRegistered = () => true;
        authenticator.AuthenticateWithBroker = broker.AuthenticateAsync;

        var credential = await authenticator.GetCredentialAsync(RequestOptionsJson, TestContext.CancellationToken).WaitAsync(TestTimeout);

        credential.Json.ShouldBe(CredentialJson);
        session.Requests.ShouldBeEmpty();
        broker.Requests.ShouldHaveSingleItem().CallbackUri.ShouldBe(CallbackUri);
    }

    [TestMethod]
    public async Task CallerCancels_ThrowsOperationCanceled()
    {
        var session = new FakeSession(null) { WaitForCancellation = true };
        var authenticator = session.CreateAuthenticator(new() { PageUri = PageUri, CallbackUri = CallbackUri });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var task = authenticator.GetCredentialAsync(RequestOptionsJson, cts.Token);
        session.Requests.ShouldHaveSingleItem();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => task.WaitAsync(TestTimeout));
    }

    [TestMethod]
    public async Task SessionResultAfterCallerCancelled_ThrowsOperationCanceled()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var session = new FakeSession(query => $"state={query["state"]}&credential={EncodeBase64Url(CredentialJson)}") { BeforeResult = cts.Cancel };
        var authenticator = session.CreateAuthenticator(new() { PageUri = PageUri, CallbackUri = CallbackUri });

        await Should.ThrowAsync<OperationCanceledException>(() => authenticator.GetCredentialAsync(RequestOptionsJson, cts.Token).WaitAsync(TestTimeout));
    }

    [TestMethod]
    public async Task AlreadyCancelled_ThrowsWithoutOpeningSession()
    {
        var session = new FakeSession(null);
        var authenticator = session.CreateAuthenticator(new() { PageUri = PageUri, CallbackUri = CallbackUri });

        await Should.ThrowAsync<OperationCanceledException>(() => authenticator.GetCredentialAsync(RequestOptionsJson, new CancellationToken(canceled: true)));

        session.Requests.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task NoPageUri_ThrowsInvalidOperation()
    {
        var session = new FakeSession(null);
        var authenticator = session.CreateAuthenticator(new() { CallbackUri = CallbackUri });

        await Should.ThrowAsync<InvalidOperationException>(() => authenticator.GetCredentialAsync(RequestOptionsJson, TestContext.CancellationToken));

        session.Requests.ShouldBeEmpty();
    }

    private static string EncodeBase64Url(string value) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(value));

    private static string DecodeBase64Url(string value) => Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value));

    /// <summary>
    /// A fake web authentication session: records the requests and returns to the callback address with the response query that the test builds from the
    /// request's query, or returns <see cref="Result"/> if there is no response.
    /// </summary>
    private sealed class FakeSession(Func<IReadOnlyDictionary<string, string>, string>? getResponseQuery)
    {
        public List<(Uri RequestUri, Uri CallbackUri, bool PrefersEphemeralWebBrowserSession)> Requests { get; } = [];

        public WebAuthenticationNativeResult Result { get; init; } = new(WebAuthenticationNativeStatus.UserCancel);

        public bool WaitForCancellation { get; init; }

        public Action? BeforeResult { get; init; }

        public PasskeyAuthenticator CreateAuthenticator(PasskeyAuthenticatorOptions options) => new(options) {
            NativeAuthenticator = AuthenticateAsync,
            IsAndroid = false,
            IsBrokerProviderRegistered = () => false,
            AuthenticateWithBroker = (_, _, _) => throw new InvalidOperationException("The web authentication broker is not used on iOS."),
            CreateLoopbackFlow = () => throw new InvalidOperationException("The loopback flow is not used on iOS."),
            GetDefaultCallbackUri = () => throw new InvalidOperationException("The default callback address is not used when the options have one."),
        };

        private async Task<WebAuthenticationNativeResult> AuthenticateAsync(
            Uri requestUri, Uri callbackUri, bool prefersEphemeralWebBrowserSession, CancellationToken cancellationToken)
        {
            Requests.Add((requestUri, callbackUri, prefersEphemeralWebBrowserSession));

            if (WaitForCancellation)
                await Task.Delay(Timeout.Infinite, cancellationToken);

            await Task.Yield();
            BeforeResult?.Invoke();

            if (getResponseQuery is null)
                return Result;

            var query = PasskeyPageProtocol.ParseQuery(requestUri.OriginalString);
            return new(WebAuthenticationNativeStatus.Success, $"{query["redirect"]}?{getResponseQuery(query)}");
        }
    }
}
