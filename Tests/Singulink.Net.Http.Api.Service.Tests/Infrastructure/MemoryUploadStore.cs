using System.Runtime.CompilerServices;
using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// An upload store that keeps uploads in memory, like a store over an object store: its content is only available as a stream, it can have chunk size
/// limits of its own, and it keeps finished uploads apart from the open ones.
/// </summary>
public sealed class MemoryUploadStore(TimeProvider timeProvider) : IUploadStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(string UserId, string Id), Entry> _uploads = [];
    private readonly Dictionary<(string UserId, string Id), Finished> _finished = [];

    public int MinChunkSize { get; init; }

    public int MaxChunkSize { get; init; } = int.MaxValue;

    public int MaxChunkCount { get; init; } = int.MaxValue;

    /// <summary>
    /// Gets who uses the store: one host (<see cref="StoreSharingMode.ProcessLocal"/>), which is the default, or several. Hosts that share the store set it to
    /// <see cref="StoreSharingMode.Shared"/>, and share a shared short-lived store too, such as a <see cref="DeclaredSharedShortLivedStore"/>.
    /// </summary>
    public StoreSharingMode SharingMode { get; init; } = StoreSharingMode.ProcessLocal;

    /// <summary>
    /// Gets <see cref="Usage"/>, after <see cref="BeforeOperation"/> with <c>GetUsage</c>.
    /// </summary>
    public ValueTask<UploadStoreUsage> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        BeforeOperation?.Invoke("GetUsage");
        return new(Usage);
    }

    /// <summary>
    /// Gets or sets the usage that <see cref="GetUsageAsync"/> reports. Default is <see cref="UploadStoreUsage.Unknown"/>.
    /// </summary>
    public UploadStoreUsage Usage { get; set; } = UploadStoreUsage.Unknown;

    /// <summary>
    /// Gets or sets an action that is called with the name of an operation before it runs: <c>Create</c>, <c>Get</c>, <c>WriteChunk</c>, <c>Renew</c> or
    /// <c>Release</c>. It makes the operation fail by throwing.
    /// </summary>
    public Action<string>? BeforeOperation { get; set; }

    /// <summary>
    /// Gets or sets an action that is called with the name of an operation once it has succeeded: <c>Create</c>, once an upload is created, and
    /// <c>WriteChunk</c>, once a chunk is stored.
    /// </summary>
    public Action<string>? AfterOperation { get; set; }

    /// <summary>
    /// Gets or sets a hook that a claim's renewal awaits with its cancellation token after the renewal was written, such as one that waits for the token and
    /// then throws, as a store that sees the cancellation only after its write does.
    /// </summary>
    public Func<CancellationToken, Task>? AfterRenewal { get; set; }

    /// <summary>
    /// Gets or sets a function that tells whether an upload is being removed, as a store's files can be: a creation of the upload then reports that it
    /// exists, while reads find none.
    /// </summary>
    public Func<UploadRecord, bool>? IsBeingRemoved { get; set; }

    /// <summary>
    /// Gets or sets a task that listings wait for, such as of a cleanup that is still running.
    /// </summary>
    public Task? ListWaitsFor { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the store has no room for chunks.
    /// </summary>
    public bool IsFull { get; set; }

    /// <summary>
    /// Gets the number of uploads, open and finished.
    /// </summary>
    public int Count
    {
        get {
            lock (_lock)
                return _uploads.Count + _finished.Count;
        }
    }

    /// <summary>
    /// Gets the number of uploads that listings have returned so far.
    /// </summary>
    public int ListedCount
    {
        get {
            lock (_lock)
                return field;
        }

        private set;
    }

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    public ValueTask<UploadCreationResult> TryCreateAsync(UploadRecord upload, CancellationToken cancellationToken = default)
    {
        BeforeOperation?.Invoke("Create");

        lock (_lock)
        {
            if (_uploads.ContainsKey(Key(upload)) || _finished.ContainsKey(Key(upload)) || IsBeingRemoved?.Invoke(upload) is true)
                return new(UploadCreationResult.Exists);

            _uploads.Add(Key(upload), new Entry(upload, new byte[upload.Length], upload.CreatedUtc));
        }

        AfterOperation?.Invoke("Create");
        return new(UploadCreationResult.Created);
    }

    public ValueTask<StoredUpload?> GetAsync(string userId, string uploadId, CancellationToken cancellationToken = default)
    {
        BeforeOperation?.Invoke("Get");

        lock (_lock)
        {
            if (_uploads.TryGetValue((userId, uploadId), out var entry))
                return new(Snapshot(entry));

            if (_finished.TryGetValue((userId, uploadId), out var finished))
                return new(new StoredUpload(finished.Record, finished.Record.CreatedUtc, outcome: finished.Outcome, outcomeUtc: finished.StoredUtc));

            return new((StoredUpload?)null);
        }
    }

    public async IAsyncEnumerable<StoredUpload> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<StoredUpload> uploads;

        if (ListWaitsFor is { } wait)
            await wait.WaitAsync(cancellationToken);

        lock (_lock)
        {
            uploads = [.. _uploads.Values.Select(Snapshot)];
            ListedCount += uploads.Count;
        }

        foreach (var upload in uploads)
        {
            await Task.Yield();
            yield return upload;
        }
    }

    public async ValueTask<ChunkWriteResult> WriteChunkAsync(UploadRecord upload, int index, Stream content, CancellationToken cancellationToken = default)
    {
        BeforeOperation?.Invoke("WriteChunk");

        if (IsFull)
            return ChunkWriteResult.NoRoom;

        // The upload is kept from the start of the write, also while a slow chunk arrives.
        lock (_lock)
        {
            if (_uploads.TryGetValue(Key(upload), out var started) && started.ClaimId is null)
                started.LastWriteUtc = UtcNow > upload.CreatedUtc ? UtcNow : upload.CreatedUtc.AddTicks(1);
        }

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);

        // The chunk is stored all at once, unless the run was claimed while it arrived.
        lock (_lock)
        {
            if (!_uploads.TryGetValue(Key(upload), out var entry))
                return _finished.ContainsKey(Key(upload)) ? ChunkWriteResult.Closed : ChunkWriteResult.NotFound;

            if (entry.ClaimId is not null)
                return ChunkWriteResult.Closed;

            buffer.ToArray().CopyTo(entry.Data, upload.GetChunkOffset(index));
            entry.Chunks.Add(index);
            entry.LastWriteUtc = UtcNow > upload.CreatedUtc ? UtcNow : upload.CreatedUtc.AddTicks(1);
        }

        AfterOperation?.Invoke("WriteChunk");
        return ChunkWriteResult.Written;
    }

    public ValueTask<IReadOnlyCollection<int>> GetReceivedChunksAsync(UploadRecord upload, CancellationToken cancellationToken = default)
    {
        lock (_lock)
            return new(_uploads.TryGetValue(Key(upload), out var entry) ? [.. entry.Chunks] : []);
    }

    public ValueTask<IUploadRunClaim?> TryClaimRunAsync(UploadRecord upload, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_uploads.TryGetValue(Key(upload), out var entry) || entry.ClaimId is not null)
                return new((IUploadRunClaim?)null);

            entry.ClaimId = Guid.NewGuid();
            entry.LeaseDuration = leaseDuration;
            entry.RunStartedUtc = entry.RenewedUtc = UtcNow;

            return new(new Claim(this, Key(upload), entry.ClaimId.Value));
        }
    }

    public ValueTask<UploadContent> GetContentAsync(UploadRecord upload, CancellationToken cancellationToken = default)
    {
        lock (_lock)
            return new(new MemoryContent(_uploads[Key(upload)].Data));
    }

    public ValueTask<bool> TrySetOutcomeAsync(UploadRecord upload, string outcome, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            // The upload moves from the open uploads to the finished ones.
            if (_finished.ContainsKey(Key(upload)) || !_uploads.Remove(Key(upload), out var entry))
                return new(false);

            _finished[Key(upload)] = new Finished(entry.Record, outcome, UtcNow);
            return new(true);
        }
    }

    public ValueTask<bool> DeleteAsync(UploadRecord upload, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            bool isDeleted = _uploads.Remove(Key(upload));
            _finished.Remove(Key(upload));

            return new(isDeleted);
        }
    }

    public ValueTask RemoveOutcomesAsync(DateTime storedBeforeUtc, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            foreach (var finished in _finished.Values.Where(f => f.StoredUtc < storedBeforeUtc).ToList())
                _finished.Remove(Key(finished.Record));
        }

        return default;
    }

    private static (string UserId, string Id) Key(UploadRecord upload) => (upload.UserId, upload.Id);

    private StoredUpload Snapshot(Entry entry)
    {
        if (entry.ClaimId is not null)
        {
            var state = entry.RenewedUtc + entry.LeaseDuration < UtcNow ? UploadRunState.Lapsed : UploadRunState.Running;
            return new StoredUpload(entry.Record, entry.LastWriteUtc, state, entry.RunStartedUtc);
        }

        return new StoredUpload(entry.Record, entry.LastWriteUtc);
    }

    private sealed class Entry(UploadRecord record, byte[] data, DateTime createdUtc)
    {
        public UploadRecord Record { get; } = record;

        public byte[] Data { get; set; } = data;

        public HashSet<int> Chunks { get; } = [];

        public DateTime LastWriteUtc { get; set; } = createdUtc;

        public Guid? ClaimId { get; set; }

        public TimeSpan LeaseDuration { get; set; }

        public DateTime RunStartedUtc { get; set; }

        public DateTime RenewedUtc { get; set; }
    }

    private sealed record Finished(UploadRecord Record, string Outcome, DateTime StoredUtc);

    private sealed class Claim(MemoryUploadStore store, (string UserId, string Id) key, Guid claimId) : IUploadRunClaim
    {
        public async ValueTask<bool> RenewAsync(CancellationToken cancellationToken = default)
        {
            store.BeforeOperation?.Invoke("Renew");

            lock (store._lock)
            {
                if (!store._uploads.TryGetValue(key, out var entry) || entry.ClaimId != claimId)
                    return false;

                entry.RenewedUtc = store.UtcNow;
            }

            if (store.AfterRenewal is { } afterRenewal)
                await afterRenewal(cancellationToken);

            return true;
        }

        public ValueTask ReleaseAsync(CancellationToken cancellationToken = default)
        {
            store.BeforeOperation?.Invoke("Release");

            lock (store._lock)
            {
                if (store._uploads.TryGetValue(key, out var entry) && entry.ClaimId == claimId)
                {
                    entry.ClaimId = null;
                    entry.LastWriteUtc = store.UtcNow;
                }
            }

            return default;
        }
    }

    /// <summary>
    /// Content without a file, which <see cref="UploadContent.MoveToAsync"/> copies.
    /// </summary>
    private sealed class MemoryContent(byte[] data) : UploadContent
    {
        public override Stream OpenReadStream() => new MemoryStream(data, writable: false);
    }
}
