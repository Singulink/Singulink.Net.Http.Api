using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// An <see cref="IShortLivedStore"/> that keeps values in memory, up to <see cref="MemoryShortLivedStoreOptions.MaxSize"/>.
/// </summary>
/// <remarks>
/// <para>
/// Values only exist in the process that added them, so the other servers of a service do not see them. A restart loses them, which interrupts the flows
/// in progress, resets rate limit counts, and empties the counts of open chunked uploads until the first cleanup of uploads after the start.
/// </para>
/// <para>
/// When the store is full, adding a value or a counter throws a <see cref="ServiceUnavailableApiException"/> (see
/// <see cref="MemoryShortLivedStoreOptions.MaxSize"/>).
/// </para>
/// </remarks>
public sealed partial class MemoryShortLivedStore : IShortLivedStore
{
    private const string FullMessage = "The service is too busy to handle this request. Please try again in a minute.";

    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    // A full store sweeps before refusing a write, but no more often than this, so that a store full of live entries does not sweep on every write.
    private static readonly TimeSpan FullSweepInterval = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan FullRetryAfter = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FullWarningInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly long _maxSize;
    private readonly ILogger _logger;

    private long _size;
    private long _refusedWrites;
    private long _lastRefusedTicks;
    private long _nextSweepTicks;
    private long _nextFullSweepTicks;
    private long _nextFullWarningTicks;

    /// <summary>
    /// Initializes a new instance of the <see cref="MemoryShortLivedStore"/> class with the default options.
    /// </summary>
    /// <param name="timeProvider">The clock that values expire by.</param>
    public MemoryShortLivedStore(TimeProvider timeProvider)
        : this(timeProvider, Options.Create(new MemoryShortLivedStoreOptions()), NullLogger<MemoryShortLivedStore>.Instance)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MemoryShortLivedStore"/> class.
    /// </summary>
    /// <param name="timeProvider">The clock that values expire by.</param>
    /// <param name="options">The options.</param>
    /// <param name="logger">The logger that a full store is logged to.</param>
    public MemoryShortLivedStore(TimeProvider timeProvider, IOptions<MemoryShortLivedStoreOptions> options, ILogger<MemoryShortLivedStore> logger)
    {
        _timeProvider = timeProvider;
        options.Value.Freeze();
        _maxSize = options.Value.MaxSize;
        _logger = logger;
    }

    /// <summary>
    /// Gets who sees the store's values: only this process (<see cref="StoreSharingMode.ProcessLocal"/>), since they exist only in its memory.
    /// </summary>
    public StoreSharingMode SharingMode => StoreSharingMode.ProcessLocal;

    /// <summary>
    /// Gets the number of entries, including expired entries that have not been swept out yet (every minute while values are written, and at once when
    /// the store is full), their estimated size, the store's <see cref="MemoryShortLivedStoreOptions.MaxSize"/>, and the writes the store refused because
    /// it was full.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public ValueTask<ShortLivedStoreUsage> GetUsageAsync(CancellationToken cancellationToken = default) => new(GetUsage());

    /// <inheritdoc/>
    public ValueTask<bool> TryAddAsync(string key, string value, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        return new(TryAdd(key, value, lifetime));
    }

    /// <inheritdoc/>
    public ValueTask SetAsync(string key, string value, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        Set(key, value, lifetime);
        return default;
    }

    /// <inheritdoc/>
    public ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default) => new(Get(key));

    /// <inheritdoc/>
    public ValueTask<string?> TakeAsync(string key, CancellationToken cancellationToken = default) => new(Take(key));

    /// <inheritdoc/>
    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_entries.TryRemove(key, out var removed))
            AddSize(-removed.Size);

        return default;
    }

    /// <inheritdoc/>
    public ValueTask<long> IncrementAsync(string key, long amount, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        return new(Increment(key, amount, lifetime));
    }

    internal long EntryCount => _entries.Count;

    internal long UsedBytes => Interlocked.Read(ref _size);

    internal long MaxBytes => _maxSize;

    internal long RefusedWrites => Interlocked.Read(ref _refusedWrites);

    internal ShortLivedStoreUsage GetUsage()
    {
        long lastRefusedTicks = Interlocked.Read(ref _lastRefusedTicks);

        return new ShortLivedStoreUsage {
            EntryCount = EntryCount,
            UsedBytes = UsedBytes,
            MaxBytes = MaxBytes,
            RefusedWrites = RefusedWrites,
            LastRefusedWriteUtc = lastRefusedTicks is 0 ? null : new DateTimeOffset(lastRefusedTicks, TimeSpan.Zero),
        };
    }

    internal bool TryAdd(string key, string value, TimeSpan lifetime)
    {
        var now = _timeProvider.GetUtcNow();
        var entry = Entry.ForValue(key, value, GetExpiry(now, lifetime));
        SweepIfDue(ref _nextSweepTicks, SweepInterval, now);

        while (true)
        {
            if (!_entries.TryGetValue(key, out var existing))
            {
                EnsureRoom(entry.Size, now);

                if (_entries.TryAdd(key, entry))
                {
                    AddSize(entry.Size);
                    return true;
                }

                continue;
            }

            if (!existing.IsExpired(now))
                return false;

            EnsureRoom(entry.Size - existing.Size, now);

            if (_entries.TryUpdate(key, entry, existing))
            {
                AddSize(entry.Size - existing.Size);
                return true;
            }
        }
    }

    internal void Set(string key, string value, TimeSpan lifetime)
    {
        var now = _timeProvider.GetUtcNow();
        var entry = Entry.ForValue(key, value, GetExpiry(now, lifetime));
        SweepIfDue(ref _nextSweepTicks, SweepInterval, now);

        while (true)
        {
            if (!_entries.TryGetValue(key, out var existing))
            {
                EnsureRoom(entry.Size, now);

                if (_entries.TryAdd(key, entry))
                {
                    AddSize(entry.Size);
                    return;
                }

                continue;
            }

            EnsureRoom(entry.Size - existing.Size, now);

            if (_entries.TryUpdate(key, entry, existing))
            {
                AddSize(entry.Size - existing.Size);
                return;
            }
        }
    }

    internal string? Get(string key)
    {
        return _entries.TryGetValue(key, out var entry) && !entry.IsExpired(_timeProvider.GetUtcNow()) ? entry.GetValue() : null;
    }

    internal string? Take(string key)
    {
        while (_entries.TryGetValue(key, out var entry))
        {
            // Removing the entry that was read, rather than whatever the key holds by now, is what makes a single caller win.
            if (_entries.TryRemove(new KeyValuePair<string, Entry>(key, entry)))
            {
                AddSize(-entry.Size);
                return entry.IsExpired(_timeProvider.GetUtcNow()) ? null : entry.GetValue();
            }
        }

        return null;
    }

    internal long Increment(string key, long amount, TimeSpan lifetime)
    {
        var now = _timeProvider.GetUtcNow();
        var expiresUtc = GetExpiry(now, lifetime);
        SweepIfDue(ref _nextSweepTicks, SweepInterval, now);

        while (true)
        {
            if (!_entries.TryGetValue(key, out var existing))
            {
                var created = Entry.ForCounter(key, amount, expiresUtc);
                EnsureRoom(created.Size, now);

                if (_entries.TryAdd(key, created))
                {
                    AddSize(created.Size);
                    return amount;
                }

                continue;
            }

            long count = existing.IsExpired(now) ? amount : checked(existing.GetCount() + amount);
            var updated = Entry.ForCounter(key, count, expiresUtc);
            EnsureRoom(updated.Size - existing.Size, now);

            if (_entries.TryUpdate(key, updated, existing))
            {
                AddSize(updated.Size - existing.Size);
                return count;
            }
        }
    }

    private static DateTimeOffset GetExpiry(DateTimeOffset now, TimeSpan lifetime)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        return lifetime < DateTimeOffset.MaxValue - now ? now + lifetime : DateTimeOffset.MaxValue;
    }

    private void AddSize(long size) => Interlocked.Add(ref _size, size);

    /// <summary>
    /// Throws if growing the store by the specified size would exceed the maximum size even after expired entries are swept out.
    /// </summary>
    private void EnsureRoom(long growth, DateTimeOffset now)
    {
        if (growth <= 0 || Interlocked.Read(ref _size) + growth <= _maxSize)
            return;

        if (SweepIfDue(ref _nextFullSweepTicks, FullSweepInterval, now) && Interlocked.Read(ref _size) + growth <= _maxSize)
            return;

        Interlocked.Increment(ref _refusedWrites);
        Interlocked.Exchange(ref _lastRefusedTicks, now.UtcTicks);

        if (IsDue(ref _nextFullWarningTicks, FullWarningInterval, now))
            LogFull(Interlocked.Read(ref _size), _maxSize);

        throw new ServiceUnavailableApiException(FullMessage) { ErrorCode = ApiErrorCodes.ShortLivedStoreFull, RetryAfter = FullRetryAfter };
    }

    // Values that are never taken would otherwise stay forever, so expired entries are swept out from time to time as new ones are written.
    private bool SweepIfDue(ref long nextSweepTicks, TimeSpan interval, DateTimeOffset now)
    {
        if (!IsDue(ref nextSweepTicks, interval, now))
            return false;

        foreach (var entry in _entries)
        {
            if (entry.Value.IsExpired(now) && _entries.TryRemove(entry))
                AddSize(-entry.Value.Size);
        }

        return true;
    }

    // Returns true for one caller once the due time has passed, and sets the next due time.
    private static bool IsDue(ref long nextTicks, TimeSpan interval, DateTimeOffset now)
    {
        long dueTicks = Interlocked.Read(ref nextTicks);
        return now.UtcTicks >= dueTicks && Interlocked.CompareExchange(ref nextTicks, (now + interval).UtcTicks, dueTicks) == dueTicks;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The short-lived store is full: its values and counters take up about {Size} of the {MaxSize} bytes allowed by " +
            "MemoryShortLivedStoreOptions.MaxSize. Requests that need to store a value or start a counter are refused with 503 Service Unavailable until " +
            "values expire. This is logged at most once a minute.")]
    private partial void LogFull(long size, long maxSize);

    /// <summary>
    /// A stored value or counter. Entries are immutable and compared by reference, so that updates and removals only succeed on the entry that was read.
    /// </summary>
    private sealed class Entry
    {
        // Roughly what an entry costs besides the characters of its key and value: the entry, the key and value strings, and the dictionary node.
        private const int Overhead = 120;

        private readonly string? _value;
        private readonly long _count;

        private Entry(string? value, long count, DateTimeOffset expiresUtc, long size)
        {
            _value = value;
            _count = count;
            ExpiresUtc = expiresUtc;
            Size = size;
        }

        public DateTimeOffset ExpiresUtc { get; }

        /// <summary>
        /// Gets the estimated memory that the entry and its key take up, in bytes.
        /// </summary>
        public long Size { get; }

        public static Entry ForValue(string key, string value, DateTimeOffset expiresUtc)
        {
            return new(value, 0, expiresUtc, Overhead + (2L * (key.Length + value.Length)));
        }

        public static Entry ForCounter(string key, long count, DateTimeOffset expiresUtc) => new(null, count, expiresUtc, Overhead + (2L * key.Length));

        public bool IsExpired(DateTimeOffset now) => ExpiresUtc <= now;

        public string GetValue() => _value ?? _count.ToString(CultureInfo.InvariantCulture);

        public long GetCount()
        {
            if (_value is null)
                return _count;

            return long.TryParse(_value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long count) ?
                count : throw new InvalidOperationException("The key holds a value that is not a count.");
        }
    }
}
