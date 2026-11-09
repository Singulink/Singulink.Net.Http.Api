namespace Singulink.Net.Http.Api.Client;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// The options of an upload (see <see cref="ApiClientBase.UploadAsync{TResult}"/> and
/// <see cref="ApiClientBase.UploadStreamingAsync{TItem}"/>).
/// </summary>
/// <remarks>
/// The first upload that uses the options checks them when it takes them, after which they can't be changed, and their setters throw an
/// <see cref="InvalidOperationException"/>.
/// </remarks>
public sealed class UploadOptions
{
    /// <summary>
    /// The largest value of <see cref="MaxConcurrency"/>.
    /// </summary>
    internal const int MaxConcurrencyLimit = 4;

    /// <summary>
    /// Gets or sets the chunk size in bytes to ask the service for. Must be at least 1. Default is <see langword="null"/>, which uses the service's default,
    /// 5 MiB unless the service sets another.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The service keeps it within its bounds: by default 5 MiB to 64 MiB. A file of up to this size, kept within those default bounds, or of up to 5 MiB
    /// when it is <see langword="null"/>, is uploaded directly, in one request.
    /// </para>
    /// <para>
    /// Each chunk that is being sent, and the file of a direct upload, is held in a buffer of its size. In the browser, a chunk also passes
    /// through a few copies of about its size on its way to the network, so the memory that an upload needs there is a few times the chunk size times
    /// <see cref="MaxConcurrency"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int? ChunkSize
    {
        get;
        set {
            EnsureNotFrozen();
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), value, "The chunk size must be positive.");

            field = value;
        }
    }

    /// <summary>
    /// Gets or sets how many chunks can be sent at the same time. Must be between 1 and 4. Default is 1.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1 or more than 4.</exception>
    public int MaxConcurrency
    {
        get;
        set {
            EnsureNotFrozen();
            if (value is < 1 or > MaxConcurrencyLimit)
                throw new ArgumentOutOfRangeException(nameof(value), value, $"The concurrency must be from 1 to {MaxConcurrencyLimit}.");

            field = value;
        }
    } = 1;

    /// <summary>
    /// Gets or sets how long the request of a chunk can go without sending any of the chunk's bytes, or, after the last of them, without its response.
    /// <see cref="Timeout.InfiniteTimeSpan"/> and values longer than <see cref="int.MaxValue"/> milliseconds mean no timeout. Must be greater than zero, or
    /// <see cref="Timeout.InfiniteTimeSpan"/>. Default is 5 minutes.
    /// </summary>
    /// <remarks>
    /// In the browser, which sends the content of a request at once, and on Android and iOS, whose HTTP handlers read the content of a request into memory
    /// before they send it, it is in effect how long the request can take, from sending it until its response, which a 5 MiB chunk takes at about
    /// 140 kbit/s with the default. A chunk whose request times out is sent again.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero, or negative other than <see cref="Timeout.InfiniteTimeSpan"/>.</exception>
    public TimeSpan ChunkTimeout
    {
        get;
        set {
            EnsureNotFrozen();
            if (value <= TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(value), value, "The timeout must be positive or Timeout.InfiniteTimeSpan.");

            field = value;
        }
    } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets how long after the last request of the upload that succeeded the requests that fail for a reason that may not last are made again. Zero
    /// makes none of them again, and <see cref="Timeout.InfiniteTimeSpan"/> makes them again without a limit. Must be at least zero, or
    /// <see cref="Timeout.InfiniteTimeSpan"/>. Default is 5 minutes.
    /// </summary>
    /// <remarks>
    /// A service built with the toolkit keeps an upload for its <c>ChunkedUploadOptions.IdleExpiry</c> after the upload's last chunk, and for its
    /// <c>ChunkedUploadOptions.UnstartedExpiry</c> after the upload's creation until the upload has a chunk, 15 minutes each by default, and reading the
    /// source is not a request, so a source that takes longer than that to produce the next chunk finds the upload gone. A request of an upload that has
    /// expired fails with <see cref="UploadErrorCodes.NotFound"/>, which is not made again, also when this timeout has not run out.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative other than <see cref="Timeout.InfiniteTimeSpan"/>.</exception>
    public TimeSpan RetryTimeout
    {
        get;
        set {
            EnsureNotFrozen();
            if (value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(value), value, "The timeout cannot be negative, other than Timeout.InfiniteTimeSpan.");

            field = value;
        }
    } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets how long the completion of the upload can send nothing, not even a keep-alive ping, before its request is made again, which picks up the
    /// processing where it is. <see cref="Timeout.InfiniteTimeSpan"/> and values longer than <see cref="int.MaxValue"/> milliseconds (about 24.8 days)
    /// mean no limit. Must be greater than zero, or <see cref="Timeout.InfiniteTimeSpan"/>. Default is 60 seconds, six of the service's default ping
    /// intervals.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero, or negative other than <see cref="Timeout.InfiniteTimeSpan"/>.</exception>
    public TimeSpan CompletionIdleTimeout
    {
        get;
        set {
            EnsureNotFrozen();
            if (value <= TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(value), value, "The timeout must be positive or Timeout.InfiniteTimeSpan.");

            field = value;
        }
    } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets or sets the progress that the upload reports to as it sends chunks, and when the upload is being completed. Default is <see langword="null"/>,
    /// which reports none.
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
    public IProgress<UploadProgress>? Progress {
        get;
        set {
            EnsureNotFrozen();
            field = value;
        }
    }

    /// <summary>
    /// Gets or sets the time provider that measures <see cref="RetryTimeout"/>, the waits before requests are made again, <see cref="ChunkTimeout"/> and
    /// <see cref="CompletionIdleTimeout"/>. Default is <see langword="null"/>, which uses <see cref="TimeProvider.System"/>.
    /// </summary>
    public TimeProvider? TimeProvider {
        get;
        set {
            EnsureNotFrozen();
            field = value;
        }
    }

    /// <summary>
    /// Gets a value indicating whether <see cref="UploadProgress.BytesUploaded"/> counts the bytes that the requests of chunks have written so far, which
    /// it does on platforms other than the browser (see <see cref="Progress"/>). Tests set it to check the browser's behavior on other platforms.
    /// </summary>
    internal bool CountsWrittenBytes { get; init; } = !OperatingSystem.IsBrowser();

    /// <summary>
    /// Gets a value indicating whether a file that fits in one chunk is uploaded directly. Tests clear it to check uploads in chunks with small files.
    /// </summary>
    internal bool AllowsDirectUpload { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether the options are in use: the first upload that uses them checked them (see <see cref="Validate"/>) when it took them, and
    /// they can no longer be changed.
    /// </summary>
    public bool IsFrozen { get; private set; }

    /// <summary>
    /// Checks the options as the first upload that uses them does when it takes them.
    /// </summary>
    /// <remarks>
    /// Each value is checked when it is set, and no rule involves more than one of them, so this does not throw.
    /// </remarks>
    public void Validate()
    {
        var problems = GetProblems();

        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(" ", problems));
    }

    /// <summary>
    /// Checks the options (see <see cref="Validate"/>) and keeps them from being changed from then on, when the first upload that uses them takes them. Options
    /// that are frozen already stay as they are.
    /// </summary>
    /// <exception cref="InvalidOperationException">The options are not valid. The message lists every problem.</exception>
    internal void Freeze()
    {
        if (IsFrozen)
            return;

        Validate();
        IsFrozen = true;
    }

    /// <summary>
    /// Gets the problems of the options, which <see cref="Validate"/> reports.
    /// </summary>
    internal List<string> GetProblems()
    {
        return [];
    }

    private void EnsureNotFrozen()
    {
        if (IsFrozen)
            throw new InvalidOperationException($"The {nameof(UploadOptions)} are in use, so they can no longer be changed.");
    }
}
