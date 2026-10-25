using System.Runtime.CompilerServices;

namespace Singulink.Net.Http.Api.Service.Uploads;

#pragma warning disable SA1402 // File may only contain a single type

/// <summary>
/// The upload store as the service uses it: a failure of the store, such as a share that can't be reached for a moment, is logged and reported as
/// <see cref="UploadErrorCodes.StoreUnavailable"/>, which clients retry, rather than as a server error, which fails the upload for good.
/// </summary>
internal sealed class GuardedUploadStore(IUploadStore store, ILogger logger) : IUploadStore
{
    /// <summary>
    /// The retry time of a request that the store failed.
    /// </summary>
    internal static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(2);

    public int MinChunkSize => store.MinChunkSize;

    public int MaxChunkSize => store.MaxChunkSize;

    public int MaxChunkCount => store.MaxChunkCount;

    public async ValueTask<UploadKeyReservation> ReserveKeyAsync(string userId, string key, string uploadId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await store.ReserveKeyAsync(userId, key, uploadId, cancellationToken);
        }
        catch (Exception ex) when (IsStoreFailure(ex, cancellationToken))
        {
            throw Unavailable(ex, nameof(ReserveKeyAsync));
        }
    }

    public async ValueTask ReleaseKeyAsync(string userId, string key, string uploadId, CancellationToken cancellationToken = default)
    {
        try
        {
            await store.ReleaseKeyAsync(userId, key, uploadId, cancellationToken);
        }
        catch (Exception ex) when (IsStoreFailure(ex, cancellationToken))
        {
            throw Unavailable(ex, nameof(ReleaseKeyAsync));
        }
    }

    public async ValueTask<bool> TryCreateAsync(UploadRecord upload, CancellationToken cancellationToken = default)
    {
        try
        {
            return await store.TryCreateAsync(upload, cancellationToken);
        }
        catch (Exception ex) when (IsStoreFailure(ex, cancellationToken))
        {
            throw Unavailable(ex, nameof(TryCreateAsync));
        }
    }

    public async ValueTask<StoredUpload?> GetAsync(string userId, string uploadId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await store.GetAsync(userId, uploadId, cancellationToken);
        }
        catch (Exception ex) when (IsStoreFailure(ex, cancellationToken))
        {
            throw Unavailable(ex, nameof(GetAsync));
        }
    }

    public async IAsyncEnumerable<StoredUpload> ListAsync(
        string? userId = null, bool withoutOutcomes = false, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var uploads = store.ListAsync(userId, withoutOutcomes, cancellationToken).GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            try
            {
                if (!await uploads.MoveNextAsync())
                    break;
            }
            catch (Exception ex) when (IsStoreFailure(ex, cancellationToken))
            {
                throw Unavailable(ex, nameof(ListAsync));
            }

            yield return uploads.Current;
        }
    }

    public async ValueTask<ChunkWriteResult> WriteChunkAsync(UploadRecord upload, int index, Stream content, CancellationToken cancellationToken = default)
    {
        try
        {
            return await store.WriteChunkAsync(upload, index, content, cancellationToken);
        }
        catch (Exception ex) when (IsStoreFailure(ex, cancellationToken) && content is not ChunkContentStream { BodyException: not null })
        {
            // A failure of the chunk's body, such as of a client that drops the connection, is the request's, which the chunk endpoint reports.
            throw Unavailable(ex, nameof(WriteChunkAsync));
        }
    }

    public async ValueTask<IReadOnlyCollection<int>> GetReceivedChunksAsync(UploadRecord upload, CancellationToken cancellationToken = default)
    {
        try
        {
            return await store.GetReceivedChunksAsync(upload, cancellationToken);
        }
        catch (Exception ex) when (IsStoreFailure(ex, cancellationToken))
        {
            throw Unavailable(ex, nameof(GetReceivedChunksAsync));
        }
    }

    public async ValueTask<IUploadRunClaim?> TryClaimRunAsync(UploadRecord upload, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        try
        {
            return await store.TryClaimRunAsync(upload, leaseDuration, cancellationToken) is { } claim ? new GuardedClaim(claim, this) : null;
        }
        catch (Exception ex) when (IsStoreFailure(ex, cancellationToken))
        {
            throw Unavailable(ex, nameof(TryClaimRunAsync));
        }
    }

    public async ValueTask<UploadContent> GetContentAsync(UploadRecord upload, CancellationToken cancellationToken = default)
    {
        try
        {
            return await store.GetContentAsync(upload, cancellationToken);
        }
        catch (Exception ex) when (IsStoreFailure(ex, cancellationToken))
        {
            throw Unavailable(ex, nameof(GetContentAsync));
        }
    }

    public async ValueTask<bool> TrySetOutcomeAsync(UploadRecord upload, string outcome, CancellationToken cancellationToken = default)
    {
        try
        {
            return await store.TrySetOutcomeAsync(upload, outcome, cancellationToken);
        }
        catch (Exception ex) when (IsStoreFailure(ex, cancellationToken))
        {
            throw Unavailable(ex, nameof(TrySetOutcomeAsync));
        }
    }

    public async ValueTask DeleteAsync(UploadRecord upload, CancellationToken cancellationToken = default)
    {
        try
        {
            await store.DeleteAsync(upload, cancellationToken);
        }
        catch (Exception ex) when (IsStoreFailure(ex, cancellationToken))
        {
            throw Unavailable(ex, nameof(DeleteAsync));
        }
    }

    public async ValueTask CleanUpAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await store.CleanUpAsync(cancellationToken);
        }
        catch (Exception ex) when (IsStoreFailure(ex, cancellationToken))
        {
            throw Unavailable(ex, nameof(CleanUpAsync));
        }
    }

    /// <summary>
    /// Gets a value indicating whether an exception of the store is a failure of the store, rather than an error that the store reports as the service
    /// would, or the cancellation of the operation.
    /// </summary>
    internal static bool IsStoreFailure(Exception exception, CancellationToken cancellationToken)
    {
        return exception is not ApiException && !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested);
    }

    private ServiceUnavailableApiException Unavailable(Exception exception, string operation)
    {
        logger.LogWarning(
            exception, "The upload store failed ({Operation}), which is reported as unavailable for a moment, so that it is tried again.", operation);
        return UploadErrors.StoreUnavailable(RetryAfter, exception);
    }

    /// <summary>
    /// Gets the store's own claim of a guarded claim, whose failures are not logged.
    /// </summary>
    internal static IUploadRunClaim Unwrap(IUploadRunClaim claim) => claim is GuardedClaim guarded ? guarded.Inner : claim;

    /// <summary>
    /// A claim of the store as the service uses it (see <see cref="GuardedUploadStore"/>).
    /// </summary>
    private sealed class GuardedClaim(IUploadRunClaim claim, GuardedUploadStore store) : IUploadRunClaim
    {
        public IUploadRunClaim Inner => claim;

        public async ValueTask<bool> RenewAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                return await claim.RenewAsync(cancellationToken);
            }
            catch (Exception ex) when (IsStoreFailure(ex, cancellationToken))
            {
                throw store.Unavailable(ex, nameof(RenewAsync));
            }
        }

        public async ValueTask ReleaseAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await claim.ReleaseAsync(cancellationToken);
            }
            catch (Exception ex) when (IsStoreFailure(ex, cancellationToken))
            {
                throw store.Unavailable(ex, nameof(ReleaseAsync));
            }
        }
    }
}
