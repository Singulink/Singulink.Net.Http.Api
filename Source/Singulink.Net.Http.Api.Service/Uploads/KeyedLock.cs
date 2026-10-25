namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// Asynchronous locks by key, such as by user. A key's lock exists while it is held or awaited.
/// </summary>
internal sealed class KeyedLock
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// Waits for the lock of a key and holds it until the returned releaser is disposed.
    /// </summary>
    public async ValueTask<Releaser> EnterAsync(string key, CancellationToken cancellationToken)
    {
        Entry entry;

        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out entry!))
                _entries[key] = entry = new Entry();

            entry.Users++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken);
        }
        catch
        {
            Leave(key, entry);
            throw;
        }

        return new Releaser(this, key, entry);
    }

    private void Leave(string key, Entry entry)
    {
        lock (_entries)
        {
            if (--entry.Users is 0)
            {
                _entries.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    /// <summary>
    /// Releases a lock that is held when it is disposed.
    /// </summary>
    internal readonly struct Releaser(KeyedLock owner, string key, Entry entry) : IDisposable
    {
        public void Dispose()
        {
            entry.Semaphore.Release();
            owner.Leave(key, entry);
        }
    }

    /// <summary>
    /// The lock of a key, and the number of callers that hold it or wait for it.
    /// </summary>
    internal sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Users { get; set; }
    }
}
