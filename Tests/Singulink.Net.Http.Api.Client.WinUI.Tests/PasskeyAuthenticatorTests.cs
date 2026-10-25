using Singulink.Net.Http.Api.DataContracts;

namespace Singulink.Net.Http.Api.Client.Passkeys;

/// <summary>
/// Runs the passkey page hand-off end to end on the desktop path, with a fake browser in place of the system browser and the passkey page.
/// </summary>
[PrefixTestClass]
public sealed class PasskeyAuthenticatorTests
{
    private const string RequestOptionsJson = """{"challenge":"Y2hhbGxlbmdl","rpId":"example.com","allowCredentials":[],"userVerification":"required"}""";
    private const string CreationOptionsJson = """{"challenge":"Y2hhbGxlbmdl","rp":{"id":"example.com","name":"Example"},"user":{"id":"dTE","name":"zoë"}}""";
    private const string CredentialJson = """{"id":"Y3JlZA","rawId":"Y3JlZA","type":"public-key","authenticatorAttachment":"platform"}""";

    private static readonly Uri PageUri = new("https://example.com/passkey?lang=en");
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Authenticator_UsesPasskeyPageAndIsSupported()
    {
        var authenticator = new PasskeyAuthenticator();

        authenticator.IsSupported.ShouldBeTrue();
        authenticator.UsesPasskeyPage.ShouldBeTrue();
    }

    [TestMethod]
    public async Task PrepareAsync_CompletesWithoutOpeningBrowser()
    {
        var page = new FakePasskeyPage(null);
        var authenticator = page.CreateAuthenticator(PageUri);

        await authenticator.PrepareAsync(TestContext.CancellationToken);

        page.Browser.Opened.IsCompleted.ShouldBeFalse();
        await Should.ThrowAsync<OperationCanceledException>(() => authenticator.PrepareAsync(new CancellationToken(canceled: true)));
    }

    [TestMethod]
    public async Task SignalUnknownCredentialAsync_CompletesWithoutOpeningBrowser()
    {
        var page = new FakePasskeyPage(null);
        IPasskeyAuthenticator authenticator = page.CreateAuthenticator(PageUri);
        var credential = new PasskeyCredential(CredentialJson, PasskeyAttachment.Platform);

        await authenticator.SignalUnknownCredentialAsync(credential, RequestOptionsJson, TestContext.CancellationToken);

        page.Browser.Opened.IsCompleted.ShouldBeFalse();
        Should.Throw<ArgumentException>(() => authenticator.SignalUnknownCredentialAsync(credential, string.Empty));
    }

    [TestMethod]
    public async Task GetCredentialAsync_SendsRequestAndReturnsCredential()
    {
        var page = new FakePasskeyPage(query => $"state={query["state"]}&credential={EncodeBase64Url(CredentialJson)}");
        var authenticator = page.CreateAuthenticator(PageUri);

        var credential = await authenticator.GetCredentialAsync(RequestOptionsJson, TestContext.CancellationToken).WaitAsync(TestTimeout);

        credential.Json.ShouldBe(CredentialJson);
        credential.Attachment.ShouldBe(PasskeyAttachment.Platform);

        (await page.Browser.Launched).AbsolutePath.ShouldBe("/start");

        var opened = await page.Browser.Opened;
        opened.GetLeftPart(UriPartial.Path).ShouldBe("https://example.com/passkey");

        var query = PasskeyPageProtocol.ParseQuery(opened.OriginalString);
        query["lang"].ShouldBe("en");
        query["op"].ShouldBe("get");
        DecodeBase64Url(query["options"]).ShouldBe(RequestOptionsJson);
        query["redirect"].ShouldMatch(@"^http://127\.0\.0\.1:\d+/callback$");
        query["state"].ShouldMatch("^[A-Za-z0-9_-]{43}$");
    }

    [TestMethod]
    public async Task CreateCredentialAsync_SendsCreateOperation()
    {
        var page = new FakePasskeyPage(query => $"state={query["state"]}&credential={EncodeBase64Url(CredentialJson)}");
        var authenticator = page.CreateAuthenticator(PageUri);

        var credential = await authenticator.CreateCredentialAsync(CreationOptionsJson, TestContext.CancellationToken).WaitAsync(TestTimeout);

        credential.Json.ShouldBe(CredentialJson);
        credential.Attachment.ShouldBe(PasskeyAttachment.Platform);

        var query = PasskeyPageProtocol.ParseQuery((await page.Browser.Opened).OriginalString);
        query["op"].ShouldBe("create");
        DecodeBase64Url(query["options"]).ShouldBe(CreationOptionsJson);
    }

    [TestMethod]
    public async Task GetCredentialAsync_ReportsPasskeyOnAnotherDevice()
    {
        const string crossPlatformJson = """{"id":"Y3JlZA","rawId":"Y3JlZA","type":"public-key","authenticatorAttachment":"cross-platform"}""";

        var page = new FakePasskeyPage(query => $"state={query["state"]}&credential={EncodeBase64Url(crossPlatformJson)}");
        var authenticator = page.CreateAuthenticator(PageUri);

        var credential = await authenticator.GetCredentialAsync(RequestOptionsJson, TestContext.CancellationToken).WaitAsync(TestTimeout);

        credential.Json.ShouldBe(crossPlatformJson);
        credential.Attachment.ShouldBe(PasskeyAttachment.CrossPlatform);
    }

    [TestMethod]
    public async Task UsesNewStateEachTime()
    {
        var page1 = new FakePasskeyPage(query => $"state={query["state"]}&credential={EncodeBase64Url(CredentialJson)}");
        var page2 = new FakePasskeyPage(query => $"state={query["state"]}&credential={EncodeBase64Url(CredentialJson)}");

        await page1.CreateAuthenticator(PageUri).GetCredentialAsync(RequestOptionsJson, TestContext.CancellationToken).WaitAsync(TestTimeout);
        await page2.CreateAuthenticator(PageUri).GetCredentialAsync(RequestOptionsJson, TestContext.CancellationToken).WaitAsync(TestTimeout);

        string state1 = PasskeyPageProtocol.ParseQuery((await page1.Browser.Opened).OriginalString)["state"];
        string state2 = PasskeyPageProtocol.ParseQuery((await page2.Browser.Opened).OriginalString)["state"];
        state1.ShouldNotBe(state2);
    }

    [TestMethod]
    [DataRow("InvalidStateError", PasskeyErrorReason.AlreadyOnAuthenticator)]
    [DataRow("AbortError", PasskeyErrorReason.Cancelled)]
    [DataRow("NotAllowedError", PasskeyErrorReason.NotAllowed)]
    public async Task PageError_ThrowsPasskeyAuthenticatorException(string errorName, PasskeyErrorReason expectedReason)
    {
        var page = new FakePasskeyPage(query => $"state={query["state"]}&error={errorName}&message=From+the+page.");
        var authenticator = page.CreateAuthenticator(PageUri);

        var ex = await Should.ThrowAsync<PasskeyAuthenticatorException>(
            () => authenticator.CreateCredentialAsync(CreationOptionsJson, TestContext.CancellationToken).WaitAsync(TestTimeout));

        ex.Reason.ShouldBe(expectedReason);
        ex.Message.ShouldBe("From the page.");
    }

    [TestMethod]
    public async Task ResponseForOtherRequest_Throws()
    {
        var page = new FakePasskeyPage(_ => $"state=forged&credential={EncodeBase64Url(CredentialJson)}");
        var authenticator = page.CreateAuthenticator(PageUri);

        var ex = await Should.ThrowAsync<PasskeyAuthenticatorException>(
            () => authenticator.GetCredentialAsync(RequestOptionsJson, TestContext.CancellationToken).WaitAsync(TestTimeout));

        ex.Reason.ShouldBe(PasskeyErrorReason.Unknown);
    }

    [TestMethod]
    public async Task CompletedMessage_FromOptions_IsShownInBrowser()
    {
        var page = new FakePasskeyPage(query => $"state={query["state"]}&credential={EncodeBase64Url(CredentialJson)}");
        var authenticator = page.CreateAuthenticator(PageUri, completedMessage: "Done. Back to the app & on we go.");

        await authenticator.GetCredentialAsync(RequestOptionsJson, TestContext.CancellationToken).WaitAsync(TestTimeout);

        var responses = await page.Browser.Responses.WaitAsync(TestTimeout);
        responses.ShouldHaveSingleItem().Body.ShouldContain("<p>Done. Back to the app &amp; on we go.</p>");
    }

    [TestMethod]
    public async Task CompletedMessage_Default_IsShownInBrowser()
    {
        var page = new FakePasskeyPage(query => $"state={query["state"]}&credential={EncodeBase64Url(CredentialJson)}");
        var authenticator = page.CreateAuthenticator(PageUri);

        await authenticator.GetCredentialAsync(RequestOptionsJson, TestContext.CancellationToken).WaitAsync(TestTimeout);

        var responses = await page.Browser.Responses.WaitAsync(TestTimeout);
        responses.ShouldHaveSingleItem().Body.ShouldContain("<p>You can close this tab and return to the app.</p>");
    }

    [TestMethod]
    public async Task RegisteredLoopbackProvider_OptionsStillApply()
    {
        // Registration is process-wide and permanent. It doesn't change what the other tests run: the authenticator runs its own loopback flow whether or
        // not the toolkit's provider is registered.
        LoopbackWebAuthenticationBrokerProvider.Register(provider => {
            provider.CompletedMessage = "From the provider.";
            provider.StartTimeout = TimeSpan.FromMilliseconds(1);
        });

        var page = new FakePasskeyPage(query => $"state={query["state"]}&credential={EncodeBase64Url(CredentialJson)}");
        var authenticator = page.CreateAuthenticator(PageUri, completedMessage: "From the options.");

        var credential = await authenticator.GetCredentialAsync(RequestOptionsJson, TestContext.CancellationToken).WaitAsync(TestTimeout);

        credential.Json.ShouldBe(CredentialJson);
        var responses = await page.Browser.Responses.WaitAsync(TestTimeout);
        responses.ShouldHaveSingleItem().Body.ShouldContain("<p>From the options.</p>");
    }

    [TestMethod]
    public async Task BrowserNeverReturns_OptionsTimeout_ThrowsTimedOut()
    {
        var page = new FakePasskeyPage(null);
        var authenticator = page.CreateAuthenticator(PageUri, timeout: TimeSpan.FromMilliseconds(200));

        var ex = await Should.ThrowAsync<PasskeyAuthenticatorException>(
            () => authenticator.GetCredentialAsync(RequestOptionsJson, TestContext.CancellationToken).WaitAsync(TestTimeout));

        ex.Reason.ShouldBe(PasskeyErrorReason.TimedOut);
        ex.IsCancellation.ShouldBeFalse();
    }

    [TestMethod]
    public async Task CallerCancels_ThrowsOperationCanceled()
    {
        var page = new FakePasskeyPage(null);
        var authenticator = page.CreateAuthenticator(PageUri);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Should.ThrowAsync<OperationCanceledException>(() => authenticator.GetCredentialAsync(RequestOptionsJson, cts.Token).WaitAsync(TestTimeout));

        await page.Browser.Opened.WaitAsync(TestTimeout);
    }

    [TestMethod]
    public async Task CallerCancelsAfterPageOpened_ThrowsOperationCanceledPromptly()
    {
        var page = new FakePasskeyPage(null);
        var authenticator = page.CreateAuthenticator(PageUri, timeout: TimeSpan.FromMinutes(10));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var task = authenticator.GetCredentialAsync(RequestOptionsJson, cts.Token);
        await page.Browser.Opened.WaitAsync(TestTimeout);
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task BrowserNeverOpens_ThrowsPageNotOpenedQuickly()
    {
        var page = new FakePasskeyPage(null, opens: false);
        var authenticator = page.CreateAuthenticator(PageUri, timeout: TimeSpan.FromMinutes(10), startTimeout: TimeSpan.FromMilliseconds(200));

        var ex = await Should.ThrowAsync<PasskeyAuthenticatorException>(
            () => authenticator.GetCredentialAsync(RequestOptionsJson, TestContext.CancellationToken).WaitAsync(TestTimeout));

        ex.Reason.ShouldBe(PasskeyErrorReason.PageNotOpened);
        ex.Message.ShouldBe("The browser did not open the passkey page.");
        ex.IsCancellation.ShouldBeFalse();
    }

    [TestMethod]
    public async Task AlreadyCancelled_ThrowsWithoutOpeningBrowser()
    {
        var page = new FakePasskeyPage(null);
        var authenticator = page.CreateAuthenticator(PageUri);

        await Should.ThrowAsync<OperationCanceledException>(() => authenticator.GetCredentialAsync(RequestOptionsJson, new CancellationToken(canceled: true)));

        page.Browser.Opened.IsCompleted.ShouldBeFalse();
    }

    [TestMethod]
    public async Task LaunchFailure_ThrowsPageNotOpened()
    {
        var page = new FakePasskeyPage(null, launchSucceeds: false);
        var authenticator = page.CreateAuthenticator(PageUri);

        var ex = await Should.ThrowAsync<PasskeyAuthenticatorException>(
            () => authenticator.GetCredentialAsync(RequestOptionsJson, TestContext.CancellationToken).WaitAsync(TestTimeout));

        ex.Reason.ShouldBe(PasskeyErrorReason.PageNotOpened);
        ex.Message.ShouldBe("The browser could not be opened.");
    }

    [TestMethod]
    public async Task NoPageUri_ThrowsInvalidOperation()
    {
        var authenticator = new PasskeyAuthenticator();

        await Should.ThrowAsync<InvalidOperationException>(() => authenticator.GetCredentialAsync(RequestOptionsJson, TestContext.CancellationToken));
        await Should.ThrowAsync<InvalidOperationException>(() => authenticator.CreateCredentialAsync(CreationOptionsJson, TestContext.CancellationToken));
    }

    [TestMethod]
    public void EmptyOptions_ThrowsArgumentException()
    {
        var authenticator = new PasskeyAuthenticator(new PasskeyAuthenticatorOptions { PageUri = PageUri });

        Should.Throw<ArgumentException>(() => authenticator.GetCredentialAsync(string.Empty));
        Should.Throw<ArgumentException>(() => authenticator.CreateCredentialAsync(string.Empty));
    }

    private static string EncodeBase64Url(string value) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(value));

    private static string DecodeBase64Url(string value) => Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value));

    /// <summary>
    /// A fake passkey page: reads the request the authenticator opened in the browser and redirects to its callback with the response query that the
    /// test builds from the request's query, or never redirects if there is no response.
    /// </summary>
    private sealed class FakePasskeyPage
    {
        public FakePasskeyPage(Func<IReadOnlyDictionary<string, string>, string>? getResponseQuery, bool launchSucceeds = true, bool opens = true)
        {
            Browser = new FakeBrowser(
                requestUri => {
                    if (getResponseQuery is null)
                        return [];

                    var query = PasskeyPageProtocol.ParseQuery(requestUri.OriginalString);
                    return [$"{query["redirect"]}?{getResponseQuery(query)}"];
                }) {
                LaunchSucceeds = launchSucceeds,
                Opens = opens,
            };
        }

        public FakeBrowser Browser { get; }

        public PasskeyAuthenticator CreateAuthenticator(
            Uri pageUri, TimeSpan? timeout = null, TimeSpan? startTimeout = null, string? completedMessage = null)
        {
            var options = new PasskeyAuthenticatorOptions { PageUri = pageUri, Timeout = timeout };

            if (startTimeout is not null)
                options.StartTimeout = startTimeout.Value;

            if (completedMessage is not null)
                options.CompletedMessage = completedMessage;

            return new(options) { CreateLoopbackFlow = () => new LoopbackBrowserFlow(Browser.LaunchAsync) };
        }
    }
}
