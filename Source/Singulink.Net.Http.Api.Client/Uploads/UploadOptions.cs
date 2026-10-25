namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// The options of an upload in chunks (see <see cref="ApiClientBase.UploadAsync{TResult}"/> and
/// <see cref="ApiClientBase.UploadStreamingAsync{TItem}"/>).
/// </summary>
public sealed class UploadOptions
{
    /// <summary>
    /// The largest value of <see cref="MaxConcurrency"/>.
    /// </summary>
    internal const int MaxConcurrencyLimit = 4;

    private readonly int _maxConcurrency = 1;
    private readonly TimeSpan _chunkTimeout = TimeSpan.FromMinutes(5);
    private readonly TimeSpan _retryTimeout = TimeSpan.FromMinutes(5);
    private readonly TimeSpan _completionIdleTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets the chunk size in bytes to ask the service for, which the service keeps within its bounds: by default 5 MiB to 64 MiB. Default is
    /// <see langword="null"/>, which uses the service's default, 5 MiB unless the service sets another.
    /// </summary>
    /// <remarks>
    /// Each chunk that is being sent is held in a buffer of its size. In the browser, a chunk also passes through a few copies of about its size on its
    /// way to the network, so the memory that an upload needs there is a few times the chunk size times <see cref="MaxConcurrency"/>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int? ChunkSize
    {
        get;
        init {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), value, "The chunk size must be positive.");

            field = value;
        }
    }

    /// <summary>
    /// Gets how many chunks can be sent at the same time, from 1 to 4. Default is 1.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1 or more than 4.</exception>
    public int MaxConcurrency
    {
        get => _maxConcurrency;
        init {
            if (value is < 1 or > MaxConcurrencyLimit)
                throw new ArgumentOutOfRangeException(nameof(value), value, $"The concurrency must be from 1 to {MaxConcurrencyLimit}.");

            _maxConcurrency = value;
        }
    }

    /// <summary>
    /// Gets how long the request of a chunk can go without sending any of the chunk's bytes, or, after the last of them, without its response. In the
    /// browser, which sends the content of a request at once, and on Android and iOS, whose HTTP handlers read the content of a request into memory before
    /// they send it, it is in effect how long the request can take, from sending it until its response, which a 5 MiB chunk takes at about 140 kbit/s with
    /// the default. <see cref="Timeout.InfiniteTimeSpan"/> and values longer than <see cref="int.MaxValue"/> milliseconds mean no timeout. A chunk whose
    /// request times out is sent again. Default is 5 minutes.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero, or negative other than <see cref="Timeout.InfiniteTimeSpan"/>.</exception>
    public TimeSpan ChunkTimeout
    {
        get => _chunkTimeout;
        init {
            if (value <= TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(value), value, "The timeout must be positive or Timeout.InfiniteTimeSpan.");

            _chunkTimeout = value;
        }
    }

    /// <summary>
    /// Gets how long after the last request of the upload that succeeded the requests that fail for a reason that may not last are made again. Zero makes
    /// none of them again, and <see cref="Timeout.InfiniteTimeSpan"/> makes them again without a limit. Default is 5 minutes.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative other than <see cref="Timeout.InfiniteTimeSpan"/>.</exception>
    public TimeSpan RetryTimeout
    {
        get => _retryTimeout;
        init {
            if (value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(value), value, "The timeout cannot be negative, other than Timeout.InfiniteTimeSpan.");

            _retryTimeout = value;
        }
    }

    /// <summary>
    /// Gets how long the completion of the upload can send nothing, not even a keep-alive ping, before its request is made again, which picks up the
    /// processing where it is. <see cref="Timeout.InfiniteTimeSpan"/> and values longer than <see cref="int.MaxValue"/> milliseconds (about 24.8 days)
    /// mean no limit. Default is 60 seconds, six of the service's default ping intervals.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero, or negative other than <see cref="Timeout.InfiniteTimeSpan"/>.</exception>
    public TimeSpan CompletionIdleTimeout
    {
        get => _completionIdleTimeout;
        init {
            if (value <= TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(value), value, "The timeout must be positive or Timeout.InfiniteTimeSpan.");

            _completionIdleTimeout = value;
        }
    }

    /// <summary>
    /// Gets the progress that the upload reports to as it sends chunks, and when the upload is being completed. Default is <see langword="null"/>, which
    /// reports none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="UploadProgress.BytesUploaded"/> counts the bytes of the chunks that the service has acknowledged, and on platforms other than the
    /// browser also the bytes that the requests of the chunks being sent have written to the connection so far, in steps of 64 KiB. The bytes of a request
    /// that fails are no longer counted, so the count goes back when a chunk is sent again, and when the service reports chunks missing that it had
    /// acknowledged. In the browser, which reads the content of a request into memory before it sends any of it, only acknowledged chunks count. On
    /// Android and iOS, whose HTTP handlers read the content of a request into memory before they send it too, a chunk counts in full once the handler has
    /// read it, before its bytes go out.
    /// </para>
    /// <para>
    /// The upload reports from the threads that send its requests and read their responses, one report at a time, and only when the progress changes. A
    /// report that takes long holds up only the thread that makes it: when the progress changes while it is made, the latest progress is reported next.
    /// </para>
    /// <para>
    /// A report that throws fails the upload: nothing more is reported, the upload is aborted on the service, and the upload throws the report's
    /// exception.
    /// </para>
    /// </remarks>
    public IProgress<UploadProgress>? Progress { get; init; }

    /// <summary>
    /// Gets the time provider that measures <see cref="RetryTimeout"/>, the waits before requests are made again, <see cref="ChunkTimeout"/> and
    /// <see cref="CompletionIdleTimeout"/>. Default is <see langword="null"/>, which uses <see cref="TimeProvider.System"/>.
    /// </summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>
    /// Gets a value indicating whether <see cref="UploadProgress.BytesUploaded"/> counts the bytes that the requests of chunks have written so far, which
    /// it does on platforms other than the browser (see <see cref="Progress"/>). Tests set it to check the browser's behavior on other platforms.
    /// </summary>
    internal bool CountsWrittenBytes { get; init; } = !OperatingSystem.IsBrowser();
}
