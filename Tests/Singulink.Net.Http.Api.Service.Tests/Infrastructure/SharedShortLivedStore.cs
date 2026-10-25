using System.Globalization;
using System.Text;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// A short-lived store that stands in for shared storage such as Redis or a database table. Every operation completes asynchronously, values are kept only
/// as UTF-8 bytes, so nothing but what was serialized survives a round trip, and any number of service instances can share it, as separate servers would.
/// </summary>
public sealed class SharedShortLivedStore(TimeProvider timeProvider) : IShortLivedStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the keys that currently have a value or counter.
    /// </summary>
    public IReadOnlyList<string> Keys
    {
        get {
            lock (_lock)
            {
                var now = timeProvider.GetUtcNow();
                return _entries.Where(e => e.Value.ExpiresUtc > now).Select(e => e.Key).ToList();
            }
        }
    }

    public async ValueTask<bool> TryAddAsync(string key, string value, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        await Task.Yield();

        lock (_lock)
        {
            var now = timeProvider.GetUtcNow();

            if (_entries.TryGetValue(key, out var entry) && entry.ExpiresUtc > now)
                return false;

            _entries[key] = new Entry(Encoding.UTF8.GetBytes(value), now + lifetime);
            return true;
        }
    }

    public async ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        lock (_lock)
            return _entries.TryGetValue(key, out var entry) && entry.ExpiresUtc > timeProvider.GetUtcNow() ? Encoding.UTF8.GetString(entry.Value) : null;
    }

    public async ValueTask<string?> TakeAsync(string key, CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        lock (_lock)
            return _entries.Remove(key, out var entry) && entry.ExpiresUtc > timeProvider.GetUtcNow() ? Encoding.UTF8.GetString(entry.Value) : null;
    }

    public async ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        lock (_lock)
            _entries.Remove(key);
    }

    public async ValueTask<long> IncrementAsync(string key, long amount, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        await Task.Yield();

        lock (_lock)
        {
            var now = timeProvider.GetUtcNow();
            long count = amount;

            if (_entries.TryGetValue(key, out var entry) && entry.ExpiresUtc > now)
            {
                count += long.TryParse(Encoding.UTF8.GetString(entry.Value), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long existing) ?
                    existing : throw new InvalidOperationException("The key holds a value that is not a count.");
            }

            _entries[key] = new Entry(Encoding.UTF8.GetBytes(count.ToString(CultureInfo.InvariantCulture)), now + lifetime);
            return count;
        }
    }

    private sealed record Entry(byte[] Value, DateTimeOffset ExpiresUtc);
}
