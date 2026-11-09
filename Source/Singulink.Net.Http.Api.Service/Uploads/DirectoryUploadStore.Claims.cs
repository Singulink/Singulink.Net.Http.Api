using System.Diagnostics;
using Singulink.IO;

namespace Singulink.Net.Http.Api.Service.Uploads;

/// <content>
/// The claims of the runs of uploads.
/// </content>
internal sealed partial class DirectoryUploadStore
{
    private const string ClaimFileName = "claim";

    public async ValueTask<IUploadRunClaim?> TryClaimRunAsync(UploadRecord upload, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        var directory = GetUploadDirectory(upload.UserId, upload.Id);

        if (!Exists(directory.CombineFile(RecordFileName, PathOptions.None)) || HasOutcomeOrIsDeleted(directory))
            return null;

        var claimFile = directory.CombineFile(ClaimFileName, PathOptions.None);
        var nowUtc = UtcNow;
        var claim = new ClaimFile { ClaimId = CreateId(), LeaseDuration = leaseDuration, StartedUtc = nowUtc, RenewedUtc = nowUtc };

        try
        {
            if (!await TryCreateFileAsync(claimFile, claim, DirectoryUploadStoreJsonContext.Default.ClaimFile, cancellationToken))
                return null;
        }
        catch (DirectoryNotFoundException)
        {
            // The upload was deleted in the meantime.
            return null;
        }

        var created = new Claim(this, directory, claim.ClaimId, upload);

        try
        {
            // A claim that stalled for long enough before its content arrived can have been taken for broken, which ends the upload as interrupted, and
            // the file is then not this claim's to remove.
            var current = await ReadCreatedFileAsync(claimFile, DirectoryUploadStoreJsonContext.Default.ClaimFile, cancellationToken);

            if (current.Value?.ClaimId != claim.ClaimId)
                return null;

            // An outcome or a deletion that came in the meantime wins, and so does a write of a chunk that is alive.
            if (!HasOutcomeOrIsDeleted(directory) && !await HasLiveWritesAsync(directory, cancellationToken))
                return created;
        }
        catch
        {
            await RemoveClaimAsync(created, claimFile, leaseDuration);
            throw;
        }

        await RemoveClaimAsync(created, claimFile, leaseDuration);
        return null;
    }

    /// <summary>
    /// Waits for a moment while the upload's run is claimed, for a claim that was made at the same time as a write of a chunk: a claim that sees the write's
    /// marker backs off, and is removed within a moment.
    /// </summary>
    /// <returns><see langword="true"/> if the run is not claimed, or <see langword="false"/> if it still is, or the upload has an outcome.</returns>
    private static async Task<bool> WaitWhileClaimedAsync(IAbsoluteDirectoryPath directory, CancellationToken cancellationToken)
    {
        var claimFile = directory.CombineFile(ClaimFileName, PathOptions.None);

        // By the system's clock rather than the time provider: another request is working on the files, which takes real time whatever a test's clock says.
        long started = Stopwatch.GetTimestamp();

        while (Exists(claimFile))
        {
            if (Stopwatch.GetElapsedTime(started) > RemovalWait)
                return false;

            await Task.Delay(ReadAgainDelay, cancellationToken);
        }

        return !Exists(directory.CombineFile(FinishedFileName, PathOptions.None));
    }

    /// <summary>
    /// Removes a claim that backs off. A claim that is left would lapse, which ends the upload as interrupted, so one that can't be removed, such as while
    /// another process has it open, is kept renewed and removed again in the background, for up to 10 minutes.
    /// </summary>
    private async Task RemoveClaimAsync(Claim claim, IAbsoluteFilePath claimFile, TimeSpan leaseDuration)
    {
        try
        {
            await claim.RemoveAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The claim {Path} that backed off could not be removed, and is removed again in the background.", claimFile.PathDisplay);

            _ = KeepRemovingAsync(
                "The claim that backed off",
                claimFile,
                () => claim.RemoveAsync(CancellationToken.None),
                async () => await claim.RenewAsync(CancellationToken.None),
                leaseDuration / 4);
        }
    }

    /// <summary>
    /// A claim of a run, kept in the upload's <c>claim</c> file.
    /// </summary>
    private sealed class Claim(DirectoryUploadStore store, IAbsoluteDirectoryPath directory, string claimId, UploadRecord upload) : IUploadRunClaim
    {
        private IAbsoluteFilePath ClaimFile { get; } = directory.CombineFile(ClaimFileName, PathOptions.None);

        public async ValueTask<bool> RenewAsync(CancellationToken cancellationToken = default)
        {
            if (HasOutcomeOrIsDeleted(directory) || await ReadCurrentAsync(cancellationToken) is not { } claim)
                return false;

            var renewed = new ClaimFile {
                ClaimId = claim.ClaimId, LeaseDuration = claim.LeaseDuration, StartedUtc = claim.StartedUtc, RenewedUtc = store.UtcNow,
            };

            await WriteFileAsync(ClaimFile, renewed, DirectoryUploadStoreJsonContext.Default.ClaimFile, cancellationToken);
            return true;
        }

        public async ValueTask ReleaseAsync(CancellationToken cancellationToken = default)
        {
            if (await ReadCurrentAsync(cancellationToken) is null)
                return;

            // The upload has all its chunks again, and is kept for as long as after its last chunk.
            try
            {
                string dataPath = directory.CombineFile(DataFileName, PathOptions.None).PathExport;
                await RetryAsync(() => File.SetLastWriteTimeUtc(dataPath, store.GetChunkTimeUtc(upload)), cancellationToken);
            }
            catch (FileNotFoundException)
            {
            }

            // A claim that is left would lapse, which ends the upload as interrupted, so a claim that can't be removed fails the release.
            await DeleteFileAsync(ClaimFile, cancellationToken);
        }

        /// <summary>
        /// Removes the claim, if it is still this one, without touching the upload.
        /// </summary>
        public async Task RemoveAsync(CancellationToken cancellationToken)
        {
            if (await ReadCurrentAsync(cancellationToken) is not null)
                await DeleteFileAsync(ClaimFile, cancellationToken);
        }

        private async Task<ClaimFile?> ReadCurrentAsync(CancellationToken cancellationToken)
        {
            var claim = await ReadFileAsync(ClaimFile, DirectoryUploadStoreJsonContext.Default.ClaimFile, cancellationToken);
            return claim?.ClaimId == claimId ? claim : null;
        }
    }
}
