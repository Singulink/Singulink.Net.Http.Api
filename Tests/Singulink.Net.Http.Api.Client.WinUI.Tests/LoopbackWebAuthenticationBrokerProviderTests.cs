using System.Net;
using System.Net.Sockets;
using Windows.Security.Authentication.Web;

namespace Singulink.Net.Http.Api.Client;

[PrefixTestClass]
public sealed class LoopbackWebAuthenticationBrokerProviderTests
{
    private static readonly Uri RequestUri = new("https://example.com/passkey?op=get");
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void GetCurrentApplicationCallbackUri_ListensOnLoopback()
    {
        var provider = new LoopbackWebAuthenticationBrokerProvider(_ => Task.FromResult(true));

        var callbackUri = provider.GetCurrentApplicationCallbackUri();

        callbackUri.Scheme.ShouldBe("http");
        callbackUri.Host.ShouldBe("127.0.0.1");
        callbackUri.AbsolutePath.ShouldBe("/callback");
        callbackUri.Port.ShouldBeGreaterThan(0);

        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, callbackUri.Port);
        client.Connected.ShouldBeTrue();
    }

    [TestMethod]
    public async Task AuthenticateAsync_ReturnsCallbackAddressAndShowsCompletedPage()
    {
        var (provider, browser, callbackUri) = Create(callback => [$"{callback}?state=abc&credential=e30"]);

        var result = await provider.AuthenticateAsync(WebAuthenticationOptions.None, RequestUri, callbackUri, TestContext.CancellationToken)
            .WaitAsync(TestTimeout);

        (await browser.Launched).ShouldBe(new Uri($"http://127.0.0.1:{callbackUri.Port}/start"));
        (await browser.Opened).ShouldBe(RequestUri);
        result.ResponseStatus.ShouldBe(WebAuthenticationStatus.Success);
        result.ResponseData.ShouldBe($"http://127.0.0.1:{callbackUri.Port}/callback?state=abc&credential=e30");

        var responses = await browser.Responses.WaitAsync(TestTimeout);
        responses.Single().Status.ShouldBe(200);
        responses.Single().Body.ShouldContain("You can close this tab and return to the app.");
    }

    [TestMethod]
    public async Task AuthenticateAsync_StartAddressRedirectsToRequest()
    {
        var (provider, browser, callbackUri) = Create(callback => [$"http://127.0.0.1:{callback.Port}/start?again", $"{callback}?state=abc"]);

        var result = await provider.AuthenticateAsync(WebAuthenticationOptions.None, RequestUri, callbackUri, TestContext.CancellationToken)
            .WaitAsync(TestTimeout);

        result.ResponseStatus.ShouldBe(WebAuthenticationStatus.Success);

        using var startResponse = await browser.StartResponse;
        startResponse.StatusCode.ShouldBe(HttpStatusCode.Found);
        startResponse.Headers.Location.ShouldBe(RequestUri);
        startResponse.Headers.CacheControl!.NoStore.ShouldBeTrue();
        startResponse.Headers.GetValues("Referrer-Policy").ShouldBe(["no-referrer"]);

        var responses = await browser.Responses.WaitAsync(TestTimeout);
        responses.Select(r => r.Status).ShouldBe([302, 200]);
    }

    [TestMethod]
    public async Task AuthenticateAsync_LongRequest_LaunchesShortAddress()
    {
        var requestUri = new Uri("https://example.com/passkey?options=" + new string('a', 20_000));
        Uri? callbackUri = null;
        var browser = new FakeBrowser(_ => [$"{callbackUri}?state=abc"]);
        var provider = new LoopbackWebAuthenticationBrokerProvider(browser.LaunchAsync);
        callbackUri = provider.GetCurrentApplicationCallbackUri();

        var result = await provider.AuthenticateAsync(WebAuthenticationOptions.None, requestUri, callbackUri, TestContext.CancellationToken)
            .WaitAsync(TestTimeout);

        result.ResponseStatus.ShouldBe(WebAuthenticationStatus.Success);
        (await browser.Launched).OriginalString.ShouldBe($"http://127.0.0.1:{callbackUri.Port}/start");
        (await browser.Opened).ShouldBe(requestUri);
    }

    [TestMethod]
    public async Task AuthenticateAsync_BrowserNeverOpens_ReturnsErrorHttpQuickly()
    {
        var browser = new FakeBrowser(_ => []) { Opens = false };
        var provider = new LoopbackWebAuthenticationBrokerProvider(browser.LaunchAsync) {
            StartTimeout = TimeSpan.FromMilliseconds(200),
            Timeout = TimeSpan.FromMinutes(10),
        };

        var callbackUri = provider.GetCurrentApplicationCallbackUri();

        var result = await provider.AuthenticateAsync(WebAuthenticationOptions.None, RequestUri, callbackUri, TestContext.CancellationToken)
            .WaitAsync(TestTimeout);

        result.ResponseStatus.ShouldBe(WebAuthenticationStatus.ErrorHttp);
        result.ResponseData.ShouldBe("The browser did not open the request.");
        await browser.Launched;
    }

    [TestMethod]
    public async Task AuthenticateAsync_LaunchNeverCompletes_ReturnsErrorHttpQuickly()
    {
        var provider = new LoopbackWebAuthenticationBrokerProvider(_ => new TaskCompletionSource<bool>().Task) {
            StartTimeout = TimeSpan.FromMilliseconds(200),
            Timeout = TimeSpan.FromMinutes(10),
        };

        var callbackUri = provider.GetCurrentApplicationCallbackUri();

        var result = await provider.AuthenticateAsync(WebAuthenticationOptions.None, RequestUri, callbackUri, TestContext.CancellationToken)
            .WaitAsync(TestTimeout);

        result.ResponseStatus.ShouldBe(WebAuthenticationStatus.ErrorHttp);
        result.ResponseData.ShouldBe("The browser did not open the request.");
    }

    [TestMethod]
    public async Task AuthenticateAsync_CancelledAfterBrowserOpened_ReturnsUserCancelPromptly()
    {
        var (provider, browser, callbackUri) = Create(_ => []);
        provider.Timeout = TimeSpan.FromMinutes(10);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var authenticateTask = provider.AuthenticateAsync(WebAuthenticationOptions.None, RequestUri, callbackUri, cts.Token);
        await browser.Opened.WaitAsync(TestTimeout);
        cts.Cancel();

        var result = await authenticateTask.WaitAsync(TimeSpan.FromSeconds(5));

        result.ResponseStatus.ShouldBe(WebAuthenticationStatus.UserCancel);

        using var client = new TcpClient();
        Should.Throw<SocketException>(() => client.Connect(IPAddress.Loopback, callbackUri.Port));
    }

    [TestMethod]
    public async Task AuthenticateAsync_KeepsQueryEscaping()
    {
        var (provider, browser, callbackUri) = Create(callback => [$"{callback}?state=a%2Bb&message=x+y%26z"]);

        var result = await provider.AuthenticateAsync(WebAuthenticationOptions.None, RequestUri, callbackUri, TestContext.CancellationToken)
            .WaitAsync(TestTimeout);

        result.ResponseData.ShouldBe($"{callbackUri}?state=a%2Bb&message=x+y%26z");
        await browser.Responses.WaitAsync(TestTimeout);
    }

    [TestMethod]
    public async Task AuthenticateAsync_IgnoresIdleConnectionsAndOtherPaths()
    {
        var (provider, browser, callbackUri) = Create(
            callback => [$"http://127.0.0.1:{callback.Port}/favicon.ico", $"http://127.0.0.1:{callback.Port}/callbackx", $"{callback}?state=abc"],
            openIdleConnectionFirst: true);

        var result = await provider.AuthenticateAsync(WebAuthenticationOptions.None, RequestUri, callbackUri, TestContext.CancellationToken)
            .WaitAsync(TestTimeout);

        result.ResponseStatus.ShouldBe(WebAuthenticationStatus.Success);
        result.ResponseData.ShouldBe($"{callbackUri}?state=abc");

        var responses = await browser.Responses.WaitAsync(TestTimeout);
        responses.Select(r => r.Status).ShouldBe([404, 404, 200]);
    }

    [TestMethod]
    public async Task AuthenticateAsync_RejectsNonGetRequests()
    {
        var provider = new LoopbackWebAuthenticationBrokerProvider(_ => Task.FromResult(true)) { Timeout = TimeSpan.FromSeconds(5) };
        var callbackUri = provider.GetCurrentApplicationCallbackUri();

        var authenticateTask = provider.AuthenticateAsync(WebAuthenticationOptions.None, RequestUri, callbackUri, TestContext.CancellationToken);

        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var postResponse = await client.PostAsync($"{callbackUri}?state=abc", new StringContent("x"), TestContext.CancellationToken);
        postResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var getResponse = await client.GetAsync($"{callbackUri}?state=def", TestContext.CancellationToken);
        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await authenticateTask.WaitAsync(TestTimeout);
        result.ResponseData.ShouldBe($"{callbackUri}?state=def");
    }

    [TestMethod]
    public async Task AuthenticateAsync_HtmlEncodesCompletedMessage()
    {
        var (provider, browser, callbackUri) = Create(callback => [$"{callback}?state=abc"]);
        provider.CompletedMessage = "<b>Done</b> & dusted";

        await provider.AuthenticateAsync(WebAuthenticationOptions.None, RequestUri, callbackUri, TestContext.CancellationToken).WaitAsync(TestTimeout);

        var responses = await browser.Responses.WaitAsync(TestTimeout);
        responses.Single().Body.ShouldContain("&lt;b&gt;Done&lt;/b&gt; &amp; dusted");
        responses.Single().Body.ShouldNotContain("<b>");
    }

    [TestMethod]
    public async Task AuthenticateAsync_StopsListeningWhenDone()
    {
        var (provider, browser, callbackUri) = Create(callback => [$"{callback}?state=abc"]);

        await provider.AuthenticateAsync(WebAuthenticationOptions.None, RequestUri, callbackUri, TestContext.CancellationToken).WaitAsync(TestTimeout);
        await browser.Responses.WaitAsync(TestTimeout);

        using var client = new TcpClient();
        Should.Throw<SocketException>(() => client.Connect(IPAddress.Loopback, callbackUri.Port));
    }

    [TestMethod]
    public async Task AuthenticateAsync_LaunchFailure_ReturnsErrorHttp()
    {
        var browser = new FakeBrowser(_ => []) { LaunchSucceeds = false };
        var provider = new LoopbackWebAuthenticationBrokerProvider(browser.LaunchAsync);
        var callbackUri = provider.GetCurrentApplicationCallbackUri();

        var result = await provider.AuthenticateAsync(WebAuthenticationOptions.None, RequestUri, callbackUri, TestContext.CancellationToken)
            .WaitAsync(TestTimeout);

        result.ResponseStatus.ShouldBe(WebAuthenticationStatus.ErrorHttp);
        result.ResponseData.ShouldBe("The browser could not be opened.");
    }

    [TestMethod]
    public async Task AuthenticateAsync_Cancelled_ReturnsUserCancel()
    {
        var provider = new LoopbackWebAuthenticationBrokerProvider(_ => Task.FromResult(true));
        var callbackUri = provider.GetCurrentApplicationCallbackUri();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var result = await provider.AuthenticateAsync(WebAuthenticationOptions.None, RequestUri, callbackUri, cts.Token).WaitAsync(TestTimeout);

        result.ResponseStatus.ShouldBe(WebAuthenticationStatus.UserCancel);
        result.ResponseData.ShouldBeNull();
    }

    [TestMethod]
    public async Task AuthenticateAsync_TimedOut_ReturnsUserCancel()
    {
        var provider = new LoopbackWebAuthenticationBrokerProvider(_ => Task.FromResult(true)) { Timeout = TimeSpan.FromMilliseconds(200) };
        var callbackUri = provider.GetCurrentApplicationCallbackUri();

        var result = await provider.AuthenticateAsync(WebAuthenticationOptions.None, RequestUri, callbackUri, TestContext.CancellationToken)
            .WaitAsync(TestTimeout);

        result.ResponseStatus.ShouldBe(WebAuthenticationStatus.UserCancel);
    }

    [TestMethod]
    public async Task AuthenticateAsync_CallbackOnOtherPort_ListensThere()
    {
        int port = GetFreePort();
        var callbackUri = new Uri($"http://127.0.0.1:{port}/oauth/done");
        var browser = new FakeBrowser(_ => [$"{callbackUri}?code=xyz"]);
        var provider = new LoopbackWebAuthenticationBrokerProvider(browser.LaunchAsync);

        var result = await provider.AuthenticateAsync(WebAuthenticationOptions.None, RequestUri, callbackUri, TestContext.CancellationToken)
            .WaitAsync(TestTimeout);

        result.ResponseStatus.ShouldBe(WebAuthenticationStatus.Success);
        result.ResponseData.ShouldBe($"{callbackUri}?code=xyz");
    }

    [TestMethod]
    public async Task AuthenticateAsync_IPv6LoopbackCallback_ListensThere()
    {
        int port = GetFreePort(IPAddress.IPv6Loopback);
        var callbackUri = new Uri($"http://[::1]:{port}/callback");
        var browser = new FakeBrowser(_ => [$"{callbackUri}?state=abc"]);
        var provider = new LoopbackWebAuthenticationBrokerProvider(browser.LaunchAsync);

        var result = await provider.AuthenticateAsync(WebAuthenticationOptions.None, RequestUri, callbackUri, TestContext.CancellationToken)
            .WaitAsync(TestTimeout);

        result.ResponseStatus.ShouldBe(WebAuthenticationStatus.Success);
        result.ResponseData.ShouldBe($"http://[::1]:{port}/callback?state=abc");
    }

    [TestMethod]
    [DataRow("myapp://auth")]
    [DataRow("https://127.0.0.1:5000/callback")]
    [DataRow("http://example.com/callback")]
    [DataRow("http://localhost:5000/callback")]
    [DataRow("http://10.0.0.1:5000/callback")]
    [DataRow("http://127.0.0.1:5000/start")]
    public async Task AuthenticateAsync_InvalidCallback_Throws(string callbackUri)
    {
        var provider = new LoopbackWebAuthenticationBrokerProvider(_ => Task.FromResult(true));

        await Should.ThrowAsync<ArgumentException>(() =>
            provider.AuthenticateAsync(WebAuthenticationOptions.None, RequestUri, new Uri(callbackUri), TestContext.CancellationToken));
    }

    [TestMethod]
    public void Properties_RejectInvalidValues()
    {
        var provider = new LoopbackWebAuthenticationBrokerProvider();

        provider.StartTimeout.ShouldBe(TimeSpan.FromSeconds(30));
        provider.Timeout.ShouldBeNull();

        Should.Throw<ArgumentException>(() => provider.CompletedMessage = string.Empty);
        Should.Throw<ArgumentOutOfRangeException>(() => provider.Timeout = TimeSpan.Zero);
        Should.Throw<ArgumentOutOfRangeException>(() => provider.StartTimeout = TimeSpan.Zero);

        provider.Timeout = TimeSpan.FromSeconds(1);
        provider.Timeout = null;
        provider.Timeout.ShouldBeNull();
    }

    private static (LoopbackWebAuthenticationBrokerProvider Provider, FakeBrowser Browser, Uri CallbackUri) Create(
        Func<Uri, IEnumerable<string>> getRequests, bool openIdleConnectionFirst = false)
    {
        Uri? callbackUri = null;
        var browser = new FakeBrowser(_ => getRequests(callbackUri!), openIdleConnectionFirst);
        var provider = new LoopbackWebAuthenticationBrokerProvider(browser.LaunchAsync);
        callbackUri = provider.GetCurrentApplicationCallbackUri();

        return (provider, browser, callbackUri);
    }

    private static int GetFreePort(IPAddress? address = null)
    {
        var listener = new TcpListener(address ?? IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return port;
    }
}
