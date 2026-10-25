using System.Runtime.CompilerServices;
using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// An upload store that keeps uploads in memory, like a store over an object store: its content is only available as a stream, and it can have chunk
/// size limits of its own.
/// </summary>
public sealed class MemoryUploadStore(TimeProvider timeProvider) : IUploadStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _uploads = new(StringComparer.Ordinal);
    private readonly Dictionary<(string UserId, string Key), UploadKeyReservation> _keys = [];

    public int MinChunkSize { get; init; }

    public int MaxChunkSize { get; init; } = int.MaxValue;

    public int MaxChunkCount { get; init; } = int.MaxValue;

    /// <summary>
    /// Gets or sets an action that is called with the name of an operation before it runs: <c>Get</c>, <c>WriteChunk</c> or <c>Renew</c>. It makes
    /// the operation fail by throwing.
    /// </summary>
    public Action<string>? BeforeOperation { get; set; }

    /// <summary>
    /// Gets or sets a hook that a claim's renewal awaits with its cancellation token after the renewal was written, such as one that waits for the token and
    /// then throws, as a store that sees the cancellation only after its write does.
    /// </summary>
    public Func<CancellationToken, Task>? AfterRenewal { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the store has no room for chunks.
    /// </summary>
    public bool IsFull { get; set; }

    public int Count
    {
        get {
            lock (_lock)
                return _uploads.Count;
        }
    }

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    public ValueTask<UploadKeyReservation> ReserveKeyAsync(string userId, string key, string uploadId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_keys.TryGetValue((userId, key), out var reservation))
                _keys[(userId, key)] = reservation = new UploadKeyReservation(uploadId, UtcNow);

            return new(reservation);
        }
    }

    public ValueTask ReleaseKeyAsync(string userId, string key, string uploadId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_keys.TryGetValue((userId, key), out var reservation) && reservation.UploadId == uploadId)
                _keys.Remove((userId, key));
        }

        return default;
    }

    public ValueTask<bool> TryCreateAsync(UploadRecord upload, CancellationToken cancellationToken = default)
    {
        lock (_lock)
            _uploads.Add(upload.Id, new Entry(upload, new byte[upload.Length], upload.CreatedUtc));

        return new(true);
    }

    public ValueTask<StoredUpload?> GetAsync(string userId, string uploadId, CancellationToken cancellationToken = default)
    {
        BeforeOperation?.Invoke("Get");

        lock (_lock)
            return new(_uploads.TryGetValue(uploadId, out var entry) && entry.Record.UserId == userId ? Snapshot(entry) : null);
    }

    public async IAsyncEnumerable<StoredUpload> ListAsync(
        string? userId = null, bool withoutOutcomes = false, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<StoredUpload> uploads;

        lock (_lock)
        {
            uploads = [
                .. _uploads.Values.Where(e => (userId is null || e.Record.UserId == userId) && (!withoutOutcomes || e.Outcome is null)).Select(Snapshot)];
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
            if (_uploads.TryGetValue(upload.Id, out var started) && started.ClaimId is null && started.Outcome is null)
                started.LastWriteUtc = UtcNow > upload.CreatedUtc ? UtcNow : upload.CreatedUtc.AddTicks(1);
        }

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);

        // The chunk is stored all at once, unless the run was claimed while it arrived.
        lock (_lock)
        {
            if (!_uploads.TryGetValue(upload.Id, out var entry))
                return ChunkWriteResult.NotFound;

            if (entry.ClaimId is not null || entry.Outcome is not null)
                return ChunkWriteResult.Closed;

            buffer.ToArray().CopyTo(entry.Data, upload.GetChunkOffset(index));
            entry.Chunks.Add(index);
            entry.LastWriteUtc = UtcNow > upload.CreatedUtc ? UtcNow : upload.CreatedUtc.AddTicks(1);

            return ChunkWriteResult.Written;
        }
    }

    public ValueTask<IReadOnlyCollection<int>> GetReceivedChunksAsync(UploadRecord upload, CancellationToken cancellationToken = default)
    {
        lock (_lock)
            return new(_uploads.TryGetValue(upload.Id, out var entry) ? [.. entry.Chunks] : []);
    }

    public ValueTask<IUploadRunClaim?> TryClaimRunAsync(UploadRecord upload, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_uploads.TryGetValue(upload.Id, out var entry) || entry.ClaimId is not null || entry.Outcome is not null)
                return new((IUploadRunClaim?)null);

            entry.ClaimId = Guid.NewGuid();
            entry.LeaseDuration = leaseDuration;
            entry.RunStartedUtc = entry.RenewedUtc = UtcNow;

            return new(new Claim(this, upload.Id, entry.ClaimId.Value));
        }
    }

    public ValueTask<UploadContent> GetContentAsync(UploadRecord upload, CancellationToken cancellationToken = default)
    {
        lock (_lock)
            return new(new MemoryContent(_uploads[upload.Id].Data));
    }

    public ValueTask<bool> TrySetOutcomeAsync(UploadRecord upload, string outcome, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_uploads.TryGetValue(upload.Id, out var entry) || entry.Outcome is not null)
                return new(false);

            entry.Outcome = outcome;
            entry.OutcomeUtc = UtcNow;
            entry.ClaimId = null;
            entry.Data = [];
            entry.Chunks.Clear();

            return new(true);
        }
    }

    public ValueTask DeleteAsync(UploadRecord upload, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _uploads.Remove(upload.Id);

            if (upload.IdempotencyKey is { } key && _keys.TryGetValue((upload.UserId, key), out var reservation) && reservation.UploadId == upload.Id)
                _keys.Remove((upload.UserId, key));
        }

        return default;
    }

    private StoredUpload Snapshot(Entry entry)
    {
        if (entry.Outcome is not null)
            return new StoredUpload(entry.Record, entry.LastWriteUtc, outcome: entry.Outcome, outcomeUtc: entry.OutcomeUtc);

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

        public string? Outcome { get; set; }

        public DateTime? OutcomeUtc { get; set; }
    }

    private sealed class Claim(MemoryUploadStore store, string uploadId, Guid claimId) : IUploadRunClaim
    {
        public async ValueTask<bool> RenewAsync(CancellationToken cancellationToken = default)
        {
            store.BeforeOperation?.Invoke("Renew");

            lock (store._lock)
            {
                if (!store._uploads.TryGetValue(uploadId, out var entry) || entry.ClaimId != claimId)
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
                if (store._uploads.TryGetValue(uploadId, out var entry) && entry.ClaimId == claimId)
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
