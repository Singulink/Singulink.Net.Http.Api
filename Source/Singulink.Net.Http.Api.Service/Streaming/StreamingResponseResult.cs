using System.Buffers;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Writes an <see cref="IAsyncEnumerable{T}"/> result as a <see cref="StreamingResponse"/>, flushing each record as it is produced.
/// </summary>
internal sealed class StreamingResponseResult : IResult
{
    private static readonly ReadOnlyMemory<byte> NewLine = "\n"u8.ToArray();

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

        var serializerOptions = httpContext.RequestServices.GetService<IOptions<JsonOptions>>()?.Value.SerializerOptions ?? JsonSerializerOptions.Web;
        var itemTypeInfo = serializerOptions.GetTypeInfo(_itemType);

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = StreamingResponse.ContentType;
        httpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var bodyWriter = response.BodyWriter;
        using var jsonWriter = new Utf8JsonWriter(bodyWriter, new JsonWriterOptions { Encoder = serializerOptions.Encoder });

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

                    await WriteErrorAsync(await ResolveErrorAsync(ex));
                    return;
                }

                if (!hasNext)
                {
                    await WriteEndAsync();
                    return;
                }

                await WriteItemAsync(enumerator.Current);
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
                    await WritePingAsync();
                }
            }
        }

        async ValueTask WriteItemAsync(object? item)
        {
            jsonWriter.WriteStartObject();
            jsonWriter.WritePropertyName("item"u8);
            JsonSerializer.Serialize(jsonWriter, item, itemTypeInfo);
            jsonWriter.WriteEndObject();
            await FlushRecordAsync();
        }

        async ValueTask WritePingAsync()
        {
            jsonWriter.WriteStartObject();
            jsonWriter.WriteBoolean("ping"u8, true);
            jsonWriter.WriteEndObject();
            await FlushRecordAsync();
        }

        async ValueTask WriteEndAsync()
        {
            jsonWriter.WriteStartObject();
            jsonWriter.WriteBoolean("end"u8, true);
            jsonWriter.WriteEndObject();
            await FlushRecordAsync();
        }

        async ValueTask<ResponseExceptionInfo> ResolveErrorAsync(Exception ex)
        {
            if (await ApiExceptionHandling.ResolveAsync(httpContext, ex) is { } apiException)
                return ResponseExceptionInfo.FromApiException(apiException);

            // Unexpected exception with no handler to map it: report a generic server error (with details only in development).
            ApiExceptionHandling.TraceSuppressed(ex, "converted to stream error");
            return ResponseExceptionInfo.FromException(ex, _isDevelopment);
        }

        async ValueTask WriteErrorAsync(ResponseExceptionInfo info)
        {
            jsonWriter.WriteStartObject();
            jsonWriter.WriteStartObject("error"u8);
            jsonWriter.WriteNumber("status"u8, info.StatusCode);

            if (info.ErrorCode.Length > 0)
                jsonWriter.WriteString("code"u8, info.ErrorCode);

            jsonWriter.WriteString("message"u8, info.Message);
            jsonWriter.WriteEndObject();
            jsonWriter.WriteEndObject();
            await FlushRecordAsync();
        }

        async ValueTask FlushRecordAsync()
        {
            jsonWriter.Flush();
            jsonWriter.Reset();
            bodyWriter.Write(NewLine.Span);
            await bodyWriter.FlushAsync(requestAborted);
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
