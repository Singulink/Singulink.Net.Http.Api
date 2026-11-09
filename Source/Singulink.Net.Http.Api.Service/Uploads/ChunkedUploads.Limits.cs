namespace Singulink.Net.Http.Api.Service.Uploads;

/// <content>
/// The limits of open uploads, and the listing of the open uploads that corrects their counts.
/// </content>
internal sealed partial class ChunkedUploads
{
    // The retry time of a creation of a user who has the most open uploads, or the largest total of their lengths, that the service allows.
    private static readonly TimeSpan LimitRetryAfter = TimeSpan.FromSeconds(1);

    private readonly OpenUploadCounters _counters;

    /// <summary>
    /// Gets the limits of the open uploads of the request's user: those of the app's limits provider, or those of the options if it has none for the user.
    /// </summary>
    private async ValueTask<OpenUploadCounters.Limits> GetLimitsAsync(HttpContext context, string userId, CancellationToken cancellationToken)
    {
        var limits = context.RequestServices.GetService<IUploadLimitsProvider>() is { } provider
            ? await provider.GetLimitsAsync(context, userId, cancellationToken)
            : null;

        return limits is null
            ? new(_options.MaxUploadsPerUser, _options.MaxTotalSizePerUser, _options.MaxTotalSize)
            : new(limits.MaxUploadsPerUser, limits.MaxTotalSizePerUser, _options.MaxTotalSize);
    }

    /// <summary>
    /// Checks the length of a new upload against the largest upload of the endpoint. An upload that is longer than the open uploads of the user can be
    /// together can't be created either, and the error names the smaller of the two limits: the user's is checked with the other limits of the user (see
    /// <see cref="GetLimitErrorAsync"/>), so that a creation made again with the key of an existing upload gets the upload, whatever the user's limits are
    /// now.
    /// </summary>
    private void CheckLength(long length, UploadEndpoint endpoint, OpenUploadCounters.Limits limits)
    {
        long maxSize = endpoint.MaxSize ?? _options.MaxSize;
        bool isUserLimitSmaller = limits.MaxTotalSizePerUser <= maxSize;

        if (length > maxSize && !isUserLimitSmaller)
        {
            _metrics?.UploadRejected("too_large");
            throw UploadErrors.TooLarge(maxSize);
        }
    }

    /// <summary>
    /// Gets the error of a new upload of a user that is over the limits of the user's open uploads or of all open uploads, by their counts.
    /// </summary>
    /// <returns>The error of the limit that the upload is over, or <see langword="null"/> if it is within all of them.</returns>
    private async ValueTask<ApiException?> GetLimitErrorAsync(string userId, long length, OpenUploadCounters.Limits limits, CancellationToken cancellationToken)
    {
        // An upload that can never fit gets its error without a read of the counts.
        if (GetNeverFitsError(length, limits) is { } neverFitsError)
            return neverFitsError;

        var limit = OpenUploadCounters.GetLimit(await _counters.ReadAsync(userId, limits, cancellationToken), length, limits);
        return limit is OpenUploadCounters.ExceededLimit.None ? null : GetLimitError(limit, limits);
    }

    /// <summary>
    /// Gets the final error of an upload that is longer than the open uploads of the user, or of all users, can be together, so that it can never be created,
    /// however many uploads end.
    /// </summary>
    /// <returns>The error, or <see langword="null"/> if the upload can fit.</returns>
    private static ApiException? GetNeverFitsError(long length, OpenUploadCounters.Limits limits)
    {
        if (limits.MaxTotalSizePerUser is { } maxTotalSizePerUser && length > maxTotalSizePerUser)
            return UploadErrors.ExceedsSizeLimit(maxTotalSizePerUser);

        if (limits.MaxTotalSize is { } maxTotalSize && length > maxTotalSize)
            return UploadErrors.TooLarge(maxTotalSize);

        return null;
    }

    /// <summary>
    /// Counts a creation that a limit rejected, by the error that rejects it, and returns the error.
    /// </summary>
    private ApiException Rejected(ApiException error)
    {
        if (_metrics is { } metrics)
        {
            // A TooLarge here is that of MaxTotalSize: the endpoint's is counted where it is thrown (see CheckLength).
            metrics.UploadRejected(error.ErrorCode switch {
                UploadErrorCodes.LimitReached => "uploads_per_user",
                UploadErrorCodes.SizeLimitReached or UploadErrorCodes.ExceedsSizeLimit => "total_size_per_user",
                UploadErrorCodes.StorageFull or UploadErrorCodes.TooLarge => "total_size",
                _ => "other",
            });
        }

        return error;
    }

    private static ApiException GetLimitError(OpenUploadCounters.ExceededLimit limit, OpenUploadCounters.Limits limits) => limit switch {
        OpenUploadCounters.ExceededLimit.UploadsPerUser => UploadErrors.LimitReached(limits.MaxUploadsPerUser, LimitRetryAfter),
        OpenUploadCounters.ExceededLimit.TotalSizePerUser => UploadErrors.SizeLimitReached(limits.MaxTotalSizePerUser!.Value, LimitRetryAfter),
        OpenUploadCounters.ExceededLimit.TotalSize => UploadErrors.StorageFull(StorageFullRetryAfter),
        _ => throw new ArgumentOutOfRangeException(nameof(limit), limit, null),
    };

    /// <summary>
    /// Removes the uploads that have expired, ends the runs whose claims have lapsed, corrects the counts of the open uploads, and removes the outcomes that
    /// are no longer kept.
    /// </summary>
    public async Task CleanUpAsync(CancellationToken cancellationToken)
    {
        var nowUtc = UtcNow;
        var startedUtc = _timeProvider.GetUtcNow();
        long started = _timeProvider.GetTimestamp();
        var listing = new CleanupListing();

        try
        {
            // The counts stay as they are if the listing fails. A correction that fails is logged, and the rest of the cleanup goes on.
            await _counters.CorrectAsync(await ListOpenUploadsAsync(listing, cancellationToken));

            await _store.RemoveOutcomesAsync(nowUtc - _options.OutcomeRetention, cancellationToken);
            await _store.CleanUpAsync(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            EndCleanup(startedUtc, started, listing, ex);
            throw;
        }

        EndCleanup(startedUtc, started, listing, null);
    }

    /// <summary>
    /// Keeps the status of a cleanup that ended, for the health check, and records its duration.
    /// </summary>
    private void EndCleanup(DateTimeOffset startedUtc, long started, CleanupListing listing, Exception? exception)
    {
        var duration = _timeProvider.GetElapsedTime(started);
        var tally = listing.Tally;

        LastCleanup = new UploadCleanupStatus {
            StartedUtc = startedUtc,
            Duration = duration,
            OpenUploads = tally?.Uploads,
            OpenUploadsSize = tally?.TotalSize,
            UsersWithOpenUploads = tally?.UserCount,
            Interrupted = listing.Interrupted,
            Expired = listing.Expired,
            Failed = listing.Failed,
            Exception = exception,
        };

        if (tally is not null)
            _lastTally = tally;

        _metrics?.CleanupEnded(duration, exception is null);
    }

    /// <summary>
    /// Lists the open uploads of all users for a correction of their counts, and tallies the open uploads that stay. It ends the runs whose claims have
    /// lapsed as interrupted, and with a process-local store also the runs that a previous process left (see <see cref="IsLeftByEndedProcess"/>), and
    /// removes the uploads that have expired: all of these take the uploads out of the counts. A run that runs on this server stays open, and so does an
    /// upload that could not be ended, which is taken out of the counts when it ends.
    /// </summary>
    private async Task<OpenUploadCounters.Tally> ListOpenUploadsAsync(CleanupListing listing, CancellationToken cancellationToken)
    {
        var found = new OpenUploadCounters.Tally();
        var nowUtc = UtcNow;

        await foreach (var upload in _store.ListAsync(cancellationToken))
        {
            var record = upload.Record;

            try
            {
                if ((upload.RunState is UploadRunState.Lapsed && !_runs.ContainsKey(UserUploadId.Of(record))) || IsLeftByEndedProcess(upload))
                {
                    await InterruptAsync(upload);
                    listing.Interrupted++;
                }
                else if (IsExpired(upload, nowUtc))
                {
                    if (await _store.DeleteAsync(record, CancellationToken.None))
                    {
                        await _counters.SubtractAsync(record);
                        _metrics?.UploadEnded("expired");
                    }

                    listing.Expired++;
                }
                else
                {
                    found.Add(record);
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // The listing goes on with the other uploads, and the next one tries again.
                _logger.LogError(ex, "Upload {UploadId} of user {UserId} could not be cleaned up.", record.Id, record.UserId);
                found.Add(record);
                listing.Failed++;
            }
        }

        listing.Tally = found;
        return found;
    }

    /// <summary>
    /// What the listing of a cleanup found and did, which it fills in as it goes, so that a cleanup that fails keeps what it got to.
    /// </summary>
    private sealed class CleanupListing
    {
        public OpenUploadCounters.Tally? Tally { get; set; }

        public int Interrupted { get; set; }

        public int Expired { get; set; }

        public int Failed { get; set; }
    }

    /// <summary>
    /// Gets a value indicating whether the run of an upload in a process-local store was claimed by a process that has ended: before this process started
    /// chunked uploads, and so before it opened the store, which no other process uses at the same time. Such a run is ended at once, rather than once its
    /// claim lapses. The runs of this process, and the claims that it releases in the background, are its own, also after a step of the system's clock.
    /// </summary>
    private bool IsLeftByEndedProcess(StoredUpload upload)
    {
        var id = UserUploadId.Of(upload.Record);
        return _store.SharingMode is StoreSharingMode.ProcessLocal && upload.RunState is UploadRunState.Running && upload.RunStartedUtc < _startedUtc &&
            !_runs.ContainsKey(id) && !_releases.ContainsKey(id);
    }
}
