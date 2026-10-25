using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Writes the records of a <see cref="StreamingResponse"/> to a response, flushing each record as it is written. The first record starts the response with
/// a <c>200 OK</c> status and the streaming content type.
/// </summary>
internal sealed class StreamRecordWriter : IDisposable
{
    private static readonly ReadOnlyMemory<byte> NewLine = "\n"u8.ToArray();

    private readonly HttpContext _httpContext;
    private readonly Utf8JsonWriter _jsonWriter;
    private readonly CancellationToken _cancellationToken;
    private bool _started;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamRecordWriter"/> class.
    /// </summary>
    /// <param name="httpContext">The context whose response the records are written to.</param>
    /// <param name="encoder">The encoder of the JSON of the records, such as the one of the app's JSON options.</param>
    /// <param name="cancellationToken">A token that cancels flushing the records, such as when the client disconnects.</param>
    public StreamRecordWriter(HttpContext httpContext, JavaScriptEncoder? encoder, CancellationToken cancellationToken)
    {
        _httpContext = httpContext;
        _jsonWriter = new Utf8JsonWriter(httpContext.Response.BodyWriter, new JsonWriterOptions { Encoder = encoder });
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Writes an item record with the item's JSON.
    /// </summary>
    public ValueTask WriteItemAsync(ReadOnlyMemory<byte> itemJson)
    {
        _jsonWriter.WriteStartObject();
        _jsonWriter.WritePropertyName("item"u8);
        _jsonWriter.WriteRawValue(itemJson.Span, skipInputValidation: true);
        _jsonWriter.WriteEndObject();
        return FlushRecordAsync();
    }

    /// <summary>
    /// Writes a ping record.
    /// </summary>
    public ValueTask WritePingAsync()
    {
        _jsonWriter.WriteStartObject();
        _jsonWriter.WriteBoolean("ping"u8, true);
        _jsonWriter.WriteEndObject();
        return FlushRecordAsync();
    }

    /// <summary>
    /// Writes the end record.
    /// </summary>
    public ValueTask WriteEndAsync()
    {
        _jsonWriter.WriteStartObject();
        _jsonWriter.WriteBoolean("end"u8, true);
        _jsonWriter.WriteEndObject();
        return FlushRecordAsync();
    }

    /// <summary>
    /// Writes an error record.
    /// </summary>
    public ValueTask WriteErrorAsync(ResponseExceptionInfo info)
    {
        string? code = info.ErrorCode.Length > 0 ? info.ErrorCode : null;
        return WriteErrorAsync(info.StatusCode, code, info.Message, info.ReferenceId?.ToString("D"), info.RetryAfterSeconds);
    }

    /// <summary>
    /// Writes an error record with the error of another error record.
    /// </summary>
    public ValueTask WriteErrorAsync(StreamErrorInfo error)
    {
        return WriteErrorAsync(error.Status, error.Code, error.Message ?? string.Empty, error.ReferenceId, error.RetryAfter);
    }

    /// <summary>
    /// Sets the status and the content type of the response, and turns off its buffering, unless that was done already. The first record does it too.
    /// </summary>
    public void Start()
    {
        if (_started)
            return;

        var response = _httpContext.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = StreamingResponse.ContentType;
        _httpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        _started = true;
    }

    public void Dispose() => _jsonWriter.Dispose();

    private ValueTask WriteErrorAsync(int status, string? code, string message, string? referenceId, long? retryAfterSeconds)
    {
        _jsonWriter.WriteStartObject();
        _jsonWriter.WriteStartObject("error"u8);
        _jsonWriter.WriteNumber("status"u8, status);

        if (code is { Length: > 0 })
            _jsonWriter.WriteString("code"u8, code);

        _jsonWriter.WriteString("message"u8, message);

        if (referenceId is not null)
            _jsonWriter.WriteString("referenceId"u8, referenceId);

        if (retryAfterSeconds is { } seconds)
            _jsonWriter.WriteNumber("retryAfter"u8, seconds);

        _jsonWriter.WriteEndObject();
        _jsonWriter.WriteEndObject();
        return FlushRecordAsync();
    }

    private async ValueTask FlushRecordAsync()
    {
        Start();

        var bodyWriter = _httpContext.Response.BodyWriter;

        _jsonWriter.Flush();
        _jsonWriter.Reset();
        bodyWriter.Write(NewLine.Span);
        await bodyWriter.FlushAsync(_cancellationToken);
    }
}
