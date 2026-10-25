using System.Text;

namespace Singulink.Net.Http.Api.Client;

[PrefixTestClass]
public sealed class RequestContentTests
{
    [TestMethod]
    public async Task ObjectContent_IsSentAsJson()
    {
        var handler = new FakeHttpMessageHandler();
        var client = new TestApiClient(handler);

        await client.SendRequestAsync(client.CreatePostRequest("items"), new TestItem(7, "Receipts"));

        handler.SingleRequest.ContentType.ShouldBe("application/json");
        handler.SingleRequest.Body.ShouldBe("""{"id":7,"name":"Receipts"}""");
    }

    [TestMethod]
    public async Task StringContent_IsSentAsText()
    {
        var handler = new FakeHttpMessageHandler();
        var client = new TestApiClient(handler);

        await client.SendRequestAsync(client.CreatePostRequest("notes"), "note text");

        handler.SingleRequest.ContentType.ShouldBe("text/plain");
        handler.SingleRequest.Body.ShouldBe("note text");
    }

    [TestMethod]
    public async Task HttpContent_IsSentAsIs()
    {
        var handler = new FakeHttpMessageHandler();
        var client = new TestApiClient(handler);

        await client.SendRequestAsync(client.CreatePostRequest("upload"), new StringContent("a,b", Encoding.UTF8, "text/csv"));

        handler.SingleRequest.ContentType.ShouldBe("text/csv");
        handler.SingleRequest.Body.ShouldBe("a,b");
    }

    [TestMethod]
    public async Task ContentAlreadySet_ThrowsArgumentException()
    {
        var handler = new FakeHttpMessageHandler();
        var client = new TestApiClient(handler);
        var request = client.CreatePostRequest("items");
        request.Content = new StringContent("existing");

        var ex = await Should.ThrowAsync<ArgumentException>(() => client.SendRequestAsync(request, "more"));

        ex.ParamName.ShouldBe("content");
        handler.Requests.ShouldBeEmpty();
    }
}
