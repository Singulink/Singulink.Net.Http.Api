using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Singulink.Net.Http.Api.Service;

[PrefixTestClass]
public sealed class StreamingResponseTests
{
    public sealed record Item(int Id, string Name);

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
    [JsonDerivedType(typeof(Circle), "circle")]
    [JsonDerivedType(typeof(Square), "square")]
    public abstract record Shape;

    public sealed record Circle(double Radius) : Shape;

    public sealed record Square(double Side) : Shape;

    private static async IAsyncEnumerable<Item> ItemsAsync(int count, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (int i = 1; i <= count; i++)
        {
            await Task.Yield();
            yield return new Item(i, $"item{i}");
        }
    }

    [TestMethod]
    public async Task Items_AreWrittenAsJsonlRecordsFollowedByEndRecord()
    {
        await using var host = await TestWebHost.StartAsync(app => app.MapGet("/items", () => ItemsAsync(3)));

        var response = await host.Client.GetAsync("/items");

        response.EnsureSuccessStatusCode();
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/jsonl");
        response.Content.Headers.ContentType.CharSet.ShouldBe("utf-8");

        var lines = await ReadLinesAsync(response);

        lines.ShouldBe([
            """{"item":{"id":1,"name":"item1"}}""",
            """{"item":{"id":2,"name":"item2"}}""",
            """{"item":{"id":3,"name":"item3"}}""",
            """{"end":true}""",
        ]);
    }

    [TestMethod]
    public async Task Reader_ReadsItemsAndCompletesOnEndRecord()
    {
        await using var host = await TestWebHost.StartAsync(app => app.MapGet("/items", () => ItemsAsync(3)));

        var items = await ReadItemsAsync<Item>(host, "/items");

        items.ShouldBe([new Item(1, "item1"), new Item(2, "item2"), new Item(3, "item3")]);
    }

    [TestMethod]
    public async Task EmptyStream_ContainsOnlyEndRecord()
    {
        await using var host = await TestWebHost.StartAsync(app => app.MapGet("/items", () => ItemsAsync(0)));

        (await ReadLinesAsync(await host.Client.GetAsync("/items"))).ShouldBe(["""{"end":true}"""]);
        (await ReadItemsAsync<Item>(host, "/items")).ShouldBeEmpty();
    }

    [TestMethod]
    public async Task NullItems_ArePassedThrough()
    {
        static async IAsyncEnumerable<Item?> WithNullsAsync()
        {
            yield return new Item(1, "a");
            yield return null;
            await Task.Yield();
            yield return new Item(2, "b");
        }

        await using var host = await TestWebHost.StartAsync(app => app.MapGet("/items", () => WithNullsAsync()));

        var lines = await ReadLinesAsync(await host.Client.GetAsync("/items"));
        lines.Count.ShouldBe(4);
        lines[1].ShouldBe("""{"item":null}""");

        (await ReadItemsAsync<Item?>(host, "/items")).ShouldBe([new Item(1, "a"), null, new Item(2, "b")]);
    }

    [TestMethod]
    public async Task TaskWrappedEnumerable_IsStreamed()
    {
        await using var host = await TestWebHost.StartAsync(app => {
            app.MapGet("/task", () => Task.FromResult(ItemsAsync(2)));
            app.MapGet("/valuetask", () => ValueTask.FromResult(ItemsAsync(2)));
        });

        (await ReadItemsAsync<Item>(host, "/task")).Count.ShouldBe(2);
        (await ReadItemsAsync<Item>(host, "/valuetask")).Count.ShouldBe(2);
    }

    [TestMethod]
    public async Task PolymorphicItems_AreSerializedUsingDeclaredItemType()
    {
        static async IAsyncEnumerable<Shape> ShapesAsync()
        {
            yield return new Circle(1.5);
            await Task.Yield();
            yield return new Square(2);
        }

        await using var host = await TestWebHost.StartAsync(app => app.MapGet("/shapes", () => ShapesAsync()));

        var lines = await ReadLinesAsync(await host.Client.GetAsync("/shapes"));
        lines[0].ShouldBe("""{"item":{"kind":"circle","radius":1.5}}""");

        var shapes = await ReadItemsAsync<Shape>(host, "/shapes");
        shapes.ShouldBe([new Circle(1.5), new Square(2)]);
    }

    [TestMethod]
    public async Task NonStreamingEndpoints_AreLeftUntouched()
    {
        await using var host = await TestWebHost.StartAsync(app => {
            app.MapGet("/list", () => new List<Item> { new(1, "a") });
            app.MapGet("/single", () => new Item(1, "a"));
        });

        var list = await host.Client.GetAsync("/list");
        list.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        (await list.Content.ReadAsStringAsync()).ShouldBe("""[{"id":1,"name":"a"}]""");

        var single = await host.Client.GetAsync("/single");
        single.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        (await single.Content.ReadAsStringAsync()).ShouldBe("""{"id":1,"name":"a"}""");
    }

    [TestMethod]
    public async Task ValueTypeItems_ThrowWhenEndpointsAreBuilt()
    {
        static async IAsyncEnumerable<int> NumbersAsync()
        {
            await Task.Yield();
            yield return 1;
        }

        await using var host = await TestWebHost.StartAsync(app => app.MapGet("/numbers", () => NumbersAsync()));

        var ex = Should.Throw<InvalidOperationException>(() => host.Endpoints);
        ex.Message.ShouldContain("IAsyncEnumerable<Int32>");
        ex.Message.ShouldContain("/numbers");
    }

    [TestMethod]
    public async Task ApiExceptionBeforeFirstRecord_ProducesRegularErrorResponse()
    {
        static async IAsyncEnumerable<Item> FailAsync()
        {
            await Task.Yield();
            throw new NotFoundApiException("nothing here") { ErrorCode = "missing" };
#pragma warning disable CS0162 // Unreachable code detected
            yield break;
#pragma warning restore CS0162
        }

        var handler = new RecordingExceptionHandler();
        await using var host = await TestWebHost.StartAsync(app => app.MapGet("/fail", () => FailAsync()), handler);

        var response = await host.Client.GetAsync("/fail");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/plain");
        response.Content.Headers.ContentType.Parameters.ShouldContain(p => p.Name == "format" && p.Value == "error-code");
        (await response.Content.ReadAsStringAsync()).ShouldBe("[missing] nothing here");

        // The regular error response goes through the middleware, which consults the same handler.
        handler.Handled.ShouldHaveSingleItem().ShouldBeOfType<NotFoundApiException>();
    }

    [TestMethod]
    public async Task ApiExceptionAfterRecords_ProducesErrorRecordAndThrowsOnRead()
    {
        static async IAsyncEnumerable<Item> FailLaterAsync()
        {
            yield return new Item(1, "a");
            await Task.Yield();
            yield return new Item(2, "b");
            throw new NotFoundApiException("gone now") { ErrorCode = "gone" };
        }

        var handler = new RecordingExceptionHandler();
        await using var host = await TestWebHost.StartAsync(app => app.MapGet("/fail", () => FailLaterAsync()), handler);

        var response = await host.Client.GetAsync("/fail");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var lines = await ReadLinesAsync(response);
        lines.Count.ShouldBe(3);
        lines[2].ShouldBe("""{"error":{"status":404,"code":"gone","message":"gone now"}}""");

        handler.Handled.ShouldHaveSingleItem().ShouldBeOfType<NotFoundApiException>();

        var received = new List<Item>();

        var ex = await Should.ThrowAsync<NotFoundApiException>(async () => {
            await foreach (var item in ReadStreamAsync<Item>(host, "/fail"))
                received.Add(item);
        });

        ex.Message.ShouldBe("gone now");
        ex.ErrorCode.ShouldBe("gone");
        received.Count.ShouldBe(2);
    }

    [TestMethod]
    public async Task UnexpectedExceptionAfterRecords_IsMaskedOutsideDevelopment()
    {
        static async IAsyncEnumerable<Item> FailLaterAsync()
        {
            yield return new Item(1, "a");
            await Task.Yield();
            throw new InvalidOperationException("secret details");
        }

        await using var host = await TestWebHost.StartAsync(app => app.MapGet("/fail", () => FailLaterAsync()));

        var lines = await ReadLinesAsync(await host.Client.GetAsync("/fail"));
        lines[1].ShouldBe("""{"error":{"status":500,"message":""}}""");

        var ex = await Should.ThrowAsync<ApiException>(async () => {
            await foreach (var ignored in ReadStreamAsync<Item>(host, "/fail")) { }
        });

        ex.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        ex.Message.ShouldNotContain("secret details");
    }

    [TestMethod]
    public async Task UnexpectedExceptionAfterRecords_IsDetailedInDevelopment()
    {
        static async IAsyncEnumerable<Item> FailLaterAsync()
        {
            yield return new Item(1, "a");
            await Task.Yield();
            throw new InvalidOperationException("secret details");
        }

        await using var host = await TestWebHost.StartAsync(app => app.MapGet("/fail", () => FailLaterAsync()), development: true);

        var ex = await Should.ThrowAsync<ApiException>(async () => {
            await foreach (var ignored in ReadStreamAsync<Item>(host, "/fail")) { }
        });

        ex.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        ex.Message.ShouldContain(nameof(InvalidOperationException));
        ex.Message.ShouldContain("secret details");
    }

    [TestMethod]
    public async Task ExceptionHandler_MapsUnexpectedExceptions_ForBothRegularAndStreamingResponses()
    {
        static async IAsyncEnumerable<Item> FailBeforeAsync()
        {
            await Task.Yield();
            throw new InvalidOperationException("boom");
#pragma warning disable CS0162 // Unreachable code detected
            yield break;
#pragma warning restore CS0162
        }

        static async IAsyncEnumerable<Item> FailAfterAsync()
        {
            yield return new Item(1, "a");
            await Task.Yield();
            throw new InvalidOperationException("boom");
        }

        var handler = new RecordingExceptionHandler { Map = (context, ex) => new ServerErrorApiException($"Logged as {ex.Message} for {context.Request.Path}") };

        await using var host = await TestWebHost.StartAsync(app => {
            app.MapGet("/before", () => FailBeforeAsync());
            app.MapGet("/after", () => FailAfterAsync());
            app.MapGet("/plain", Item () => throw new InvalidOperationException("boom"));
        }, handler);

        var plain = await host.Client.GetAsync("/plain");
        plain.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await plain.Content.ReadAsStringAsync()).ShouldBe("Logged as boom for /plain");

        var before = await host.Client.GetAsync("/before");
        before.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await before.Content.ReadAsStringAsync()).ShouldBe("Logged as boom for /before");

        var after = await host.Client.GetAsync("/after");
        after.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadLinesAsync(after))[1].ShouldBe("""{"error":{"status":500,"message":"Logged as boom for /after"}}""");

        var ex = await Should.ThrowAsync<ServerErrorApiException>(async () => {
            await foreach (var ignored in ReadStreamAsync<Item>(host, "/after")) { }
        });

        ex.Message.ShouldBe("Logged as boom for /after");
        handler.Handled.Count.ShouldBe(4);
        handler.Handled.ShouldAllBe(e => e is InvalidOperationException);
    }

    [TestMethod]
    public async Task ExceptionHandler_ReturningNull_FallsBackToDefaultHandling()
    {
        static async IAsyncEnumerable<Item> FailAfterAsync()
        {
            yield return new Item(1, "a");
            await Task.Yield();
            throw new InvalidOperationException("secret details");
        }

        var handler = new RecordingExceptionHandler { Map = (_, _) => null };

        await using var host = await TestWebHost.StartAsync(app => {
            app.MapGet("/after", () => FailAfterAsync());
            app.MapGet("/api", Item () => throw new ForbiddenApiException("nope"));
            app.MapGet("/plain", Item () => throw new InvalidOperationException("boom"));
        }, handler);

        // API exceptions are still reported as-is.
        var api = await host.Client.GetAsync("/api");
        api.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await api.Content.ReadAsStringAsync()).ShouldBe("nope");

        // Unexpected exceptions in streams are masked, and in regular responses they propagate through the pipeline.
        (await ReadLinesAsync(await host.Client.GetAsync("/after")))[1].ShouldBe("""{"error":{"status":500,"message":""}}""");
        await Should.ThrowAsync<InvalidOperationException>(() => host.Client.GetAsync("/plain"));
    }

    [TestMethod]
    public async Task KeepAlivePing_EmitsPingRecordsWhileWaitingForItems()
    {
        await using var host = await TestWebHost.StartAsync(app => app.MapGet("/slow", SlowAsync));

        var lines = await ReadLinesAsync(await host.Client.GetAsync("/slow"));

        lines.First().ShouldBe("""{"item":{"id":1,"name":"a"}}""");
        lines.Last().ShouldBe("""{"end":true}""");
        lines.Count(l => l == """{"ping":true}""").ShouldBeGreaterThanOrEqualTo(2);
        lines.Count(l => l.StartsWith("{\"item\"", StringComparison.Ordinal)).ShouldBe(2);

        (await ReadItemsAsync<Item>(host, "/slow")).Select(i => i.Id).ShouldBe([1, 2]);

        [KeepAlivePing(0.05)]
        static async IAsyncEnumerable<Item> SlowAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new Item(1, "a");
            await Task.Delay(300, cancellationToken);
            yield return new Item(2, "b");
        }
    }

    [TestMethod]
    public async Task KeepAlivePing_OnNonStreamingEndpoint_ThrowsWhenEndpointsAreBuilt()
    {
        await using var host = await TestWebHost.StartAsync(app => app.MapGet("/list", NotAStream));

        var ex = Should.Throw<InvalidOperationException>(() => host.Endpoints);
        ex.Message.ShouldContain(nameof(KeepAlivePingAttribute));

        [KeepAlivePing(1)]
        static List<Item> NotAStream() => [];
    }

    [TestMethod]
    public async Task StreamingEndpoints_AdvertiseJsonlItemTypeMetadata()
    {
        await using var host = await TestWebHost.StartAsync(app => {
            app.MapGet("/items", () => ItemsAsync(1));
            app.MapGet("/list", () => new List<Item>());
        });

        var streaming = host.Endpoints.OfType<RouteEndpoint>().Single(e => e.RoutePattern.RawText == "/items");
        var produces = streaming.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>().Where(m => m.StatusCode == 200).ShouldHaveSingleItem();
        produces.Type.ShouldBe(typeof(Item));
        produces.ContentTypes.ShouldBe(["application/jsonl"]);

        var list = host.Endpoints.OfType<RouteEndpoint>().Single(e => e.RoutePattern.RawText == "/list");
        list.Metadata.GetMetadata<IProducesResponseTypeMetadata>()!.ContentTypes.ShouldContain("application/json");
    }

    [TestMethod]
    public async Task ClientDisconnect_CancelsAndDisposesEnumerator()
    {
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<Item> HangAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                yield return new Item(1, "a");
                await Task.Delay(Timeout.Infinite, cancellationToken);
                yield return new Item(2, "b");
            }
            finally
            {
                disposed.TrySetResult();
            }
        }

        await using var host = await TestWebHost.StartAsync(app => app.MapGet("/hang", HangAsync));

        using var cts = new CancellationTokenSource();
        var response = await host.Client.GetAsync("/hang", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var stream = await response.Content.ReadAsStreamAsync(cts.Token);

        // Read the first record, then drop the connection.
        var buffer = new byte[256];
        (await stream.ReadAsync(buffer, cts.Token)).ShouldBeGreaterThan(0);
        cts.Cancel();
        response.Dispose();

        (await Task.WhenAny(disposed.Task, Task.Delay(5000))).ShouldBeSameAs(disposed.Task, "enumerator was not disposed after the client disconnected");
    }

    [TestMethod]
    public async Task SessionRefresh_IsIssuedWithStreamingResponse()
    {
        // The deferred session cookie refresh runs when the response starts, which for streaming responses is when the first record is flushed.

        var store = new InMemorySessionStore();

        await using var host = await TestWebHost.StartAsync(
            app => app.MapGet("/items", async (HttpContext context) => {
                await context.GetRequiredSessionTokenAsync<TestSessionToken>(SessionAccessOptions.OptionalUserIdPrecondition);
                return ItemsAsync(2);
            }),
            configureServices: services => {
                services.AddSingleton<IOriginValidator>(new OriginValidator("localhost"));
                services.AddSingleton(store);
                services.AddHttpSessionHandling<TestSessionToken, TestSessionData, InMemorySessionStoreFactory>();
            });

        var utcNow = DateTime.UtcNow;
        store.UserStamps[1] = 1;
        store.Sessions[1] = new TestSessionData { Id = 1, UserId = 1, Device = "TestClient/1.0", RefreshedUtc = utcNow.AddMinutes(-11), ValidFor = TimeSpan.FromDays(30), IsPersistent = true };
        var token = new TestSessionToken(1, 1, 1, utcNow.AddMinutes(-11), TimeSpan.FromDays(30), 0, true, 0);

        var protector = host.App.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector($"Singulink/Session[{typeof(TestSessionToken).FullName}]");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/items");
        request.Headers.Add("Cookie", $"session-token={protector.Protect(JsonSerializer.Serialize(token))}");
        request.Headers.Add("User-Agent", "TestClient/1.0");

        var response = await host.Client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        (await ReadLinesAsync(response)).Count.ShouldBe(3);
        response.Headers.TryGetValues("Set-Cookie", out var cookies).ShouldBeTrue();
        cookies!.ShouldHaveSingleItem().ShouldStartWith("session-token=");
        store.Sessions[1].Generation.ShouldBe(1);
    }

    [TestMethod]
    public async Task Reader_TruncatedStream_ThrowsHttpIOException()
    {
        var received = new List<Item>();

        var ex = await Should.ThrowAsync<HttpIOException>(async () => {
            await foreach (var item in ReadRawAsync<Item>("""{"item":{"id":1,"name":"a"}}""" + "\n"))
                received.Add(item);
        });

        ex.HttpRequestError.ShouldBe(HttpRequestError.ResponseEnded);
        received.Count.ShouldBe(1);
    }

    [TestMethod]
    [DataRow("""{"unknown":1}""")]
    [DataRow("null")]
    [DataRow("""{"error":{"status":200,"message":"not an error"}}""")]
    public async Task Reader_InvalidRecord_ThrowsFormatException(string record)
    {
        await Should.ThrowAsync<FormatException>(async () => {
            await foreach (var ignored in ReadRawAsync<Item>(record + "\n")) { }
        });
    }

    [TestMethod]
    [DataRow(400, typeof(BadRequestApiException))]
    [DataRow(401, typeof(UnauthorizedApiException))]
    [DataRow(403, typeof(ForbiddenApiException))]
    [DataRow(404, typeof(NotFoundApiException))]
    [DataRow(412, typeof(UserChangedApiException))]
    [DataRow(422, typeof(ValidationApiException))]
    [DataRow(428, typeof(UserRequiredApiException))]
    [DataRow(500, typeof(ServerErrorApiException))]
    [DataRow(418, typeof(ApiException))]
    [DataRow(503, typeof(ApiException))]
    public async Task Reader_ErrorRecord_ThrowsMatchingApiException(int status, Type exceptionType)
    {
        var ex = await Should.ThrowAsync<ApiException>(async () => {
            await foreach (var ignored in ReadRawAsync<Item>($$$"""{"error":{"status":{{{status}}},"code":"the-code","message":"the message"}}""" + "\n")) { }
        });

        ex.ShouldBeOfType(exceptionType);
        ex.StatusCode.ShouldBe((HttpStatusCode)status);
        ex.ErrorCode.ShouldBe("the-code");
        ex.Message.ShouldBe("the message");
    }

    [TestMethod]
    public async Task Reader_PingRecords_AreIgnored()
    {
        var items = new List<Item>();

        await foreach (var item in ReadRawAsync<Item>("""{"ping":true}""" + "\n" + """{"item":{"id":1,"name":"a"}}""" + "\n" + """{"ping":true}""" + "\n" + """{"end":true}""" + "\n"))
            items.Add(item);

        items.ShouldBe([new Item(1, "a")]);
    }

    private static IAsyncEnumerable<T> ReadRawAsync<T>(string body)
    {
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));
        return StreamingResponse.ReadItemsAsync<T>(stream, JsonSerializerOptions.Web);
    }

    private static async Task<List<string>> ReadLinesAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        body.ShouldEndWith("\n");
        return body.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static async IAsyncEnumerable<T> ReadStreamAsync<T>(TestWebHost host, string path)
    {
        using var response = await host.Client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        response.Content.Headers.ContentType!.MediaType.ShouldBe(StreamingResponse.MediaType);

        await using var stream = await response.Content.ReadAsStreamAsync();

        await foreach (var item in StreamingResponse.ReadItemsAsync<T>(stream, JsonSerializerOptions.Web))
            yield return item;
    }

    private static async Task<List<T>> ReadItemsAsync<T>(TestWebHost host, string path)
    {
        var items = new List<T>();

        await foreach (var item in ReadStreamAsync<T>(host, path))
            items.Add(item);

        return items;
    }

    /// <summary>
    /// Exception handler that records the exceptions it is asked to map. By default returns API exceptions as-is and null for everything else.
    /// </summary>
    public sealed class RecordingExceptionHandler : IApiExceptionHandler
    {
        public List<Exception> Handled { get; } = [];

        public Func<HttpContext, Exception, ApiException?> Map { get; set; } = (_, ex) => ex as ApiException;

        public ValueTask<ApiException?> HandleAsync(HttpContext httpContext, Exception exception)
        {
            Handled.Add(exception);
            return ValueTask.FromResult(Map(httpContext, exception));
        }
    }
}
