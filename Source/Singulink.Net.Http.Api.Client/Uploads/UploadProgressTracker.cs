namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Tracks the progress of an upload and reports it: the bytes of the acknowledged chunks, and the bytes that the requests of the chunks being sent have
/// written so far. Reports are made one at a time, outside of the tracker's lock, and only when the progress changes: the thread that changes the progress
/// while no report is being made reports it, and goes on reporting the latest progress while other threads change it. A report that throws ends the
/// reports and fails the upload with its exception (see <see cref="ReportFailure"/>).
/// </summary>
internal sealed class UploadProgressTracker
{
    private readonly IProgress<UploadProgress>? _progress;
    private readonly long _length;
    private readonly CancellationTokenSource _reportFailed = new();

#if NET
    private readonly Lock _lock = new();
#else
    private readonly object _lock = new();
#endif

    private long _acknowledged;
    private long _written;
    private bool _isProcessing;
    private bool _isReporting;
    private UploadProgress? _lastReported;
    private Exception? _reportFailure;

    public UploadProgressTracker(IProgress<UploadProgress>? progress, long length)
    {
        _progress = progress;
        _length = length;
    }

    /// <summary>
    /// Gets the token that is cancelled when a report throws, which stops the requests of the upload.
    /// </summary>
    public CancellationToken ReportFailedToken => _reportFailed.Token;

    /// <summary>
    /// Gets the exception of the report that threw, which the upload fails with, or <see langword="null"/> if no report threw.
    /// </summary>
    public Exception? ReportFailure
    {
        get {
            lock (_lock)
                return _reportFailure;
        }
    }

    /// <summary>
    /// Starts counting the bytes of a request of a chunk.
    /// </summary>
    public SendAttempt StartAttempt() => new(this);

    /// <summary>
    /// Sets the bytes of the acknowledged chunks, such as after the service reported chunks missing, and reports that the upload is sending chunks.
    /// </summary>
    public void SetAcknowledged(long bytes)
    {
        bool report;

        lock (_lock)
        {
            _acknowledged = bytes;
            _isProcessing = false;
            report = StartReportingLocked();
        }

        if (report)
            Report();
    }

    /// <summary>
    /// Reports that the service has every chunk and the upload is being completed.
    /// </summary>
    public void SetProcessing()
    {
        bool report;

        lock (_lock)
        {
            _isProcessing = true;
            report = StartReportingLocked();
        }

        if (report)
            Report();
    }

    /// <summary>
    /// Makes the calling thread the one that reports, unless there is nothing to report to, another thread is reporting or a report threw. Must be called
    /// under the lock.
    /// </summary>
    private bool StartReportingLocked()
    {
        if (_progress is null || _isReporting || _reportFailure is not null)
            return false;

        _isReporting = true;
        return true;
    }

    /// <summary>
    /// Reports the progress until it no longer changes. Called by the thread that started reporting. An exception of the report is kept for the upload to
    /// fail with, rather than thrown to the thread, which can be sending a request.
    /// </summary>
    private void Report()
    {
        while (true)
        {
            UploadProgress progress;

            lock (_lock)
            {
                progress = new UploadProgress(_acknowledged + _written, _length, _isProcessing);

                if (progress == _lastReported)
                {
                    _isReporting = false;
                    return;
                }

                _lastReported = progress;
            }

            try
            {
                _progress!.Report(progress);
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _isReporting = false;
                    _reportFailure = ex;
                }

                _reportFailed.Cancel();
                return;
            }
        }
    }

    /// <summary>
    /// Counts the bytes that a request of a chunk has written until it ends. Bytes written after it ended, such as by a handler that still writes the
    /// content of a request that failed, are not counted.
    /// </summary>
    internal sealed class SendAttempt(UploadProgressTracker tracker)
    {
        private long _written;
        private bool _ended;

        /// <summary>
        /// Counts bytes that the request has written.
        /// </summary>
        public void OnWritten(int bytes)
        {
            bool report;

            lock (tracker._lock)
            {
                if (_ended)
                    return;

                _written += bytes;
                tracker._written += bytes;
                report = tracker.StartReportingLocked();
            }

            if (report)
                tracker.Report();
        }

        /// <summary>
        /// Stops counting the bytes written so far, because the request is writing its content again.
        /// </summary>
        public void OnRestarted()
        {
            bool report;

            lock (tracker._lock)
            {
                if (_ended || _written is 0)
                    return;

                tracker._written -= _written;
                _written = 0;
                report = tracker.StartReportingLocked();
            }

            if (report)
                tracker.Report();
        }

        /// <summary>
        /// Ends the request: stops counting its bytes, and adds the bytes of its chunk to the acknowledged bytes if the service acknowledged it.
        /// </summary>
        /// <param name="acknowledgedBytes">The length of the chunk if the service acknowledged it; otherwise zero.</param>
        public void End(int acknowledgedBytes)
        {
            bool report;

            lock (tracker._lock)
            {
                if (_ended)
                    return;

                _ended = true;
                tracker._written -= _written;
                tracker._acknowledged += acknowledgedBytes;
                report = tracker.StartReportingLocked();
            }

            if (report)
                tracker.Report();
        }
    }
}
