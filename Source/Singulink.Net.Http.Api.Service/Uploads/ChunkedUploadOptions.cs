namespace Singulink.Net.Http.Api.Service.Uploads;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Provides options for chunked uploads: where the directory store keeps them, their sizes and chunk sizes, the limits of open uploads, and how long
/// uploads and their outcomes are kept.
/// </summary>
/// <remarks>
/// Chunk sizes are always from 5 MiB, the smallest part size that object stores such as Amazon S3 accept for all but the last part, to 64 MiB, which keeps
/// every request well under the smallest request body limit of Cloudflare's plans (100 MB). A file smaller than a chunk is sent as a single chunk. The
/// <see cref="IUploadStore"/> can narrow the bounds further (see <see cref="IUploadStore.MinChunkSize"/> and <see cref="IUploadStore.MaxChunkSize"/>).
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
    /// Gets or sets the directory that the directory store keeps uploads in. Relative paths are resolved against the application's content root. Servers
    /// that share uploads use the same directory, such as a network share mounted at the same path. Required unless the app registers an
    /// <see cref="IUploadStore"/> of its own: the application fails to start if neither is set.
    /// </summary>
    public string? Directory {
        get;
        set {
            if (value is not null)
                ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(value));

            field = value;
        }
    }

    /// <summary>
    /// Gets or sets the largest upload, in bytes, for endpoints whose <see cref="ChunkedUploadEndpointOptions.MaxSize"/> is not set. Default is 1 GiB.
    /// </summary>
    public long MaxSize {
        get;
        set {
            ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(value));
            field = value;
        }
    } = 1L << 30;

    /// <summary>
    /// Gets or sets the chunk size of uploads whose client does not ask for one, in bytes. It must be from <see cref="MinChunkSize"/> to
    /// <see cref="MaxChunkSize"/>, which is checked when the application starts. Default is 5 MiB.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than <see cref="ChunkSizeFloor"/> or greater than
    /// <see cref="ChunkSizeCeiling"/>.</exception>
    public int DefaultChunkSize {
        get;
        set {
            CheckChunkSize(value);
            field = value;
        }
    } = ChunkedUploadProtocol.DefaultChunkSize;

    /// <summary>
    /// Gets or sets the smallest chunk size that a client can ask for, in bytes. Smaller requested sizes are raised to it. Default is 5 MiB, which is
    /// also the smallest value.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than <see cref="ChunkSizeFloor"/> or greater than
    /// <see cref="ChunkSizeCeiling"/>.</exception>
    public int MinChunkSize {
        get;
        set {
            CheckChunkSize(value);
            field = value;
        }
    } = ChunkSizeFloor;

    /// <summary>
    /// Gets or sets the largest chunk size that a client can ask for, in bytes. Larger requested sizes are lowered to it. Default is 64 MiB, which is also
    /// the largest value.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than <see cref="ChunkSizeFloor"/> or greater than
    /// <see cref="ChunkSizeCeiling"/>.</exception>
    public int MaxChunkSize {
        get;
        set {
            CheckChunkSize(value);
            field = value;
        }
    } = ChunkSizeCeiling;

    /// <summary>
    /// Gets or sets the largest number of chunks of an upload. An upload that would have more chunks at the requested chunk size gets larger chunks, up to
    /// <see cref="MaxChunkSize"/>. Default is 10,000.
    /// </summary>
    public int MaxChunkCount {
        get;
        set {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, nameof(value));
            field = value;
        }
    } = 10_000;

    /// <summary>
    /// Gets or sets the largest number of open uploads of a user, counted across all upload endpoints. Open uploads are the uploads that are receiving
    /// chunks or whose handler is running. Default is 20.
    /// </summary>
    /// <remarks>
    /// A server counts a user's open uploads for one creation of the user at a time, so only creations on several servers at the same moment can exceed it.
    /// </remarks>
    public int MaxUploadsPerUser {
        get;
        set {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, nameof(value));
            field = value;
        }
    } = 20;

    /// <summary>
    /// Gets or sets the largest total length of the open uploads of a user, in bytes, counted across all upload endpoints, or <see langword="null"/> for no
    /// limit. Each upload counts with its length from its creation. Default is <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// A server counts a user's open uploads for one creation of the user at a time, so only creations on several servers at the same moment can exceed it.
    /// An upload that is longer than this can't be created, and fails with <see cref="UploadErrorCodes.ExceedsSizeLimit"/>, unless the maximum size of its
    /// endpoint (see <see cref="MaxSize"/>) is smaller than this and the upload is longer than that too, when it fails with
    /// <see cref="UploadErrorCodes.TooLarge"/>.
    /// </remarks>
    public long? MaxTotalSizePerUser {
        get;
        set {
            if (value is { } size)
                ArgumentOutOfRangeException.ThrowIfNegative(size, nameof(value));

            field = value;
        }
    }

    /// <summary>
    /// Gets or sets the largest total length of all open uploads, in bytes, or <see langword="null"/> for no limit. Default is <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// The total is computed every few minutes, when expired uploads are removed, and each server adds the uploads it creates to it in between, reserving
    /// an upload's length before it creates the upload. A server stays within the limit, but servers that share uploads can exceed it with the uploads they
    /// created since the total was computed. An upload that ends, with an outcome or a deletion, stops counting right away on the server where it ends, and
    /// on other servers when they next compute the total. The application logs a warning when it starts if this is not set.
    /// </remarks>
    public long? MaxTotalSize {
        get;
        set {
            if (value is { } size)
                ArgumentOutOfRangeException.ThrowIfNegative(size, nameof(value));

            field = value;
        }
    }

    /// <summary>
    /// Gets or sets how long an upload is kept after its last chunk while it is receiving chunks, and how long its handler can run, up to 24 days. Default
    /// is 1 day.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero, negative or longer than 24 days.</exception>
    public TimeSpan IdleExpiry {
        get;
        set {
            CheckDuration(value);
            field = value;
        }
    } = TimeSpan.FromDays(1);

    /// <summary>
    /// Gets or sets how long an upload that has received no chunk is kept after its creation, up to <see cref="IdleExpiry"/>. It stops counting toward
    /// the limits of open uploads when it expires. Once a chunk arrives, <see cref="IdleExpiry"/> applies. Default is 15 minutes, up to 24 days.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero, negative or longer than 24 days.</exception>
    public TimeSpan UnstartedExpiry {
        get;
        set {
            CheckDuration(value);
            field = value;
        }
    } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Gets or sets how long the outcome of an upload, which is its handler's last item or its error, is kept for completion requests that come back for
    /// it, up to 24 days. Default is 1 hour.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero, negative or longer than 24 days.</exception>
    public TimeSpan OutcomeRetention {
        get;
        set {
            CheckDuration(value);
            field = value;
        }
    } = TimeSpan.FromHours(1);

    /// <summary>
    /// Gets or sets how long a completion stream waits for the handler's next item before it sends a ping record. A <see cref="KeepAlivePingAttribute"/>
    /// on the handler sets the interval of its endpoint instead. Up to 24 days, and default is 10 seconds.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero, negative or longer than 24 days.</exception>
    public TimeSpan CompletionPingInterval {
        get;
        set {
            CheckDuration(value);
            field = value;
        }
    } = TimeSpan.FromSeconds(10);

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
}
