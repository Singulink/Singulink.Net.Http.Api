using System.Text.Json;
using Singulink.IO;

namespace Singulink.Net.Http.Api.Service.Uploads;

/// <content>
/// The cleanup removes the outcomes that are no longer kept by their names, and the directories of creations that stopped before they moved them to their
/// uploads' names by their creation times. It removes the directories of uploads that 4.x finished, whose outcomes are in an <c>outcome.json</c> of their
/// own, without keeping their outcomes, and the reservations of the idempotency keys of 4.x, which uploads no longer have.
/// </content>
internal sealed partial class DirectoryUploadStore
{
    // The directory of the reservations of the idempotency keys of a user in 4.x.
    // TODO 6.0: remove, with its removal below.
    private const string LegacyKeysDirectoryName = "keys";

    // How old the files of a creation that failed, temporary files and the markers of writes that stopped must be before they are removed, which leaves time
    // for the creation or the write to finish.
    private static readonly TimeSpan StrayDataAge = TimeSpan.FromHours(1);

    public async ValueTask CleanUpAsync(CancellationToken cancellationToken = default)
    {
        // The file system sets the times of files and directories by the system's clock, which they are compared with, so that a time provider ahead of the
        // system's clock can't make a file that is being written look stray.
        var strayFilesBeforeUtc = TimeProvider.System.GetUtcNow().UtcDateTime - StrayDataAge;

        foreach (var userDirectory in GetDirectories(RootDirectory))
        {
            TryDeleteDirectory(userDirectory.CombineDirectory(LegacyKeysDirectoryName, PathOptions.None));

            foreach (var uploadDirectory in GetDirectories(userDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (uploadDirectory.Name.EndsWith(NewDirectoryExtension, StringComparison.Ordinal))
                {
                    if (Directory.GetCreationTimeUtc(uploadDirectory.PathExport) < strayFilesBeforeUtc)
                        TryDeleteDirectory(uploadDirectory);

                    continue;
                }

                // The directory of the outcomes is not an upload.
                if (!UploadIds.IsWellFormed(uploadDirectory.Name))
                    continue;

                try
                {
                    await CleanUpUploadDirectoryAsync(uploadDirectory, strayFilesBeforeUtc, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    _logger.LogWarning(ex, "The leftovers of uploads in {Directory} could not be removed.", uploadDirectory.PathDisplay);
                }
            }

            var outcomesDirectory = userDirectory.CombineDirectory(OutcomesDirectoryName, PathOptions.None);
            RemoveStrayFiles(outcomesDirectory, TemporaryFileExtension, strayFilesBeforeUtc);
            TryDeleteEmptyDirectory(outcomesDirectory);

            try
            {
                // A creation of an upload creates the user's directory before the upload's directory in it, so a directory that changed lately is kept.
                if (Directory.GetLastWriteTimeUtc(userDirectory.PathExport) < strayFilesBeforeUtc && !userDirectory.GetChildEntries().Any())
                    userDirectory.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A new upload of the user was created in the meantime, or the directory is in use.
            }
        }
    }

    /// <summary>
    /// Removes what is left in the directory of an upload: the directory of a deleted upload, of a creation that failed or of an upload that 4.x finished,
    /// whose outcome is not kept, and the temporary files and the markers of writes that stopped. Moves a finished upload on to its outcome.
    /// </summary>
    private async Task CleanUpUploadDirectoryAsync(IAbsoluteDirectoryPath directory, DateTime strayFilesBeforeUtc, CancellationToken cancellationToken)
    {
        var recordFile = directory.CombineFile(RecordFileName, PathOptions.None);
        bool isDeleted = Exists(directory.CombineFile(DeletedFileName, PathOptions.None));
        bool hasRecord = Exists(recordFile);

        if (isDeleted || (!hasRecord && Directory.GetCreationTimeUtc(directory.PathExport) < strayFilesBeforeUtc))
        {
            TryDeleteDirectory(directory);
            return;
        }

        if (Exists(directory.CombineFile(FinishedFileName, PathOptions.None)))
        {
            await MoveOnFinishedAsync(directory, cancellationToken);
            return;
        }

        if (hasRecord && Exists(directory.CombineFile(LegacyOutcomeFileName, PathOptions.None)))
        {
            if (await ReadFileAsync(recordFile, DirectoryUploadStoreJsonContext.Default.RecordFile, cancellationToken) is { } record)
                await DeleteAsync(ToRecord(record), cancellationToken);
            else
                TryDeleteDirectory(directory);

            return;
        }

        var chunksDirectory = directory.CombineDirectory(ChunksDirectoryName, PathOptions.None);
        RemoveStrayFiles(directory, TemporaryFileExtension, strayFilesBeforeUtc);
        RemoveStrayFiles(chunksDirectory, TemporaryFileExtension, strayFilesBeforeUtc);
        RemoveStrayFiles(chunksDirectory, WritingFileExtension, strayFilesBeforeUtc);
    }

    private static void RemoveStrayFiles(IAbsoluteDirectoryPath directory, string extension, DateTime strayBeforeUtc)
    {
        try
        {
            foreach (var file in directory.GetChildFiles("*" + extension))
            {
                if (File.GetLastWriteTimeUtc(file.PathExport) < strayBeforeUtc)
                    TryDeleteFile(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Removed by a later cleanup.
        }
    }
}
