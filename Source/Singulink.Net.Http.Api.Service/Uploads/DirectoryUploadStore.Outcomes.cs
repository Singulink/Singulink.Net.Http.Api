using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Singulink.IO;

namespace Singulink.Net.Http.Api.Service.Uploads;

/// <content>
/// A finished upload leaves its directory: its outcome is stored with its record in <c>finished.json</c> in the directory, which only one server can
/// create, then copied to <c>{Directory}/{userKey}/outcomes/{uploadId}.{ticks}.json</c>, where <c>ticks</c> is the time it was stored, by the server's
/// clock, and the directory is then removed. The copy is written under a temporary name and moved to its name, so it is complete whenever it can be read,
/// and every server that copies an outcome writes the same file. The names of the outcomes tell which ones to remove without opening them, and the open
/// uploads are the only directories that a listing of them reads. Reading a finished upload looks for its outcome by its ID among the names of the user's
/// outcomes, which on a share lists them over the network. A directory that still has its <c>finished.json</c>, such as of a server that stopped while it
/// finished the upload, or whose record another process had open while it was removed, reads as finished, and is moved on by the next attempt to store an
/// outcome or the next cleanup.
/// </content>
internal sealed partial class DirectoryUploadStore
{
    private const string FinishedFileName = "finished.json";
    private const string OutcomesDirectoryName = "outcomes";
    private const string OutcomeFileExtension = "json";

    public async ValueTask<bool> TrySetOutcomeAsync(UploadRecord upload, string outcome, CancellationToken cancellationToken = default)
    {
        var directory = GetUploadDirectory(upload.UserId, upload.Id);
        var recordFile = directory.CombineFile(RecordFileName, PathOptions.None);
        var deletedFile = directory.CombineFile(DeletedFileName, PathOptions.None);

        if (!Exists(recordFile) || Exists(deletedFile))
            return false;

        var finished = new OutcomeFile { Record = ToRecordFile(upload), Outcome = outcome, StoredUtc = UtcNow };
        var finishedFile = directory.CombineFile(FinishedFileName, PathOptions.None);

        try
        {
            // Of the servers that finish an upload at the same time, the one that creates the file stores its outcome.
            if (!await TryCreateFileAsync(finishedFile, finished, DirectoryUploadStoreJsonContext.Default.OutcomeFile, cancellationToken))
            {
                await MoveOnFinishedAsync(directory, cancellationToken);
                return false;
            }
        }
        catch (DirectoryNotFoundException)
        {
            // The upload was deleted in the meantime.
            return false;
        }

        // A deletion that found no outcome in the directory, and so deleted the open upload, created its marker before this file was created. The marker is
        // there now, unless the deletion has removed the directory since: it removes the record first, which the late check below finds. A file in a directory
        // that is being removed can't be opened for a moment on Windows, so the marker is opened again until the directory is gone.
        if (await ExistsAsync(deletedFile, cancellationToken))
            return false;

        var userDirectory = directory.ParentDirectory!;

        // A server that moves the upload on, such as one whose outcome lost to this one, copies the outcome to the user's outcomes before it removes the
        // record and the file. A file that was created after that, in a directory that is being removed, finds another outcome there, or no record and not
        // its own outcome, as after a deletion too, and the upload's outcome is not this one.
        var outcomeFile = GetOutcomeFile(userDirectory, finished);
        bool isLate = FindOutcomeFiles(outcomeFile.ParentDirectory, upload.Id).Any(f => f.Name != outcomeFile.Name) ||
            (!await ExistsAsync(recordFile, cancellationToken) && !Exists(outcomeFile));

        if (isLate)
        {
            await RemoveDirectoryAsync(directory);
            return false;
        }

        await PublishOutcomeAsync(userDirectory, finished, cancellationToken);

        // A deletion of the upload that started in the meantime may have looked for its outcome before it was there, and is completed here. Only a
        // deletion, or a creation that gives up the directory, creates the marker: a server that moves the upload on removes the record and then the
        // directory, whose files can't be opened for a moment on Windows while it is removed.
        if (await ExistsAsync(deletedFile, cancellationToken))
        {
            await RemoveOutcomeFilesAsync(userDirectory, upload.Id, cancellationToken);
            return false;
        }

        await RemoveUploadDirectoryAsync(directory);
        return true;
    }

    public async ValueTask RemoveOutcomesAsync(DateTime storedBeforeUtc, CancellationToken cancellationToken = default)
    {
        foreach (var userDirectory in GetDirectories(RootDirectory))
        {
            // Only the names of the outcomes are read.
            foreach (var info in EnumerateFiles(userDirectory.CombineDirectory(OutcomesDirectoryName, PathOptions.None)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = info.Path;

                if (!TryParseOutcomeFileName(file.Name, out _, out long storedTicks) || storedTicks >= storedBeforeUtc.Ticks)
                    continue;

                try
                {
                    await DeleteFileAsync(file, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Removed by a later cleanup.
                    _logger.LogWarning(ex, "The outcome of an upload in {Path} could not be removed.", file.PathDisplay);
                }
            }
        }
    }

    /// <summary>
    /// Moves a finished upload on from its directory, which another server, or a server that stopped, left with its <c>finished.json</c>: copies its
    /// outcome to the user's outcomes, if it is not there yet, and removes the directory. An outcome that is still being written is left to its writer,
    /// and one that can't be read is kept as an outcome that can't be read. An upload that a deletion has marked is removed without its outcome, as by
    /// <see cref="TrySetOutcomeAsync"/>.
    /// </summary>
    private async Task MoveOnFinishedAsync(IAbsoluteDirectoryPath directory, CancellationToken cancellationToken)
    {
        var deletedFile = directory.CombineFile(DeletedFileName, PathOptions.None);

        if (await ExistsAsync(deletedFile, cancellationToken))
        {
            // The record goes first, as in a deletion, so that a store of the outcome that no longer finds the marker finds no record either.
            await RemoveUploadDirectoryAsync(directory);
            return;
        }

        var finishedFile = directory.CombineFile(FinishedFileName, PathOptions.None);
        var finished = await ReadCreatedFileAsync(finishedFile, DirectoryUploadStoreJsonContext.Default.OutcomeFile, cancellationToken);
        OutcomeFile? outcome;

        if (finished.State is CreatedFileState.Complete)
        {
            outcome = finished.Value;
        }
        else if (finished.State is CreatedFileState.Broken)
        {
            _logger.LogWarning(
                "The outcome of the upload in {Directory} cannot be read, and is kept as an outcome that can't be read.", directory.PathDisplay);

            var record = await ReadFileAsync(
                directory.CombineFile(RecordFileName, PathOptions.None), DirectoryUploadStoreJsonContext.Default.RecordFile, cancellationToken);
            outcome = record is null ? null : new OutcomeFile { Record = record, Outcome = string.Empty, StoredUtc = finished.WrittenUtc };
        }
        else
        {
            return;
        }

        if (outcome is not null)
        {
            var userDirectory = directory.ParentDirectory!;
            await PublishOutcomeAsync(userDirectory, outcome, cancellationToken);

            // A deletion of the upload that started in the meantime may have removed its outcomes before the copy was there (see TrySetOutcomeAsync).
            if (await ExistsAsync(deletedFile, cancellationToken))
            {
                await RemoveOutcomeFilesAsync(userDirectory, outcome.Record.Id, cancellationToken);
                return;
            }
        }

        await RemoveUploadDirectoryAsync(directory);
    }

    /// <summary>
    /// Copies the outcome of a finished upload to the user's outcomes, unless it is there already, such as when another server copied it. The copy is
    /// written under a temporary name and moved to its name, so that it is complete whenever it can be read, also after a crash while it is written.
    /// Servers that copy an outcome at the same time write the same file, and the first move gives it its name.
    /// </summary>
    private static async Task PublishOutcomeAsync(IAbsoluteDirectoryPath userDirectory, OutcomeFile outcome, CancellationToken cancellationToken)
    {
        var outcomesDirectory = userDirectory.CombineDirectory(OutcomesDirectoryName, PathOptions.None);

        if (FindOutcomeFiles(outcomesDirectory, outcome.Record.Id).Any())
            return;

        var file = GetOutcomeFile(userDirectory, outcome);

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                outcomesDirectory.Create();
                var typeInfo = DirectoryUploadStoreJsonContext.Default.OutcomeFile;
                var temporaryFile = await WriteTemporaryFileAsync(file, outcome, typeInfo, flushToDisk: true, cancellationToken);

                try
                {
                    await RetryAsync(() => MoveUnlessExists(temporaryFile, file), cancellationToken);
                }
                finally
                {
                    // Another server moved its copy to the name first, or the move failed.
                    TryDeleteFile(temporaryFile);
                }

                return;
            }
            catch (DirectoryNotFoundException) when (attempt < Attempts)
            {
                // The cleanup removed the directory of the outcomes while it was empty.
            }
        }

        static void MoveUnlessExists(IAbsoluteFilePath temporaryFile, IAbsoluteFilePath file)
        {
            try
            {
                temporaryFile.MoveTo(file, overwrite: false);
            }
            catch (IOException) when (Exists(file))
            {
            }
        }
    }

    private static async Task RemoveOutcomeFilesAsync(IAbsoluteDirectoryPath userDirectory, string uploadId, CancellationToken cancellationToken)
    {
        foreach (var file in FindOutcomeFiles(userDirectory.CombineDirectory(OutcomesDirectoryName, PathOptions.None), uploadId).ToList())
            await DeleteFileAsync(file, cancellationToken);
    }

    /// <summary>
    /// Reads the outcome of a finished upload from the user's outcomes, which is complete whenever it can be read (see <see cref="PublishOutcomeAsync"/>).
    /// </summary>
    /// <returns>The finished upload, or <see langword="null"/> if it has no outcome there, or one that can't be read, such as a file that was damaged on
    /// the disk, which holds the upload's record too.</returns>
    private async Task<StoredUpload?> ReadOutcomeAsync(
        IAbsoluteDirectoryPath userDirectory, string userId, string uploadId, CancellationToken cancellationToken)
    {
        if (FindOutcomeFiles(userDirectory.CombineDirectory(OutcomesDirectoryName, PathOptions.None), uploadId).FirstOrDefault() is not { } file)
            return null;

        OutcomeFile? outcome;

        try
        {
            // An outcome that is removed in the meantime is missing.
            outcome = await ReadFileAsync(file, DirectoryUploadStoreJsonContext.Default.OutcomeFile, cancellationToken);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "The outcome of an upload in {Path} cannot be read.", file.PathDisplay);
            return null;
        }

        // File names are not case-sensitive on some file systems, so the record has the exact ID and user.
        if (outcome?.Record is not { } record || record.Id != uploadId || record.UserId != userId)
            return null;

        return new StoredUpload(ToRecord(record), record.CreatedUtc, outcome: outcome.Outcome, outcomeUtc: outcome.StoredUtc);
    }

    /// <summary>
    /// Gets the outcomes of an upload in a directory of outcomes. An upload has one: every server that copies it gives it the same name. The names are
    /// matched as written rather than as Win32 patterns with their special cases, and what can't be read fails the search rather than being left out.
    /// </summary>
    private static IEnumerable<IAbsoluteFilePath> FindOutcomeFiles(IAbsoluteDirectoryPath outcomesDirectory, string uploadId)
    {
        IAbsoluteFilePath[] files;

        try
        {
            files = [.. outcomesDirectory.GetChildFiles(uploadId + ".*")];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }

        return files.Where(f => TryParseOutcomeFileName(f.Name, out string? id, out _) && id == uploadId);
    }

    /// <summary>
    /// Gets a value indicating whether the user's outcomes have an outcome of an upload.
    /// </summary>
    private static bool HasOutcome(IAbsoluteDirectoryPath userDirectory, string uploadId) =>
        FindOutcomeFiles(userDirectory.CombineDirectory(OutcomesDirectoryName, PathOptions.None), uploadId).Any();

    /// <summary>
    /// Gets the file of an outcome in the user's outcomes.
    /// </summary>
    private static IAbsoluteFilePath GetOutcomeFile(IAbsoluteDirectoryPath userDirectory, OutcomeFile outcome)
    {
        var record = outcome.Record;
        string name = string.Create(CultureInfo.InvariantCulture, $"{record.Id}.{outcome.StoredUtc.Ticks}.{OutcomeFileExtension}");

        return userDirectory.CombineDirectory(OutcomesDirectoryName, PathOptions.None).CombineFile(name, PathOptions.None);
    }

    /// <summary>
    /// Reads the name of the file of an outcome: <c>{uploadId}.{ticks}.json</c>. IDs have no dots.
    /// </summary>
    private static bool TryParseOutcomeFileName(ReadOnlySpan<char> name, [NotNullWhen(true)] out string? uploadId, out long storedTicks)
    {
        uploadId = null;
        storedTicks = 0;

        // The name is parsed without allocating, since the cleanup parses the names of all outcomes, and only the ID is made a string.
        Span<Range> parts = stackalloc Range[4];

        if (name.Split(parts, '.') is not 3 || !name[parts[2]].SequenceEqual(OutcomeFileExtension) ||
            !long.TryParse(name[parts[1]], NumberStyles.None, CultureInfo.InvariantCulture, out storedTicks) || !UploadIds.IsWellFormed(name[parts[0]]))
        {
            return false;
        }

        uploadId = name[parts[0]].ToString();
        return true;
    }
}
