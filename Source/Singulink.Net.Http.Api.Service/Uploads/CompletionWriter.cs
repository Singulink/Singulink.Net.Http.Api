using System.Text.Encodings.Web;
using System.Threading.Channels;

namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// Writes the completion stream of an upload, a <see cref="StreamingResponse"/>, for as long as the client is connected: once it is gone, writing does
/// nothing. An error before the stream has started is thrown instead, so that it becomes a regular error response.
/// </summary>
internal sealed class CompletionWriter : IDisposable
{
    private readonly HttpContext _context;
    private readonly StreamRecordWriter _records;
    private readonly TimeProvider _timeProvider;
    private bool _isClientGone;

    public CompletionWriter(HttpContext context, JavaScriptEncoder? encoder, CancellationToken clientAborted, TimeSpan pingInterval, TimeProvider timeProvider)
    {
        _context = context;
        _records = new StreamRecordWriter(context, encoder, clientAborted);
        _timeProvider = timeProvider;
        ClientAborted = clientAborted;
        PingInterval = pingInterval;
    }

    /// <summary>
    /// Gets the token that is cancelled when the client disconnects.
    /// </summary>
    public CancellationToken ClientAborted { get; }

    public TimeSpan PingInterval { get; }

    public bool HasStarted => _context.Response.HasStarted;

    public bool IsClientGone => _isClientGone || ClientAborted.IsCancellationRequested;

    public ValueTask WriteItemAsync(ReadOnlyMemory<byte> itemJson) => WriteAsync(static (records, json) => records.WriteItemAsync(json), itemJson);

    public ValueTask WritePingAsync() => WriteAsync<object?>(static (records, _) => records.WritePingAsync(), null);

    /// <summary>
    /// Writes an outcome: its item, if it has one and <paramref name="writeItem"/> is <see langword="true"/>, and the end record, or its error. The error of
    /// an interrupted upload is the service's own, which the app's exception handler maps as any error.
    /// </summary>
    public async ValueTask WriteOutcomeAsync(UploadOutcome outcome, bool writeItem)
    {
        if (outcome.Error is { Code: UploadErrorCodes.Interrupted })
        {
            await FailAsync(UploadErrors.InterruptedException());
            return;
        }

        if (outcome.Error is { } error)
        {
            await FailAsync(error);
            return;
        }

        if (writeItem && outcome.HasItem)
            await WriteItemAsync(outcome.GetItemJson());

        await WriteAsync<object?>(static (records, _) => records.WriteEndAsync(), null);
    }

    /// <summary>
    /// Reports an error of the service: throws it if the stream has not started, so that it is reported as any error of a request, and otherwise writes
    /// it as an error record, as the app's exception handler maps it (see <see cref="IApiExceptionHandler"/>).
    /// </summary>
    public async ValueTask FailAsync(ApiException exception)
    {
        if (!HasStarted)
            throw exception;

        await WriteErrorAsync(ResponseExceptionInfo.FromApiException(await MapAsync(exception)));
    }

    /// <summary>
    /// Reports a stored error outcome, which the app's exception handler mapped when the run failed: writes it as the error response if the stream has not
    /// started, without passing it to the exception handler again, and as an error record otherwise.
    /// </summary>
    public async ValueTask FailAsync(StreamErrorInfo error)
    {
        if (!HasStarted && ResponseExceptionInfo.CreateStreamError(error) is { } exception)
        {
            await FailMappedAsync(exception);
            return;
        }

        await WriteAsync(static (records, e) => records.WriteErrorAsync(e), error);
    }

    /// <summary>
    /// Reports the error of the request that ran the handler, which the app's exception handler has mapped already: writes it as the error response if the
    /// stream has not started, without passing it to the exception handler again, and as an error record otherwise.
    /// </summary>
    public async ValueTask FailMappedAsync(ApiException exception)
    {
        if (HasStarted)
        {
            await WriteErrorAsync(ResponseExceptionInfo.FromApiException(exception));
            return;
        }

        if (IsClientGone)
            return;

        try
        {
            await _context.Response.WriteApiErrorAsync(exception, ClientAborted);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException || ClientAborted.IsCancellationRequested)
        {
            _isClientGone = true;
        }
    }

    /// <summary>
    /// Reads the next event of a run, writing a ping record whenever none arrives for <see cref="PingInterval"/>.
    /// </summary>
    /// <returns>The event, or <see langword="null"/> if the client is gone.</returns>
    public async ValueTask<RunEvent?> ReadAsync(ChannelReader<RunEvent> events)
    {
        var read = events.ReadAsync(ClientAborted).AsTask();

        while (true)
        {
            try
            {
                return await read.WaitAsync(PingInterval, _timeProvider);
            }
            catch (TimeoutException)
            {
                await WritePingAsync();
            }
            catch (OperationCanceledException) when (ClientAborted.IsCancellationRequested)
            {
                return null;
            }
        }
    }

    public void Dispose() => _records.Dispose();

    private async ValueTask<ApiException> MapAsync(ApiException exception)
    {
        try
        {
            return await ApiExceptionHandling.ResolveAsync(_context, exception) ?? exception;
        }
        catch (Exception ex)
        {
            ApiExceptionHandling.TraceSuppressed(ex, "mapping of an error of an upload's completion");
            return exception;
        }
    }

    private async ValueTask WriteErrorAsync(ResponseExceptionInfo info)
    {
        await WriteAsync(static (records, i) => records.WriteErrorAsync(i), info);
    }

    private async ValueTask WriteAsync<T>(Func<StreamRecordWriter, T, ValueTask> write, T state)
    {
        if (IsClientGone)
            return;

        try
        {
            await write(_records, state);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException || ClientAborted.IsCancellationRequested)
        {
            // The client is gone. The handler goes on, and the client gets its outcome when it comes back.
            _isClientGone = true;
        }
    }
}
