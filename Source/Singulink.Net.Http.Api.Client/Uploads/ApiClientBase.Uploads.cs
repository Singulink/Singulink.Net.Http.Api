using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Singulink.Net.Http.Api.Client.InternalServices;
using Singulink.Net.Http.Api.DataContracts;

namespace Singulink.Net.Http.Api.Client;

/// <content>
/// Uploads in chunks.
/// </content>
public abstract partial class ApiClientBase
{
    /// <summary>
    /// How many times the completion of an upload can be answered with <see cref="UploadErrorCodes.Incomplete"/> before the upload fails.
    /// </summary>
    private const int MaxIncompleteCompletions = 5;

    private static readonly TimeSpan UploadAbortTimeout = TimeSpan.FromSeconds(10);
    private static readonly UploadOptions DefaultUploadOptions = new();

    /// <summary>
    /// Gets the callback that <see cref="SendStreamingAsync{TItem}(HttpRequestMessage, object?, CancellationToken)"/> calls whenever a read of the
    /// streaming response that it starts returns bytes, keep-alive pings included. An upload sets it while it waits for the next item of its completion.
    /// </summary>
    private static readonly AsyncLocal<Action?> StreamActivityCallback = new();

    /// <summary>
    /// Uploads a file in chunks to an upload endpoint of the service and returns the result of the endpoint's handler.
    /// </summary>
    /// <typeparam name="TResult">The type of the handler's result.</typeparam>
    /// <param name="path">The path of the upload endpoint, relative to the base address. It cannot have a query string.</param>
    /// <param name="source">The file to upload.</param>
    /// <param name="query">The query string parameters of the requests that create and complete the upload, which the endpoint's validator and handler
    /// bind. Parameters with a <see langword="null"/> value are left out.</param>
    /// <param name="command">The app's command, which the request that creates the upload sends to the endpoint, serialized as JSON with
    /// <see cref="SerializerOptions"/>. Default is <see langword="null"/>, which sends none.</param>
    /// <param name="options">The options of the upload. Default is <see langword="null"/>, which uses the defaults of <see cref="UploadOptions"/>.</param>
    /// <param name="cancellationToken">A token to cancel the upload, which aborts it on the service.</param>
    /// <remarks>
    /// <para>
    /// The upload is created with a <c>POST</c> to <c>{path}/uploads</c>, which runs the endpoint's validator. The creation has a random idempotency key
    /// (see <see cref="CreateUploadCommand.IdempotencyKey"/>), the same each time it is made, so a creation made again after its response was lost gets
    /// the upload that it created. Its chunks are then sent, and the upload is completed with a <c>POST</c> to <c>{path}/uploads/{id}/complete</c>, which
    /// runs the handler and streams its result. Every request is created with
    /// <see cref="CreateRequest(HttpMethod, InterpolatedApiPathHandler, ReadOnlySpan{ValueTuple{string, object}})"/> and sent with
    /// <see cref="SendAsync{TResponse}(HttpRequestMessage, object?, CancellationToken)"/> or
    /// <see cref="SendStreamingAsync{TItem}(HttpRequestMessage, object?, CancellationToken)"/>, like any request of the client.
    /// </para>
    /// <para>
    /// A request that fails for a reason that may not last (see <see cref="ApiFailures.IsTransient(Exception)"/>), with a 408 (Request Timeout) or with a
    /// 429 (Too Many Requests) that has a <see cref="ApiException.RetryAfter"/> is made again after a wait: the <see cref="ApiException.RetryAfter"/> of
    /// the failure if it has one, and otherwise 1 second after the first failure, doubling up to 30 seconds, each reduced by up to half at random. Failures
    /// are retried until no request of the upload has succeeded for <see cref="UploadOptions.RetryTimeout"/>. A chunk is sent again from the buffer it was
    /// read into, without reading the source again.
    /// </para>
    /// <para>
    /// The handler runs once, and keeps running when the connection drops. When the completion's stream ends early or sends nothing for
    /// <see cref="UploadOptions.CompletionIdleTimeout"/>, the completion is made again, which waits for the run in progress or returns its outcome. When
    /// the service answers the completion with <see cref="UploadErrorCodes.Incomplete"/>, the chunks that its status lists as missing are read and sent
    /// again, and the upload is completed again, up to five times.
    /// </para>
    /// <para>
    /// When the upload is cancelled, the source fails to read, a progress report throws (see <see cref="UploadOptions.Progress"/>), or a request fails for
    /// another reason or for longer than the retry timeout, the upload is aborted with a <c>DELETE</c> request to <c>{path}/uploads/{id}</c>, which has a
    /// timeout of 10 seconds, and the exception is thrown: once the abort has ended for a failure, and right away for a cancellation, while the abort goes on.
    /// An upload that can't be aborted, such as one whose creation got no response, or whose failure is a 401, 412 or 428, or a 404 with
    /// <see cref="UploadErrorCodes.NotFound"/>, is left for the service to remove when it expires.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="path"/> has a query string or a fragment, is not valid or leads outside the base address.
    /// </exception>
    /// <exception cref="IOException">The source failed to read, such as for a file that changed, or ended before its length.</exception>
    /// <exception cref="FormatException">The completion streamed no result or more than one, or a response of the service is not valid for the protocol.
    /// </exception>
    /// <exception cref="JsonException">The handler's result is <see langword="null"/>, or a response of the service is not valid JSON for its type, such as
    /// a missing or <see langword="null"/> value where the type does not allow one.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled, also when the upload failed for another reason at
    /// the same time, whose exception is then the inner exception.</exception>
    /// <inheritdoc cref="SendAsync{T}(HttpRequestMessage, object?, CancellationToken)"
    ///     path="/exception[@cref!='T:System.ArgumentException' and @cref!='T:System.Text.Json.JsonException']"/>
    protected Task<TResult> UploadAsync<TResult>(
        InterpolatedApiPathHandler path,
        UploadSource source,
        ReadOnlySpan<(string Name, object? Value)> query = default,
        object? command = null,
        UploadOptions? options = null,
        CancellationToken cancellationToken = default)
        where TResult : notnull
    {
        string endpointPath = GetUploadEndpointPath(path);
        return UploadAndReadResultAsync<TResult>(endpointPath, query.ToArray(), source, command, options ?? DefaultUploadOptions, cancellationToken);
    }

    /// <summary>
    /// Uploads a file in chunks to an upload endpoint of the service and returns the items that the endpoint's handler streams.
    /// </summary>
    /// <typeparam name="TItem">The type of the handler's items.</typeparam>
    /// <remarks>
    /// <para>
    /// The upload runs as <see cref="UploadAsync{TResult}(InterpolatedApiPathHandler, UploadSource, ReadOnlySpan{ValueTuple{string, object}}, object?,
    /// UploadOptions?, CancellationToken)"/> describes. It starts when the enumeration starts, and again for each enumeration, and a path that is not valid
    /// throws when the method is called. Null items are yielded as <see langword="null"/>, whether or not the item type is nullable.
    /// </para>
    /// <para>
    /// When the completion is made again, its items start with the latest item that the handler produced, which can be the last item already yielded.
    /// Ending the enumeration early stops reading the items without aborting the upload, and the handler runs on.
    /// </para>
    /// </remarks>
    /// <exception cref="FormatException">A response of the service is not valid for the protocol, or contained an unrecognized record.</exception>
    /// <exception cref="JsonException">A response of the service is not valid JSON for its type, such as an item with a missing value where the type does
    /// not allow one.</exception>
    /// <inheritdoc cref="UploadAsync{TResult}" path="/param"/>
    /// <inheritdoc cref="UploadAsync{TResult}" path="/exception[@cref!='T:System.FormatException' and @cref!='T:System.Text.Json.JsonException']"/>
    protected IAsyncEnumerable<TItem> UploadStreamingAsync<TItem>(
        InterpolatedApiPathHandler path,
        UploadSource source,
        ReadOnlySpan<(string Name, object? Value)> query = default,
        object? command = null,
        UploadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string endpointPath = GetUploadEndpointPath(path);
        return RunUploadAsync<TItem>(endpointPath, query.ToArray(), source, command, options ?? DefaultUploadOptions, false, cancellationToken);
    }

    /// <summary>
    /// Gets the path of an upload endpoint without a trailing slash, which the paths of its uploads are built on, and checks it, so that the upload methods
    /// throw for a path that is not valid when they are called.
    /// </summary>
    private string GetUploadEndpointPath(InterpolatedApiPathHandler path)
    {
        string value = path.ToString();

        if (value.IndexOf('?') >= 0 || value.IndexOf('#') >= 0)
        {
            throw new ArgumentException(
                $"The path '{value}' of an upload endpoint cannot have a query string or a fragment. Query string parameters go in the query argument.",
                nameof(path));
        }

        value = value.TrimEnd('/');

        // Throws for a path that is not valid or leads outside the base address, as the requests of the upload would.
        string uploadsPath = value.Length > 0 ? value + "/" + ChunkedUploadProtocol.UploadsSegment : ChunkedUploadProtocol.UploadsSegment;
        GetApiUrl(uploadsPath);

        return value;
    }

    private async Task<TResult> UploadAndReadResultAsync<TResult>(
        string endpointPath, (string Name, object? Value)[] query, UploadSource source, object? command, UploadOptions options, CancellationToken cancellationToken)
        where TResult : notnull
    {
        bool hasResult = false;
        TResult? result = default;

        await foreach (var item in RunUploadAsync<TResult>(endpointPath, query, source, command, options, true, cancellationToken).ConfigureAwait(false))
        {
            result = item;
            hasResult = true;
        }

        if (!hasResult)
            throw new FormatException("The completion of the upload streamed no result.");

        return result ?? throw new JsonException("The result of the upload is null.");
    }

    /// <summary>
    /// Runs an upload: creates it, sends its chunks and completes it, and yields the items of the completion. With <paramref name="singleItem"/>, a
    /// response of the completion that streams more than one item fails, as the completion of a handler that returns a result doesn't.
    /// </summary>
    private async IAsyncEnumerable<TItem> RunUploadAsync<TItem>(
        string endpointPath,
        (string Name, object? Value)[] query,
        UploadSource source,
        object? command,
        UploadOptions options,
        bool singleItem,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var run = new UploadRun(endpointPath, query, source, options);

        try
        {
            await CreateUploadAsync(run, command, cancellationToken).ConfigureAwait(false);
            await SendChunksAsync(run, [.. Enumerable.Range(0, run.ChunkCount)], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ExceptionDispatchInfo.Capture(await FailUploadAsync(run, ex, cancellationToken).ConfigureAwait(false)).Throw();
        }

        while (true)
        {
            run.Progress.SetProcessing();

            if (run.Progress.ReportFailure is { } reportFailure)
                ExceptionDispatchInfo.Capture(await FailUploadAsync(run, reportFailure, cancellationToken).ConfigureAwait(false)).Throw();

            Exception? failure = null;
            bool isItemLate = false;
            bool hasItems = false;
            int itemCount = 0;

            using (var watchdog = new CompletionWatchdog(run, cancellationToken))
            {
                var items = SendCompletionAsync<TItem>(run, watchdog, watchdog.Token).GetAsyncEnumerator(watchdog.Token);

                try
                {
                    while (true)
                    {
                        bool hasItem;

                        try
                        {
                            hasItem = await items.MoveNextAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            failure = ex;
                            break;
                        }

                        if (!hasItem)
                            break;

                        // The time the app takes with the item is not silence of the stream. An item that arrives just as the stream is found silent for
                        // too long is left to the completion that is made again, which starts with the latest item: this one or a later one.
                        if (!watchdog.TryPause())
                        {
                            isItemLate = true;
                            break;
                        }

                        hasItems = true;

                        if (singleItem && ++itemCount > 1)
                        {
                            failure = new FormatException(
                                "The completion of the upload streamed more than one item. UploadStreamingAsync reads the items of a handler that " +
                                "streams them.");
                            break;
                        }

                        yield return items.Current;
                        watchdog.Resume();
                    }
                }
                finally
                {
                    await items.DisposeAsync().ConfigureAwait(false);
                }

                if (failure is null && !isItemLate)
                    yield break;

                if (watchdog.HasExpired && !cancellationToken.IsCancellationRequested)
                    failure = CreateCompletionIdleException(options.CompletionIdleTimeout, failure);

                failure ??= new OperationCanceledException(cancellationToken);

                // Items and pings of a stream that broke off show that the handler runs, so the completion is made again as after a first failure. A stream
                // that ends with an error of the service, such as a handler that can't run at the moment, is no success, items or not, since the handler
                // that runs again starts over. Only the errors of a server that stops and of a store that can't be reached for a moment, which say nothing
                // about the handler, end the stream as if it broke off.
                bool isBrokenOff = failure is not ApiException || IsErrorOfTheServer(failure);

                if (isBrokenOff && (hasItems || watchdog.WasActive))
                {
                    run.Retry.OnSuccess();
                    run.FailedCompletions = 0;
                }
            }

            try
            {
                await RecoverCompletionAsync(run, failure, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ExceptionDispatchInfo.Capture(await FailUploadAsync(run, ex, cancellationToken).ConfigureAwait(false)).Throw();
            }
        }
    }

    /// <summary>
    /// Creates the upload on the service.
    /// </summary>
    private async Task CreateUploadAsync(UploadRun run, object? command, CancellationToken cancellationToken)
    {
        var source = run.Source;
        var body = new CreateUploadCommand(
            source.FileName, source.Length, source.ContentType, run.Options.ChunkSize, SerializeUploadCommand(command), run.IdempotencyKey);

        var session = await SendUploadRequestAsync(
            run,
            () => SendForUploadJsonAsync(
                CreateRequest(HttpMethod.Post, run.UploadsPath, run.Query),
                JsonContent.Create(body, UploadJsonContext.Default.CreateUploadCommand),
                UploadJsonContext.Default.UploadSession,
                cancellationToken),
            cancellationToken).ConfigureAwait(false);

        if (session.Id.Length is 0)
            throw new FormatException("The service created an upload without an ID.");

        run.UploadPath = $"{run.UploadsPath}/{Uri.EscapeDataString(session.Id)}";

        if (session.ChunkSize <= 0)
            throw new FormatException($"The service created an upload with a chunk size of {session.ChunkSize} bytes.");

        // Each chunk that is being sent is held in a buffer of its size.
        if (session.ChunkSize > ChunkedUploadProtocol.MaxChunkSize)
        {
            throw new FormatException(
                $"The service created an upload with a chunk size of {session.ChunkSize} bytes, which is larger than the largest chunk size of 64 MiB.");
        }

        long chunkCount = (source.Length / session.ChunkSize) + (source.Length % session.ChunkSize > 0 ? 1 : 0);

        if (chunkCount > int.MaxValue)
            throw new FormatException($"The service created an upload with a chunk size of {session.ChunkSize} bytes, which makes too many chunks.");

        run.ChunkSize = session.ChunkSize;
        run.ChunkCount = (int)chunkCount;
    }

    /// <summary>
    /// Serializes the app's command with the client's serializer options, or returns <see langword="null"/> for none.
    /// </summary>
    private JsonElement? SerializeUploadCommand(object? command)
    {
        if (command is null)
            return null;

        return JsonSerializer.SerializeToElement(command, SerializerOptions.GetTypeInfo(command.GetType()));
    }

    /// <summary>
    /// Reads the chunks with the specified indexes, in order, and sends them, as many at a time as the options allow. Releases the source once no chunk
    /// is being read or sent. A progress report that throws stops the chunks, and its exception is thrown.
    /// </summary>
    private async Task SendChunksAsync(UploadRun run, List<int> chunks, CancellationToken cancellationToken)
    {
        if (chunks.Count is 0)
            return;

        var queue = new ChunkQueue(chunks);
        using var phase = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, run.Progress.ReportFailedToken);

        try
        {
            var workers = new Task[Math.Min(run.Options.MaxConcurrency, chunks.Count)];

            for (int i = 0; i < workers.Length; i++)
                workers[i] = SendQueuedChunksAsync(run, queue, phase);

            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await run.Source.ReleaseAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The release doesn't decide how the upload goes on: once the chunks are sent, the upload is completed, and the failure of a chunk is
                // thrown as it would be otherwise.
                TraceSuppressed(ex, "upload source release");
            }
        }

        // A request that the failed report stopped can fail with another exception.
        if ((run.Progress.ReportFailure ?? queue.Failure) is { } failure)
            ExceptionDispatchInfo.Capture(failure).Throw();

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Takes chunks from the queue, reads each into the worker's buffer and sends it, until the queue is empty or a chunk fails, which stops the other
    /// workers.
    /// </summary>
    private async Task SendQueuedChunksAsync(UploadRun run, ChunkQueue queue, CancellationTokenSource phase)
    {
        var cancellationToken = phase.Token;
        byte[]? buffer = null;

        // A buffer goes back to the pool only once the service acknowledged its last request, which then has read the whole body: a handler can still
        // read the content of a request that failed.
        bool bufferInUse = false;

        try
        {
            while (true)
            {
                int index;
                int length;

                // Chunks are read one at a time, in order, so that a source only reads forward.
                await queue.ReadLock.WaitAsync(cancellationToken).ConfigureAwait(false);

                try
                {
                    if (!queue.TryTake(out index))
                        return;

                    length = run.GetChunkLength(index);
                    buffer ??= ArrayPool<byte>.Shared.Rent((int)Math.Min(run.ChunkSize, run.Source.Length));
                    await run.Source.ReadExactlyAsync((long)index * run.ChunkSize, buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    queue.ReadLock.Release();
                }

                bufferInUse = true;
                await SendChunkAsync(run, index, buffer, length, cancellationToken).ConfigureAwait(false);
                bufferInUse = false;
            }
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                queue.SetFailure(ex);

            phase.Cancel();
        }
        finally
        {
            if (buffer is not null && !bufferInUse)
                ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Sends a chunk from its buffer, again after each failure that can be retried. Where the client sees the chunk's bytes go out, the chunk timeout runs
    /// while none go out, and after the last until the response arrives. In the browser, which sends a request's content at once, and where the platform's
    /// handler reads the content into memory before it sends it, as on Android and iOS, it is in effect the time of the whole request.
    /// </summary>
    private async Task SendChunkAsync(UploadRun run, int index, byte[] buffer, int length, CancellationToken cancellationToken)
    {
        long first = (long)index * run.ChunkSize;

        for (int failedAttempts = 0; ;)
        {
            var attempt = run.Progress.StartAttempt();
            using var inactivity = new InactivityTimeout(run.Options.ChunkTimeout, run.Retry.TimeProvider, cancellationToken);

            try
            {
                var request = CreateRequest(HttpMethod.Put, run.UploadPath!);
                SetRequestTimeout(request, Timeout.InfiniteTimeSpan);

                HttpContent content = run.Options.CountsWrittenBytes
                    ? new ChunkContent(buffer, length, attempt, inactivity)
                    : new ByteArrayContent(buffer, 0, length);
                content.Headers.ContentType = new MediaTypeHeaderValue(ChunkedUploadProtocol.ChunkContentType);
                content.Headers.ContentRange = new ContentRangeHeaderValue(first, first + length - 1, run.Source.Length);

                await SendAsync(request, content, inactivity.Token).ConfigureAwait(false);

                attempt.End(length);
                run.Retry.OnSuccess();

                return;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                attempt.End(0);

                // A request that the inactivity timeout cancelled fails as one that timed out.
                bool isIdle = inactivity.HasExpired && ex is OperationCanceledException;
                var failure = isIdle ? CreateChunkIdleException(run.Options.ChunkTimeout, ex) : ex;

                if (run.Retry.GetRetryDelay(failure, ++failedAttempts) is not { } delay)
                {
                    if (isIdle)
                        throw failure;

                    throw;
                }

                await run.Retry.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                attempt.End(0);
            }
        }
    }

    /// <summary>
    /// Sends the request that completes the upload when the enumeration starts, and yields the items of its streaming response. The request has no
    /// timeout of its own: the watchdog cancels it when its stream sends nothing for too long.
    /// </summary>
    private async IAsyncEnumerable<TItem> SendCompletionAsync<TItem>(
        UploadRun run, CompletionWatchdog watchdog, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The path is already escaped, so it is passed as a string rather than interpolated.
        string completionPath = run.UploadPath + "/" + ChunkedUploadProtocol.CompleteSegment;

        var request = CreateRequest(HttpMethod.Post, completionPath, run.Query);
        SetRequestTimeout(request, Timeout.InfiniteTimeSpan);

        var items = SendStreamingAsync<TItem>(request, null, cancellationToken).GetAsyncEnumerator(cancellationToken);

        try
        {
            while (true)
            {
                bool hasItem;

                // The callback reaches the reads of the response through the execution context rather than the request, so that it also reaches them
                // when an override of SendStreamingAsync sends another request.
                StreamActivityCallback.Value = watchdog.OnActivity;

                try
                {
                    hasItem = await items.MoveNextAsync().ConfigureAwait(false);
                }
                finally
                {
                    StreamActivityCallback.Value = null;
                }

                if (!hasItem)
                    break;

                yield return items.Current;
            }
        }
        finally
        {
            await items.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets a value indicating whether the error that ended a completion's stream is one of the server that the completion reached, which says nothing about
    /// the handler: a server that stops, or a store that the server can't reach for a moment.
    /// </summary>
    private static bool IsErrorOfTheServer(Exception failure)
    {
        return failure is ServiceUnavailableApiException { ErrorCode: UploadErrorCodes.ServerStopping or UploadErrorCodes.StoreUnavailable };
    }

    /// <summary>
    /// Gets the upload ready to be completed again after its completion failed: sends the chunks that the service lacks, or waits before a retry. Throws
    /// the failure if the completion can't be made again.
    /// </summary>
    private async Task RecoverCompletionAsync(UploadRun run, Exception failure, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (failure is ConflictApiException { ErrorCode: UploadErrorCodes.Incomplete })
        {
            if (++run.IncompleteCompletions > MaxIncompleteCompletions)
                ExceptionDispatchInfo.Capture(failure).Throw();

            run.Retry.OnSuccess();

            var missing = await GetMissingChunksAsync(run, cancellationToken).ConfigureAwait(false);
            run.Progress.SetAcknowledged(run.Source.Length - missing.Sum(i => (long)run.GetChunkLength(i)));

            if (run.Progress.ReportFailure is { } reportFailure)
                ExceptionDispatchInfo.Capture(reportFailure).Throw();

            if (missing.Count > 0)
                await SendChunksAsync(run, missing, cancellationToken).ConfigureAwait(false);
            else
                await run.Retry.DelayAsync(run.Retry.NextBackoff(run.IncompleteCompletions), cancellationToken).ConfigureAwait(false);

            return;
        }

        if (run.Retry.GetRetryDelay(failure, ++run.FailedCompletions) is not { } delay)
            ExceptionDispatchInfo.Capture(failure).Throw();
        else
            await run.Retry.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the indexes of the chunks that the status of the upload lists as missing, in order.
    /// </summary>
    private async Task<List<int>> GetMissingChunksAsync(UploadRun run, CancellationToken cancellationToken)
    {
        var status = await SendUploadRequestAsync(
            run,
            () => SendForUploadJsonAsync(CreateRequest(HttpMethod.Get, run.UploadPath!), null, UploadJsonContext.Default.UploadStatus, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        if (status.Length != run.Source.Length || status.ChunkSize != run.ChunkSize)
        {
            throw new FormatException(
                $"The status of the upload has a length of {status.Length} bytes and chunks of {status.ChunkSize} bytes, rather than the upload's " +
                $"{run.Source.Length} and {run.ChunkSize} bytes.");
        }

        var missing = new List<int>();

        foreach (var range in status.MissingChunks)
        {
            if (range.First < 0 || range.Last < range.First || range.Last >= run.ChunkCount)
                throw new FormatException($"The status of the upload lists missing chunks {range}, which are not among its {run.ChunkCount} chunks.");

            for (int index = range.First; index <= range.Last; index++)
                missing.Add(index);
        }

        return missing;
    }

    /// <summary>
    /// Makes a request of the upload, again after each failure that can be retried.
    /// </summary>
    private static async Task<T> SendUploadRequestAsync<T>(UploadRun run, Func<Task<T>> send, CancellationToken cancellationToken)
    {
        for (int failedAttempts = 0; ;)
        {
            try
            {
                var result = await send().ConfigureAwait(false);
                run.Retry.OnSuccess();

                return result;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (run.Retry.GetRetryDelay(ex, ++failedAttempts) is not { } delay)
                    throw;

                await run.Retry.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Sends a request of the upload protocol and reads its response with the toolkit's type info of the protocol's contracts, within the request's
    /// timeout (see <see cref="SetRequestTimeout(HttpRequestMessage, TimeSpan)"/>).
    /// </summary>
    private async Task<T> SendForUploadJsonAsync<T>(HttpRequestMessage request, HttpContent? content, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        var timeout = GetRequestTimeout(request);
        using var timeoutSource = CreateTimeoutSource(timeout, cancellationToken);
        var token = timeoutSource?.Token ?? cancellationToken;

        try
        {
            var response = await SendAsync<HttpResponseMessage>(request, content, token).ConfigureAwait(false);

            try
            {
                var read = response.Content.ReadFromJsonAsync(typeInfo, token);
#if !NET
                read = read.WaitOrAbortAsync(response.Content, token);
#endif
                return await read.ConfigureAwait(false) ?? throw new JsonException("The response body is null.");
            }
            catch (IOException ex)
            {
                throw CreateResponseReadException(ex);
            }
            finally
            {
                DisposeResponse(response);
            }
        }
        catch (OperationCanceledException ex) when (timeoutSource?.IsCancellationRequested is true && !cancellationToken.IsCancellationRequested)
        {
            throw CreateTimeoutException(timeout, ex);
        }
    }

    /// <summary>
    /// Aborts the upload on the service after it failed or was cancelled, and gets the exception to throw: an <see cref="OperationCanceledException"/> if
    /// the upload was cancelled, whatever else failed at the same time, and otherwise the failure.
    /// </summary>
    private async Task<Exception> FailUploadAsync(UploadRun run, Exception failure, CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested)
        {
            await AbortUploadAsync(run, failure).ConfigureAwait(false);
            return failure;
        }

        // The cancellation doesn't wait for the abort, which goes on within its timeout and never throws.
        _ = AbortUploadAsync(run, failure);

        return failure as OperationCanceledException ?? new OperationCanceledException("The upload was cancelled.", failure, cancellationToken);
    }

    /// <summary>
    /// Aborts the upload on the service after it failed, unless it was not created or the failure shows that the request can't succeed: a 401, 412 or
    /// 428, which the request would get as well, or a 404 with <see cref="UploadErrorCodes.NotFound"/>. A failure of the request is ignored, since the
    /// service removes the upload when it expires.
    /// </summary>
    private async Task AbortUploadAsync(UploadRun run, Exception failure)
    {
        if (run.UploadPath is null)
            return;

        if (failure is UnauthorizedApiException or UserChangedApiException or UserRequiredApiException or
            NotFoundApiException { ErrorCode: UploadErrorCodes.NotFound })
        {
            return;
        }

        try
        {
            var request = CreateRequest(HttpMethod.Delete, run.UploadPath);
            SetRequestTimeout(request, UploadAbortTimeout);

            await SendAsync(request, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable RCS1075 // Avoid empty catch clause that catches System.Exception
        catch (Exception)
        {
            // The upload's failure is thrown instead, and the service removes the upload when it expires.
        }
#pragma warning restore RCS1075
    }

    /// <summary>
    /// Traces an exception that does not change how the upload goes on.
    /// </summary>
    private static void TraceSuppressed(Exception exception, string context)
    {
        if (Trace.Listeners.Count > 0)
            Trace.TraceWarning($"[Singulink.Net.Http.Api] Unexpected exception suppressed ({context}): {exception}");
    }

    private static TaskCanceledException CreateChunkIdleException(TimeSpan timeout, Exception exception)
    {
        double seconds = timeout.TotalSeconds;
        string message = string.Format(
            CultureInfo.InvariantCulture,
            "The request of a chunk of the upload sent nothing and got no response for {0} {1}.",
            seconds,
            seconds is 1 ? "second" : "seconds");

        // Like a request timeout, which ApiFailures.IsTransient counts as transient.
        return new TaskCanceledException(message, new TimeoutException(message, exception));
    }

    private static TaskCanceledException CreateCompletionIdleException(TimeSpan timeout, Exception? exception)
    {
        double seconds = timeout.TotalSeconds;
        string message = string.Format(
            CultureInfo.InvariantCulture,
            "The completion of the upload sent nothing for {0} {1}.",
            seconds,
            seconds is 1 ? "second" : "seconds");

        // Like a request timeout, which ApiFailures.IsTransient counts as transient.
        return new TaskCanceledException(message, new TimeoutException(message, exception));
    }

    /// <summary>
    /// The state of one run of an upload.
    /// </summary>
    private sealed class UploadRun
    {
        public UploadRun(string endpointPath, (string Name, object? Value)[] query, UploadSource source, UploadOptions options)
        {
            UploadsPath = endpointPath.Length > 0 ? $"{endpointPath}/{ChunkedUploadProtocol.UploadsSegment}" : ChunkedUploadProtocol.UploadsSegment;
            Query = query;
            Source = source;
            Options = options;
            Retry = new UploadRetryPolicy(options.RetryTimeout, options.TimeProvider ?? TimeProvider.System);
            Progress = new UploadProgressTracker(options.Progress, source.Length);
        }

        /// <summary>
        /// Gets the path that creates uploads: <c>{path}/uploads</c>.
        /// </summary>
        public string UploadsPath { get; }

        /// <summary>
        /// Gets the idempotency key of the creation: random, and the same each time the creation is made.
        /// </summary>
        public string IdempotencyKey { get; } = Guid.NewGuid().ToString("N");

        public (string Name, object? Value)[] Query { get; }

        public UploadSource Source { get; }

        public UploadOptions Options { get; }

        public UploadRetryPolicy Retry { get; }

        public UploadProgressTracker Progress { get; }

        /// <summary>
        /// Gets or sets the path of the upload once it is created: <c>{path}/uploads/{id}</c>.
        /// </summary>
        public string? UploadPath { get; set; }

        public int ChunkSize { get; set; }

        public int ChunkCount { get; set; }

        /// <summary>
        /// Gets or sets how many completions in a row failed without their stream delivering anything.
        /// </summary>
        public int FailedCompletions { get; set; }

        /// <summary>
        /// Gets or sets how many completions the service answered with <see cref="UploadErrorCodes.Incomplete"/>.
        /// </summary>
        public int IncompleteCompletions { get; set; }

        public int GetChunkLength(int index) => (int)Math.Min(ChunkSize, Source.Length - ((long)index * ChunkSize));
    }

    /// <summary>
    /// The chunks that the workers of a <see cref="SendChunksAsync(UploadRun, List{int}, CancellationToken)"/> take, in order, and the first failure of
    /// a worker.
    /// </summary>
    private sealed class ChunkQueue(List<int> chunks)
    {
        private int _next;
        private Exception? _failure;

        /// <summary>
        /// Gets the lock that a worker holds while it takes a chunk and reads it.
        /// </summary>
        public SemaphoreSlim ReadLock { get; } = new(1, 1);

        public Exception? Failure => Volatile.Read(ref _failure);

        /// <summary>
        /// Takes the next chunk. Must be called with <see cref="ReadLock"/> held.
        /// </summary>
        public bool TryTake(out int index)
        {
            if (_next < chunks.Count)
            {
                index = chunks[_next++];
                return true;
            }

            index = -1;
            return false;
        }

        public void SetFailure(Exception exception) => Interlocked.CompareExchange(ref _failure, exception, null);
    }

    /// <summary>
    /// Cancels a completion whose stream sends nothing, not even a keep-alive ping, for the completion idle timeout, and records whether the stream
    /// delivered anything.
    /// </summary>
    private sealed class CompletionWatchdog : IDisposable
    {
#if NET
        private readonly Lock _lock = new();
#else
        private readonly object _lock = new();
#endif

        private readonly CancellationTokenSource _cancellation;
        private readonly UploadRetryPolicy _retry;
        private readonly TimeSpan _timeout;
        private readonly ITimer? _timer;
        private long _lastActivity;
        private bool _isPaused;
        private bool _hasExpired;
        private bool _wasActive;

        public CompletionWatchdog(UploadRun run, CancellationToken cancellationToken)
        {
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _retry = run.Retry;
            _timeout = run.Options.CompletionIdleTimeout;
            _lastActivity = _retry.TimeProvider.GetTimestamp();

            // Timers take at most about 49.7 days, and like request timeouts, longer values than int.MaxValue milliseconds mean no limit.
            if (_timeout != Timeout.InfiniteTimeSpan && _timeout.TotalMilliseconds <= int.MaxValue)
                _timer = _retry.TimeProvider.CreateTimer(static state => ((CompletionWatchdog)state!).Expire(), this, _timeout, Timeout.InfiniteTimeSpan);
        }

        /// <summary>
        /// Gets the token that the completion is sent with, which is cancelled when the upload is cancelled or the stream is silent for too long.
        /// </summary>
        public CancellationToken Token => _cancellation.Token;

        /// <summary>
        /// Gets a value indicating whether the stream was silent for too long.
        /// </summary>
        public bool HasExpired
        {
            get {
                lock (_lock)
                    return _hasExpired;
            }
        }

        /// <summary>
        /// Gets a value indicating whether the stream delivered anything.
        /// </summary>
        public bool WasActive
        {
            get {
                lock (_lock)
                    return _wasActive;
            }
        }

        public void OnActivity()
        {
            lock (_lock)
            {
                if (_hasExpired)
                    return;

                _wasActive = true;
                _lastActivity = _retry.TimeProvider.GetTimestamp();
            }

            _timer?.Change(_timeout, Timeout.InfiniteTimeSpan);
        }

        /// <summary>
        /// Stops measuring the silence while the app has an item, unless the stream was found silent for too long before the item could be handed to the
        /// app.
        /// </summary>
        /// <returns><see langword="true"/> if the watchdog is paused, or <see langword="false"/> if it has expired.</returns>
        public bool TryPause()
        {
            lock (_lock)
            {
                if (_hasExpired)
                    return false;

                _isPaused = true;
            }

            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return true;
        }

        public void Resume()
        {
            lock (_lock)
            {
                _isPaused = false;
                _lastActivity = _retry.TimeProvider.GetTimestamp();
            }

            _timer?.Change(_timeout, Timeout.InfiniteTimeSpan);
        }

        public void Dispose()
        {
            _timer?.Dispose();
            _cancellation.Dispose();
        }

        private void Expire()
        {
            lock (_lock)
            {
                if (_isPaused || _hasExpired)
                    return;

                // A timer that fires just as the stream delivers something, or that was set again, fires late, and waits for the rest of the time.
                var silence = _retry.TimeProvider.GetElapsedTime(_lastActivity);

                if (silence < _timeout)
                {
                    _timer?.Change(_timeout - silence, Timeout.InfiniteTimeSpan);
                    return;
                }

                _hasExpired = true;
            }

            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The completion has ended.
            }
        }
    }
}
