namespace Singulink.Net.Http.Api.Client;

[PrefixTestClass]
public sealed class HttpClientTests
{
    [TestMethod]
    public async Task Handler_ReceivesRequestsWithSessionAndUserAgent()
    {
        var handler = new FakeHttpMessageHandler();
        var client = new TestApiClient(handler, "saved-token");

        await client.SendRequestAsync(client.CreateGetRequest("documents"));

        handler.SingleRequest.Uri.AbsoluteUri.ShouldBe("https://api.example.com/v1/documents");
        handler.SingleRequest.Cookie.ShouldBe("__Host-session-token=saved-token");
        handler.SingleRequest.UserAgent.ShouldBe(client.CurrentUserAgent);
    }

    [TestMethod]
    public async Task Handler_IsNotDisposed()
    {
        var handler = new DisposeTrackingHandler();
        var client = new TestApiClient(handler);

        await client.SendRequestAsync(client.CreateGetRequest("documents"));

        handler.IsDisposed.ShouldBeFalse();
    }

    [TestMethod]
    public async Task ChildClient_UsesParentHandler()
    {
        var handler = new FakeHttpMessageHandler();
        var parent = new TestApiClient(handler);
        var child = new TestApiClient(parent) { BaseAddress = new Uri("https://users.example.com/") };

        await child.SendRequestAsync(child.CreateGetRequest("profile"));

        handler.SingleRequest.Uri.AbsoluteUri.ShouldBe("https://users.example.com/profile");
    }

    [TestMethod]
    public async Task ChildClient_OwnHandlerTakesPrecedence()
    {
        var parentHandler = new FakeHttpMessageHandler();
        var childHandler = new FakeHttpMessageHandler();
        var parent = new TestApiClient(parentHandler);
        var child = new TestApiClient(parent, handler: childHandler);

        await child.SendRequestAsync(child.CreateGetRequest("profile"));
        await parent.SendRequestAsync(parent.CreateGetRequest("documents"));

        childHandler.SingleRequest.Uri.AbsolutePath.ShouldBe("/v1/profile");
        parentHandler.SingleRequest.Uri.AbsolutePath.ShouldBe("/v1/documents");
    }

#if NETFRAMEWORK
    [TestMethod]
    public async Task SharedHandler_LimitsConnectionLeaseTimeForCalledAddress()
    {
        // Nothing listens on this address. The connection lease time is set before the request is sent, so the request failing does not matter.
        var uri = new Uri("http://127.0.0.1:9/dns-refresh-test");
        ServicePointManager.FindServicePoint(uri).ConnectionLeaseTimeout.ShouldBe(-1);

        using var invoker = new HttpMessageInvoker(ApiClientBase.SharedHttpHandler!, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<Exception>(() => invoker.SendAsync(request, cts.Token));

        ServicePointManager.FindServicePoint(uri).ConnectionLeaseTimeout.ShouldBe(120 * 1000);
    }
#endif

    private sealed class DisposeTrackingHandler : HttpMessageHandler
    {
        public bool IsDisposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Responses.NoContent());

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
