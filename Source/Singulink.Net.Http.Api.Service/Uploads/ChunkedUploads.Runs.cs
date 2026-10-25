using System.Net;
using System.Text.Json;

namespace Singulink.Net.Http.Api.Service.Uploads;

/// <content>
/// The completion of uploads: the runs of their handlers, and the completion requests that run them, attach to them or wait for their outcomes.
/// </content>
internal sealed partial class ChunkedUploads
{
    // A run's claim lasts this long unless it is renewed, which happens every quarter of it. Azure blob leases last at most a minute.
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RenewInterval = LeaseDuration / 4;

    // How often a completion request checks on a run on another server.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    // The retry time of a completion request that the application's shutdown ends, or that arrives while the application stops.
    private static readonly TimeSpan StoppingRetryAfter = TimeSpan.FromSeconds(1);

    // The longest delay of a timer.
    private static readonly TimeSpan MaxTimerDelay = TimeSpan.FromMilliseconds(int.MaxValue);

    // How often a claim that could not be released is released again, and for how long.
    private static readonly TimeSpan ReleaseRetryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxReleaseRetryTime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Completes an upload: <c>POST {path}/uploads/{id}/complete</c>. The first completion runs the handler, and the request streams its items. A later one
    /// attaches to the run, or gets the outcome once the run has ended.
    /// </summary>
    public async Task CompleteAsync(HttpContext context, UploadEndpoint endpoint)
    {
        string userId = await _user.GetUserIdAsync(context);
        var clientAborted = context.RequestAborted;
        var serializerOptions = GetSerializerOptions(context);
        using var writer = new CompletionWriter(
            context, serializerOptions.Encoder, clientAborted, endpoint.PingInterval ?? _options.CompletionPingInterval, _timeProvider);

        try
        {
            while (!writer.IsClientGone)
            {
                var found = await FindAsync(context, endpoint, 3, userId, clientAborted);

                if (found is not var (upload, metadata))
                {
                    await writer.FailAsync(UploadErrors.NotFound());
                    return;
                }

                // The handler binds the query string that the validator bound.
                if (!QueriesEqual(metadata.Query, GetQuery(context)))
                {
                    await writer.FailAsync(UploadErrors.QueryMismatch());
                    return;
                }

                var record = upload.Record;

                if (upload.Outcome is { } outcome)
                {
                    await WriteOutcomeAsync(writer, record, outcome);
                    return;
                }

                if (_releases.ContainsKey(record.Id))
                {
                    await writer.FailAsync(UploadErrors.StoreUnavailable(GuardedUploadStore.RetryAfter));
                    return;
                }

                if (_runs.TryGetValue(record.Id, out var localRun))
                {
                    if (await ViewRunAsync(localRun, writer, isRunner: false))
                        return;

                    continue;
                }

                if (upload.RunState is UploadRunState.Lapsed)
                {
                    // An outcome that another server is storing, or that a server left partly written, keeps the outcome of an interrupted upload from
                    // being stored, and the request looks again a moment later.
                    if ((await InterruptAsync(upload))?.Outcome is null)
                        await PauseAsync(writer);

                    continue;
                }

                if (upload.RunState is UploadRunState.Running)
                {
                    if (await WaitForOtherServerAsync(writer, record))
                        return;

                    continue;
                }

                var received = await _store.GetReceivedChunksAsync(record, clientAborted);

                if (GetMissingChunks(received, record.ChunkCount).Count > 0)
                {
                    await writer.FailAsync(UploadErrors.Incomplete());
                    return;
                }

                // A run that starts while the application stops would be stopped right away, which ends the upload as interrupted.
                if (_stopping.IsCancellationRequested)
                {
                    await writer.FailAsync(UploadErrors.ServerStopping(StoppingRetryAfter));
                    return;
                }

                if (await _store.TryClaimRunAsync(record, LeaseDuration, CancellationToken.None) is not { } claim)
                {
                    // A request on this server claimed the run in the meantime, which the request attaches to. Otherwise another server claimed it, or a
                    // chunk is being written, and the request waits a moment before it looks again.
                    if (!_runs.ContainsKey(record.Id) && await WaitForOtherServerAsync(writer, record))
                        return;

                    continue;
                }

                long claimedTimestamp = _timeProvider.GetTimestamp();

                // A write of a chunk that failed after the chunks were checked left the chunk missing. Once the run is claimed, chunks no longer change.
                if (GetMissingChunks(await _store.GetReceivedChunksAsync(record, clientAborted), record.ChunkCount).Count > 0)
                {
                    await ReleaseClaimAsync(record, claim, claimedTimestamp);
                    await writer.FailAsync(UploadErrors.Incomplete());
                    return;
                }

                if (_stopping.IsCancellationRequested)
                {
                    await ReleaseClaimAsync(record, claim, claimedTimestamp);
                    await writer.FailAsync(UploadErrors.ServerStopping(StoppingRetryAfter));
                    return;
                }

                await RunAsync(context, endpoint, record, metadata, claim, writer, serializerOptions);
                return;
            }
        }
        catch (ApiException ex) when (writer.HasStarted && !writer.IsClientGone)
        {
            // An error after the stream started, such as of a store that can't be reached while the request waits for a run on another server, ends the
            // stream with an error record, which the app's exception handler maps as any error of the service.
            await writer.FailAsync(ex);
        }
        finally
        {
            context.RequestAborted = clientAborted;
        }
    }

    private static StreamErrorInfo ToStreamError(ApiException exception) => ToStreamError(ResponseExceptionInfo.FromApiException(exception));

    private static StreamErrorInfo ToStreamError(ResponseExceptionInfo info) => new() {
        Status = info.StatusCode,
        Code = info.ErrorCode.Length > 0 ? info.ErrorCode : null,
        Message = info.Message,
        ReferenceId = info.ReferenceId?.ToString("D"),
        RetryAfter = info.RetryAfterSeconds,
    };

    private async ValueTask WriteOutcomeAsync(CompletionWriter writer, UploadRecord record, string outcome)
    {
        if (UploadOutcome.Read(outcome) is { } uploadOutcome)
        {
            await writer.WriteOutcomeAsync(uploadOutcome, writeItem: true);
            return;
        }

        _logger.LogError("The outcome of upload {UploadId} cannot be read.", record.Id);
        await writer.FailAsync(UploadErrors.OutcomeUnreadable());
    }

    /// <summary>
    /// Waits while the upload's handler runs on another server, with ping records, until the run's state changes. Checks the state every second, so for an
    /// upload whose run is not claimed, it waits a second.
    /// </summary>
    /// <returns><see langword="true"/> if the request is done, because the client is gone or the application is stopping, or <see langword="false"/> if
    /// the run's state changed.</returns>
    private async Task<bool> WaitForOtherServerAsync(CompletionWriter writer, UploadRecord record)
    {
        long lastPing = _timeProvider.GetTimestamp();
        long lastPoll = lastPing;

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(writer.ClientAborted, _stopping);

        try
        {
            while (true)
            {
                // Pings go out on time, so that the response starts within the ping interval, whatever the poll interval.
                var untilPing = writer.PingInterval - _timeProvider.GetElapsedTime(lastPing);
                var untilPoll = PollInterval - _timeProvider.GetElapsedTime(lastPoll);
                var delay = untilPing < untilPoll ? untilPing : untilPoll;

                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, _timeProvider, cancellation.Token);

                if (_timeProvider.GetElapsedTime(lastPing) >= writer.PingInterval)
                {
                    await writer.WritePingAsync();
                    lastPing = _timeProvider.GetTimestamp();
                }

                if (_timeProvider.GetElapsedTime(lastPoll) >= PollInterval)
                {
                    var upload = await _store.GetAsync(record.UserId, record.Id, cancellation.Token);
                    lastPoll = _timeProvider.GetTimestamp();

                    if (upload is null || upload.Outcome is not null || upload.RunState is not UploadRunState.Running || _runs.ContainsKey(record.Id))
                        return false;
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested && !writer.ClientAborted.IsCancellationRequested)
        {
            // The client gets the outcome from another server.
            await writer.FailAsync(UploadErrors.ServerStopping(StoppingRetryAfter));
        }
        catch (OperationCanceledException) when (writer.ClientAborted.IsCancellationRequested)
        {
        }

        return true;
    }

    /// <summary>
    /// Streams a run of the handler on this server to a completion request.
    /// </summary>
    /// <returns><see langword="true"/> if the request is done, or <see langword="false"/> if the run ended before the handler was called and the request
    /// starts again.</returns>
    private async Task<bool> ViewRunAsync(UploadRun run, CompletionWriter writer, bool isRunner)
    {
        using var subscription = run.Subscribe();

        if (subscription.End is { } end)
            return await WriteEndAsync(writer, end, hasItems: false, isRunner);

        if (subscription.LatestItem is { } latestItem)
            await writer.WriteItemAsync(latestItem);

        while (await writer.ReadAsync(subscription.Events!) is { } runEvent)
        {
            if (runEvent.Item is { } item)
                await writer.WriteItemAsync(item);
            else
                return await WriteEndAsync(writer, runEvent.End!, hasItems: true, isRunner);
        }

        return true;
    }

    /// <summary>
    /// Writes how a run ended to a completion request, with the outcome's item unless the request has received the run's items.
    /// </summary>
    /// <returns><see langword="true"/> if the request is done, or <see langword="false"/> if the run ended before the handler was called and the request
    /// starts again.</returns>
    private static async Task<bool> WriteEndAsync(CompletionWriter writer, RunEnd end, bool hasItems, bool isRunner)
    {
        // The request that ran the handler reports its error as any endpoint does, which includes an error of its parameters.
        if (isRunner && end.RunnerError is { } runnerError)
        {
            await writer.FailMappedAsync(runnerError);
            return true;
        }

        if (end.Outcome is { } outcome)
        {
            await writer.WriteOutcomeAsync(outcome, writeItem: !hasItems);
            return true;
        }

        if (end.RetryError is { } retryError)
        {
            await writer.FailAsync(retryError);
            return true;
        }

        if (end.IsAborted)
        {
            await writer.FailAsync(UploadErrors.NotFound());
            return true;
        }

        // The run ended before the handler was called, and the upload can be run again.
        return false;
    }

    /// <summary>
    /// Runs the upload's handler in the completion request that claimed its run, streams the run to the request, and keeps the request open until the run
    /// has ended, also after the client has disconnected.
    /// </summary>
    private async Task RunAsync(
        HttpContext context,
        UploadEndpoint endpoint,
        UploadRecord record,
        UploadMetadata metadata,
        IUploadRunClaim claim,
        CompletionWriter writer,
        JsonSerializerOptions serializerOptions)
    {
        using var run = new UploadRun(claim, _stopping, _options.IdleExpiry, _timeProvider);
        _runs[record.Id] = run;

        // The handler's cancellation token, and the request's token that its services see, are the run's: the client disconnecting does not stop the
        // handler, so that the client gets its outcome when it comes back.
        context.RequestAborted = run.Token;

        // The request attaches to the run before the handler starts, so that it gets all of the handler's items.
        var view = ViewRunAsync(run, writer, isRunner: true);
        var driver = Task.Run(() => DriveAsync(context, endpoint, record, metadata, run, serializerOptions));

        try
        {
            await driver;
        }
        finally
        {
            await view;
        }
    }

    /// <summary>
    /// Runs the upload's handler and ends the run with its outcome. The run always ends, also when storing its outcome or something else fails, so that
    /// the completion requests attached to it end. A handler that runs on after its time limit keeps the request open until it finishes, since it still
    /// uses the request's services and context.
    /// </summary>
    private async Task DriveAsync(
        HttpContext context, UploadEndpoint endpoint, UploadRecord record, UploadMetadata metadata, UploadRun run, JsonSerializerOptions serializerOptions)
    {
        RunEnd? end = null;
        Task? lateHandler = null;

        try
        {
            (end, lateHandler) = await RunHandlerAsync(context, endpoint, record, metadata, run, serializerOptions);

            try
            {
                end = await StoreEndAsync(record, run, end);
            }
            catch (Exception ex)
            {
                // The completion requests attached to the run still get its end, and the claim lapses, which ends the upload as interrupted.
                _logger.LogError(ex, "The end of the run of the handler of upload {UploadId} could not be stored.", record.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The run of the handler of upload {UploadId} failed. Its claim lapses, which ends the upload as interrupted.", record.Id);
        }
        finally
        {
            run.End(end ?? new RunEnd { Outcome = UploadOutcome.Failure(UploadErrors.Interrupted()) });
            _runs.TryRemove(new KeyValuePair<string, UploadRun>(record.Id, run));
        }

        if (lateHandler is not null)
            await lateHandler;
    }

    /// <summary>
    /// Runs the upload's handler while the claim of its run is renewed. A handler that is still running a lease after its time limit, although its
    /// cancellation token was cancelled at the limit, is left to finish on its own, and the run ends as interrupted.
    /// </summary>
    /// <returns>How the run ended, and the task that waits for a handler that runs on after it, if one does.</returns>
    private async Task<(RunEnd End, Task? LateHandler)> RunHandlerAsync(
        HttpContext context, UploadEndpoint endpoint, UploadRecord record, UploadMetadata metadata, UploadRun run, JsonSerializerOptions serializerOptions)
    {
        var renewals = RenewAsync(run, record);

        try
        {
            var handler = InvokeHandlerAsync(context, endpoint, record, metadata, run, serializerOptions);
            var deadline = Task.Delay(Min(_options.IdleExpiry + LeaseDuration, MaxTimerDelay), _timeProvider, run.RenewalToken);

            if (await Task.WhenAny(handler, deadline) != handler)
            {
                _logger.LogError(
                    "The handler of upload {UploadId} did not stop when it reached its time limit, so the upload is ended as interrupted while it runs on.",
                    record.Id);

                return (new RunEnd { Outcome = UploadOutcome.Failure(UploadErrors.Interrupted()) }, ObserveLateHandlerAsync(handler, record));
            }

            return (await handler, null);
        }
        catch (Exception ex)
        {
            // The handler could not be run, such as when the store cannot provide the file, so the upload is left with all its chunks.
            var error = await ResolveAsync(context, ex, record);
            return (new RunEnd { RetryError = ToStreamError(error), RunnerError = error }, null);
        }
        finally
        {
            run.StopRenewing();
            await renewals;
        }

        static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
    }

    /// <summary>
    /// Waits a poll interval before a completion request looks at its upload again, or until the client is gone.
    /// </summary>
    private async Task PauseAsync(CompletionWriter writer)
    {
        try
        {
            await Task.Delay(PollInterval, _timeProvider, writer.ClientAborted);
        }
        catch (OperationCanceledException) when (writer.ClientAborted.IsCancellationRequested)
        {
            // The request ends.
        }
    }

    /// <summary>
    /// Releases the claim of a run that did not take place or can take place again. A claim whose release fails, such as while another process has it open
    /// or a share can't be reached for a moment, would lapse and end the upload as interrupted. It is released again in the background every 2 seconds
    /// instead, and renewed meanwhile, for up to 10 minutes or until the application stops, and this server answers the upload's chunks and completions
    /// with a <c>503</c> (<c>upload-store-unavailable</c>) meanwhile, which the client retries. Other servers take the claim for a running run. A claim
    /// whose renewal fails too, as the directory store's does in those cases, lapses once its lease runs out, about 30 to 45 seconds after the run.
    /// </summary>
    /// <param name="record">The record of the run's upload.</param>
    /// <param name="claim">The claim of the run.</param>
    /// <param name="renewedTimestamp">The timestamp of the time provider from after the claim was last made or renewed.</param>
    private async ValueTask ReleaseClaimAsync(UploadRecord record, IUploadRunClaim claim, long renewedTimestamp)
    {
        try
        {
            await claim.ReleaseAsync(CancellationToken.None);
        }
        catch (ServiceUnavailableApiException ex) when (ex.ErrorCode is UploadErrorCodes.StoreUnavailable)
        {
            var release = new object();
            _releases[record.Id] = release;
            _ = KeepReleasingAsync(record, claim, renewedTimestamp, release);
        }
    }

    /// <summary>
    /// Releases a claim that could not be released (see <see cref="ReleaseClaimAsync"/>) again at an interval, renewing it as a run does meanwhile, until the
    /// release succeeds, the claim is gone or has lapsed, 10 minutes have passed, or the application stops. The store's failures are logged once, with
    /// the first release, and the last one when the claim is given up.
    /// </summary>
    private async Task KeepReleasingAsync(UploadRecord record, IUploadRunClaim guardedClaim, long renewedTimestamp, object release)
    {
        var claim = GuardedUploadStore.Unwrap(guardedClaim);
        long started = _timeProvider.GetTimestamp();
        long renewed = renewedTimestamp;
        Exception? failure = null;

        try
        {
            while (_timeProvider.GetElapsedTime(started) < MaxReleaseRetryTime)
            {
                // A claim that could not be renewed for its lease has lapsed, and the next request for the upload ends it as interrupted.
                if (_timeProvider.GetElapsedTime(renewed) >= LeaseDuration)
                {
                    _logger.LogError(failure, "The claim of the run of upload {UploadId} could not be released or renewed, so it lapsed.", record.Id);
                    return;
                }

                await Task.Delay(ReleaseRetryInterval, _timeProvider, _stopping);

                try
                {
                    await claim.ReleaseAsync(CancellationToken.None);
                    return;
                }
                catch (Exception ex) when (GuardedUploadStore.IsStoreFailure(ex, CancellationToken.None))
                {
                    failure = ex;
                }

                if (_timeProvider.GetElapsedTime(renewed) >= RenewInterval)
                {
                    try
                    {
                        // A claim that is gone, such as of an upload that was deleted meanwhile, needs no release.
                        if (!await claim.RenewAsync(CancellationToken.None))
                            return;

                        renewed = _timeProvider.GetTimestamp();
                    }
                    catch (Exception ex) when (GuardedUploadStore.IsStoreFailure(ex, CancellationToken.None))
                    {
                        failure = ex;
                    }
                }
            }

            _logger.LogError(
                failure,
                "The claim of the run of upload {UploadId} could not be released for 10 minutes, so it lapses, which ends the upload as interrupted.",
                record.Id);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            _logger.LogWarning(
                failure, "The claim of the run of upload {UploadId} could not be released before the application stopped, so it lapses.", record.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "The claim of the run of upload {UploadId} could not be released, so it lapses, which ends the upload as interrupted.", record.Id);
        }
        finally
        {
            _releases.TryRemove(new KeyValuePair<string, object>(record.Id, release));
        }
    }

    /// <summary>
    /// Waits for a handler that runs on after its run ended, and logs that it finished.
    /// </summary>
    private async Task ObserveLateHandlerAsync(Task<RunEnd> handler, UploadRecord record)
    {
        try
        {
            await handler;
            _logger.LogWarning("The handler of upload {UploadId} finished after the upload was ended as interrupted.", record.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The handler of upload {UploadId} failed after the upload was ended as interrupted.", record.Id);
        }
    }

    private async Task<RunEnd> InvokeHandlerAsync(
        HttpContext context, UploadEndpoint endpoint, UploadRecord record, UploadMetadata metadata, UploadRun run, JsonSerializerOptions serializerOptions)
    {
        UploadContent content;

        try
        {
            content = await _store.GetContentAsync(record, run.Token);
        }
        catch (Exception ex) when (run.Token.IsCancellationRequested)
        {
            return GetCancelledEnd(run, record, ex);
        }

        var itemTypeInfo = serializerOptions.GetTypeInfo(endpoint.ItemType);
        var file = new UploadedFile(record.Id, metadata.FileName, record.Length, metadata.ContentType, content);
        new UploadedFileBinding(file, content, metadata.Command, serializerOptions).Set(context);
        var invocation = HandlerInvocation.Start(context);

        try
        {
            await endpoint.Handler(context);

            if (endpoint.IsStreaming)
            {
                var items = invocation.Result as IAsyncEnumerable<object?> ??
                    throw new InvalidOperationException($"The handler of upload endpoint '{metadata.Endpoint}' returned null instead of its items.");

                await foreach (object? item in items.WithCancellation(run.Token))
                    run.Publish(JsonSerializer.SerializeToUtf8Bytes(item, itemTypeInfo));
            }
            else
            {
                run.Publish(JsonSerializer.SerializeToUtf8Bytes(invocation.Result, itemTypeInfo));
            }

            return new RunEnd { Outcome = UploadOutcome.Success(run.LatestItem) };
        }
        catch (Exception ex) when (!invocation.IsInvoked)
        {
            // The parameters could not be bound, so the handler did not run.
            return new RunEnd { IsNotStarted = true, RunnerError = await ResolveAsync(context, ex, record) };
        }
        catch (Exception ex) when (run.Token.IsCancellationRequested)
        {
            return GetCancelledEnd(run, record, ex);
        }
        catch (Exception ex)
        {
            var error = await ResolveAsync(context, ex, record);

            // A handler that cannot run at the moment leaves the upload with all its chunks, so that the next completion runs it again.
            if (error.StatusCode is HttpStatusCode.ServiceUnavailable)
                return new RunEnd { RetryError = ToStreamError(error), RunnerError = error };

            return new RunEnd { Outcome = UploadOutcome.Failure(ToStreamError(error)), RunnerError = error };
        }
    }

    /// <summary>
    /// Maps an exception of the handler, or of the binding of its parameters, as the request's exceptions are mapped (see
    /// <see cref="IApiExceptionHandler"/>). An exception that is not mapped is logged with a new reference ID and becomes a
    /// <see cref="ServerErrorApiException"/> with it, since every completion request for the upload gets it as the upload's outcome.
    /// </summary>
    private async ValueTask<ApiException> ResolveAsync(HttpContext context, Exception exception, UploadRecord record)
    {
        try
        {
            if (await ApiExceptionHandling.ResolveAsync(context, exception) is { } mappedException)
                return mappedException;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The API exception handler failed to map an exception of the handler of upload {UploadId}.", record.Id);

            if (exception is ApiException apiException)
                return apiException;
        }

        var referenceId = Guid.CreateVersion7();

        _logger.LogError(
            exception, "Unhandled exception while running the handler of upload {UploadId} (reference ID: {ReferenceId}).", record.Id, referenceId);

        string message = _isDevelopment
            ? ResponseExceptionInfo.FromException(exception, developmentMode: true).Message
            : $"An unexpected error has occurred and has been logged. Reference ID: {referenceId}.";

        return new ServerErrorApiException(message, exception) { ReferenceId = referenceId };
    }

    private RunEnd GetCancelledEnd(UploadRun run, UploadRecord record, Exception exception)
    {
        var reason = run.CancelReason;

        if (reason is RunCancelReason.Aborted)
            return new RunEnd { IsAborted = true };

        // An orderly shutdown stops the handler through its token, so its exception tells nothing more.
        if (reason is RunCancelReason.Stopping)
        {
            _logger.LogWarning(
                "The handler of upload {UploadId} was stopped by the application's shutdown before it finished, so the upload is ended as interrupted.",
                record.Id);
        }
        else
        {
            _logger.LogWarning(
                exception,
                "The handler of upload {UploadId} was stopped ({Reason}) before it finished, so the upload is ended as interrupted.",
                record.Id,
                reason);
        }

        return new RunEnd { Outcome = UploadOutcome.Failure(UploadErrors.Interrupted()) };
    }

    /// <summary>
    /// Stores how a run ended: releases the claim for a run that can run again, deletes an aborted upload, or stores the outcome.
    /// </summary>
    /// <returns>How the run ended for the completion requests attached to it, which is the outcome that another server stored if it ended the run
    /// first.</returns>
    private async Task<RunEnd> StoreEndAsync(UploadRecord record, UploadRun run, RunEnd end)
    {
        if (end.IsNotStarted || end.RetryError is not null)
        {
            await ReleaseClaimAsync(record, run.Claim, run.RenewedTimestamp);
            return end;
        }

        if (end.IsAborted)
        {
            await _store.DeleteAsync(record, CancellationToken.None);
            _total.End(record.Id);
            return end;
        }

        if (await _store.TrySetOutcomeAsync(record, end.Outcome!.Write(), CancellationToken.None))
        {
            _total.End(record.Id);
            return end;
        }

        // Another server ended the run first, because its claim lapsed, or the upload was deleted.
        var upload = await _store.GetAsync(record.UserId, record.Id, CancellationToken.None);

        if (upload?.Outcome is { } storedOutcome && UploadOutcome.Read(storedOutcome) is { } outcome)
        {
            _logger.LogWarning(
                "The outcome of the handler of upload {UploadId} was not stored, because the upload had already been ended as interrupted.", record.Id);

            return new RunEnd { Outcome = outcome };
        }

        return new RunEnd { IsAborted = true };
    }

    /// <summary>
    /// Renews the claim of a run until the run has ended, and stops the handler if the claim is lost, or can't be renewed before its lease runs out.
    /// </summary>
    private async Task RenewAsync(UploadRun run, UploadRecord record)
    {
        try
        {
            using var timer = new PeriodicTimer(RenewInterval, _timeProvider);

            while (await timer.WaitForNextTickAsync(run.RenewalToken))
            {
                bool isRenewed;

                try
                {
                    isRenewed = await run.Claim.RenewAsync(run.RenewalToken);
                }
                catch (Exception) when (run.RenewalToken.IsCancellationRequested)
                {
                    // The run ended while the renewal ran, which the store may have written before it saw the cancellation, whatever exception it reports
                    // that with, so the claim counts as renewed now: its lease is then never taken to run out before it does.
                    run.RenewedTimestamp = _timeProvider.GetTimestamp();
                    return;
                }
                catch (Exception ex) when (!run.RenewalToken.IsCancellationRequested)
                {
                    // The claim is renewed again at the next interval, unless its lease runs out before then.
                    if (_timeProvider.GetElapsedTime(run.RenewedTimestamp) + RenewInterval < LeaseDuration)
                    {
                        _logger.LogWarning(ex, "The claim of the run of the handler of upload {UploadId} could not be renewed.", record.Id);
                        continue;
                    }

                    _logger.LogError(
                        ex,
                        "The claim of the run of the handler of upload {UploadId} could not be renewed before its lease ran out, so the handler is stopped.",
                        record.Id);

                    run.Cancel(RunCancelReason.ClaimLost);
                    return;
                }

                if (isRenewed)
                {
                    run.RenewedTimestamp = _timeProvider.GetTimestamp();
                    continue;
                }

                run.Cancel(await IsDeletedAsync(run, record) ? RunCancelReason.Aborted : RunCancelReason.ClaimLost);
                return;
            }
        }
        catch (OperationCanceledException) when (run.RenewalToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Such as an exception of a callback of the handler's cancellation token.
            _logger.LogError(ex, "The renewals of the claim of the run of the handler of upload {UploadId} failed.", record.Id);
        }
    }

    /// <summary>
    /// Gets a value indicating whether the upload of a run whose claim was lost was deleted, such as by an abort on another server, rather than ended by
    /// another server.
    /// </summary>
    private async ValueTask<bool> IsDeletedAsync(UploadRun run, UploadRecord record)
    {
        if (run.CancelReason is not RunCancelReason.None)
            return false;

        try
        {
            return await _store.GetAsync(record.UserId, record.Id) is null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Upload {UploadId} could not be read after the claim of its run was lost.", record.Id);
            return false;
        }
    }
}
