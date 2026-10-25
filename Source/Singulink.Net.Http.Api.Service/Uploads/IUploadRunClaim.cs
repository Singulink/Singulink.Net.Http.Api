namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// Represents a server's claim of the run of an upload's handler, which <see cref="IUploadStore.TryClaimRunAsync"/> returns.
/// </summary>
public interface IUploadRunClaim
{
    /// <summary>
    /// Renews the claim's lease for another lease duration.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns><see langword="true"/> if the claim was renewed, or <see langword="false"/> if it was lost: the upload was deleted, it has an outcome, or
    /// its claim is no longer this one. The service then stops the handler.</returns>
    ValueTask<bool> RenewAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gives up the claim without an outcome, so that the upload is back to having all its chunks and its run can be claimed again. Releasing a claim that
    /// was lost does nothing.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    ValueTask ReleaseAsync(CancellationToken cancellationToken = default);
}
