<div class="article">

# Streaming Responses

Endpoints that return <xref:System.Collections.Generic.IAsyncEnumerable`1> are converted into streaming responses that deliver each item as soon as it is produced, keep the connection alive while waiting, and propagate errors to the client. This guide covers writing such endpoints and consuming them.

### When to stream

Streaming suits results that take time to produce and are useful before they are complete: search results grouped as they are found, progress of a long-running upload, or a large export. For results that are only useful whole, a regular response is simpler and can be cached and retried normally.

## Writing a Streaming Endpoint

Return <xref:System.Collections.Generic.IAsyncEnumerable`1> of a reference type from the handler. <xref:Singulink.Net.Http.Api.Service.WebApplicationExtensions.UseApiResponseHandling*> detects the return type when the endpoints are built and attaches the conversion, so there is nothing to register:

```csharp
app.MapGet("/search", SearchAsync);

static async IAsyncEnumerable<SearchResultGroup> SearchAsync(
    string query, SessionToken token, SearchService search, [EnumeratorCancellation] CancellationToken cancellationToken)
{
    await foreach (var group in search.SearchAsync(query, token.UserId, cancellationToken))
        yield return group;
}
```

Each item is serialized with the application's JSON options using the endpoint's declared item type, so polymorphic item types annotated with `JsonPolymorphic` work as they do elsewhere. Items are flushed individually, and null items are sent as null. The declared type may also be wrapped in a <xref:System.Threading.Tasks.Task`1> or <xref:System.Threading.Tasks.ValueTask`1>.

> [!NOTE]
> Value type items are not supported and cause an error when the endpoints are built. Wrap the value in a record. A synchronous <xref:System.Collections.Generic.IEnumerable`1> is deliberately left as a regular JSON array, since a list is rarely meant to stream.

#### Keeping the connection alive

Long gaps between items can hit proxy or client idle timeouts. Mark the endpoint with <xref:Singulink.Net.Http.Api.Service.KeepAlivePingAttribute> to send a ping record whenever no item has been produced for the given interval:

```csharp
[KeepAlivePing(10)]
static async IAsyncEnumerable<UploadProgress> ProcessUploadAsync(...)
```

Pings are handled by the client reader and never surface as items. The attribute is rejected at startup if placed on an endpoint that does not stream.

#### Errors during streaming

An exception thrown before the first item is written produces a regular error response, exactly as for a non-streaming endpoint. An exception thrown after that cannot change the status code, so it is written into the stream as an error record that terminates the stream, and the client throws it from the enumeration. In both cases the exception is mapped by the registered <xref:Singulink.Net.Http.Api.Service.IApiExceptionHandler>, so unexpected failures get a reference ID the same way (see [Exception Handling](exception-handling.md)).

Cancellation flows both ways: when the client disconnects, the handler's cancellation token is cancelled and the enumerator is disposed.

## Consuming a Stream

On the client, <xref:Singulink.Net.Http.Api.Client.ApiClientBase.SendStreamingAsync*> returns the items as an <xref:System.Collections.Generic.IAsyncEnumerable`1>:

```csharp
public IAsyncEnumerable<SearchResultGroup> SearchAsync(string query, CancellationToken cancellationToken = default)
{
    var request = CreateRequest(HttpMethod.Get, "search", ("query", query));
    return SendStreamingAsync<SearchResultGroup>(request, cancellationToken);
}
```

```csharp
await foreach (var group in client.SearchAsync("invoice"))
    Results.Add(group);
```

An error record surfaces as the corresponding <xref:Singulink.Net.Http.Api.ApiException> thrown from the enumeration after the items that preceded it were yielded. A stream that ends without its terminal record (a dropped connection) throws <xref:System.Net.Http.HttpIOException>, so a truncated result is never mistaken for a complete one. Breaking out of the loop cancels the request.

If the endpoint can produce null items, use a nullable item type argument; nulls are yielded as null.

### Reading a stream without the client base class

<xref:Singulink.Net.Http.Api.StreamingResponse.ReadItemsAsync*> reads the format from any <xref:System.IO.Stream>, for code that uses a raw <xref:System.Net.Http.HttpClient>:

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

</div>
