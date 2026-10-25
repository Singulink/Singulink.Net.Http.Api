using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service.Testing;

/// <summary>
/// Checks that an <see cref="IUploadStore"/> keeps the guarantees that chunked uploads rely on (see <see cref="IUploadStore"/>), also when several
/// requests use it at the same time.
/// </summary>
public static class UploadStoreContract
{
    private const string UserId = "upload-store-contract";
    private const int Concurrency = 8;
    private const int Rounds = 10;

    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Checks the store with uploads of a user of its own, which it deletes once their checks pass: creations, reads and lists, chunk writes, claims of runs
    /// and their exclusion of chunk writes, outcomes, deletions, and reservations of idempotency keys, each also at the same time from several callers.
    /// </summary>
    /// <remarks>
    /// A check waits a moment for the lease of a claim to lapse by the store's clock, so a store whose time provider stands still doesn't pass it. A check
    /// that fails leaves its uploads in the store, under the user ID <c>upload-store-contract</c>, until they expire.
    /// </remarks>
    /// <param name="store">The store to check.</param>
    /// <param name="cancellationToken">A token to cancel the checks.</param>
    /// <exception cref="InvalidOperationException">The store does not keep a guarantee, which the message describes.</exception>
    public static async Task CheckAsync(IUploadStore store, CancellationToken cancellationToken = default)
    {
        await CheckCreationAsync(store, cancellationToken);
        await CheckChunkWritesAsync(store, cancellationToken);
        await CheckClaimsAsync(store, cancellationToken);
        await CheckClaimsExcludeChunkWritesAsync(store, cancellationToken);
        await CheckClaimsAndChunkWritesThatStartTogetherAsync(store, cancellationToken);
        await CheckOutcomesAsync(store, cancellationToken);
        await CheckClaimsAndOutcomesThatStartTogetherAsync(store, cancellationToken);
        await CheckDeletionAsync(store, cancellationToken);
        await CheckKeyReservationsAsync(store, cancellationToken);
    }

    private static async Task CheckCreationAsync(IUploadStore store, CancellationToken cancellationToken)
    {
        var record = await CreateAsync(store, cancellationToken);
        var upload = await GetAsync(store, record, cancellationToken);

        Check(
            upload.Record.Length == record.Length && upload.Record.ChunkSize == record.ChunkSize && upload.Record.Metadata == record.Metadata,
            "An upload is read with another record than it was created with.");

        Check(upload.LastWriteUtc == record.CreatedUtc, "The last write of an upload that has received no chunk is not its creation.");
        Check(upload.RunState is UploadRunState.None && upload.Outcome is null, "A new upload has a run or an outcome.");

        var listed = await ListAsync(store, withoutOutcomes: false, cancellationToken);
        Check(listed.Contains(record.Id), "A new upload is not listed.");

        bool isListedForAll = false;

        await foreach (var all in store.ListAsync(null, withoutOutcomes: false, cancellationToken))
            isListedForAll |= all.Record.Id == record.Id;

        Check(isListedForAll, "A new upload is not listed among the uploads of all users.");

        await store.DeleteAsync(record, cancellationToken);
    }

    private static async Task CheckChunkWritesAsync(IUploadStore store, CancellationToken cancellationToken)
    {
        var record = await CreateAsync(store, cancellationToken);
        byte[] first = CreateChunk(record, 0, seed: 1);

        // The upload's last write moves when a write starts, so that a slow chunk keeps an upload that has received none from expiring.
        var held = new HeldStream(first);
        var heldWrite = store.WriteChunkAsync(record, 0, held, cancellationToken).AsTask();
        await held.WaitUntilReadAsync(cancellationToken);

        var writing = await GetAsync(store, record, cancellationToken);
        Check(writing.LastWriteUtc > record.CreatedUtc, "The last write of an upload whose first chunk is being written is not later than its creation.");

        held.Release();
        Check(await WithinTimeAsync(heldWrite) is ChunkWriteResult.Written, "A chunk is not written.");

        Check(await WriteAsync(store, record, 0, first, cancellationToken) is ChunkWriteResult.Written, "A chunk is not written.");
        Check((await store.GetReceivedChunksAsync(record, cancellationToken)).SequenceEqual([0]), "A written chunk is not reported as received.");

        var upload = await GetAsync(store, record, cancellationToken);
        Check(upload.LastWriteUtc > record.CreatedUtc, "The last write of an upload that has received a chunk is not later than its creation.");

        // Chunks are written again at the same time, as a client that sends a chunk again after a lost response can.
        var writes = await Task.WhenAll(Enumerable.Range(0, Concurrency).Select(_ => WriteAsync(store, record, 0, first, cancellationToken).AsTask()));
        Check(writes.All(w => w is ChunkWriteResult.Written), "A chunk that is written again at the same time is not written.");

        byte[] rewritten = await ReadContentWithClaimAsync(store, record, cancellationToken);
        Check(rewritten.AsSpan(0, first.Length).SequenceEqual(first), "A chunk that is written again at the same time has other bytes than were written.");

        // A write that fails partway leaves the chunk missing, or as it was.
        await Ignore(store.WriteChunkAsync(record, 0, new FailingStream(CreateChunk(record, 0, seed: 2)), cancellationToken));

        Check(await WriteAsync(store, record, 1, CreateChunk(record, 1, seed: 1), cancellationToken) is ChunkWriteResult.Written, "A chunk is not written.");

        if ((await store.GetReceivedChunksAsync(record, cancellationToken)).Contains(0))
        {
            var claim = await store.TryClaimRunAsync(record, LeaseDuration, cancellationToken);
            Check(claim is not null, "The run of an upload that has all its chunks can't be claimed.");

            byte[] content = await ReadContentAsync(store, record, cancellationToken);
            Check(content.AsSpan(0, first.Length).SequenceEqual(first), "A write that failed changed a chunk that is reported as received.");

            await claim!.ReleaseAsync(cancellationToken);
        }

        await store.DeleteAsync(record, cancellationToken);
    }

    private static async Task CheckClaimsAsync(IUploadStore store, CancellationToken cancellationToken)
    {
        for (int round = 0; round < Rounds; round++)
        {
            var record = await CreateCompleteAsync(store, cancellationToken);

            var claims = await Task.WhenAll(
                Enumerable.Range(0, Concurrency).Select(_ => store.TryClaimRunAsync(record, LeaseDuration, cancellationToken).AsTask()));

            int count = claims.Count(c => c is not null);
            Check(count is 1, $"{count} of {Concurrency} claims of a run made at the same time succeeded, rather than one.");

            var claim = claims.Single(c => c is not null)!;
            Check((await GetAsync(store, record, cancellationToken)).RunState is UploadRunState.Running, "A claimed run is not reported as running.");
            Check(await store.TryClaimRunAsync(record, LeaseDuration, cancellationToken) is null, "A run that is claimed is claimed again.");
            Check(await claim.RenewAsync(cancellationToken), "A claim is not renewed.");

            await claim.ReleaseAsync(cancellationToken);
            Check((await GetAsync(store, record, cancellationToken)).RunState is UploadRunState.None, "A released claim is still reported.");

            var again = await store.TryClaimRunAsync(record, LeaseDuration, cancellationToken);
            Check(again is not null, "The run of an upload whose claim was released can't be claimed again.");

            await again!.ReleaseAsync(cancellationToken);
            await store.DeleteAsync(record, cancellationToken);
        }

        // A claim whose lease lapsed is reported as lapsed, and keeps the run from being claimed until the upload is ended.
        var lapsedRecord = await CreateCompleteAsync(store, cancellationToken);
        var lapsing = await store.TryClaimRunAsync(lapsedRecord, TimeSpan.FromMilliseconds(1), cancellationToken);
        Check(lapsing is not null, "The run of an upload that has all its chunks can't be claimed.");

        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        var lapsedUpload = await GetAsync(store, lapsedRecord, cancellationToken);
        Check(lapsedUpload.RunState is UploadRunState.Lapsed, "A claim whose lease lapsed is not reported as lapsed.");
        Check(await store.TryClaimRunAsync(lapsedRecord, LeaseDuration, cancellationToken) is null, "The run of an upload whose claim lapsed is claimed.");

        await store.DeleteAsync(lapsedRecord, cancellationToken);
    }

    private static async Task CheckClaimsExcludeChunkWritesAsync(IUploadStore store, CancellationToken cancellationToken)
    {
        var record = await CreateCompleteAsync(store, cancellationToken);
        byte[] original = await ReadContentWithClaimAsync(store, record, cancellationToken);

        // A chunk is written again with other bytes, and its body arrives slowly.
        var body = new HeldStream(CreateChunk(record, 0, seed: 3));
        var write = store.WriteChunkAsync(record, 0, body, cancellationToken).AsTask();
        await body.WaitUntilReadAsync(cancellationToken);

        var claim = await store.TryClaimRunAsync(record, LeaseDuration, cancellationToken);
        body.Release();
        var result = await WithinTimeAsync(write);

        if (claim is not null)
        {
            // A store that lets the claim succeed leaves the file as it is.
            Check(result is ChunkWriteResult.Closed, "A write of a chunk during which the run was claimed is not closed.");

            byte[] content = await ReadContentAsync(store, record, cancellationToken);
            Check(content.SequenceEqual(original), "A write of a chunk during which the run was claimed changed the file.");

            await claim.ReleaseAsync(cancellationToken);
        }

        // A write that starts once a run is claimed leaves the file as it is.
        if (result is ChunkWriteResult.Written)
            original = await ReadContentWithClaimAsync(store, record, cancellationToken);

        var running = await store.TryClaimRunAsync(record, LeaseDuration, cancellationToken);
        Check(running is not null, "The run of an upload that has all its chunks can't be claimed.");

        var late = await WriteAsync(store, record, 0, CreateChunk(record, 0, seed: 4), cancellationToken);
        Check(late is ChunkWriteResult.Closed, "A write of a chunk of an upload whose run is claimed is not closed.");
        Check((await ReadContentAsync(store, record, cancellationToken)).SequenceEqual(original), "A write of a chunk of a claimed run changed the file.");

        await running!.ReleaseAsync(cancellationToken);
        await store.DeleteAsync(record, cancellationToken);
    }

    private static async Task CheckClaimsAndChunkWritesThatStartTogetherAsync(IUploadStore store, CancellationToken cancellationToken)
    {
        var record = await CreateCompleteAsync(store, cancellationToken);

        for (int round = 0; round < Rounds; round++)
        {
            byte[] original = await ReadContentWithClaimAsync(store, record, cancellationToken);

            // A chunk is written again and the run is claimed at the same time. The chunk's body is held until the claim has its answer, so that the write
            // is still in progress then.
            var body = new HeldStream(CreateChunk(record, 0, seed: 10 + round));
            var write = Task.Run(() => store.WriteChunkAsync(record, 0, body, cancellationToken).AsTask(), cancellationToken);
            var claim = await Task.Run(() => store.TryClaimRunAsync(record, LeaseDuration, cancellationToken).AsTask(), cancellationToken);

            body.Release();
            var result = await WithinTimeAsync(write);

            if (claim is null)
            {
                Check(result is ChunkWriteResult.Written, "A write of a chunk that started with a claim of the run that failed is not written.");
                continue;
            }

            Check(result is ChunkWriteResult.Closed, "A write of a chunk that started with a claim of the run that succeeded is not closed.");

            byte[] content = await ReadContentAsync(store, record, cancellationToken);
            Check(content.SequenceEqual(original), "A write of a chunk that started with a claim of the run that succeeded changed the file.");

            await claim.ReleaseAsync(cancellationToken);
        }

        await store.DeleteAsync(record, cancellationToken);
    }

    private static async Task CheckOutcomesAsync(IUploadStore store, CancellationToken cancellationToken)
    {
        for (int round = 0; round < Rounds; round++)
        {
            var record = await CreateCompleteAsync(store, cancellationToken);

            var stored = await Task.WhenAll(
                Enumerable.Range(0, Concurrency).Select(i => store.TrySetOutcomeAsync(record, $"outcome {i}", cancellationToken).AsTask()));

            int count = stored.Count(s => s);
            Check(count is 1, $"{count} of {Concurrency} outcomes of an upload stored at the same time were stored, rather than one.");

            var upload = await GetAsync(store, record, cancellationToken);
            Check(upload.Outcome == $"outcome {Array.IndexOf(stored, true)}", "An upload's outcome is not the one that was stored.");
            Check(upload.OutcomeUtc is not null && upload.RunState is UploadRunState.None, "An upload with an outcome has no outcome time, or a run.");
            Check(await store.TryClaimRunAsync(record, LeaseDuration, cancellationToken) is null, "The run of an upload with an outcome is claimed.");

            var write = await WriteAsync(store, record, 0, CreateChunk(record, 0, seed: 1), cancellationToken);
            Check(write is ChunkWriteResult.Closed, "A write of a chunk of an upload with an outcome is not closed.");

            var open = await ListAsync(store, withoutOutcomes: true, cancellationToken);
            Check(!open.Contains(record.Id), "An upload with an outcome is listed among those without one.");

            await store.DeleteAsync(record, cancellationToken);
        }
    }

    private static async Task CheckClaimsAndOutcomesThatStartTogetherAsync(IUploadStore store, CancellationToken cancellationToken)
    {
        for (int round = 0; round < Rounds; round++)
        {
            var record = await CreateCompleteAsync(store, cancellationToken);

            // A run is claimed while another server stores the upload's outcome, such as the outcome of a run whose claim lapsed.
            var claiming = Task.Run(() => store.TryClaimRunAsync(record, LeaseDuration, cancellationToken).AsTask(), cancellationToken);
            bool isStored = await Task.Run(() => store.TrySetOutcomeAsync(record, "outcome", cancellationToken).AsTask(), cancellationToken);
            var claim = await claiming;

            Check(isStored, "An outcome stored at the same time as a claim of the upload's run is not stored.");

            var upload = await GetAsync(store, record, cancellationToken);
            Check(upload.Outcome == "outcome" && upload.RunState is UploadRunState.None, "An upload whose outcome is stored has no outcome, or a run.");

            if (claim is not null)
                Check(!await claim.RenewAsync(cancellationToken), "The claim of the run of an upload with an outcome is renewed.");

            await store.DeleteAsync(record, cancellationToken);
        }
    }

    private static async Task CheckDeletionAsync(IUploadStore store, CancellationToken cancellationToken)
    {
        var record = await CreateCompleteAsync(store, cancellationToken);
        var claim = await store.TryClaimRunAsync(record, LeaseDuration, cancellationToken);
        Check(claim is not null, "The run of an upload that has all its chunks can't be claimed.");

        // Deletions at the same time, such as an abort and the end of the upload's run, all succeed.
        await Task.WhenAll(Enumerable.Range(0, Concurrency).Select(_ => store.DeleteAsync(record, cancellationToken).AsTask()));

        Check(await store.GetAsync(UserId, record.Id, cancellationToken) is null, "A deleted upload is still read.");
        Check(!await claim!.RenewAsync(cancellationToken), "The claim of the run of a deleted upload is renewed.");
        Check(await WriteAsync(store, record, 0, CreateChunk(record, 0, seed: 1), cancellationToken) is ChunkWriteResult.NotFound,
            "A chunk of a deleted upload is not reported as not found.");
        Check(!(await ListAsync(store, withoutOutcomes: false, cancellationToken)).Contains(record.Id), "A deleted upload is listed.");
    }

    private static async Task CheckKeyReservationsAsync(IUploadStore store, CancellationToken cancellationToken)
    {
        for (int round = 0; round < Rounds; round++)
        {
            string key = "key-" + ShortLivedId.Create();

            // Reservations of a key at the same time all get the same reservation.
            var reservations = await Task.WhenAll(Enumerable.Range(0, Concurrency).Select(
                _ => store.ReserveKeyAsync(UserId, key, ShortLivedId.Create(), cancellationToken).AsTask()));

            int count = reservations.Select(r => r.UploadId).Distinct(StringComparer.Ordinal).Count();
            Check(count is 1, $"{count} reservations of a key made at the same time succeeded, rather than one.");

            // Takeovers of the reservation at the same time, which each release it and reserve the key again, all get the same new reservation.
            string reserved = reservations[0].UploadId;

            var takeovers = await Task.WhenAll(Enumerable.Range(0, Concurrency).Select(async _ => {
                await store.ReleaseKeyAsync(UserId, key, reserved, cancellationToken);
                return await store.ReserveKeyAsync(UserId, key, ShortLivedId.Create(), cancellationToken);
            }));

            count = takeovers.Select(r => r.UploadId).Distinct(StringComparer.Ordinal).Count();
            Check(count is 1, $"{count} takeovers of a reservation made at the same time succeeded, rather than one.");
            Check(takeovers[0].UploadId != reserved, "A released reservation is still reserved.");

            // Releasing the reservation for another upload leaves it.
            await store.ReleaseKeyAsync(UserId, key, ShortLivedId.Create(), cancellationToken);
            var kept = await store.ReserveKeyAsync(UserId, key, ShortLivedId.Create(), cancellationToken);
            Check(kept.UploadId == takeovers[0].UploadId, "A reservation is released for an upload that it is not reserved for.");

            await store.ReleaseKeyAsync(UserId, key, kept.UploadId, cancellationToken);
        }

        // Deleting an upload releases the key of its record.
        string uploadKey = "key-" + ShortLivedId.Create();
        string uploadId = ShortLivedId.Create();
        await store.ReserveKeyAsync(UserId, uploadKey, uploadId, cancellationToken);

        var record = await CreateAsync(store, cancellationToken, uploadId, uploadKey);

        // A cleanup leaves the uploads that are open, and the reservations of their keys.
        await store.CleanUpAsync(cancellationToken);
        Check(await store.GetAsync(UserId, record.Id, cancellationToken) is not null, "A cleanup removed an upload that is open.");

        var afterCleanup = await store.ReserveKeyAsync(UserId, uploadKey, ShortLivedId.Create(), cancellationToken);
        Check(afterCleanup.UploadId == uploadId, "A cleanup released the reservation of the key of an upload that is open.");

        await store.DeleteAsync(record, cancellationToken);

        var after = await store.ReserveKeyAsync(UserId, uploadKey, ShortLivedId.Create(), cancellationToken);
        Check(after.UploadId != uploadId, "Deleting an upload does not release its key.");

        await store.ReleaseKeyAsync(UserId, uploadKey, after.UploadId, cancellationToken);
    }

    /// <summary>
    /// Waits for a write of a chunk, and fails the check if it doesn't end within 20 seconds, such as in a store whose write waits for a claim to be
    /// released rather than being closed.
    /// </summary>
    private static async Task<ChunkWriteResult> WithinTimeAsync(Task<ChunkWriteResult> write)
    {
        using var timeout = new CancellationTokenSource();
        bool hasEnded = await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(20), timeout.Token)) == write;
        timeout.Cancel();

        Check(hasEnded, "A write of a chunk did not end within 20 seconds.");
        return await write;
    }

    private static async Task<UploadRecord> CreateAsync(IUploadStore store, CancellationToken cancellationToken, string? uploadId = null, string? key = null)
    {
        // Two chunks of the smallest size that both the service and the store allow.
        int chunkSize = Math.Max(store.MinChunkSize, ChunkedUploadOptions.ChunkSizeFloor);
        var record = new UploadRecord(uploadId ?? ShortLivedId.Create(), UserId, chunkSize + 10, chunkSize, DateTime.UtcNow, "{}", key);

        Check(await store.TryCreateAsync(record, cancellationToken), "An upload can't be created.");
        return record;
    }

    private static async Task<UploadRecord> CreateCompleteAsync(IUploadStore store, CancellationToken cancellationToken)
    {
        var record = await CreateAsync(store, cancellationToken);

        for (int index = 0; index < record.ChunkCount; index++)
        {
            var result = await WriteAsync(store, record, index, CreateChunk(record, index, seed: 1), cancellationToken);
            Check(result is ChunkWriteResult.Written, "A chunk is not written.");
        }

        return record;
    }

    private static async Task<StoredUpload> GetAsync(IUploadStore store, UploadRecord record, CancellationToken cancellationToken)
    {
        var upload = await store.GetAsync(UserId, record.Id, cancellationToken);
        Check(upload is not null, "An upload that was created is not read.");

        return upload!;
    }

    private static async Task<HashSet<string>> ListAsync(IUploadStore store, bool withoutOutcomes, CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var upload in store.ListAsync(UserId, withoutOutcomes, cancellationToken))
            ids.Add(upload.Record.Id);

        return ids;
    }

    private static ValueTask<ChunkWriteResult> WriteAsync(IUploadStore store, UploadRecord record, int index, byte[] chunk, CancellationToken cancellationToken)
    {
        return store.WriteChunkAsync(record, index, new MemoryStream(chunk), cancellationToken);
    }

    private static async Task<byte[]> ReadContentWithClaimAsync(IUploadStore store, UploadRecord record, CancellationToken cancellationToken)
    {
        var claim = await store.TryClaimRunAsync(record, LeaseDuration, cancellationToken);
        Check(claim is not null, "The run of an upload that has all its chunks can't be claimed.");

        byte[] content = await ReadContentAsync(store, record, cancellationToken);
        await claim!.ReleaseAsync(cancellationToken);

        return content;
    }

    private static async Task<byte[]> ReadContentAsync(IUploadStore store, UploadRecord record, CancellationToken cancellationToken)
    {
        var content = await store.GetContentAsync(record, cancellationToken);
        using var buffer = new MemoryStream();

        await using (var stream = content.OpenReadStream())
            await stream.CopyToAsync(buffer, cancellationToken);

        Check(buffer.Length == record.Length, "The content of an upload is not of its length.");
        return buffer.ToArray();
    }

    private static byte[] CreateChunk(UploadRecord record, int index, int seed)
    {
        byte[] chunk = new byte[record.GetChunkLength(index)];
        new Random(seed + index).NextBytes(chunk);

        return chunk;
    }

    private static async Task Ignore(ValueTask<ChunkWriteResult> write)
    {
        try
        {
            await write;
        }
        catch (IOException)
        {
        }
    }

    private static void Check(bool condition, string failure)
    {
        if (!condition)
            throw new InvalidOperationException("The upload store does not keep its guarantees: " + failure);
    }

    /// <summary>
    /// The body of a chunk whose request fails after half of its bytes, as a connection that drops does.
    /// </summary>
    private sealed class FailingStream(byte[] bytes) : MemoryStream(bytes)
    {
        private readonly int _failsAt = bytes.Length / 2;

        public override int Read(byte[] buffer, int offset, int count) => Fail(base.Read(buffer, offset, Math.Min(count, _failsAt - (int)Position)));

        public override int Read(Span<byte> buffer) => Fail(base.Read(buffer[..Math.Min(buffer.Length, _failsAt - (int)Position)]));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer, offset, count));

        private int Fail(int read) => read > 0 ? read : throw new IOException("The connection was reset.");
    }

    /// <summary>
    /// The body of a chunk that holds its bytes after the first ones until it is released.
    /// </summary>
    private sealed class HeldStream(byte[] bytes) : Stream
    {
        private readonly TaskCompletionSource _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => bytes.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public Task WaitUntilReadAsync(CancellationToken cancellationToken) => _read.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);

        public void Release() => _release.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position > 0)
                await _release.Task.WaitAsync(cancellationToken);

            int count = Math.Min(buffer.Length, _position is 0 ? 1 : bytes.Length - _position);
            bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            _read.TrySetResult();

            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
