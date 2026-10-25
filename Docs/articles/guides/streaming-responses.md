<div class="article">

# Streaming Responses

Endpoints that return <xref:System.Collections.Generic.IAsyncEnumerable`1> are converted into streaming responses that deliver each item as soon as it is produced, keep the connection alive while waiting, and propagate errors to the client. This guide covers writing such endpoints and consuming them.

## Writing a Streaming Endpoint

Return <xref:System.Collections.Generic.IAsyncEnumerable`1> of a reference type from the handler. <xref:Singulink.Net.Http.Api.Service.WebApplicationExtensions.UseApiResponseHandling*>, with its services registered by <xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddApiResponseHandling*>, detects the return type when the endpoints are built and attaches the conversion:

```csharp
app.MapGet("/search", SearchAsync);

static async IAsyncEnumerable<SearchResultGroup> SearchAsync(
    string query, SessionToken token, SearchService search, [EnumeratorCancellation] CancellationToken cancellationToken)
{
    await foreach (var group in search.SearchAsync(query, token.UserId, cancellationToken))
        yield return group;
}
```

The conversion is attached at startup to the minimal API route handlers of the app's own route builder, including the ones in route groups, whether they are mapped before or after <xref:Singulink.Net.Http.Api.Service.WebApplicationExtensions.UseApiResponseHandling*> is called. The conversion runs when the host starts, so a pipeline that is built without starting the host returns regular JSON arrays. Controller actions and endpoints of branch pipelines with their own routing return regular JSON arrays too. An app that calls `UseEndpoints` after mapping endpoints on the app, or maps endpoints in it, fails to start, since `UseEndpoints` registers the app's endpoints with its `EndpointDataSource` service before they are converted.

Each item is serialized with the application's JSON options using the endpoint's declared item type, so polymorphic item types annotated with `JsonPolymorphic` work as they do elsewhere. Items are flushed individually, and null items are sent as null. The declared type may also be wrapped in a <xref:System.Threading.Tasks.Task`1> or <xref:System.Threading.Tasks.ValueTask`1>.

> [!NOTE]
> Value type items are not supported and cause an error when the endpoints are built. A synchronous <xref:System.Collections.Generic.IEnumerable`1> is written as a regular JSON array.

#### Keeping the connection alive

Long gaps between items can hit proxy or client idle timeouts, such as Cloudflare's, which ends a response that sends nothing for 125 seconds (see [Slow responses](cloudflare.md#slow-responses)). Mark the endpoint with <xref:Singulink.Net.Http.Api.Service.KeepAlivePingAttribute> to send a ping record whenever no item has been produced for the given interval:

```csharp
[KeepAlivePing(10)]
static async IAsyncEnumerable<ImportProgress> ImportAsync(...)
```

Pings are handled by the client reader and never surface as items. The attribute is rejected at startup if placed on an endpoint that does not stream.

#### Errors during streaming

An exception thrown before the first item is written produces a regular error response, exactly as for a non-streaming endpoint. An exception thrown after that cannot change the status code, so it is written into the stream as an error record that terminates the stream, and the client throws it from the enumeration. In both cases the exception is mapped by the registered <xref:Singulink.Net.Http.Api.Service.IApiExceptionHandler>, so unexpected failures get a reference ID the same way (see [Exception Handling](exception-handling.md)).

Cancellation flows both ways: when the client disconnects, the handler's cancellation token is cancelled and the enumerator is disposed. The completion of a [chunked upload](uploads.md), which streams its handler's items in the same format, is different: its handler keeps running when the client disconnects, and always sends ping records (see [Running the Handler](uploads.md#running-the-handler)).

## Consuming a Stream

On the client, <xref:Singulink.Net.Http.Api.Client.ApiClientBase.GetStreamingAsync*> and <xref:Singulink.Net.Http.Api.Client.ApiClientBase.PostStreamingAsync*> return the items as an <xref:System.Collections.Generic.IAsyncEnumerable`1>. The request is created and sent when the enumeration starts, and again for each enumeration. <xref:Singulink.Net.Http.Api.Client.ApiClientBase.SendStreamingAsync*> sends a request created with <xref:Singulink.Net.Http.Api.Client.ApiClientBase.CreateRequest*>.

```csharp
public IAsyncEnumerable<SearchResultGroup> SearchAsync(string query, CancellationToken cancellationToken = default) =>
    GetStreamingAsync<SearchResultGroup>("search", [("query", query)], cancellationToken);
```

```csharp
await foreach (var group in client.SearchAsync("invoice"))
    Results.Add(group);
```

An error record surfaces as the corresponding <xref:Singulink.Net.Http.Api.ApiException> thrown from the enumeration after the items that preceded it were yielded. A stream that ends without its terminal record (a dropped connection) throws <xref:System.Net.Http.HttpIOException> on .NET 10 and later (<xref:System.IO.IOException> on other runtimes), so a truncated result is never mistaken for a complete one. Breaking out of the loop cancels the request, except in the browser, where the client reads the rest of the response in the background for up to 5 seconds, unless the loop's token is cancelled, so that a response whose end follows the item that the loop stopped at, such as the result that the app waited for, ends without its request being aborted, which the browser would report as a failed request.

Null items are yielded as null, whether or not the item type argument is nullable.

### Reading a stream without the client base class

<xref:Singulink.Net.Http.Api.StreamingResponse.ReadItemsAsync*?displayProperty=nameWithType> reads the format from any <xref:System.IO.Stream>, for code that uses a raw <xref:System.Net.Http.HttpClient>:

```csharp
using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
await using var body = await response.Content.ReadAsStreamAsync();

await foreach (var item in StreamingResponse.ReadItemsAsync<SearchResultGroup>(body, serializerOptions))
    ...
```

## Next Steps

Continue with these related articles:

- [Streaming Response Format](../concepts/streaming-format.md) - The JSON Lines record format on the wire.
- [Exception Handling](exception-handling.md) - How errors are mapped before and after the stream starts.
- [Chunked Uploads](uploads.md) - Upload handlers, whose results and progress are streamed in the same format.

</div>
