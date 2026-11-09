namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// Represents the storage of chunked uploads, which keeps everything the service knows about them: the records, chunks and run claims of the open
/// uploads, and the outcomes of the finished ones, by their user and ID (see <see cref="UploadRecord.Id"/>). Chunked uploads use the store that the app
/// registers as a singleton <see cref="IUploadStore"/> service: the directory store (see <see cref="ServiceCollectionExtensions.AddDirectoryUploadStore"/>)
/// or a store of its own.
/// </summary>
/// <remarks>
/// <para>
/// Servers that share a store share its uploads, so every operation must be safe to call at the same time from several requests and servers:
/// </para>
/// <list type="bullet">
/// <item>Writing a chunk is idempotent: writing a chunk again replaces it with the same bytes. A chunk is reported as received only once all of its bytes
/// are stored durably, and a write that fails or is interrupted leaves it missing, or as it was, also when it was received before.</item>
/// <item>Claiming a run is atomic: of claims made at the same time, at most one succeeds, and none succeeds while the upload has a claim, whether its lease
/// is current or has lapsed, or has an outcome.</item>
/// <item>Claiming a run excludes chunk writes: a claim does not succeed while a chunk is being written, and a write that starts once a run is claimed
/// does not change the upload's file. A write and a claim that start at the same time don't both fail: if the claim fails, the write is written.</item>
/// <item>Storing an outcome is atomic and durable: only the first outcome of an upload is stored, and once stored it is kept until it is removed (see
/// <see cref="RemoveOutcomesAsync"/>) or the upload is deleted. An upload with an outcome is finished: it is no longer listed, and only its record and
/// its outcome are kept. A read of an upload while its outcome is stored gets the open upload or the finished one.</item>
/// <item>Creating an upload is atomic: of creations of a user's upload with the same ID at the same time, at most one creates it, and none does while the
/// user has an upload with the ID, open or with an outcome.</item>
/// </list>
/// <para>
/// The service applies all of the protocol's rules, such as the users and endpoints that uploads belong to, their states, limits and expiry, and the
/// lengths of chunks, so a store only keeps what it is given. <see cref="UploadRecord.Metadata"/> and outcomes are text in a format of the service's own,
/// which a store keeps as it is.
/// </para>
/// <para>
/// The service counts the open uploads in the <see cref="IShortLivedStore"/> of <see cref="ShortLivedStoreKeys.Uploads"/>, and lists them in each cleanup, when
/// the application starts and every five minutes on each server, to correct the counts. It reads finished uploads only for requests that come back for
/// their outcomes, so a store that keeps finished uploads apart from the open ones lists the open uploads as fast with many finished uploads as with none.
/// </para>
/// <para>
/// The service takes an exception of the store, other than an <see cref="ApiException"/> and the cancellation of the operation, for a failure that may not
/// last, such as of a share that can't be reached for a moment, and reports it with <see cref="UploadErrorCodes.StoreUnavailable"/>, which clients retry
/// for a while. A store reports an error that lasts with an <see cref="ApiException"/>.
/// </para>
/// </remarks>
public interface IUploadStore
{
    /// <summary>
    /// Gets the smallest chunk size that the store accepts for every chunk but the last, in bytes. Default is 0, for no limit of the store's own.
    /// </summary>
    /// <remarks>
    /// The service never uses chunks smaller than 5 MiB.
    /// </remarks>
    int MinChunkSize => 0;

    /// <summary>
    /// Gets the largest chunk size that the store accepts, in bytes. Default is <see cref="int.MaxValue"/>, for no limit of the store's own.
    /// </summary>
    /// <remarks>
    /// The service never uses chunks larger than 64 MiB.
    /// </remarks>
    int MaxChunkSize => int.MaxValue;

    /// <summary>
    /// Gets the largest number of chunks that the store accepts for an upload. Default is <see cref="int.MaxValue"/>, for no limit of the store's own.
    /// </summary>
    int MaxChunkCount => int.MaxValue;

    /// <summary>
    /// Gets who uses the store's uploads: only this process (<see cref="StoreSharingMode.ProcessLocal"/>), or every process or server that uses the same
    /// storage (<see cref="StoreSharingMode.Shared"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The service reads it when the application starts. A shared store requires a shared <see cref="IShortLivedStore"/> of
    /// <see cref="ShortLivedStoreKeys.Uploads"/> (see <see cref="IShortLivedStore.SharingMode"/>), or the application fails to start, as it does if the value
    /// is <see cref="StoreSharingMode.Unspecified"/> or not a value of <see cref="StoreSharingMode"/>. With a process-local store and a shared short-lived
    /// store, the counts of open uploads are kept for each machine (see the uploads guide).
    /// </para>
    /// <para>
    /// With a shared store, a server whose upload's handler runs checks every <see cref="ChunkedUploadOptions.CancelCheckInterval"/> whether the upload was
    /// cancelled on another server. With a process-local store, each request for an upload reaches the process that keeps it, so a cancellation reaches the
    /// handler at once, and the runs that a store's uploads have when the application starts were left by a process that has ended, so the service ends them
    /// as interrupted.
    /// </para>
    /// </remarks>
    StoreSharingMode SharingMode { get; }

    /// <summary>
    /// Gets how much room the store has.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <remarks>
    /// Values that the store can't tell are <see langword="null"/>, and a store that can tell none returns <see cref="UploadStoreUsage.Unknown"/>. An
    /// exception that it throws shows that the store can't be read. The health check of chunked uploads reads it each time it runs (see
    /// <see cref="HttpApiHealthChecksBuilderExtensions.AddChunkedUploadsCheck"/>), and nothing reads it on the path of a request.
    /// </remarks>
    ValueTask<UploadStoreUsage> GetUsageAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an upload with the specified record, reserving room for its length if the store can, unless the user has an upload with its ID: open, with an
    /// outcome, or one that is being created or removed.
    /// </summary>
    /// <param name="upload">The record of the new upload.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>Whether the upload was created, or why not.</returns>
    /// <remarks>
    /// Of creations of the same upload at the same time, at most one returns <see cref="UploadCreationResult.Created"/>, and the others
    /// <see cref="UploadCreationResult.Exists"/>, after which the service reads the upload with <see cref="GetAsync"/>, and if the read finds none, such as
    /// of an upload that is being created or removed, reads and creates it again every 100 ms for up to 5 seconds. An upload that was deleted, or whose
    /// outcome was removed, can be created again.
    /// </remarks>
    ValueTask<UploadCreationResult> TryCreateAsync(UploadRecord upload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets an upload of a user with its current state: an open upload, or a finished one with its outcome (see <see cref="StoredUpload.Outcome"/>).
    /// </summary>
    /// <param name="userId">The ID of the user the upload belongs to.</param>
    /// <param name="uploadId">The ID of the upload.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The upload, or <see langword="null"/> if the user has no upload with the ID, such as when it was deleted or its outcome was removed.
    /// </returns>
    ValueTask<StoredUpload?> GetAsync(string userId, string uploadId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the open uploads of all users with their current state, which are the uploads that have no outcome.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <remarks>
    /// The service lists them in each cleanup, to find the uploads to remove and to correct their counts.
    /// </remarks>
    IAsyncEnumerable<StoredUpload> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a chunk of an upload, replacing the chunk if it was already written, unless the run of the upload's handler is claimed or the upload has an
    /// outcome.
    /// </summary>
    /// <remarks>
    /// The chunk is reported as received once this returns <see cref="ChunkWriteResult.Written"/>. The upload's <see cref="StoredUpload.LastWriteUtc"/> is
    /// the time when a write starts, again every few seconds while bytes of the chunk arrive, so that the upload doesn't expire while a slow chunk arrives,
    /// and again when it ends. While no bytes arrive it stays as it is, so that an upload whose chunk stalled expires. The contract checks of the testing
    /// package don't cover this, since they would have to wait for it.
    /// </remarks>
    /// <param name="upload">The record of the upload.</param>
    /// <param name="index">The index of the chunk.</param>
    /// <param name="content">The chunk's bytes, which are <see cref="UploadRecord.GetChunkLength(int)"/> bytes long. Reading it throws when the request
    /// does not have exactly that many bytes, and the chunk is then left missing. It reads the request's body, which a synchronous read reads
    /// synchronously, as the server allows for any body.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>Whether the chunk was written, or why not.</returns>
    ValueTask<ChunkWriteResult> WriteChunkAsync(UploadRecord upload, int index, Stream content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the indexes of the chunks of an upload that have been received.
    /// </summary>
    /// <param name="upload">The record of the upload.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    ValueTask<IReadOnlyCollection<int>> GetReceivedChunksAsync(UploadRecord upload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims the run of an upload's handler for the server that calls it.
    /// </summary>
    /// <param name="upload">The record of the upload, whose chunks have all been received.</param>
    /// <param name="leaseDuration">How long the claim lasts unless it is renewed: the service renews it every quarter of this time while the handler
    /// runs. The service uses 1 minute.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The claim, or <see langword="null"/> if the upload already has a claim or an outcome, a chunk of it is being written, or it no longer
    /// exists.</returns>
    /// <remarks>
    /// While the claim's lease is current, <see cref="GetAsync"/> reports the upload as <see cref="UploadRunState.Running"/>, and once it has lapsed, as
    /// <see cref="UploadRunState.Lapsed"/>, which the service ends with an outcome of its own.
    /// </remarks>
    ValueTask<IUploadRunClaim?> TryClaimRunAsync(UploadRecord upload, TimeSpan leaseDuration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the complete file of an upload whose run the caller has claimed, assembling it from the chunks first if the store needs to.
    /// </summary>
    /// <param name="upload">The record of the upload.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <remarks>
    /// It can be called again for the same upload, such as when a run is claimed again after a claim was released.
    /// </remarks>
    ValueTask<UploadContent> GetContentAsync(UploadRecord upload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finishes an upload with its outcome, unless it already has one or no longer exists: stores the outcome with the upload's record, apart from the open
    /// uploads, and removes everything else of the upload, such as its content, its chunks and its run claim.
    /// </summary>
    /// <param name="upload">The record of the upload.</param>
    /// <param name="outcome">The outcome, which <see cref="StoredUpload.Outcome"/> returns from then on.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns><see langword="true"/> if the outcome was stored, or <see langword="false"/> if the upload already has an outcome or no longer
    /// exists.</returns>
    /// <remarks>
    /// An upload that has an outcome can still have parts that a server left when it stopped while it finished the upload, such as its claim, which lapses.
    /// This removes them before it returns <see langword="false"/>, so that the upload then reads as finished: the service ends a run whose claim lapsed
    /// with an outcome of its own, and reads the upload again when that fails.
    /// </remarks>
    ValueTask<bool> TrySetOutcomeAsync(UploadRecord upload, string outcome, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an upload, open or finished, and everything the store keeps for it, including its outcome.
    /// </summary>
    /// <param name="upload">The record of the upload.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns><see langword="true"/> if this call deleted the upload while it was open, or <see langword="false"/> if the upload was finished or no
    /// longer exists, or another deletion deleted it.</returns>
    /// <remarks>
    /// <para>
    /// If the upload's run is claimed, the claim's next renewal fails, so the server that runs the handler stops it. Deleting an upload that no longer
    /// exists does nothing.
    /// </para>
    /// <para>
    /// The service takes an upload out of the counts of open uploads when this returns <see langword="true"/>, or when <see cref="TrySetOutcomeAsync"/> does,
    /// so of all the calls that end an upload, at most one returns <see langword="true"/> (the directory store has one narrow window in which both a deletion
    /// and the storing of the outcome do, described in the uploads guide). Of deletions of an open upload at the same time, also from several servers, one
    /// returns <see langword="true"/>, and a deletion of an upload whose outcome is stored returns <see langword="false"/>. A deletion and a storing of the
    /// upload's outcome at the same time don't both return <see langword="true"/>, but can both return <see langword="false"/>, which leaves the upload in the
    /// counts until the next cleanup corrects them.
    /// </para>
    /// </remarks>
    ValueTask<bool> DeleteAsync(UploadRecord upload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the outcomes of the finished uploads of all users that were stored before the specified time (see <see cref="StoredUpload.OutcomeUtc"/>),
    /// with their records.
    /// </summary>
    /// <param name="storedBeforeUtc">The time in UTC before which the outcomes to remove were stored.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <remarks>
    /// The service calls it in each of its cleanup passes, every few minutes, with the current time less <see cref="ChunkedUploadOptions.OutcomeRetention"/>.
    /// </remarks>
    ValueTask RemoveOutcomesAsync(DateTime storedBeforeUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes data that belongs to no upload, such as what is left of a creation that failed or of a deletion that could not finish. Default does nothing.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <remarks>
    /// The service calls it after each of its cleanup passes, every few minutes.
    /// </remarks>
    ValueTask CleanUpAsync(CancellationToken cancellationToken = default) => default;
}
