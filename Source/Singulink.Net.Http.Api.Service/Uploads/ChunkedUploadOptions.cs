namespace Singulink.Net.Http.Api.Service.Uploads;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Provides options for chunked uploads: their sizes and chunk sizes, the limits of open uploads, and how long uploads and their outcomes are kept.
/// </summary>
/// <remarks>
/// <para>
/// Chunked uploads check the options when they take them, after which they can't be changed, and their setters throw an
/// <see cref="InvalidOperationException"/>.
/// </para>
/// <para>
/// Chunk sizes are always from 5 MiB, the smallest part size that object stores such as Amazon S3 accept for all but the last part, to 64 MiB, which keeps
/// every request well under the smallest request body limit of Cloudflare's plans (100 MB). A file smaller than a chunk is sent as a single chunk. The
/// <see cref="IUploadStore"/> can narrow the bounds further (see <see cref="IUploadStore.MinChunkSize"/> and <see cref="IUploadStore.MaxChunkSize"/>).
/// </para>
/// </remarks>
public sealed class ChunkedUploadOptions
{
    /// <summary>
    /// The smallest chunk size, in bytes: 5 MiB.
    /// </summary>
    public const int ChunkSizeFloor = ChunkedUploadProtocol.MinChunkSize;

    /// <summary>
    /// The largest chunk size, in bytes: 64 MiB.
    /// </summary>
    public const int ChunkSizeCeiling = ChunkedUploadProtocol.MaxChunkSize;

    /// <summary>
    /// Gets or sets the largest upload, in bytes, for endpoints whose <see cref="ChunkedUploadEndpointOptions.MaxSize"/> is not set. Must be at least zero.
    /// Default is 1 GiB.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public long MaxSize {
        get;
        set {
            EnsureNotFrozen();
            ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(value));
            field = value;
        }
    } = 1L << 30;

    /// <summary>
    /// Gets or sets the chunk size of uploads whose client does not ask for one, in bytes. Must be between <see cref="MinChunkSize"/> and
    /// <see cref="MaxChunkSize"/>. Default is 5 MiB.
    /// </summary>
    /// <remarks>
    /// That it is between them is checked when the application starts.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than <see cref="ChunkSizeFloor"/> or greater than
    /// <see cref="ChunkSizeCeiling"/>.</exception>
    public int DefaultChunkSize {
        get;
        set {
            EnsureNotFrozen();
            CheckChunkSize(value);
            field = value;
        }
    } = ChunkedUploadProtocol.DefaultChunkSize;

    /// <summary>
    /// Gets or sets the smallest chunk size that a client can ask for, in bytes. Must be between 5 MiB and 64 MiB. Default is 5 MiB.
    /// </summary>
    /// <remarks>
    /// Smaller requested sizes are raised to it.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than <see cref="ChunkSizeFloor"/> or greater than
    /// <see cref="ChunkSizeCeiling"/>.</exception>
    public int MinChunkSize {
        get;
        set {
            EnsureNotFrozen();
            CheckChunkSize(value);
            field = value;
        }
    } = ChunkSizeFloor;

    /// <summary>
    /// Gets or sets the largest chunk size that a client can ask for, in bytes. Must be between 5 MiB and 64 MiB. Default is 64 MiB.
    /// </summary>
    /// <remarks>
    /// Larger requested sizes are lowered to it.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than <see cref="ChunkSizeFloor"/> or greater than
    /// <see cref="ChunkSizeCeiling"/>.</exception>
    public int MaxChunkSize {
        get;
        set {
            EnsureNotFrozen();
            CheckChunkSize(value);
            field = value;
        }
    } = ChunkSizeCeiling;

    /// <summary>
    /// Gets or sets the largest number of chunks of an upload. Must be at least 1. Default is 10,000.
    /// </summary>
    /// <remarks>
    /// An upload that would have more chunks at the requested chunk size gets larger chunks, up to <see cref="MaxChunkSize"/>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int MaxChunkCount {
        get;
        set {
            EnsureNotFrozen();
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, nameof(value));
            field = value;
        }
    } = 10_000;

    /// <summary>
    /// Gets or sets the largest number of open uploads of a user, counted across all upload endpoints, for users whose limits an
    /// <see cref="IUploadLimitsProvider"/> doesn't set. Open uploads are the uploads that are receiving chunks or whose handler is running. Must be at
    /// least 1. Default is 20.
    /// </summary>
    /// <remarks>
    /// The open uploads are counted in the <see cref="IShortLivedStore"/>, as the uploads guide describes, and the counts are corrected at each cleanup, when
    /// the application starts and every five minutes. A creation over it fails with <see cref="UploadErrorCodes.LimitReached"/> and a retry time of a second.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int MaxUploadsPerUser {
        get;
        set {
            EnsureNotFrozen();
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, nameof(value));
            field = value;
        }
    } = 20;

    /// <summary>
    /// Gets or sets the largest total length of the open uploads of a user, in bytes, counted across all upload endpoints, or <see langword="null"/> for no
    /// limit, for users whose limits an <see cref="IUploadLimitsProvider"/> doesn't set. Must be at least zero. Default is <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Each upload counts with its length from its creation. It is counted as <see cref="MaxUploadsPerUser"/> describes. A creation over it fails with
    /// <see cref="UploadErrorCodes.SizeLimitReached"/> and a retry time of a second. An upload that is longer than this can't be created, and fails with
    /// <see cref="UploadErrorCodes.ExceedsSizeLimit"/>. An upload longer than both this and the maximum size of its endpoint (see <see cref="MaxSize"/>)
    /// gets the error of the smaller limit.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public long? MaxTotalSizePerUser {
        get;
        set {
            EnsureNotFrozen();
            if (value is { } size)
                ArgumentOutOfRangeException.ThrowIfNegative(size, nameof(value));

            field = value;
        }
    }

    /// <summary>
    /// Gets or sets the largest total length of all open uploads, in bytes, or <see langword="null"/> for no limit. Must be at least zero. Default is
    /// <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// The total is counted as <see cref="MaxUploadsPerUser"/> describes. A creation over it fails with <see cref="UploadErrorCodes.StorageFull"/> and a
    /// retry time of a minute, and an upload that is longer than this can't be created, and fails with <see cref="UploadErrorCodes.TooLarge"/>. The
    /// application logs a warning when it starts if this is not set.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public long? MaxTotalSize {
        get;
        set {
            EnsureNotFrozen();
            if (value is { } size)
                ArgumentOutOfRangeException.ThrowIfNegative(size, nameof(value));

            field = value;
        }
    }

    /// <summary>
    /// Gets or sets how long an upload that is receiving chunks is kept after its last chunk. Must be greater than zero and at most 24 days. Default is
    /// 15 minutes.
    /// </summary>
    /// <remarks>
    /// An upload whose run of the handler ends without an outcome, such as when the handler reports with a <c>503</c> that it can't run at the moment, is
    /// receiving chunks again, and is kept for this long after the run.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero, negative or longer than 24 days.</exception>
    public TimeSpan IdleExpiry {
        get;
        set {
            EnsureNotFrozen();
            CheckDuration(value);
            field = value;
        }
    } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Gets or sets how long the handler of an upload can run. Must be greater than zero and at most 24 days. Default is 1 hour.
    /// </summary>
    /// <remarks>
    /// Its cancellation token is cancelled when it has run for this long, and an upload whose handler is still running a minute later is ended as
    /// interrupted (see <see cref="UploadErrorCodes.Interrupted"/>). An upload whose handler runs does not expire.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero, negative or longer than 24 days.</exception>
    public TimeSpan ProcessingTimeout {
        get;
        set {
            EnsureNotFrozen();
            CheckDuration(value);
            field = value;
        }
    } = TimeSpan.FromHours(1);

    /// <summary>
    /// Gets or sets how long an upload that has received no chunk is kept after its creation. Must be greater than zero and at most 24 days. Default is
    /// 15 minutes.
    /// </summary>
    /// <remarks>
    /// It is capped at <see cref="IdleExpiry"/> when it is longer. Once a chunk arrives, <see cref="IdleExpiry"/> applies.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero, negative or longer than 24 days.</exception>
    public TimeSpan UnstartedExpiry {
        get;
        set {
            EnsureNotFrozen();
            CheckDuration(value);
            field = value;
        }
    } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Gets or sets how long the outcome of an upload, which is its handler's last item or its error, is kept for completion requests that come back for
    /// it. Must be greater than zero and at most 24 days. Default is 1 hour.
    /// </summary>
    /// <remarks>
    /// A creation with the upload's ID gets the finished upload as long.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero, negative or longer than 24 days.</exception>
    public TimeSpan OutcomeRetention {
        get;
        set {
            EnsureNotFrozen();
            CheckDuration(value);
            field = value;
        }
    } = TimeSpan.FromHours(1);

    /// <summary>
    /// Gets or sets how often a server checks whether an upload whose handler it runs was cancelled on another server. Must be greater than zero and at
    /// most 24 days. Default is 1 second.
    /// </summary>
    /// <remarks>
    /// The servers check only when the upload store is shared (see <see cref="IUploadStore.SharingMode"/>), and cancel the handler's cancellation token
    /// when they find the cancellation. Each check reads one value of the <see cref="IShortLivedStore"/>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero, negative or longer than 24 days.</exception>
    public TimeSpan CancelCheckInterval {
        get;
        set {
            EnsureNotFrozen();
            CheckDuration(value);
            field = value;
        }
    } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets how long a completion stream waits for the handler's next item before it sends a ping record. Must be greater than zero and at most
    /// 24 days. Default is 10 seconds.
    /// </summary>
    /// <remarks>
    /// A <see cref="KeepAlivePingAttribute"/> on the handler sets the interval of its endpoint instead.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero, negative or longer than 24 days.</exception>
    public TimeSpan CompletionPingInterval {
        get;
        set {
            EnsureNotFrozen();
            CheckDuration(value);
            field = value;
        }
    } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets a value indicating whether the options are in use: chunked uploads checked them (see <see cref="Validate"/>) when they took them, and they can no
    /// longer be changed.
    /// </summary>
    public bool IsFrozen { get; private set; }

    /// <summary>
    /// Checks the options as chunked uploads do when they take them. The chunk sizes are in order: <see cref="MinChunkSize"/> is not greater than
    /// <see cref="MaxChunkSize"/>, and <see cref="DefaultChunkSize"/> is from one to the other.
    /// </summary>
    /// <exception cref="InvalidOperationException">The options are not valid. The message lists every problem.</exception>
    public void Validate()
    {
        var problems = GetProblems();

        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(" ", problems));
    }

    /// <summary>
    /// Checks the options (see <see cref="Validate"/>) and keeps them from being changed from then on, when chunked uploads take them. Options that are frozen
    /// already stay as they are.
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
        List<string> problems = [];

        if (MinChunkSize > MaxChunkSize)
        {
            problems.Add(
                $"{nameof(ChunkedUploadOptions)}.{nameof(MinChunkSize)} ({MinChunkSize}) is greater than {nameof(MaxChunkSize)} ({MaxChunkSize}).");
        }
        else if (DefaultChunkSize < MinChunkSize || DefaultChunkSize > MaxChunkSize)
        {
            problems.Add(
                $"{nameof(ChunkedUploadOptions)}.{nameof(DefaultChunkSize)} ({DefaultChunkSize}) is not from {nameof(MinChunkSize)} ({MinChunkSize}) to " +
                $"{nameof(MaxChunkSize)} ({MaxChunkSize}).");
        }

        return problems;
    }

    /// <summary>
    /// Checks a duration, which timers measure: they take at most about 24.8 days.
    /// </summary>
    private static void CheckDuration(TimeSpan value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, nameof(value));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, TimeSpan.FromDays(24), nameof(value));
    }

    private static void CheckChunkSize(int value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, ChunkSizeFloor, nameof(value));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, ChunkSizeCeiling, nameof(value));
    }

    private void EnsureNotFrozen()
    {
        if (IsFrozen)
            throw new InvalidOperationException($"The {nameof(ChunkedUploadOptions)} are in use, so they can no longer be changed.");
    }
}
