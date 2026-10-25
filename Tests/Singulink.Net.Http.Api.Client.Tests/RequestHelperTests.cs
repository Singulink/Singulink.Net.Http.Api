namespace Singulink.Net.Http.Api.Client;

[PrefixTestClass]
public sealed class RequestHelperTests
{
    private const string ItemJson = """{"id":5,"name":"Receipts"}""";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Get_SendsGetAndReadsResponse()
    {
        var handler = new FakeHttpMessageHandler(_ => Responses.Json(ItemJson));
        var client = new ItemsClient(handler);

        var item = await client.GetItemAsync("a/b");

        item.ShouldBe(new TestItem(5, "Receipts"));
        handler.SingleRequest.Method.ShouldBe(HttpMethod.Get);
        handler.SingleRequest.Uri.AbsoluteUri.ShouldBe("https://api.example.com/v1/items/a%2Fb");
        handler.SingleRequest.Body.ShouldBeNull();
    }

    [TestMethod]
    public async Task Get_WithQueryAndCancellationToken()
    {
        var handler = new FakeHttpMessageHandler(_ => Responses.Json($"[{ItemJson}]"));
        var client = new ItemsClient(handler);

        var items = await client.FindItemsAsync("a b", null, TestContext.CancellationToken);

        items.ShouldBe([new TestItem(5, "Receipts")]);
        handler.SingleRequest.Uri.AbsoluteUri.ShouldBe("https://api.example.com/v1/items?q=a%20b");
    }

    [TestMethod]
    public async Task Get_WithCancellationToken()
    {
        var handler = new FakeHttpMessageHandler(_ => Responses.Json(ItemJson));
        var client = new ItemsClient(handler);

        await client.GetItemAsync(5, TestContext.CancellationToken);
        await client.CheckItemAsync(5, TestContext.CancellationToken);

        handler.Requests.Select(r => (r.Method, r.Uri.AbsolutePath)).ShouldBe([(HttpMethod.Get, "/v1/items/5"), (HttpMethod.Get, "/v1/items/5/check")]);
    }

    [TestMethod]
    public async Task Post_SendsContentAsJson()
    {
        var handler = new FakeHttpMessageHandler(_ => Responses.Json(ItemJson));
        var client = new ItemsClient(handler);

        var item = await client.CreateItemAsync(new TestItem(0, "Receipts"));

        item.Id.ShouldBe(5);
        handler.SingleRequest.Method.ShouldBe(HttpMethod.Post);
        handler.SingleRequest.Uri.AbsolutePath.ShouldBe("/v1/items");
        handler.SingleRequest.ContentType.ShouldBe("application/json");
        handler.SingleRequest.Body.ShouldBe("""{"id":0,"name":"Receipts"}""");
    }

    [TestMethod]
    public async Task Post_WithCancellationTokenOnly_SendsNoContent()
    {
        var handler = new FakeHttpMessageHandler();
        var client = new ItemsClient(handler);

        await client.ArchiveItemAsync(5, TestContext.CancellationToken);
        await client.ArchiveItemTypedAsync(5, TestContext.CancellationToken);

        handler.Requests.Count.ShouldBe(2);
        handler.Requests.ShouldAllBe(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/v1/items/5/archive" && r.Body == null);
    }

    [TestMethod]
    public async Task Post_WithContentQueryAndCancellationToken()
    {
        var handler = new FakeHttpMessageHandler();
        var client = new ItemsClient(handler);

        await client.MoveItemAsync(5, new TestItem(5, "Moved"), "folder 1", TestContext.CancellationToken);
        await client.MoveItemAsync(5, new TestItem(5, "Moved"), TestContext.CancellationToken);

        handler.Requests[0].Uri.AbsoluteUri.ShouldBe("https://api.example.com/v1/items/5/move?to=folder%201");
        handler.Requests[1].Uri.AbsoluteUri.ShouldBe("https://api.example.com/v1/items/5/move");
        handler.Requests.ShouldAllBe(r => r.Body == """{"id":5,"name":"Moved"}""");
    }

    [TestMethod]
    public async Task Put_SendsPut()
    {
        var handler = new FakeHttpMessageHandler(_ => Responses.Json(ItemJson));
        var client = new ItemsClient(handler);

        await client.ReplaceItemAsync(5, new TestItem(5, "New"), TestContext.CancellationToken);
        await client.ReplaceItemAsync(5, new TestItem(5, "New"));

        handler.Requests.ShouldAllBe(r => r.Method == HttpMethod.Put && r.Uri.AbsolutePath == "/v1/items/5" && r.Body == """{"id":5,"name":"New"}""");
    }

    [TestMethod]
    public async Task Patch_SendsPatch()
    {
        var handler = new FakeHttpMessageHandler(_ => Responses.Json(ItemJson));
        var client = new ItemsClient(handler);

        await client.RenameItemAsync(5, "Receipts");
        var item = await client.RenameItemTypedAsync(5, "Receipts", TestContext.CancellationToken);

        item.Id.ShouldBe(5);
        handler.Requests.Select(r => r.Method.Method).ShouldBe(["PATCH", "PATCH"]);
        handler.Requests.ShouldAllBe(r => r.Uri.AbsolutePath == "/v1/items/5" && r.Body == "Receipts");
    }

    [TestMethod]
    public async Task Delete_SendsDelete()
    {
        var handler = new FakeHttpMessageHandler(_ => Responses.Json(ItemJson));
        var client = new ItemsClient(handler);

        await client.DeleteItemAsync(5);
        await client.DeleteItemAsync(5, TestContext.CancellationToken);
        var deleted = await client.DeleteItemAsync(5, permanent: true, TestContext.CancellationToken);

        deleted.Id.ShouldBe(5);
        handler.Requests.ShouldAllBe(r => r.Method == HttpMethod.Delete && r.Body == null);
        handler.Requests.Select(r => r.Uri.PathAndQuery).ShouldBe(["/v1/items/5", "/v1/items/5", "/v1/items/5?permanent=True"]);
    }

    [TestMethod]
    public async Task ErrorResponse_Throws()
    {
        var handler = new FakeHttpMessageHandler(_ => Responses.Error(HttpStatusCode.NotFound, "No such item."));
        var client = new ItemsClient(handler);

        var ex = await Should.ThrowAsync<NotFoundApiException>(() => client.GetItemAsync("x"));

        ex.Message.ShouldBe("No such item.");
    }

    [TestMethod]
    public void AbsolutePath_Throws()
    {
        var client = new ItemsClient(new FakeHttpMessageHandler());

        Should.Throw<ArgumentException>(() => { _ = client.GetAbsoluteAsync(); });
    }

    /// <summary>
    /// A client whose request methods use the request helpers, as an app's client would.
    /// </summary>
    private sealed class ItemsClient : ApiClientBase
    {
        public ItemsClient(FakeHttpMessageHandler handler) : base(TestJsonContext.Default)
        {
            HttpHandler = handler;
        }

        public Task<TestItem> GetItemAsync(string id) => GetAsync<TestItem>($"items/{id}");

        public Task<TestItem> GetItemAsync(int id, CancellationToken cancellationToken) => GetAsync<TestItem>($"items/{id}", cancellationToken);

        public Task CheckItemAsync(int id, CancellationToken cancellationToken) => GetAsync($"items/{id}/check", cancellationToken);

        public Task<List<TestItem>> FindItemsAsync(string? search, int? page, CancellationToken cancellationToken) =>
            GetAsync<List<TestItem>>("items", [("q", search), ("page", page)], cancellationToken);

        public Task<TestItem> CreateItemAsync(TestItem item) => PostAsync<TestItem>("items", item);

        public Task ArchiveItemAsync(int id, CancellationToken cancellationToken) => PostAsync($"items/{id}/archive", cancellationToken);

        public Task<HttpResponseMessage> ArchiveItemTypedAsync(int id, CancellationToken cancellationToken) =>
            PostAsync<HttpResponseMessage>($"items/{id}/archive", cancellationToken);

        public Task MoveItemAsync(int id, TestItem item, string folder, CancellationToken cancellationToken) =>
            PostAsync($"items/{id}/move", item, [("to", folder)], cancellationToken);

        public Task MoveItemAsync(int id, TestItem item, CancellationToken cancellationToken) => PostAsync($"items/{id}/move", item, cancellationToken);

        public Task<TestItem> ReplaceItemAsync(int id, TestItem item, CancellationToken cancellationToken) =>
            PutAsync<TestItem>($"items/{id}", item, cancellationToken);

        public Task ReplaceItemAsync(int id, TestItem item) => PutAsync($"items/{id}", item);

        public Task RenameItemAsync(int id, string name) => PatchAsync($"items/{id}", name);

        public Task<TestItem> RenameItemTypedAsync(int id, string name, CancellationToken cancellationToken) =>
            PatchAsync<TestItem>($"items/{id}", name, cancellationToken);

        public Task DeleteItemAsync(int id) => DeleteAsync($"items/{id}");

        public Task DeleteItemAsync(int id, CancellationToken cancellationToken) => DeleteAsync($"items/{id}", cancellationToken);

        public Task<TestItem> DeleteItemAsync(int id, bool permanent, CancellationToken cancellationToken) =>
            DeleteAsync<TestItem>($"items/{id}", [("permanent", permanent)], cancellationToken);

        public Task GetAbsoluteAsync() => GetAsync("https://other.example.com/items");

        protected override Uri GetBaseAddress() => TestApiClient.DefaultBaseAddress;
    }
}
