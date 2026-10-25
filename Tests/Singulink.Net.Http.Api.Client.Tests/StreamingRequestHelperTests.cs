namespace Singulink.Net.Http.Api.Client;

[PrefixTestClass]
public sealed class StreamingRequestHelperTests
{
    private static readonly string[] ItemRecords = ["""{"item":{"id":1,"name":"a"}}""", """{"item":{"id":2,"name":"b"}}""", """{"end":true}"""];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task GetStreaming_SendsGetWithQueryAndYieldsItems()
    {
        var handler = new FakeHttpMessageHandler(_ => Responses.Stream(ItemRecords));
        var client = new SearchClient(handler);

        var items = await ToListAsync(client.SearchAsync("a b", TestContext.CancellationToken));

        items.ShouldBe([new TestItem(1, "a"), new TestItem(2, "b")]);
        handler.SingleRequest.Method.ShouldBe(HttpMethod.Get);
        handler.SingleRequest.Uri.AbsoluteUri.ShouldBe("https://api.example.com/v1/search/a%20b?page=2");
        handler.SingleRequest.Body.ShouldBeNull();
    }

    [TestMethod]
    public async Task PostStreaming_SendsContentAsJson()
    {
        var handler = new FakeHttpMessageHandler(_ => Responses.Stream(ItemRecords));
        var client = new SearchClient(handler);

        var items = await ToListAsync(client.ImportAsync(new TestItem(7, "Receipts"), TestContext.CancellationToken));

        items.Count.ShouldBe(2);
        handler.SingleRequest.Method.ShouldBe(HttpMethod.Post);
        handler.SingleRequest.Uri.AbsoluteUri.ShouldBe("https://api.example.com/v1/import");
        handler.SingleRequest.ContentType.ShouldBe("application/json");
        handler.SingleRequest.Body.ShouldBe("""{"id":7,"name":"Receipts"}""");
    }

    [TestMethod]
    public async Task EachEnumeration_SendsItsOwnRequest()
    {
        var handler = new FakeHttpMessageHandler(_ => Responses.Stream(ItemRecords));
        var client = new SearchClient(handler);
        var items = client.SearchAsync("x", TestContext.CancellationToken);

        handler.Requests.ShouldBeEmpty();

        (await ToListAsync(items)).Count.ShouldBe(2);
        (await ToListAsync(items)).Count.ShouldBe(2);

        handler.Requests.Count.ShouldBe(2);
    }

    [TestMethod]
    public async Task PathOutsideBaseAddress_ThrowsWhenEnumerationStarts()
    {
        var handler = new FakeHttpMessageHandler(_ => Responses.Stream(ItemRecords));
        var client = new SearchClient(handler);
        var items = client.GetOutsideAsync();

        await Should.ThrowAsync<ArgumentException>(() => ToListAsync(items));
        handler.Requests.ShouldBeEmpty();
    }

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> items)
    {
        var list = new List<T>();

        await foreach (var item in items)
            list.Add(item);

        return list;
    }

    /// <summary>
    /// A client whose request methods use the streaming request helpers, as an app's client would.
    /// </summary>
    private sealed class SearchClient : ApiClientBase
    {
        public SearchClient(FakeHttpMessageHandler handler) : base(TestJsonContext.Default)
        {
            HttpHandler = handler;
        }

        public IAsyncEnumerable<TestItem> SearchAsync(string text, CancellationToken cancellationToken) =>
            GetStreamingAsync<TestItem>($"search/{text}", [("page", 2), ("filter", null)], cancellationToken);

        public IAsyncEnumerable<TestItem> ImportAsync(TestItem item, CancellationToken cancellationToken) =>
            PostStreamingAsync<TestItem>("import", item, cancellationToken);

        public IAsyncEnumerable<TestItem> GetOutsideAsync() => GetStreamingAsync<TestItem>("../outside");

        protected override Uri GetBaseAddress() => TestApiClient.DefaultBaseAddress;
    }
}
