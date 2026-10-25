using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Singulink.Net.Http.Api;

/// <summary>
/// Defines the streaming response format used for endpoints that return <see cref="IAsyncEnumerable{T}"/> results and provides methods for reading it.
/// </summary>
/// <remarks>
/// <para>
/// A streaming response is a <c>application/jsonl</c> body containing one JSON record per line. Each record is an object with exactly one of the following
/// properties:
/// </para>
/// <list type="bullet">
/// <item><c>"item"</c>: an item produced by the endpoint (which may be <see langword="null"/>), serialized using the service's JSON options for the
/// endpoint's declared item type.</item>
/// <item><c>"ping"</c>: <see langword="true"/>. A keep-alive record sent while the endpoint is waiting for its next item. Ignored by readers.</item>
/// <item><c>"error"</c>: an object with <c>"status"</c>, optional <c>"code"</c> and <c>"message"</c> properties describing an exception thrown during
/// enumeration. This terminates the stream.</item>
/// <item><c>"end"</c>: <see langword="true"/>. Marks successful completion of the stream.</item>
/// </list>
/// <para>
/// Every successful stream ends with an <c>"end"</c> record, so a stream that ends without an <c>"end"</c> or <c>"error"</c> record was truncated. Exceptions
/// thrown before any record has been written are reported as a regular (non-streaming) error response instead.
/// </para>
/// </remarks>
public static class StreamingResponse
{
    /// <summary>
    /// The media type of streaming responses (<c>application/jsonl</c>).
    /// </summary>
    public const string MediaType = "application/jsonl";

    /// <summary>
    /// The full content type of streaming responses, including the character set.
    /// </summary>
    public const string ContentType = MediaType + "; charset=utf-8";

    /// <summary>
    /// Reads the items from a streaming response body. Ping records are skipped, error records are thrown as the corresponding <see cref="ApiException"/>, and
    /// the enumeration completes when the end record is received. Null items are yielded as <see langword="null"/>, whether or not the item type is
    /// nullable.
    /// </summary>
    /// <typeparam name="TItem">The item type.</typeparam>
    /// <param name="stream">The response body stream.</param>
    /// <param name="itemTypeInfo">The JSON type info used to deserialize items.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <exception cref="ApiException">The stream contained an error record.</exception>
    /// <exception cref="IOException">The stream ended before an end or error record was received. On .NET 10 and later, this is an <see
    /// cref="T:System.Net.Http.HttpIOException"/>.</exception>
    /// <exception cref="FormatException">The stream contained an unrecognized record.</exception>
    public static IAsyncEnumerable<TItem> ReadItemsAsync<TItem>(Stream stream, JsonTypeInfo<TItem> itemTypeInfo, CancellationToken cancellationToken = default)
    {
        return ReadItemsAsync(stream, itemTypeInfo, null, cancellationToken);
    }

    /// <summary>
    /// Reads the items from a streaming response body (see <see cref="ReadItemsAsync{TItem}(Stream, JsonTypeInfo{TItem}, CancellationToken)"/>). The
    /// exception for an error record with a <c>412</c> carries the specified user ID precondition of the request, if any (see
    /// <see cref="UserChangedApiException.ExpectedUserId"/>).
    /// </summary>
    internal static async IAsyncEnumerable<TItem> ReadItemsAsync<TItem>(
        Stream stream, JsonTypeInfo<TItem> itemTypeInfo, string? expectedUserId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        bool ended = false;

        var records = JsonSerializer.DeserializeAsyncEnumerable(stream, StreamJsonContext.Default.StreamRecord, topLevelValues: true, cancellationToken);

        await foreach (var record in records.ConfigureAwait(false))
        {
            if (record is null)
                throw new FormatException("Streaming response contained a null record.");

            if (record.Error is { } error)
            {
                ResponseExceptionInfo.ThrowStreamError(error, expectedUserId);
                throw new FormatException("Streaming response contained an error record with a non-error status.");
            }

            if (record.End is true)
            {
                ended = true;
                break;
            }

            if (record.Ping is true)
                continue;

            if (record.Item.ValueKind is not JsonValueKind.Undefined)
            {
                yield return record.Item.Deserialize(itemTypeInfo)!;
                continue;
            }

            throw new FormatException("Streaming response contained an unrecognized record.");
        }

        if (!ended)
        {
            const string message = "The streaming response ended before the end-of-stream record was received.";
#if NET
            throw new HttpIOException(HttpRequestError.ResponseEnded, message);
#else
            throw new StreamingResponseEndedException(message);
#endif
        }
    }

    /// <summary>
    /// Reads the items from a streaming response body using the specified serializer options to deserialize items.
    /// </summary>
    /// <inheritdoc cref="ReadItemsAsync{TItem}(Stream, JsonTypeInfo{TItem}, CancellationToken)" path="/exception"/>
    /// <typeparam name="TItem">The item type.</typeparam>
    /// <param name="stream">The response body stream.</param>
    /// <param name="serializerOptions">The serializer options used to deserialize items.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    public static IAsyncEnumerable<TItem> ReadItemsAsync<TItem>(Stream stream, JsonSerializerOptions serializerOptions, CancellationToken cancellationToken = default)
    {
        return ReadItemsAsync(stream, serializerOptions.GetTypeInfo<TItem>(), cancellationToken);
    }
}
