using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Singulink.Net.Http.Api.Client.InternalServices;
using Singulink.Net.Http.Api.Client.Utilities;
using Singulink.Net.Http.Api.DataContracts;

namespace Singulink.Net.Http.Api.Client;

/// <content>
/// Uploads of files, directly or in chunks.
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
    /// Gets the callbacks of the streaming response that <see cref="SendStreamingAsync{TItem}(HttpRequestMessage, object?, CancellationToken)"/> starts,
    /// which an upload sets while it waits for the next item of its completion. They reach the response through the execution context rather than the
    /// request, so that they also reach it when an override of the method sends another request.
    /// </summary>
    private static readonly AsyncLocal<StreamCallbacks?> UploadStreamCallbacks = new();

    /// <summary>
    /// Uploads a file to an upload endpoint of the service, directly or in chunks, and returns the result of the endpoint's handler.
    /// </summary>
    /// <typeparam name="TResult">The type of the handler's result.</typeparam>
    /// <param name="path">The path of the upload endpoint, relative to the base address. It cannot have a query string.</param>
    /// <param name="source">The file to upload.</param>
    /// <param name="query">The query string parameters of the requests that create and complete the upload, which the endpoint's validator and handler
    /// bind. Parameters with a <see langword="null"/> value are left out.</param>
    /// <param name="command">The app's command, which the request that creates the upload sends to the endpoint, serialized as JSON with
    /// <see cref="SerializerOptions"/>. Default is <see langword="null"/>, which sends none.</param>
    /// <param name="options">The options of the upload. Default is <see langword="null"/>, which uses the defaults of <see cref="UploadOptions"/>.</param>
    /// <param name="cancellationToken">A token to cancel the upload, which cancels it on the service (see the remarks).</param>
    /// <remarks>
    /// <para>
    /// The upload is created with a <c>POST</c> to <c>{path}/uploads</c>, which runs the endpoint's validator. The client chooses the upload's ID, a random
    /// GUID (see <see cref="CreateUploadCommand.Id"/>), the same each time the creation is made, so a creation made again after its response was lost gets
    /// the upload that it created. Its chunks are then sent, and the upload is completed with a <c>POST</c> to <c>{path}/uploads/{id}/complete</c>, which
    /// runs the handler and streams its result. Every request is created with
    /// <see cref="CreateRequest(HttpMethod, InterpolatedApiPathHandler, ReadOnlySpan{ValueTuple{string, object}})"/> and sent with
    /// <see cref="SendAsync{TResponse}(HttpRequestMessage, object?, CancellationToken)"/> or
    /// <see cref="SendStreamingAsync{TItem}(HttpRequestMessage, object?, CancellationToken)"/>, like any request of the client.
    /// </para>
    /// <para>
    /// A file that fits in one chunk, of up to <see cref="UploadOptions.ChunkSize"/> (5 MiB by default), is uploaded directly instead, in one request: a
    /// <c>POST</c> to <c>{path}/uploads/direct</c> with the file as its body and the values of the creation in a header, which runs the validator and then
    /// the handler, and streams its result. A file whose creation has a command of more than about 6 KiB of JSON takes the three requests. The request is
    /// made again with the same ID until the service answers it, and once the service has answered with the upload's path, the upload goes on as one in
    /// chunks: its completion is made again, and its chunk sent again if the service lacks it.
    /// </para>
    /// <para>
    /// These get the three requests instead, for this upload, with the same ID:
    /// </para>
    /// <list type="bullet">
    /// <item>a 4xx that is not in the toolkit's error format, other than a 408, such as from a service without direct uploads or a server or a proxy in front
    /// of the service;</item>
    /// <item>a 431 (Request Header Fields Too Large) in any format;</item>
    /// <item>a success that is not the upload's stream;</item>
    /// <item>a service whose largest chunk is smaller than the file (<see cref="UploadErrorCodes.DirectTooLarge"/>);</item>
    /// <item>a 429 (Too Many Requests) in any format, such as from a rate limit or at a limit of the user's open uploads
    /// (<see cref="UploadErrorCodes.LimitReached"/> and <see cref="UploadErrorCodes.SizeLimitReached"/>), and a service at its limit of open uploads
    /// (<see cref="UploadErrorCodes.StorageFull"/>), so that the file is sent once the creation has got through rather than with each attempt;</item>
    /// <item>a second failure without a response.</item>
    /// </list>
    /// <para>
    /// A request that fails for a reason that may not last (see <see cref="ApiFailures.IsTransient(Exception)"/>), with a 408 (Request Timeout) or with a
    /// 429 (Too Many Requests) that has a <see cref="ApiException.RetryAfter"/>, such as a creation while the user has the most open uploads that the
    /// service allows (<see cref="UploadErrorCodes.LimitReached"/>), is made again after a wait: the <see cref="ApiException.RetryAfter"/> of the failure
    /// if it has one, raised by up to half at random, and otherwise 1 second after the first failure, doubling up to 30 seconds, each reduced by up to half
    /// at random. Failures are retried until no request of the upload has succeeded for <see cref="UploadOptions.RetryTimeout"/>. A chunk, or the file of
    /// a direct upload, is sent again from the buffer it was read into, without reading the source again, until the upload goes on with the three requests,
    /// which read it again.
    /// </para>
    /// <para>
    /// The handler runs once, and keeps running when the connection drops. When the completion's stream ends early or sends nothing for
    /// <see cref="UploadOptions.CompletionIdleTimeout"/>, the completion is made again, which waits for the run in progress or returns its outcome. When
    /// the service answers the completion with <see cref="UploadErrorCodes.Incomplete"/>, the chunks that its status lists as missing are read and sent
    /// again, and the upload is completed again, up to five times.
    /// </para>
    /// <para>
    /// Cancelling <paramref name="cancellationToken"/> cancels the upload: the upload either throws an <see cref="OperationCanceledException"/>, when it was
    /// cancelled, or returns the handler's result, when the handler finished first. Until a request that can start the handler is sent, which is the direct
    /// upload's request or the completion's, no handler can run, and the upload throws at once and is aborted as below, while the abort goes on. Once one was
    /// sent, the request in progress is stopped at once, and a <c>DELETE</c> request to <c>{path}/uploads/{id}</c> asks the service to cancel the upload. It
    /// is made again after a wait for as long as it fails as a request that is retried, each time with a timeout of 10 seconds, until the service answers it,
    /// for up to the retry timeout from the cancellation, after which its failure is thrown. When the service answers that it removed the upload, or that the
    /// upload doesn't exist (<c>204</c>), no handler runs on it, and the upload throws. When the upload's handler runs (<c>202</c>) or has finished
    /// (<c>409</c>, <see cref="UploadErrorCodes.Finished"/>), the completion is made, as without the cancellation, until it ends: with the handler's result,
    /// which is returned, or with <see cref="UploadErrorCodes.Cancelled"/>, <see cref="UploadErrorCodes.NotFound"/> or
    /// <see cref="UploadErrorCodes.Incomplete"/>, which the <see cref="OperationCanceledException"/> that is thrown has as its inner exception. Any other
    /// failure, such as the handler's error, is thrown as without the cancellation. There is no time limit: a handler that runs on after its token is
    /// cancelled holds the upload until it ends.
    /// </para>
    /// <para>
    /// When the upload is cancelled before a request that can start the handler was sent, the source fails to read, a progress report throws (see
    /// <see cref="UploadOptions.Progress"/>), or a request fails for another reason or for longer than the retry timeout, the upload is aborted with a
    /// <c>DELETE</c> request to <c>{path}/uploads/{id}</c>, which has a timeout of 10 seconds, and the exception is thrown: once the abort has ended for a
    /// failure, and right away for a cancellation, while the abort goes on. An upload that the service can't have created, because no request that creates
    /// it was sent or the service rejected it, one whose failure is a 401, 412 or 428, or a 404 with <see cref="UploadErrorCodes.NotFound"/>, is not
    /// aborted, and one whose abort fails is left for the service to remove when it expires.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="path"/> has a query string or a fragment, is not valid or leads outside the base address.
    /// </exception>
    /// <exception cref="IOException">The source failed to read, such as for a file that changed, or ended before its length.</exception>
    /// <exception cref="FormatException">The completion streamed no result or more than one, or a response of the service is not valid for the protocol.
    /// </exception>
    /// <exception cref="JsonException">The handler's result is <see langword="null"/>, or a response of the service is not valid JSON for its type, such as
    /// a missing or <see langword="null"/> value where the type does not allow one.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled and the handler didn't finish: before a request that
    /// can start the handler was sent, also when the upload failed for another reason at the same time, whose exception is then the inner exception, or
    /// after it, once the service has ended the upload without the handler finishing.</exception>
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
        options ??= DefaultUploadOptions;
        options.Freeze();

        return UploadAndReadResultAsync<TResult>(endpointPath, query.ToArray(), source, command, options, cancellationToken);
    }

    /// <summary>
    /// Uploads a file to an upload endpoint of the service, directly or in chunks, and returns the items that the endpoint's handler streams.
    /// </summary>
    /// <typeparam name="TItem">The type of the handler's items.</typeparam>
    /// <remarks>
    /// <para>
    /// The upload runs as <see cref="UploadAsync{TResult}"/> describes. It starts when the enumeration starts, and again for each enumeration, and a path
    /// that is not valid throws when the method is called. Null items are yielded as <see langword="null"/>, whether or not the item type is nullable.
    /// </para>
    /// <para>
    /// When the completion is made again, its items start with the latest item that the handler produced, which can be the last item already yielded.
    /// Ending the enumeration early stops reading the items without aborting the upload, and the handler runs on. Items that arrive after the upload was
    /// cancelled, while its completion is read until it ends, are yielded too.
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
        options ??= DefaultUploadOptions;
        options.Freeze();

        return RunUploadAsync<TItem>(endpointPath, query.ToArray(), source, command, options, false, cancellationToken);
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
        // The app's token stops the upload's requests through this one, and once a request that can start the handler was sent, the service is asked to
        // cancel the upload too (see CancelOnServiceAsync).
        using var cancellation = new UploadCancellation(cancellationToken);
        var token = cancellation.Token;
        var run = new UploadRun(endpointPath, query, source, options, cancellation);

        // Whether the next request is the direct upload of a file that fits in one chunk, which the service has not answered yet.
        bool isDirect = false;

        try
        {
            run.Command = SerializeUploadCommand(command);
            isDirect = await TryReadForDirectUploadAsync(run, token).ConfigureAwait(false);

            if (!isDirect)
                await CreateAndSendChunksAsync(run, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ExceptionDispatchInfo.Capture(await FailUploadAsync(run, ex).ConfigureAwait(false)).Throw();
        }

        while (true)
        {
            // A direct upload reports the processing once the service has its body.
            if (!isDirect)
                run.Progress.SetProcessing();

            if (run.Progress.ReportFailure is { } reportFailure)
                ExceptionDispatchInfo.Capture(await FailUploadAsync(run, reportFailure).ConfigureAwait(false)).Throw();

            Exception? failure = null;
            bool isItemLate = false;
            bool hasItems = false;
            int itemCount = 0;

            using (var watchdog = new CompletionWatchdog(run, token))
            {
                var stream = isDirect ? SendDirectAsync<TItem>(run, watchdog, watchdog.Token) : SendCompletionAsync<TItem>(run, watchdog, watchdog.Token);
                var items = stream.GetAsyncEnumerator(watchdog.Token);

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

                if (watchdog.HasExpired && !token.IsCancellationRequested)
                    failure = CreateCompletionIdleException(options.CompletionIdleTimeout, failure);

                failure ??= new OperationCanceledException(token);

                // Items and pings of a stream that broke off show that the handler runs, so the completion is made again as after a first failure. A stream
                // that ends with an error of the service, such as a handler that can't run at the moment, is no success, items or not, since the handler
                // that runs again starts over. Only the errors of a server that stops and of a store that can't be reached for a moment, which say nothing
                // about the handler, end the stream as if it broke off.
                bool isBrokenOff = failure is not ApiException || SaysNothingAboutTheHandler(failure);

                if (isBrokenOff && (hasItems || watchdog.WasActive))
                {
                    run.Retry.OnSuccess();
                    run.FailedCompletions = 0;
                }
            }

            try
            {
                bool isDirectFailure = isDirect && run.UploadPath is null;
                token.ThrowIfCancellationRequested();

                // A request that the failed report stopped fails with another exception.
                if (run.Progress.ReportFailure is { } failedReport)
                    ExceptionDispatchInfo.Capture(failedReport).Throw();

                if (isDirectFailure)
                {
                    isDirect = await RecoverDirectUploadAsync(run, failure, token).ConfigureAwait(false);
                    continue;
                }

                // Once the service has answered the direct upload with the upload's path, the upload goes on as one in chunks.
                isDirect = false;
                await RecoverCompletionAsync(run, failure, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (cancellation.IsRequested && !run.IsCancelSent)
            {
                // A request that can start the handler was sent, so the service is asked to cancel the upload, and its answer tells how the upload goes on:
                // it ends at once, or its completion tells how it ended.
                run.IsCancelSent = true;

                // A cancellation that fails for good throws its failure as is, without FailUploadAsync: how the upload ended is unknown, so it isn't
                // aborted, and the service removes it when it expires if it is still there.
                if (!await CancelOnServiceAsync(run).ConfigureAwait(false))
                    throw cancellation.CreateException(run.Progress.ReportFailure ?? ex);

                // The completion is made without the app's token, which is cancelled, with the upload's ID if the service hasn't answered with its path.
                token = CancellationToken.None;
                isDirect = false;

                if (run.UploadPath is null)
                    run.SetUploadPath(run.Id.ToString("D"));
            }
            catch (Exception ex)
            {
                ExceptionDispatchInfo.Capture(await FailUploadAsync(run, ex).ConfigureAwait(false)).Throw();
            }
        }
    }

    /// <summary>
    /// Creates the upload on the service and sends all its chunks.
    /// </summary>
    private async Task CreateAndSendChunksAsync(UploadRun run, CancellationToken cancellationToken)
    {
        await CreateUploadAsync(run, cancellationToken).ConfigureAwait(false);
        await SendChunksAsync(run, [.. Enumerable.Range(0, run.ChunkCount)], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the file into a buffer for its direct upload, if it can be uploaded directly: if it fits in one chunk of the size that the options ask for, and
    /// its creation fits in the request's header.
    /// </summary>
    /// <returns><see langword="true"/> if the file is uploaded directly; otherwise, <see langword="false"/>.</returns>
    private static async Task<bool> TryReadForDirectUploadAsync(UploadRun run, CancellationToken cancellationToken)
    {
        var source = run.Source;
        int maxLength = Math.Min(Math.Max(run.Options.ChunkSize ?? ChunkedUploadProtocol.DefaultChunkSize, ChunkedUploadProtocol.MinChunkSize),
            ChunkedUploadProtocol.MaxChunkSize);

        if (!run.Options.AllowsDirectUpload || source.Length > maxLength)
            return false;

        byte[] creation = JsonSerializer.SerializeToUtf8Bytes(run.CreateCommand(), UploadJsonContext.Default.CreateUploadCommand);
        string creationHeader = Base64UrlEncoder.Encode(creation);

        if (creationHeader.Length > ChunkedUploadProtocol.MaxCreationHeaderLength)
            return false;

        run.CreationHeader = creationHeader;

        int length = (int)source.Length;
        byte[] body = length is 0 ? [] : ArrayPool<byte>.Shared.Rent(length);

        try
        {
            await source.ReadExactlyAsync(0, body.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (length > 0)
                ArrayPool<byte>.Shared.Return(body);

            throw;
        }
        finally
        {
            try
            {
                await source.ReleaseAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                TraceSuppressed(ex, "upload source release");
            }
        }

        run.DirectBody = body;
        return true;
    }

    /// <summary>
    /// Sends the request of a direct upload, which creates the upload on the service, runs its handler and streams its items, when the enumeration starts.
    /// The chunk timeout applies until the response starts (see <see cref="SendChunkAsync"/>), and the completion's watchdog after that. The response
    /// starts with the upload's path, which the request sets in <see cref="UploadRun.UploadPath"/>, so that the upload goes on as one in chunks if the
    /// request fails after it.
    /// </summary>
    private async IAsyncEnumerable<TItem> SendDirectAsync<TItem>(
        UploadRun run, CompletionWatchdog watchdog, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        byte[] body = run.DirectBody!;
        int length = (int)run.Source.Length;

        // A progress report that throws stops the request, as it stops the chunks.
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, run.Progress.ReportFailedToken);
        using var inactivity = new InactivityTimeout(run.Options.ChunkTimeout, run.Retry.TimeProvider, requestCancellation.Token);
        var attempt = run.Progress.StartAttempt();
        watchdog.TryPause();

        var request = CreateStreamingUploadRequest(run, run.UploadsPath, ChunkedUploadProtocol.DirectSegment);
        request.Headers.TryAddWithoutValidation(ChunkedUploadProtocol.CreationHeaderName, run.CreationHeader);

        // The service can create the upload and start the handler once it has the whole file.
        if (!run.Cancellation.TryMarkHandlerMayRun())
            throw run.Cancellation.CreateException(null);

        run.IsCreationSent = true;

        HttpContent content = run.Options.CountsWrittenBytes ? new ChunkContent(body, length, attempt, inactivity) : new ByteArrayContent(body, 0, length);
        content.Headers.ContentType = new MediaTypeHeaderValue(ChunkedUploadProtocol.ChunkContentType);

        var items = SendStreamingAsync<TItem>(request, content, inactivity.Token).GetAsyncEnumerator(inactivity.Token);
        var callbacks = new StreamCallbacks(watchdog.OnActivity, response => OnDirectUploadResponse(run, response, attempt, inactivity, watchdog));

        try
        {
            while (true)
            {
                bool hasItem;
                UploadStreamCallbacks.Value = callbacks;

                try
                {
                    hasItem = await items.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (inactivity.HasExpired && run.UploadPath is null && !requestCancellation.IsCancellationRequested)
                {
                    throw CreateChunkIdleException(run.Options.ChunkTimeout, ex, "The direct upload's request");
                }
                finally
                {
                    UploadStreamCallbacks.Value = null;
                }

                if (!hasItem)
                    break;

                yield return items.Current;
            }
        }
        finally
        {
            // An attempt that the response ended counts the file's bytes already, and ending it again does nothing.
            attempt.End(0);
            await items.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Takes the start of the response of a direct upload: the service has the file, and the response has the upload's path, from which the upload goes on
    /// as one in chunks. The file's buffer goes back to the pool, since the service has read the whole body.
    /// </summary>
    /// <exception cref="FormatException">The response has no <c>Location</c> header with the upload's path, which the request then takes as a response of
    /// a service without direct uploads.</exception>
    private static void OnDirectUploadResponse(
        UploadRun run, HttpResponseMessage response, UploadProgressTracker.SendAttempt attempt, InactivityTimeout inactivity, CompletionWatchdog watchdog)
    {
        string location = response.Headers.Location?.OriginalString ?? string.Empty;
        string uploadId = Uri.UnescapeDataString(location.Substring(location.LastIndexOf('/') + 1));

        if (uploadId.Length is 0)
            throw new FormatException("The response of the direct upload has no Location header with the upload's path.");

        run.SetUploadPath(uploadId);
        inactivity.Stop();
        attempt.End((int)run.Source.Length);
        run.Retry.OnSuccess();
        run.ReturnDirectBody();
        run.Progress.SetProcessing();
        watchdog.Resume();
    }

    /// <summary>
    /// Gets a value indicating whether a response rejected the request of a direct upload: a 4xx other than a 408, which is of a body that came too slowly,
    /// for an upload that the request can have created.
    /// </summary>
    private static bool IsRejection(Exception failure)
    {
        return failure is ApiException {
            StatusCode: >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError and not HttpStatusCode.RequestTimeout
        };
    }

    /// <summary>
    /// Gets the upload ready to be made again after its direct upload failed before the service answered it with the upload's path: makes the request
    /// again after a wait, or creates the upload and sends its chunks if the direct request can't work. Throws the failure if the upload can't go on.
    /// </summary>
    /// <returns><see langword="true"/> if the direct upload is made again, or <see langword="false"/> if the upload goes on as one in chunks.</returns>
    private async Task<bool> RecoverDirectUploadAsync(UploadRun run, Exception failure, CancellationToken cancellationToken)
    {
        // A service at a limit has read the body before it answered, so its buffer goes back to the pool. The creation waits for the limit as long as the
        // retry timeout allows, without a body, and the file is then sent once.
        if (failure is ApiException { ErrorCode: UploadErrorCodes.LimitReached or UploadErrorCodes.SizeLimitReached or UploadErrorCodes.StorageFull })
        {
            run.ReturnDirectBody();
            await CreateAndSendChunksAsync(run, cancellationToken).ConfigureAwait(false);
            return false;
        }

        bool isNoResponse = failure is not (ApiException or FormatException);
        bool isSecondNoResponse = isNoResponse && run.HasFailedWithoutAResponse;
        run.HasFailedWithoutAResponse |= isNoResponse;

        // A rejection that isn't an error of the toolkit came from something other than the service's upload code: a service without direct uploads, or a
        // server or a proxy in front of the service.
        bool isForeignRejection = IsRejection(failure) && failure is ApiException { ErrorContent: not null };

        // A service whose largest chunk is smaller than the file reports it.
        bool isOverLargestChunk = failure is ContentTooLargeApiException { ErrorCode: UploadErrorCodes.DirectTooLarge };

        // A 431 can be in plain text too. A 429 of any format, such as from an app's rate limit, isn't made again with the file: the creation waits for its
        // Retry-After, without a body.
        bool isThrottledOrHeaderTooLarge = failure is ApiException { StatusCode: (HttpStatusCode)429 or (HttpStatusCode)431 };

        // A service without direct uploads can answer with a success of its own, such as the page of a fallback route, which has no upload's path.
        bool isNotTheStream = failure is FormatException;

        // These, and a server that closes the connection while the body is still being sent, after it has answered before the body arrived, go on with the
        // three requests, which have the same ID. A rejection that holds for them too is thrown from them.
        if (isForeignRejection || isOverLargestChunk || isThrottledOrHeaderTooLarge || isNotTheStream || isSecondNoResponse)
        {
            // The buffer is left to the garbage collector, since a handler of a request that sent it can still read it.
            run.DirectBody = null;
            await CreateAndSendChunksAsync(run, cancellationToken).ConfigureAwait(false);
            return false;
        }

        // A request that failed without a response is made again once, whatever it failed with, also an error of an app's override of a method, which the
        // creation then throws.
        var delay = isNoResponse ? run.Retry.GetRetryDelay(++run.FailedDirectUploads) : run.Retry.GetRetryDelay(failure, ++run.FailedDirectUploads);

        if (delay is not { } wait)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
            return false;
        }

        await run.Retry.DelayAsync(wait, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Creates the upload on the service.
    /// </summary>
    private async Task CreateUploadAsync(UploadRun run, CancellationToken cancellationToken)
    {
        var session = await SendUploadRequestAsync(run, CreateAsync, cancellationToken).ConfigureAwait(false);

        if (session.Id.Length is 0)
            throw new FormatException("The service created an upload without an ID.");

        run.SetUploadPath(session.Id);
        SetChunkSize(run, session.ChunkSize, "The service created an upload");

        async Task<UploadSession> CreateAsync()
        {
            run.IsCreationSent = true;

            try
            {
                return await SendForUploadJsonAsync(
                    CreateRequest(HttpMethod.Post, run.UploadsPath, run.Query),
                    JsonContent.Create(run.CreateCommand(), UploadJsonContext.Default.CreateUploadCommand),
                    UploadJsonContext.Default.UploadSession,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not (ApiException or FormatException))
            {
                run.HasFailedWithoutAResponse = true;
                throw;
            }
        }
    }

    /// <summary>
    /// Sets the chunk size of the upload that the service chose, which a created upload has, and which a direct upload gets from its status.
    /// </summary>
    private static void SetChunkSize(UploadRun run, int chunkSize, string subject)
    {
        if (chunkSize <= 0)
            throw new FormatException($"{subject} with a chunk size of {chunkSize} bytes.");

        // Each chunk that is being sent is held in a buffer of its size.
        if (chunkSize > ChunkedUploadProtocol.MaxChunkSize)
            throw new FormatException($"{subject} with a chunk size of {chunkSize} bytes, which is larger than the largest chunk size of 64 MiB.");

        long length = run.Source.Length;
        long chunkCount = (length / chunkSize) + (length % chunkSize > 0 ? 1 : 0);

        if (chunkCount > int.MaxValue)
            throw new FormatException($"{subject} with a chunk size of {chunkSize} bytes, which makes too many chunks.");

        run.ChunkSize = chunkSize;
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

        // A buffer goes back to the pool only once the service acknowledged its last request or closed the upload, which it answers after it has read the
        // whole body: a handler can still read the content of a request that failed.
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

                try
                {
                    await SendChunkAsync(run, index, buffer, length, cancellationToken).ConfigureAwait(false);
                }
                catch (ConflictApiException ex) when (ex.ErrorCode is UploadErrorCodes.Closed)
                {
                    // The upload's handler has started, such as for the direct upload of an earlier attempt, so no chunk is needed, and the completion
                    // attaches to the run or gets its outcome.
                    queue.Close();
                    bufferInUse = false;
                    return;
                }

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
                var failure = isIdle ? CreateChunkIdleException(run.Options.ChunkTimeout, ex, "The request of a chunk of the upload") : ex;

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
    /// Creates a <c>POST</c> of an upload, with its query string, whose streaming response has no timeout of its own. The path is already escaped, so it is
    /// passed as a string rather than interpolated.
    /// </summary>
    private HttpRequestMessage CreateStreamingUploadRequest(UploadRun run, string escapedPath, string segment)
    {
        var request = CreateRequest(HttpMethod.Post, escapedPath + "/" + segment, run.Query);
        SetRequestTimeout(request, Timeout.InfiniteTimeSpan);

        return request;
    }

    /// <summary>
    /// Sends the request that completes the upload when the enumeration starts, and yields the items of its streaming response. The request has no
    /// timeout of its own: the watchdog cancels it when its stream sends nothing for too long.
    /// </summary>
    private async IAsyncEnumerable<TItem> SendCompletionAsync<TItem>(
        UploadRun run, CompletionWatchdog watchdog, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!run.Cancellation.TryMarkHandlerMayRun())
            throw run.Cancellation.CreateException(null);

        var request = CreateStreamingUploadRequest(run, run.UploadPath!, ChunkedUploadProtocol.CompleteSegment);
        var items = SendStreamingAsync<TItem>(request, null, cancellationToken).GetAsyncEnumerator(cancellationToken);
        var callbacks = new StreamCallbacks(watchdog.OnActivity, null);

        try
        {
            while (true)
            {
                bool hasItem;

                UploadStreamCallbacks.Value = callbacks;

                try
                {
                    hasItem = await items.MoveNextAsync().ConfigureAwait(false);
                }
                finally
                {
                    UploadStreamCallbacks.Value = null;
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
    /// the handler: a server that stops, or a store that the server can't reach for a moment, which can be the short-lived store of the cancellations.
    /// </summary>
    private static bool SaysNothingAboutTheHandler(Exception failure)
    {
        return failure is ServiceUnavailableApiException {
            ErrorCode: UploadErrorCodes.ServerStopping or UploadErrorCodes.StoreUnavailable or UploadErrorCodes.CancellationUnavailable
        };
    }

    /// <summary>
    /// Gets the upload ready to be completed again after its completion failed: sends the chunks that the service lacks, or waits before a retry. Throws
    /// the failure if the completion can't be made again.
    /// </summary>
    private async Task RecoverCompletionAsync(UploadRun run, Exception failure, CancellationToken cancellationToken)
    {
        if (failure is ConflictApiException { ErrorCode: UploadErrorCodes.Incomplete })
        {
            // An upload that was cancelled ends: no handler runs on it.
            if (++run.IncompleteCompletions > MaxIncompleteCompletions || run.Cancellation.IsRequested)
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

        // A direct upload learns its chunk size from its status.
        if (run.ChunkSize is 0 && status.Length == run.Source.Length)
            SetChunkSize(run, status.ChunkSize, "The status of the upload describes an upload");

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
    /// Aborts the upload on the service after it failed or was cancelled, and gets the exception to throw. An upload that was cancelled before a request that
    /// can start the handler was sent throws an <see cref="OperationCanceledException"/>, whatever else failed at the same time. One that was cancelled after,
    /// whose cancellation the service answered, throws one when the service ended the upload without its handler finishing, and otherwise fails as one that
    /// was not cancelled does, without an abort.
    /// </summary>
    private async Task<Exception> FailUploadAsync(UploadRun run, Exception failure)
    {
        var cancellation = run.Cancellation;

        // An exception of the app's token is the upload's cancellation already.
        bool isCancellation = failure is OperationCanceledException exception && exception.CancellationToken == cancellation.AppToken;

        if (cancellation.IsRequested)
            return isCancellation ? failure : IsCancelledByTheService(failure) ? cancellation.CreateException(failure) : failure;

        if (!cancellation.IsCancelled)
        {
            await AbortUploadAsync(run, failure).ConfigureAwait(false);
            return failure;
        }

        // The cancellation doesn't wait for the abort, which goes on within its timeout and never throws.
        _ = AbortUploadAsync(run, failure);

        return isCancellation ? failure : cancellation.CreateException(failure);
    }

    /// <summary>
    /// Gets a value indicating whether the failure of an upload whose cancellation the service answered shows that its handler did not finish: the service
    /// ended the upload for the cancellation (<see cref="UploadErrorCodes.Cancelled"/>) or deleted it (<see cref="UploadErrorCodes.NotFound"/>), or it
    /// answered a completion with <see cref="UploadErrorCodes.Incomplete"/>, which runs no handler.
    /// </summary>
    private static bool IsCancelledByTheService(Exception failure) => failure switch {
        ConflictApiException { ErrorCode: UploadErrorCodes.Cancelled or UploadErrorCodes.Incomplete } => true,
        NotFoundApiException { ErrorCode: UploadErrorCodes.NotFound } => true,
        _ => false,
    };

    /// <summary>
    /// Asks the service to cancel the upload once the app's token was cancelled after a request that can start the handler was sent: sends
    /// <c>DELETE {path}/uploads/{id}</c> with the upload's ID, also when the service hasn't answered with the upload's path yet. A request that fails for a
    /// reason that may not last is made again after a wait, as the upload's requests are, each with a timeout of 10 seconds that the upload's own requests
    /// don't share, until the service answers it or no request succeeded for the retry timeout, which counts from the cancellation.
    /// </summary>
    /// <returns><see langword="false"/> if the service deleted the upload, or has none (<c>204</c>), so that no handler runs on it; or
    /// <see langword="true"/> if the upload's handler runs (<c>202</c>) or has finished (<c>409</c>, <see cref="UploadErrorCodes.Finished"/>), so that the
    /// upload's completion tells how it ended.</returns>
    /// <exception cref="Exception">The failure of the last request, which failed for good or for longer than the retry timeout.</exception>
    private async Task<bool> CancelOnServiceAsync(UploadRun run)
    {
        string path = run.CancelPath;
        run.Retry.OnSuccess();

        for (int failedAttempts = 0; ;)
        {
            try
            {
                var request = CreateRequest(HttpMethod.Delete, path);
                SetRequestTimeout(request, UploadAbortTimeout);

                var response = await SendAsync<HttpResponseMessage>(request, null, CancellationToken.None).ConfigureAwait(false);
                var status = response.StatusCode;
                DisposeResponse(response);

                // Only 202 is specified for a handler that runs; any other success is read as one, so that the completion tells how the upload ended.
                run.Retry.OnSuccess();
                return status is not HttpStatusCode.NoContent;
            }
            catch (ConflictApiException ex) when (ex.ErrorCode is UploadErrorCodes.Finished)
            {
                run.Retry.OnSuccess();
                return true;
            }
            catch (Exception ex)
            {
                if (run.Retry.GetRetryDelay(ex, ++failedAttempts) is not { } delay)
                    throw;

                await run.Retry.DelayAsync(delay, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Aborts the upload on the service after it failed, with <c>DELETE {path}/uploads/{id}</c>, unless the service can't have created it: no request that
    /// creates it was sent, or the service rejected the only ones, which all got a response. It isn't aborted either when the failure shows that the request
    /// can't succeed: a 401, 412 or 428, which the request would get as well, or a 404 with <see cref="UploadErrorCodes.NotFound"/>. A failure of the request
    /// is ignored, since the service removes the upload when it expires.
    /// </summary>
    private async Task AbortUploadAsync(UploadRun run, Exception failure)
    {
        bool mayExist = run.UploadPath is not null || (run.IsCreationSent && (run.HasFailedWithoutAResponse || !IsRejection(failure)));

        if (!mayExist || failure is UnauthorizedApiException or UserChangedApiException or UserRequiredApiException or
            NotFoundApiException { ErrorCode: UploadErrorCodes.NotFound })
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(UploadAbortTimeout);
            var request = CreateRequest(HttpMethod.Delete, run.CancelPath);
            SetRequestTimeout(request, UploadAbortTimeout);

            await SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The upload's failure is thrown instead, and the service removes the upload when it expires.
            TraceSuppressed(ex, "upload abort");
        }
    }

    /// <summary>
    /// Traces an exception that does not change how the upload goes on.
    /// </summary>
    private static void TraceSuppressed(Exception exception, string context)
    {
        if (Trace.Listeners.Count > 0)
            Trace.TraceWarning($"[Singulink.Net.Http.Api] Unexpected exception suppressed ({context}): {exception}");
    }

    /// <summary>
    /// Creates the exception of a request that sent nothing and got no response for the chunk timeout.
    /// </summary>
    /// <param name="timeout">The chunk timeout.</param>
    /// <param name="exception">The exception of the cancellation of the request.</param>
    /// <param name="request">The request, as the subject of the message, such as "The request of a chunk of the upload".</param>
    private static TaskCanceledException CreateChunkIdleException(TimeSpan timeout, Exception exception, string request)
    {
        double seconds = timeout.TotalSeconds;
        string message = string.Format(
            CultureInfo.InvariantCulture,
            "{0} sent nothing and got no response for {1} {2}.",
            request,
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
    /// The callbacks of a streaming response of an upload (see <see cref="UploadStreamCallbacks"/>).
    /// </summary>
    /// <param name="OnActivity">Called whenever a read of the response returns bytes, keep-alive pings included.</param>
    /// <param name="OnResponse">Called with the response once its headers have arrived, before its records are read, or <see langword="null"/> for none. A
    /// direct upload learns the upload's path from it.</param>
    private sealed record StreamCallbacks(Action OnActivity, Action<HttpResponseMessage>? OnResponse);

    /// <summary>
    /// The state of one run of an upload.
    /// </summary>
    private sealed class UploadRun
    {
        public UploadRun(
            string endpointPath, (string Name, object? Value)[] query, UploadSource source, UploadOptions options, UploadCancellation cancellation)
        {
            UploadsPath = endpointPath.Length > 0 ? $"{endpointPath}/{ChunkedUploadProtocol.UploadsSegment}" : ChunkedUploadProtocol.UploadsSegment;
            Query = query;
            Source = source;
            Options = options;
            Cancellation = cancellation;
            Retry = new UploadRetryPolicy(options.RetryTimeout, options.TimeProvider ?? TimeProvider.System);
            Progress = new UploadProgressTracker(options.Progress, source.Length);
        }

        public UploadCancellation Cancellation { get; }

        /// <summary>
        /// Gets the path that creates uploads: <c>{path}/uploads</c>.
        /// </summary>
        public string UploadsPath { get; }

        /// <summary>
        /// Gets the ID of the upload, which the client chooses: random, and the same for the direct upload, its creation and every request made again, so that
        /// they are all for one upload.
        /// </summary>
        public Guid Id { get; } = Guid.NewGuid();

        public (string Name, object? Value)[] Query { get; }

        /// <summary>
        /// Gets or sets the app's command as JSON, or <see langword="null"/> for none.
        /// </summary>
        public JsonElement? Command { get; set; }

        /// <summary>
        /// Gets or sets the creation's header of the direct upload: its JSON in UTF-8, base64url-encoded.
        /// </summary>
        public string? CreationHeader { get; set; }

        /// <summary>
        /// Gets or sets the buffer with the file, from the pool unless it is empty, which the direct upload sends, or <see langword="null"/> once the service
        /// has it or the upload is sent in chunks.
        /// </summary>
        public byte[]? DirectBody { get; set; }

        public UploadSource Source { get; }

        public UploadOptions Options { get; }

        public UploadRetryPolicy Retry { get; }

        public UploadProgressTracker Progress { get; }

        /// <summary>
        /// Gets the path of the upload once the service answered with it: <c>{path}/uploads/{id}</c>, with the ID that the service answered with, which is
        /// <see cref="Id"/> unless the service is of an older version, which chooses its own.
        /// </summary>
        public string? UploadPath { get; private set; }

        /// <summary>
        /// Gets the path that cancels or aborts the upload: <see cref="UploadPath"/>, or the path of <see cref="Id"/> if the service hasn't answered with it.
        /// </summary>
        public string CancelPath => UploadPath ?? GetPath(Id.ToString("D"));

        /// <summary>
        /// Gets or sets a value indicating whether a request that can create the upload was sent: its creation or the direct upload.
        /// </summary>
        public bool IsCreationSent { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the service was asked to cancel the upload, and answered (see <see cref="CancelOnServiceAsync"/>).
        /// </summary>
        public bool IsCancelSent { get; set; }

        public int ChunkSize { get; set; }

        public int ChunkCount { get; set; }

        /// <summary>
        /// Gets or sets how many completions in a row failed without their stream delivering anything.
        /// </summary>
        public int FailedCompletions { get; set; }

        /// <summary>
        /// Gets or sets how many requests of the direct upload failed before the service answered one.
        /// </summary>
        public int FailedDirectUploads { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether a request that can create the upload, its creation or the direct upload, failed without a response of the
        /// service, such as of a connection that failed, so that the service may have created the upload.
        /// </summary>
        public bool HasFailedWithoutAResponse { get; set; }

        /// <summary>
        /// Gets or sets how many completions the service answered with <see cref="UploadErrorCodes.Incomplete"/>.
        /// </summary>
        public int IncompleteCompletions { get; set; }

        public int GetChunkLength(int index) => (int)Math.Min(ChunkSize, Source.Length - ((long)index * ChunkSize));

        public void SetUploadPath(string uploadId) => UploadPath = GetPath(uploadId);

        /// <summary>
        /// Gets the path of an upload with the specified ID: <c>{path}/uploads/{id}</c>.
        /// </summary>
        public string GetPath(string uploadId) => $"{UploadsPath}/{Uri.EscapeDataString(uploadId)}";

        /// <summary>
        /// Creates the upload's creation, which the creation's body has, or the header of the direct upload.
        /// </summary>
        public CreateUploadCommand CreateCommand()
        {
            return new CreateUploadCommand(Source.FileName, Source.Length, Source.ContentType, Options.ChunkSize, Command, id: Id);
        }

        /// <summary>
        /// Returns the buffer of the direct upload to the pool, once the service has read the whole body.
        /// </summary>
        public void ReturnDirectBody()
        {
            if (DirectBody is { Length: > 0 } body)
                ArrayPool<byte>.Shared.Return(body);

            DirectBody = null;
        }
    }

    /// <summary>
    /// The cancellation of an upload by the app's token, which stops the upload's requests at once (see <see cref="Token"/>). Whether a request that can start
    /// the handler was sent before the app's token is cancelled decides how the upload ends: if one was, the service is asked to cancel the upload (see
    /// <see cref="CancelOnServiceAsync"/>), and its answer tells how the upload goes on; if not, no handler can run, and the upload ends at once. The decision
    /// is made once, by whichever sees the app's token cancelled first: its callback or a member of this class.
    /// </summary>
    private sealed class UploadCancellation : IDisposable
    {
#if NET
        private readonly Lock _lock = new();
#else
        private readonly object _lock = new();
#endif

        private readonly CancellationTokenSource _requests = new();
        private readonly CancellationTokenRegistration _registration;
        private bool _mayRunHandler;
        private State _state;

        public UploadCancellation(CancellationToken appToken)
        {
            AppToken = appToken;
            _registration = appToken.Register(static state => ((UploadCancellation)state!).Decide(), this);
        }

        private enum State
        {
            None,
            Cancelled,
            Requested,
        }

        /// <summary>
        /// Gets the app's token of the upload.
        /// </summary>
        public CancellationToken AppToken { get; }

        /// <summary>
        /// Gets the token of the upload's requests, which the app's token cancels.
        /// </summary>
        public CancellationToken Token => _requests.Token;

        /// <summary>
        /// Gets a value indicating whether the app's token was cancelled before a request that can start the handler was sent.
        /// </summary>
        public bool IsCancelled => Decide() is State.Cancelled;

        /// <summary>
        /// Gets a value indicating whether the app's token was cancelled after a request that can start the handler was sent.
        /// </summary>
        public bool IsRequested => Decide() is State.Requested;

        /// <summary>
        /// Marks that a request that can start the handler is about to be sent, unless the app's token cancelled the upload before. The mark is never
        /// removed.
        /// </summary>
        /// <returns><see langword="true"/> if the request can be sent; otherwise, <see langword="false"/>.</returns>
        public bool TryMarkHandlerMayRun()
        {
            State state;

            lock (_lock)
            {
                state = DecideLocked();

                if (state is State.None)
                    _mayRunHandler = true;
            }

            Apply(state);
            return state is not State.Cancelled;
        }

        /// <summary>
        /// Creates the exception of an upload that was cancelled, with the failure that ended it, if any, as the inner exception, but not the cancellation
        /// of the upload's requests by the app's token.
        /// </summary>
        public OperationCanceledException CreateException(Exception? failure)
        {
            if (failure is OperationCanceledException { CancellationToken: var token } && token == Token)
                failure = null;

            return new OperationCanceledException("The upload was cancelled.", failure, AppToken);
        }

        public void Dispose()
        {
            // Waits for the callback if it runs. The token source is only cancelled, not disposed, so that the requests that may still run, such as an
            // abort, can read their tokens.
            _registration.Dispose();
        }

        private State Decide()
        {
            State state;

            lock (_lock)
                state = DecideLocked();

            Apply(state);
            return state;
        }

        private State DecideLocked()
        {
            if (_state is State.None && AppToken.IsCancellationRequested)
                _state = _mayRunHandler ? State.Requested : State.Cancelled;

            return _state;
        }

        private void Apply(State state)
        {
            if (state is not State.None)
                _requests.Cancel();
        }
    }

    /// <summary>
    /// The chunks that the workers of a <see cref="SendChunksAsync(UploadRun, List{int}, CancellationToken)"/> take, in order, and the first failure of
    /// a worker.
    /// </summary>
    private sealed class ChunkQueue(List<int> chunks)
    {
        private int _next;
        private Exception? _failure;
        private volatile bool _isClosed;

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
            if (!_isClosed && _next < chunks.Count)
            {
                index = chunks[_next++];
                return true;
            }

            index = -1;
            return false;
        }

        public void SetFailure(Exception exception) => Interlocked.CompareExchange(ref _failure, exception, null);

        /// <summary>
        /// Leaves the chunks that are not taken yet, which the upload doesn't need.
        /// </summary>
        public void Close() => _isClosed = true;
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
