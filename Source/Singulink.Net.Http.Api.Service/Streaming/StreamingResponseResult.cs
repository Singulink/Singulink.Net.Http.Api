using System.Buffers;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Writes an <see cref="IAsyncEnumerable{T}"/> result as a <see cref="StreamingResponse"/>, flushing each record as it is produced.
/// </summary>
internal sealed class StreamingResponseResult : IResult
{
    // Used when the app has no options service, as ASP.NET Core does for minimal API results.
    private static readonly JsonOptions DefaultJsonOptions = new();

    private readonly IAsyncEnumerable<object?> _source;
    private readonly Type _itemType;
    private readonly TimeSpan? _pingInterval;
    private readonly bool _isDevelopment;

    public StreamingResponseResult(IAsyncEnumerable<object?> source, Type itemType, TimeSpan? pingInterval, bool isDevelopment)
    {
        _source = source;
        _itemType = itemType;
        _pingInterval = pingInterval;
        _isDevelopment = isDevelopment;
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var response = httpContext.Response;
        var requestAborted = httpContext.RequestAborted;

        var serializerOptions = (httpContext.RequestServices.GetService<IOptions<JsonOptions>>()?.Value ?? DefaultJsonOptions).SerializerOptions;
        var itemTypeInfo = serializerOptions.GetTypeInfo(_itemType);

        using var recordWriter = new StreamRecordWriter(httpContext, serializerOptions.Encoder, requestAborted);
        recordWriter.Start();

        var writerOptions = new JsonWriterOptions { Encoder = serializerOptions.Encoder };

        // Each item is serialized into a buffer first, so that an item that cannot be serialized leaves nothing of it in the response and is reported like
        // an exception of the endpoint.
        var itemBuffer = new ArrayBufferWriter<byte>();
        using var itemWriter = new Utf8JsonWriter(itemBuffer, writerOptions);

        // The enumeration token is linked to the request so that a pending MoveNextAsync can be cancelled promptly when the client disconnects.
        using var enumerationCts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        var enumerator = _source.GetAsyncEnumerator(enumerationCts.Token);
        Task<bool>? pendingMoveNext = null;

        try
        {
            while (true)
            {
                bool hasNext;

                try
                {
                    hasNext = await MoveNextAsync();

                    if (hasNext)
                        SerializeItem(enumerator.Current);
                }
                catch (OperationCanceledException) when (requestAborted.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Nothing has been sent yet, so the exception can produce a regular error response through the normal pipeline.
                    if (!response.HasStarted)
                        throw;

                    await recordWriter.WriteErrorAsync(await ResolveErrorAsync(ex));
                    return;
                }

                if (!hasNext)
                {
                    await recordWriter.WriteEndAsync();
                    return;
                }

                await recordWriter.WriteItemAsync(itemBuffer.WrittenMemory);
            }
        }
        finally
        {
            await DisposeEnumeratorAsync();
        }

        async ValueTask<bool> MoveNextAsync()
        {
            if (_pingInterval is not { } pingInterval)
                return await enumerator.MoveNextAsync();

            var moveNext = enumerator.MoveNextAsync();

            if (moveNext.IsCompleted)
                return await moveNext;

            var moveNextTask = moveNext.AsTask();
            pendingMoveNext = moveNextTask;

            while (true)
            {
                try
                {
                    bool result = await moveNextTask.WaitAsync(pingInterval, requestAborted);
                    pendingMoveNext = null;
                    return result;
                }
                catch (TimeoutException)
                {
                    await recordWriter.WritePingAsync();
                }
            }
        }

        void SerializeItem(object? item)
        {
            itemBuffer.ResetWrittenCount();
            itemWriter.Reset();
            JsonSerializer.Serialize(itemWriter, item, itemTypeInfo);
            itemWriter.Flush();
        }

        async ValueTask<ResponseExceptionInfo> ResolveErrorAsync(Exception ex)
        {
            if (await ApiExceptionHandling.ResolveAsync(httpContext, ex) is { } apiException)
                return ResponseExceptionInfo.FromApiException(apiException);

            // Unexpected exception with no handler to map it: report a generic server error (with details only in development).
            ApiExceptionHandling.TraceSuppressed(ex, "converted to stream error");
            return ResponseExceptionInfo.FromException(ex, _isDevelopment);
        }

        async ValueTask DisposeEnumeratorAsync()
        {
            try
            {
                if (pendingMoveNext is { } pending)
                {
                    // The enumerator cannot be disposed while a MoveNextAsync is in flight, so cancel it and wait for it to complete first.
                    if (!pending.IsCompleted)
                        enumerationCts.Cancel();

                    try
                    {
                        await pending;
                    }
                    catch
                    {
                        // Ignore - the enumeration is being abandoned.
                    }
                }

                await enumerator.DisposeAsync();
            }
            catch (Exception ex) when (!requestAborted.IsCancellationRequested)
            {
                ApiExceptionHandling.TraceSuppressed(ex, "enumerator disposal");
            }
            catch
            {
                // Ignore - the client is gone.
            }
        }
    }
}
