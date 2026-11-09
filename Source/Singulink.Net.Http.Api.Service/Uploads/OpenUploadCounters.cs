using System.Globalization;

namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// Counts the open uploads for their limits, in counters in the <see cref="IShortLivedStore"/>: the number of each user's open uploads, the total of their
/// lengths, and the total length of the open uploads of all users. The servers that share the short-lived store share the counters, unless they are kept
/// for each machine. A creation adds its upload to the counters before it creates it. The request or cleanup that ends an upload takes it out of them when
/// the store says that this ending ended the upload.
/// </summary>
/// <remarks>
/// The counters can differ from the open uploads in the store for a while. A subtraction that fails leaves them too high. An upload that ends after its
/// counters lapsed leaves them too low, even negative. A store that keeps them in memory starts them at 0 after a restart. Each cleanup sets them to the
/// open uploads that its listing of the store found (see <see cref="CorrectAsync"/>).
/// </remarks>
internal sealed class OpenUploadCounters
{
    // How long a counter is kept after it last changed or was corrected: three cleanups, each of which keeps the counters of the users with open uploads.
    private static readonly TimeSpan Lifetime = 3 * ChunkedUploads.CleanupInterval;

    private readonly IShortLivedStore _store;
    private readonly ILogger _logger;
    private readonly string _countKeyPrefix;
    private readonly string _sizeKeyPrefix;
    private readonly string _totalSizeKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenUploadCounters"/> class.
    /// </summary>
    /// <param name="store">The short-lived store that keeps the counters.</param>
    /// <param name="machineName">The name of the machine whose open uploads the counters count, or <see langword="null"/> to count those of all servers
    /// that share the short-lived store.</param>
    /// <param name="logger">The logger.</param>
    public OpenUploadCounters(IShortLivedStore store, string? machineName, ILogger logger)
    {
        // Machine names have no colon, so the machine's keys can't be taken for those of another machine, and the name follows an at sign where the keys
        // of all servers have a colon, so neither can be taken for the other.
        string scope = machineName is null ? string.Empty : "@" + machineName;

        _store = store;
        _logger = logger;
        _countKeyPrefix = ShortLivedKeys.UploadCount + scope + ":";
        _sizeKeyPrefix = ShortLivedKeys.UploadSize + scope + ":";
        _totalSizeKey = ShortLivedKeys.UploadTotalSize + scope;
    }

    /// <summary>
    /// Reads the total length of the open uploads of all users, as the limit of all of them sees it: 0 while the counter has no value.
    /// </summary>
    public async ValueTask<long> ReadTotalSizeAsync(CancellationToken cancellationToken)
    {
        string? value = await _store.GetAsync(_totalSizeKey, cancellationToken);
        return long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long size) ? size : 0;
    }

    /// <summary>
    /// Specifies the limit of open uploads that a new upload would be over.
    /// </summary>
    public enum ExceededLimit
    {
        None,
        UploadsPerUser,
        TotalSizePerUser,
        TotalSize,
    }

    /// <summary>
    /// Specifies the counters that an upload is added to, in the order in which <see cref="TryAddAsync"/> adds it to them.
    /// </summary>
    private enum Counter
    {
        None,
        Count,
        Size,
        TotalSize,
    }

    /// <summary>
    /// Reads the counters of a user's open uploads and the total length of all open uploads, without changing them. The counters of limits that aren't set
    /// aren't read, and count as 0.
    /// </summary>
    public async ValueTask<Counts> ReadAsync(string userId, Limits limits, CancellationToken cancellationToken)
    {
        long uploads = ParseCount(await _store.GetAsync(_countKeyPrefix + userId, cancellationToken));

        long size = limits.MaxTotalSizePerUser is null ? 0 : ParseCount(await _store.GetAsync(_sizeKeyPrefix + userId, cancellationToken));
        long totalSize = limits.MaxTotalSize is null ? 0 : ParseCount(await _store.GetAsync(_totalSizeKey, cancellationToken));

        return new(uploads, size, totalSize);
    }

    /// <summary>
    /// Gets the limit that a new upload would be over by the counts of the open uploads, or <see cref="ExceededLimit.None"/> if it is within all of them.
    /// </summary>
    /// <param name="counts">The counts of the open uploads of the upload's user and of all users.</param>
    /// <param name="length">The length of the upload, which is no longer than the total sizes of the limits.</param>
    /// <param name="limits">The limits.</param>
    public static ExceededLimit GetLimit(Counts counts, long length, Limits limits)
    {
        if (counts.Uploads >= limits.MaxUploadsPerUser)
            return ExceededLimit.UploadsPerUser;

        if (limits.MaxTotalSizePerUser is { } maxTotalSizePerUser && counts.Size > maxTotalSizePerUser - length)
            return ExceededLimit.TotalSizePerUser;

        if (limits.MaxTotalSize is { } maxTotalSize && counts.TotalSize > maxTotalSize - length)
            return ExceededLimit.TotalSize;

        return ExceededLimit.None;
    }

    /// <summary>
    /// Adds a new upload to the counters before it is created, one counter after another. If the upload would then be over a limit, or the store fails, it
    /// is taken out of the counters that it was added to. It takes no cancellation token, so that it never leaves the upload in only some of the counters.
    /// </summary>
    /// <returns><see cref="ExceededLimit.None"/> if the upload was added, or the limit that it would be over.</returns>
    public async ValueTask<ExceededLimit> TryAddAsync(UploadRecord upload, Limits limits)
    {
        var added = Counter.None;
        ExceededLimit limit;

        try
        {
            limit = await AddAsync();
        }
        catch
        {
            await SubtractAsync(upload, added);
            throw;
        }

        if (limit is not ExceededLimit.None)
            await SubtractAsync(upload, added);

        return limit;

        async ValueTask<ExceededLimit> AddAsync()
        {
            long uploads = await _store.IncrementAsync(_countKeyPrefix + upload.UserId, 1, Lifetime, CancellationToken.None);
            added = Counter.Count;

            if (uploads > limits.MaxUploadsPerUser)
                return ExceededLimit.UploadsPerUser;

            long size = await _store.IncrementAsync(_sizeKeyPrefix + upload.UserId, upload.Length, Lifetime, CancellationToken.None);
            added = Counter.Size;

            if (size > limits.MaxTotalSizePerUser)
                return ExceededLimit.TotalSizePerUser;

            long totalSize = await _store.IncrementAsync(_totalSizeKey, upload.Length, Lifetime, CancellationToken.None);
            added = Counter.TotalSize;

            return totalSize > limits.MaxTotalSize ? ExceededLimit.TotalSize : ExceededLimit.None;
        }
    }

    /// <summary>
    /// Takes an upload out of the counters, such as one that ended. A failure is logged and left to the next cleanup, so that it never fails the request that
    /// ended the upload.
    /// </summary>
    public ValueTask SubtractAsync(UploadRecord upload) => SubtractAsync(upload, Counter.TotalSize);

    /// <summary>
    /// Corrects the counters to the open uploads that a listing of the open uploads of all users found: those of the users who have open uploads, and the
    /// total length of all open uploads. The counters are read once the listing has ended, since the uploads that it ended were taken out of them already.
    /// A change of the open uploads while the listing ran is lost until the next correction. A counter that can't be corrected stays as it is, and the
    /// failure is logged.
    /// </summary>
    public async ValueTask CorrectAsync(Tally found)
    {
        Exception? failure = null;
        int failures = 0;

        foreach (string userId in found.UserIds)
        {
            var (uploads, size) = found.GetUser(userId);
            await TryCorrectAsync(_countKeyPrefix + userId, uploads);
            await TryCorrectAsync(_sizeKeyPrefix + userId, size);
        }

        await TryCorrectAsync(_totalSizeKey, found.TotalSize);

        if (failure is not null)
        {
            _logger.LogError(
                failure,
                "Counters of open uploads could not be corrected, and stay as they are until the next cleanup. Counters that failed: {Failures}.",
                failures);
        }

        async ValueTask TryCorrectAsync(string key, long count)
        {
            try
            {
                // Adding 0 reads the counter and keeps it for its lifetime again.
                long counted = await _store.IncrementAsync(key, 0, Lifetime, CancellationToken.None);

                if (counted != count)
                    await _store.IncrementAsync(key, count - counted, Lifetime, CancellationToken.None);
            }
            catch (Exception ex)
            {
                failure ??= ex;
                failures++;
            }
        }
    }

    private static long ParseCount(string? value)
    {
        return long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long count) ? count : 0;
    }

    /// <summary>
    /// Takes an upload out of the counters up to the specified one, in the order in which <see cref="TryAddAsync"/> adds it to them.
    /// </summary>
    private async ValueTask SubtractAsync(UploadRecord upload, Counter last)
    {
        try
        {
            if (last >= Counter.Count)
                await _store.IncrementAsync(_countKeyPrefix + upload.UserId, -1, Lifetime, CancellationToken.None);

            if (last >= Counter.Size)
                await _store.IncrementAsync(_sizeKeyPrefix + upload.UserId, -upload.Length, Lifetime, CancellationToken.None);

            if (last >= Counter.TotalSize)
                await _store.IncrementAsync(_totalSizeKey, -upload.Length, Lifetime, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Upload {UploadId} could not be taken out of the counts of open uploads, which stay too high until the next cleanup corrects them.",
                upload.Id);
        }
    }

    /// <summary>
    /// The counts of the open uploads of a user and of all users.
    /// </summary>
    /// <param name="Uploads">The number of open uploads of the user.</param>
    /// <param name="Size">The total length of the open uploads of the user.</param>
    /// <param name="TotalSize">The total length of the open uploads of all users.</param>
    public readonly record struct Counts(long Uploads, long Size, long TotalSize);

    /// <summary>
    /// The limits of the open uploads of a user and of all users.
    /// </summary>
    /// <param name="MaxUploadsPerUser">The largest number of open uploads of the user.</param>
    /// <param name="MaxTotalSizePerUser">The largest total length of the open uploads of the user, or <see langword="null"/> for none.</param>
    /// <param name="MaxTotalSize">The largest total length of the open uploads of all users, or <see langword="null"/> for none.</param>
    public readonly record struct Limits(int MaxUploadsPerUser, long? MaxTotalSizePerUser, long? MaxTotalSize);

    /// <summary>
    /// The open uploads that a listing of the store found: the number of each user's and the total of their lengths, and the total length of all of them.
    /// </summary>
    public sealed class Tally
    {
        private readonly Dictionary<string, (long Uploads, long Size)> _users = new(StringComparer.Ordinal);

        public IEnumerable<string> UserIds => _users.Keys;

        public int UserCount => _users.Count;

        public long Uploads { get; private set; }

        public long TotalSize { get; private set; }

        public void Add(UploadRecord upload)
        {
            var (uploads, size) = _users.GetValueOrDefault(upload.UserId);
            _users[upload.UserId] = (uploads + 1, size + upload.Length);
            Uploads++;
            TotalSize += upload.Length;
        }

        public (long Uploads, long Size) GetUser(string userId) => _users.GetValueOrDefault(userId);
    }
}
