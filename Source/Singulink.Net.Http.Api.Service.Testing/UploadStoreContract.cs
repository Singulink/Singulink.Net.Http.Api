using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service.Testing;

/// <summary>
/// Checks that an <see cref="IUploadStore"/> keeps the guarantees that chunked uploads rely on (see <see cref="IUploadStore"/>), also when several
/// requests use it at the same time.
/// </summary>
public static class UploadStoreContract
{
    private const string UserId = "upload-store-contract";

    // Another user, whose uploads have the same IDs as some of the first user's.
    private const string OtherUserId = "upload-store-contract-other";
    private const int Concurrency = 8;
    private const int Rounds = 10;

    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Checks the store with uploads of two users of its own, which it deletes once their checks pass: creations, also of uploads with the same ID, reads and
    /// lists, chunk writes, claims of runs and their exclusion of chunk writes, outcomes and deletions, each also at the same time from several callers, and
    /// the store's usage.
    /// </summary>
    /// <remarks>
    /// The removal of outcomes, which removes those of all users, is checked by <see cref="CheckOutcomeRemovalAsync"/>. A check waits a moment for the lease
    /// of a claim to lapse by the store's clock, so a store whose time provider stands still doesn't pass it. A check that fails leaves its uploads in the
    /// store, under the user IDs <c>upload-store-contract</c> and <c>upload-store-contract-other</c>, until they expire. It doesn't check that a write that
    /// takes long keeps the upload's last write time current, since it would have to wait for that.
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
        await CheckReadsWhileOutcomesAreStoredAsync(store, cancellationToken);
        await CheckClaimsAndOutcomesThatStartTogetherAsync(store, cancellationToken);
        await CheckDeletionAsync(store, cancellationToken);
        await CheckCreationsWithTheSameIdAsync(store, cancellationToken);
        await CheckUsageAsync(store, cancellationToken);
    }

    /// <summary>
    /// Checks the removal of outcomes (see <see cref="IUploadStore.RemoveOutcomesAsync"/>) with uploads of its own user: outcomes stored before the time are
    /// removed, after which an upload with the ID can be created again, and later ones are kept.
    /// </summary>
    /// <remarks>
    /// Run it only against a store that holds test data: it removes the outcomes of all users that were stored before one of its own. It stores outcomes a
    /// moment apart, so a store whose time provider stands still doesn't pass it.
    /// </remarks>
    /// <param name="store">The store to check.</param>
    /// <param name="cancellationToken">A token to cancel the check.</param>
    /// <exception cref="InvalidOperationException">The store does not keep a guarantee, which the message describes.</exception>
    public static async Task CheckOutcomeRemovalAsync(IUploadStore store, CancellationToken cancellationToken = default)
    {
        var older = await CreateCompleteAsync(store, cancellationToken);
        var newer = await CreateCompleteAsync(store, cancellationToken);

        Check(await store.TrySetOutcomeAsync(older, "older", cancellationToken), "An outcome is not stored.");
        var olderUtc = (await GetAsync(store, older, cancellationToken)).OutcomeUtc!.Value;

        await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        Check(await store.TrySetOutcomeAsync(newer, "newer", cancellationToken), "An outcome is not stored.");
        var newerUtc = (await GetAsync(store, newer, cancellationToken)).OutcomeUtc!.Value;
        Check(newerUtc > olderUtc, "An outcome stored later has no later time.");

        await store.RemoveOutcomesAsync(olderUtc.AddTicks(1), cancellationToken);

        Check(await store.GetAsync(UserId, older.Id, cancellationToken) is null, "An outcome stored before the time is not removed.");
        Check(await store.GetAsync(UserId, newer.Id, cancellationToken) is not null, "An outcome stored after the time is removed.");

        Check(await store.TryCreateAsync(newer, cancellationToken) is UploadCreationResult.Exists, "An upload whose outcome is kept is created again.");
        Check(await store.TryCreateAsync(older, cancellationToken) is UploadCreationResult.Created,
            "An upload whose outcome was removed can't be created again.");

        await store.DeleteAsync(older, cancellationToken);
        await store.DeleteAsync(newer, cancellationToken);
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

        var listed = await ListAsync(store, cancellationToken);
        Check(listed.Contains(record.Id), "A new upload is not listed.");

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

            Check(
                upload.Record.Length == record.Length && upload.Record.Metadata == record.Metadata,
                "A finished upload is read with another record than it was created with.");

            Check(!await store.TrySetOutcomeAsync(record, "again", cancellationToken), "A second outcome of an upload is stored.");
            Check((await GetAsync(store, record, cancellationToken)).Outcome == upload.Outcome, "A second outcome of an upload replaced the first.");
            Check(await store.TryClaimRunAsync(record, LeaseDuration, cancellationToken) is null, "The run of an upload with an outcome is claimed.");

            var write = await WriteAsync(store, record, 0, CreateChunk(record, 0, seed: 1), cancellationToken);
            Check(write is ChunkWriteResult.Closed, "A write of a chunk of an upload with an outcome is not closed.");
            Check(!(await ListAsync(store, cancellationToken)).Contains(record.Id), "An upload with an outcome is listed among the open uploads.");

            Check(await store.TryCreateAsync(record, cancellationToken) is UploadCreationResult.Exists, "A finished upload is created again.");

            // Deleting a finished upload removes its outcome, after which an upload with its ID can be created again.
            await store.DeleteAsync(record, cancellationToken);
            Check(await store.GetAsync(UserId, record.Id, cancellationToken) is null, "A finished upload that was deleted is still read.");

            Check(await store.TryCreateAsync(record, cancellationToken) is UploadCreationResult.Created,
                "An upload with the ID of a finished upload that was deleted can't be created.");

            await store.DeleteAsync(record, cancellationToken);
        }
    }

    private static async Task CheckReadsWhileOutcomesAreStoredAsync(IUploadStore store, CancellationToken cancellationToken)
    {
        for (int round = 0; round < Rounds; round++)
        {
            var record = await CreateCompleteAsync(store, cancellationToken);
            var claim = await store.TryClaimRunAsync(record, LeaseDuration, cancellationToken);
            Check(claim is not null, "The run of an upload that has all its chunks can't be claimed.");

            // Reads of the upload and creations made again with its ID, as other servers make them while the run ends, find it, open or finished.
            using var stop = new CancellationTokenSource();
            var reads = Task.Run(() => ReadUntilStoppedAsync(store, record, stop.Token), cancellationToken);

            await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
            bool isStored = await store.TrySetOutcomeAsync(record, "outcome", cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
            stop.Cancel();

            string? failure = await reads;
            Check(isStored, "The outcome of an upload whose run is claimed is not stored.");
            Check(failure is null, failure ?? string.Empty);

            await store.DeleteAsync(record, cancellationToken);
        }

        static async Task<string?> ReadUntilStoppedAsync(IUploadStore store, UploadRecord record, CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                if (await store.GetAsync(UserId, record.Id, CancellationToken.None) is null)
                    return "An upload read while its outcome is stored is not found.";

                if (await store.TryCreateAsync(record, CancellationToken.None) is not UploadCreationResult.Exists)
                    return "An upload whose outcome is being stored is created again.";
            }

            return null;
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

    /// <summary>
    /// Checks that the store reports its usage (see <see cref="IUploadStore.GetUsageAsync"/>) without throwing, with values that are not negative.
    /// </summary>
    private static async Task CheckUsageAsync(IUploadStore store, CancellationToken cancellationToken)
    {
        UploadStoreUsage usage;

        try
        {
            usage = await store.GetUsageAsync(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"The upload store does not keep its guarantees: its usage can't be read ({ex.Message}).", ex);
        }

        Check(usage.AvailableBytes is not < 0 && usage.TotalBytes is not < 0, "it reports a negative space.");
    }

    private static async Task CheckDeletionAsync(IUploadStore store, CancellationToken cancellationToken)
    {
        for (int round = 0; round < Rounds; round++)
        {
            var record = await CreateCompleteAsync(store, cancellationToken);
            var claim = await store.TryClaimRunAsync(record, LeaseDuration, cancellationToken);
            Check(claim is not null, "The run of an upload that has all its chunks can't be claimed.");

            // Deletions at the same time, such as an abort and the end of the upload's run, all succeed, and one of them deleted the open upload.
            var deleted = await Task.WhenAll(Enumerable.Range(0, Concurrency).Select(_ => store.DeleteAsync(record, cancellationToken).AsTask()));
            int count = deleted.Count(d => d);
            Check(count is 1, $"{count} deletions of an open upload made at the same time report that they deleted it, rather than one.");

            Check(await store.GetAsync(UserId, record.Id, cancellationToken) is null, "A deleted upload is still read.");
            Check(!await claim!.RenewAsync(cancellationToken), "The claim of the run of a deleted upload is renewed.");
            Check(await WriteAsync(store, record, 0, CreateChunk(record, 0, seed: 1), cancellationToken) is ChunkWriteResult.NotFound,
                "A chunk of a deleted upload is not reported as not found.");
            Check(!(await ListAsync(store, cancellationToken)).Contains(record.Id), "A deleted upload is listed.");
            Check(!await store.DeleteAsync(record, cancellationToken), "A deletion of an upload that no longer exists reports that it deleted it.");
        }

        var finished = await CreateCompleteAsync(store, cancellationToken);
        Check(await store.TrySetOutcomeAsync(finished, "outcome", cancellationToken), "An outcome is not stored.");
        Check(!await store.DeleteAsync(finished, cancellationToken), "A deletion of a finished upload reports that it deleted an open upload.");
        Check(await store.GetAsync(UserId, finished.Id, cancellationToken) is null, "A deleted finished upload is still read.");

        // Deletions while the outcome of the upload is stored, such as an abort while the handler finishes, and the storing don't report more than once
        // that they ended the open upload. The deletions start roughly apart, from at once to a few dozen milliseconds later (Windows timers step about
        // 15 ms), so that they come at various steps of the storing.
        for (int round = 0; round < Rounds; round++)
        {
            var record = await CreateCompleteAsync(store, cancellationToken);
            var storing = Task.Run(() => store.TrySetOutcomeAsync(record, "outcome", cancellationToken).AsTask(), cancellationToken);

            var deletions = Enumerable.Range(0, Concurrency - 1).Select(i => Task.Run(
                async () => {
                    await Task.Delay(TimeSpan.FromMilliseconds(i * round), cancellationToken);
                    return await store.DeleteAsync(record, cancellationToken);
                },
                cancellationToken));

            bool[] ended = await Task.WhenAll(deletions.Prepend(storing));
            int count = ended.Count(e => e);

            Check(count <= 1, $"{count} deletions and a storing of the outcome of an open upload at the same time report that they ended it, rather than one.");
            Check(!(await ListAsync(store, cancellationToken)).Contains(record.Id), "An upload that was deleted while its outcome was stored is listed.");

            // An outcome whose storing ended the upload is removed.
            await store.DeleteAsync(record, cancellationToken);
        }
    }

    private static async Task CheckCreationsWithTheSameIdAsync(IUploadStore store, CancellationToken cancellationToken)
    {
        for (int round = 0; round < Rounds; round++)
        {
            // Creations of an upload at the same time, such as a creation made again while the first still runs, create it once.
            var record = NewRecord(store, UserId);
            var results = await Task.WhenAll(Enumerable.Range(0, Concurrency).Select(
                _ => Task.Run(() => store.TryCreateAsync(record, cancellationToken).AsTask(), cancellationToken)));

            int count = results.Count(r => r is UploadCreationResult.Created);
            Check(count is 1, $"{count} creations of an upload made at the same time created it, rather than one.");
            Check(results.All(r => r is UploadCreationResult.Created or UploadCreationResult.Exists), "A creation of an upload that exists reports no room.");

            var upload = await GetAsync(store, record, cancellationToken);
            Check(upload.Outcome is null && upload.Record.Length == record.Length, "An upload created at the same time as others is not read as created.");
            Check(await store.TryCreateAsync(record, cancellationToken) is UploadCreationResult.Exists, "An open upload is created again.");

            // Another user's upload with the same ID is another upload.
            var other = NewRecord(store, OtherUserId, record.Id);
            Check(await store.TryCreateAsync(other, cancellationToken) is UploadCreationResult.Created,
                "An upload can't be created with the ID of another user's upload.");

            Check(await WriteAsync(store, other, 0, CreateChunk(other, 0, seed: 5), cancellationToken) is ChunkWriteResult.Written, "A chunk is not written.");
            Check((await store.GetReceivedChunksAsync(record, cancellationToken)).Count is 0, "A chunk of another user's upload with the ID is received.");

            Check(await store.DeleteAsync(other, cancellationToken), "An open upload that was deleted is not reported as deleted.");
            Check(await store.GetAsync(UserId, record.Id, cancellationToken) is not null, "Deleting another user's upload with the ID deleted the upload.");
            Check(await store.GetAsync(OtherUserId, record.Id, cancellationToken) is null, "A deleted upload is still read.");

            // A deleted upload can be created again.
            await store.DeleteAsync(record, cancellationToken);
            Check(await store.TryCreateAsync(record, cancellationToken) is UploadCreationResult.Created, "A deleted upload can't be created again.");

            // A cleanup leaves the uploads that are open.
            await store.CleanUpAsync(cancellationToken);
            Check(await store.GetAsync(UserId, record.Id, cancellationToken) is not null, "A cleanup removed an upload that is open.");

            await store.DeleteAsync(record, cancellationToken);
        }

        // A finished upload of a user doesn't keep another user from creating an upload with its ID.
        var finished = await CreateCompleteAsync(store, cancellationToken);
        Check(await store.TrySetOutcomeAsync(finished, "outcome", cancellationToken), "An outcome is not stored.");

        var otherOfFinished = NewRecord(store, OtherUserId, finished.Id);
        Check(await store.TryCreateAsync(otherOfFinished, cancellationToken) is UploadCreationResult.Created,
            "An upload can't be created with the ID of another user's finished upload.");

        Check((await GetAsync(store, finished, cancellationToken)).Outcome == "outcome", "Another user's upload with the ID changed a finished upload.");

        await store.DeleteAsync(otherOfFinished, cancellationToken);
        await store.DeleteAsync(finished, cancellationToken);
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

    /// <summary>
    /// Makes the record of a new upload of two chunks of the smallest size that both the service and the store allow, with a new ID, as clients choose them,
    /// unless one is specified.
    /// </summary>
    private static UploadRecord NewRecord(IUploadStore store, string userId, string? uploadId = null)
    {
        int chunkSize = Math.Max(store.MinChunkSize, ChunkedUploadOptions.ChunkSizeFloor);
        return new UploadRecord(uploadId ?? Guid.NewGuid().ToString("D"), userId, chunkSize + 10, chunkSize, DateTime.UtcNow, "{}");
    }

    private static async Task<UploadRecord> CreateAsync(IUploadStore store, CancellationToken cancellationToken)
    {
        var record = NewRecord(store, UserId);

        Check(await store.TryCreateAsync(record, cancellationToken) is UploadCreationResult.Created, "An upload can't be created.");
        return record;
    }

    /// <summary>
    /// Creates an upload with all its chunks.
    /// </summary>
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

    private static async Task<HashSet<string>> ListAsync(IUploadStore store, CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var upload in store.ListAsync(cancellationToken))
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
