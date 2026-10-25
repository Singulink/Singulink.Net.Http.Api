using System.Text.Json;

namespace Singulink.Net.Http.Api.Client;

[PrefixTestClass]
public sealed class ResponseTests
{
    [TestMethod]
    public async Task JsonResponse_IsDeserialized()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => Responses.Json("""{"id":5,"name":"Invoices"}""")));

        var item = await client.SendRequestAsync<TestItem>(client.CreateGetRequest("items/5"));

        item.ShouldBe(new TestItem(5, "Invoices"));
    }

    [TestMethod]
    public async Task JsonResponse_CollectionIsDeserialized()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => Responses.Json("""[{"id":1,"name":"a"},{"id":2,"name":"b"}]""")));

        var items = await client.SendRequestAsync<List<TestItem>>(client.CreateGetRequest("items"));

        items.ShouldBe([new TestItem(1, "a"), new TestItem(2, "b")]);
    }

    [TestMethod]
    public async Task JsonResponse_NullBody_ThrowsJsonException()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => Responses.Json("null")));

        var ex = await Should.ThrowAsync<JsonException>(() => client.SendRequestAsync<TestItem>(client.CreateGetRequest("items/5")));

        ex.Message.ShouldBe("The response body is null.");
    }

    [TestMethod]
    public async Task StringResponse_ReturnsBody()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => Responses.Text("plain text body")));

        string result = await client.SendRequestAsync<string>(client.CreateGetRequest("text"));

        result.ShouldBe("plain text body");
    }

    [TestMethod]
    public async Task VoidResponse_IgnoresBody()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => Responses.Json("""{"ignored":true}""")));

        await client.SendRequestAsync(client.CreatePostRequest("items/5/archive"));
    }

    [TestMethod]
    public async Task VoidResponse_NoContent_Completes()
    {
        var handler = new FakeHttpMessageHandler();
        var client = new TestApiClient(handler);

        await client.SendRequestAsync(client.CreatePostRequest("items/5/archive"));

        handler.SingleRequest.Method.ShouldBe(HttpMethod.Post);
        handler.SingleRequest.Uri.AbsoluteUri.ShouldBe("https://api.example.com/v1/items/5/archive");
    }

    [TestMethod]
    public async Task HttpResponseMessageResponse_IsReturnedUnread()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => Responses.Text("body")));

        using var response = await client.SendRequestAsync<HttpResponseMessage>(client.CreateGetRequest("file"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldBe("body");
    }
}
