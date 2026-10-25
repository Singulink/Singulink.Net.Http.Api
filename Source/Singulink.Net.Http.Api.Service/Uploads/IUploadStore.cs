namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// Represents the storage of chunked uploads, which keeps everything the service knows about them: their records, their chunks, the claims of their
/// handler runs and their outcomes. Chunked uploads use a store in a directory by default (see <see cref="ChunkedUploadOptions.Directory"/>), and a store
/// that the app registers as a singleton <see cref="IUploadStore"/> service instead.
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
/// <item>Storing an outcome is atomic and durable: only the first outcome of an upload is stored, and once stored it is kept until the upload is
/// deleted.</item>
/// <item>Reserving an idempotency key is atomic: of reservations of a user's key made at the same time, one succeeds, and the others get it.</item>
/// </list>
/// <para>
/// The service applies all of the protocol's rules, such as the users and endpoints that uploads belong to, their states, limits and expiry, and the
/// lengths of chunks, so a store only keeps what it is given. <see cref="UploadRecord.Metadata"/> and outcomes are text in a format of the service's own,
/// which a store keeps as it is.
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
    /// Gets the smallest chunk size that the store accepts for every chunk but the last, in bytes. The service never uses chunks smaller than 5 MiB.
    /// Default is 0, for no limit of the store's own.
    /// </summary>
    int MinChunkSize => 0;

    /// <summary>
    /// Gets the largest chunk size that the store accepts, in bytes. The service never uses chunks larger than 64 MiB. Default is <see cref="int.MaxValue"/>,
    /// for no limit of the store's own.
    /// </summary>
    int MaxChunkSize => int.MaxValue;

    /// <summary>
    /// Gets the largest number of chunks that the store accepts for an upload. Default is <see cref="int.MaxValue"/>, for no limit of the store's own.
    /// </summary>
    int MaxChunkCount => int.MaxValue;

    /// <summary>
    /// Reserves an idempotency key of a user for an upload that is about to be created, unless the key is reserved already.
    /// </summary>
    /// <param name="userId">The ID of the user.</param>
    /// <param name="key">The idempotency key, of 1 to 128 characters.</param>
    /// <param name="uploadId">The ID of the upload that is about to be created.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The key's reservation: for <paramref name="uploadId"/> if the key was reserved now, or the reservation that was made before.</returns>
    /// <remarks>
    /// A key stays reserved until it is released with <see cref="ReleaseKeyAsync"/> or the upload it is reserved for is deleted, which releases the key
    /// of its record (<see cref="UploadRecord.IdempotencyKey"/>).
    /// </remarks>
    ValueTask<UploadKeyReservation> ReserveKeyAsync(string userId, string key, string uploadId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the reservation of an idempotency key of a user if it is reserved for the specified upload, such as when the upload's creation failed.
    /// </summary>
    /// <param name="userId">The ID of the user.</param>
    /// <param name="key">The idempotency key.</param>
    /// <param name="uploadId">The ID of the upload that the key is reserved for.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    ValueTask ReleaseKeyAsync(string userId, string key, string uploadId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an upload with the specified record, reserving room for its length if the store can.
    /// </summary>
    /// <param name="upload">The record of the new upload, with a new random ID.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns><see langword="true"/> if the upload was created, or <see langword="false"/> if the store has no room for it, which the service reports
    /// as <see cref="UploadErrorCodes.StorageFull"/>.</returns>
    ValueTask<bool> TryCreateAsync(UploadRecord upload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets an upload of a user with its current state.
    /// </summary>
    /// <param name="userId">The ID of the user the upload belongs to.</param>
    /// <param name="uploadId">The ID of the upload, which the service has checked to be a well-formed ID.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The upload, or <see langword="null"/> if the user has no upload with the ID, such as when it was deleted.</returns>
    ValueTask<StoredUpload?> GetAsync(string userId, string uploadId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists uploads with their current state: the uploads of a user, or all of them. The service lists them to count a user's open uploads, and to find
    /// the uploads to remove and their total size.
    /// </summary>
    /// <param name="userId">The ID of the user whose uploads are listed, or <see langword="null"/> to list all uploads.</param>
    /// <param name="withoutOutcomes">Whether uploads that have an outcome are left out, which the store can do without reading them. The service leaves
    /// them out when it counts a user's open uploads.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    IAsyncEnumerable<StoredUpload> ListAsync(string? userId = null, bool withoutOutcomes = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a chunk of an upload, replacing the chunk if it was already written, unless the run of the upload's handler is claimed or the upload has an
    /// outcome. The chunk is reported as received once this returns <see cref="ChunkWriteResult.Written"/>. The upload's
    /// <see cref="StoredUpload.LastWriteUtc"/> is the time when a write starts, and again when it ends.
    /// </summary>
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
    /// Gets the complete file of an upload whose run the caller has claimed, assembling it from the chunks first if the store needs to. It can be called
    /// again for the same upload, such as when a run is claimed again after a claim was released.
    /// </summary>
    /// <param name="upload">The record of the upload.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    ValueTask<UploadContent> GetContentAsync(UploadRecord upload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the outcome of an upload, unless it already has one or no longer exists, and removes its content, its chunks and its run claim.
    /// </summary>
    /// <param name="upload">The record of the upload.</param>
    /// <param name="outcome">The outcome, which <see cref="StoredUpload.Outcome"/> returns from then on.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns><see langword="true"/> if the outcome was stored, or <see langword="false"/> if the upload already has an outcome or no longer
    /// exists.</returns>
    ValueTask<bool> TrySetOutcomeAsync(UploadRecord upload, string outcome, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an upload and everything the store keeps for it, including the reservation of its idempotency key. If the upload's run is claimed, the
    /// claim's next renewal fails, so the server that runs the handler stops it. Deleting an upload that no longer exists does nothing.
    /// </summary>
    /// <param name="upload">The record of the upload.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    ValueTask DeleteAsync(UploadRecord upload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes data that belongs to no upload, such as what is left of a creation that failed or of a deletion that could not finish, and reservations of
    /// idempotency keys for uploads that were never created. The service calls it after each of its cleanup passes, every few minutes. Default does
    /// nothing.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    ValueTask CleanUpAsync(CancellationToken cancellationToken = default) => default;
}
