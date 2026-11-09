using System.Runtime.CompilerServices;
using System.Text.Json;
using Singulink.IO;

namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// The <see cref="IUploadStore"/> that keeps uploads in a directory (see <see cref="DirectoryUploadStoreOptions"/>) on a local disk or a share that several
/// servers mount.
/// </summary>
/// <remarks>
/// <para>
/// Each open upload has a directory, <c>{Directory}/{userKey}/{uploadId}</c>, where <c>userKey</c> is a hash of the user ID. It holds <c>upload.json</c>,
/// the upload's record, and <c>data</c>, the file, which is created with the upload's length and, where the file system can, space reserved for it. A
/// creation makes the directory under a name of its own, <c>{uploadId}.{random}.new</c>, and then moves it to the upload's name, which fails while the user
/// has an upload with the ID, so that of creations of an upload at the same time one creates it, and an upload exists only once it is complete. Each
/// chunk is written at its offset through to the disk, and only then is an empty marker, <c>chunks/{index}</c>, created for it, so a chunk whose write was
/// interrupted is missing. A write of a chunk that was received removes its marker first.
/// </para>
/// <para>
/// Paths are Singulink.IO paths. The operations that Singulink.IO would make with an extra call to the file system, such as the check of a directory's type
/// before a move, or of a file's existence before its times are read or set, are made on <see cref="IAbsolutePath.PathExport"/> instead, since on a share
/// each call is a round trip.
/// </para>
/// </remarks>
internal sealed partial class DirectoryUploadStore : IUploadStore, IDisposable
{
    private const string RecordFileName = "upload.json";
    private const string DeletedFileName = "deleted";

    // The lock file of a directory that only this process uses, in the root directory.
    private const string LockFileName = "uploads.lock";

    // The extension of the directory that a creation makes an upload in, before it moves the directory to the upload's name.
    private const string NewDirectoryExtension = ".new";

    // The outcome of an upload that 4.x finished, in the upload's directory.
    private const string LegacyOutcomeFileName = "outcome.json";

    // How long the metrics go without the store's free space after a read of it failed, such as on a share that can't be reached, where a read waits for a
    // timeout.
    private static readonly TimeSpan AvailableBytesRetryDelay = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    // Stops the removals in the background once the store is disposed.
    private readonly CancellationTokenSource _disposed = new();

    // The lock file, open without sharing while the store is in use, of a directory that only this process uses.
    private readonly FileStream? _lockFile;

    // When a read of the free space for the metrics last failed, in UTC ticks, or 0 if none did.
    private long _availableBytesFailureTicks;

    public DirectoryUploadStore(DirectoryUploadStoreOptions options, TimeProvider timeProvider, ILoggerFactory loggerFactory)
    {
        // Freezing checks the options, so the required values are set, and they can no longer change from then on.
        options.Freeze();
        RootDirectory = options.Directory;
        SharingMode = options.SharingMode;

        _timeProvider = timeProvider;
        _logger = loggerFactory.CreateLogger(typeof(DirectoryUploadStore).FullName!);

        if (SharingMode is StoreSharingMode.ProcessLocal)
            _lockFile = OpenLockFile(RootDirectory);
    }

    public IAbsoluteDirectoryPath RootDirectory { get; }

    public StoreSharingMode SharingMode { get; }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public ValueTask<UploadStoreUsage> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        var (directory, availableBytes) = ReadAvailableBytes();

        return new(new UploadStoreUsage {
            Location = RootDirectory.PathDisplay,
            AvailableBytes = availableBytes,
            TotalBytes = directory.TotalSize,
        });
    }

    /// <summary>
    /// Gets the space available to the store for the metrics, or <see langword="null"/> if it can't be read. After a read that failed, the space isn't
    /// read again for a few minutes.
    /// </summary>
    internal long? TryGetAvailableBytes()
    {
        long failureTicks = Interlocked.Read(ref _availableBytesFailureTicks);

        if (failureTicks is not 0 && _timeProvider.GetUtcNow().UtcTicks - failureTicks < AvailableBytesRetryDelay.Ticks)
            return null;

        try
        {
            return ReadAvailableBytes().Bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Interlocked.Exchange(ref _availableBytesFailureTicks, _timeProvider.GetUtcNow().UtcTicks);
            return null;
        }
    }

    /// <summary>
    /// Reads the space available to the store in its directory, or, while the directory doesn't exist, in the nearest directory that does, whose volume it
    /// will be on: a shared directory exists once the first upload is created in it.
    /// </summary>
    /// <returns>The directory whose space was read, and the space.</returns>
    private (IAbsoluteDirectoryPath Directory, long Bytes) ReadAvailableBytes()
    {
        try
        {
            return (RootDirectory, RootDirectory.AvailableFreeSpace);
        }
        catch (DirectoryNotFoundException)
        {
            var directory = RootDirectory.GetLastExistingDirectory();
            return (directory, directory.AvailableFreeSpace);
        }
    }

    public void Dispose()
    {
        // The token source is only cancelled, not disposed, so that the removals still running can read their token.
        _disposed.Cancel();
        _lockFile?.Dispose();
    }

    /// <summary>
    /// Opens the lock file of a directory that only this process uses, without sharing, so that no other process can use the directory while this one does.
    /// It is removed when it is closed. The open does not wait: another process that holds the lock, such as the previous process of an overlapped restart,
    /// may hold it for minutes. Windows enforces the lock, also on an SMB share. .NET on Linux and macOS emulates it with an advisory lock
    /// (<c>flock</c>), which holds on a local disk and is not guaranteed on an NFS mount.
    /// </summary>
    /// <exception cref="IOException">Another process holds the lock, or the lock file can't be opened.</exception>
    private static FileStream OpenLockFile(IAbsoluteDirectoryPath rootDirectory)
    {
        var file = rootDirectory.CombineFile(LockFileName, PathOptions.None);

        try
        {
            rootDirectory.Create();
            return file.OpenStream(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException ex)
        {
            throw new IOException(
                $"The lock file of the upload directory ({file.PathDisplay}) can't be opened, so the directory may be in use by another process, such as the " +
                $"previous process of an overlapped restart. {nameof(DirectoryUploadStoreOptions)}.{nameof(DirectoryUploadStoreOptions.SharingMode)} is " +
                $"{nameof(StoreSharingMode.ProcessLocal)}, so only one process can use the directory at a time.",
                ex);
        }
    }

    public async ValueTask<UploadCreationResult> TryCreateAsync(UploadRecord upload, CancellationToken cancellationToken = default)
    {
        var userDirectory = GetUserDirectory(upload.UserId);
        var directory = userDirectory.CombineDirectory(upload.Id, PathOptions.None);

        // A finished upload has its outcome among the user's outcomes, and its directory only while it leaves it. This only saves building the upload for
        // nothing: the move below is what tells, since another creation can get there in between.
        if (directory.Exists || HasOutcome(userDirectory, upload.Id))
            return UploadCreationResult.Exists;

        // The upload is made in a directory of its own and then moved to its name, which only one creation can do, so that it is complete once it exists. The
        // move fails if the name exists: Windows refuses it, and on Unix rename(2) only replaces an empty directory, which the new one never is. The one gap
        // is on Unix, a removal of an earlier upload with the ID that has just emptied its directory.
        var newDirectory = userDirectory.CombineDirectory($"{upload.Id}.{CreateId()}{NewDirectoryExtension}", PathOptions.None);

        try
        {
            var dataFile = newDirectory.CombineFile(DataFileName, PathOptions.None);

            try
            {
                // The space is reserved up front, so that a full disk fails the creation rather than a chunk, on file systems that can reserve it.
                var dataOptions = new FileStreamOptions {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.ReadWrite | FileShare.Delete,
                    PreallocationSize = upload.Length,
                };

                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        newDirectory.Create();

                        using var data = new FileStream(dataFile.PathExport, dataOptions);
                        data.SetLength(upload.Length);
                        break;
                    }
                    catch (DirectoryNotFoundException) when (attempt < Attempts)
                    {
                        // A cleanup removed the user's directory, which had been empty for a while, before the upload's directory was created in it.
                    }
                }
            }
            catch (IOException ex) when (IsOutOfSpace(ex, upload.Length, isReservation: true))
            {
                _logger.LogWarning(ex, "An upload of {Length} bytes could not be created in {Directory}.", upload.Length, RootDirectory.PathDisplay);
                TryDeleteDirectory(newDirectory);
                return UploadCreationResult.NoRoom;
            }

            // The file's last write is the upload's creation until a chunk arrives.
            await RetryAsync(() => File.SetLastWriteTimeUtc(dataFile.PathExport, upload.CreatedUtc), cancellationToken);
            newDirectory.CombineDirectory(ChunksDirectoryName, PathOptions.None).Create();

            await WriteFileAsync(
                newDirectory.CombineFile(RecordFileName, PathOptions.None), ToRecordFile(upload), DirectoryUploadStoreJsonContext.Default.RecordFile,
                cancellationToken);

            try
            {
                Directory.Move(newDirectory.PathExport, directory.PathExport);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && directory.Exists)
            {
                // Another creation of the upload moved its directory first, or the directory of an earlier upload with the ID is being removed.
                TryDeleteDirectory(newDirectory);
                return UploadCreationResult.Exists;
            }
        }
        catch
        {
            TryDeleteDirectory(newDirectory);
            throw;
        }

        // An earlier upload with the ID that finished in the meantime copied its outcome to the user's outcomes before it removed its directory, which had to
        // be gone for the move to succeed, so its outcome is there now.
        if (HasOutcome(userDirectory, upload.Id))
        {
            TryDeleteFile(directory.CombineFile(RecordFileName, PathOptions.None));
            await RemoveDirectoryAsync(directory);
            return UploadCreationResult.Exists;
        }

        return UploadCreationResult.Created;
    }

    public async ValueTask<StoredUpload?> GetAsync(string userId, string uploadId, CancellationToken cancellationToken = default)
    {
        if (!UploadIds.IsWellFormed(uploadId))
            return null;

        // An upload that is no longer open has no directory, or one that is being removed, and has finished if it has an outcome.
        return await ReadAsync(GetUploadDirectory(userId, uploadId), userId, uploadId, withoutOutcome: false, cancellationToken) ??
            await ReadOutcomeAsync(GetUserDirectory(userId), userId, uploadId, cancellationToken);
    }

    public async IAsyncEnumerable<StoredUpload> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The directories are listed up front, so that uploads that are removed while they are listed don't disturb the listing.
        foreach (var userDirectory in GetDirectories(RootDirectory))
        {
            string userKey = userDirectory.Name;

            foreach (var uploadDirectory in GetDirectories(userDirectory))
            {
                string uploadId = uploadDirectory.Name;

                if (!UploadIds.IsWellFormed(uploadId))
                    continue;

                var upload = await ReadAsync(uploadDirectory, userId: null, uploadId, withoutOutcome: true, cancellationToken);

                if (upload is not null && ShortLivedId.Hash(upload.Record.UserId) == userKey)
                    yield return upload;
            }
        }
    }

    public ValueTask<UploadContent> GetContentAsync(UploadRecord upload, CancellationToken cancellationToken = default)
    {
        var dataFile = GetUploadDirectory(upload.UserId, upload.Id).CombineFile(DataFileName, PathOptions.None);
        return new(new FileContent(dataFile));
    }

    public async ValueTask<bool> DeleteAsync(UploadRecord upload, CancellationToken cancellationToken = default)
    {
        var directory = GetUploadDirectory(upload.UserId, upload.Id);
        var recordFile = directory.CombineFile(RecordFileName, PathOptions.None);
        bool isRemoving = true;
        bool isDeleted = false;

        try
        {
            // Of the deletions at the same time, the one that creates the marker deletes the upload if it is open: it had its record, and has no outcome in
            // its directory. The others remove it all the same. The record is checked before the marker is created: the others remove the record only once
            // they have failed to create the marker, so they can't remove it before the one that created the marker checks it.
            bool hadRecord = Exists(recordFile);
            bool isMarked = await TryCreateEmptyFileAsync(directory.CombineFile(DeletedFileName, PathOptions.None));
            isDeleted = hadRecord && isMarked && !Exists(directory.CombineFile(FinishedFileName, PathOptions.None));
        }
        catch (DirectoryNotFoundException)
        {
            // The upload has finished, or another deletion removed it.
            isRemoving = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && !Exists(recordFile))
        {
            // Another deletion of the upload at the same time, such as the end of its run after it was aborted, removed the record and is removing the
            // directory, or the upload has finished and its directory is being removed.
            isRemoving = false;
        }

        if (isRemoving)
        {
            // The record goes first, so that the upload stays gone even if the marker is removed before files that cannot be removed yet.
            TryDeleteFile(recordFile);
            await RemoveDirectoryAsync(directory);
        }

        await RemoveOutcomeFilesAsync(GetUserDirectory(upload.UserId), upload.Id, cancellationToken);
        return isDeleted;
    }

    private static string CreateId() => Guid.NewGuid().ToString("N");

    private static RecordFile ToRecordFile(UploadRecord upload) => new() {
        Id = upload.Id,
        UserId = upload.UserId,
        Length = upload.Length,
        ChunkSize = upload.ChunkSize,
        CreatedUtc = upload.CreatedUtc,
        Metadata = upload.Metadata,
    };

    private static UploadRecord ToRecord(RecordFile record)
    {
        return new UploadRecord(record.Id, record.UserId, record.Length, record.ChunkSize, record.CreatedUtc, record.Metadata);
    }

    /// <summary>
    /// Gets a value indicating whether an upload whose directory exists has an outcome, or has been deleted.
    /// </summary>
    private static bool HasOutcomeOrIsDeleted(IAbsoluteDirectoryPath directory)
    {
        return Exists(directory.CombineFile(FinishedFileName, PathOptions.None)) || Exists(directory.CombineFile(DeletedFileName, PathOptions.None));
    }

    /// <summary>
    /// Gets a value indicating whether an upload has been deleted or is being deleted: a deletion creates the <c>deleted</c> marker first and then removes
    /// the record before the other files.
    /// </summary>
    private static bool IsBeingDeleted(IAbsoluteDirectoryPath directory)
    {
        return Exists(directory.CombineFile(DeletedFileName, PathOptions.None)) || !Exists(directory.CombineFile(RecordFileName, PathOptions.None));
    }

    private IAbsoluteDirectoryPath GetUserDirectory(string userId) => RootDirectory.CombineDirectory(ShortLivedId.Hash(userId), PathOptions.None);

    private IAbsoluteDirectoryPath GetUploadDirectory(string userId, string uploadId) => GetUserDirectory(userId).CombineDirectory(uploadId, PathOptions.None);

    /// <summary>
    /// Reads an upload with its current state. Files can be added and removed while it is read, such as when its claim is released or its outcome is
    /// stored, so a read that finds a file gone that it listed starts again a moment later.
    /// </summary>
    private async ValueTask<StoredUpload?> ReadAsync(
        IAbsoluteDirectoryPath directory, string? userId, string uploadId, bool withoutOutcome, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            StoredUpload? upload;
            bool isConsistent;

            try
            {
                (upload, isConsistent) = await TryReadAsync(directory, userId, uploadId, withoutOutcome, isLastAttempt: attempt == Attempts, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && IsBeingDeleted(directory))
            {
                // On Windows, a file or directory that is being removed cannot be opened for a moment, such as while a deletion of the upload at the same
                // time removes its files.
                return null;
            }

            if (isConsistent || attempt == Attempts)
                return upload;

            await Task.Delay(ReadAgainDelay, cancellationToken);
        }
    }

    /// <summary>
    /// Reads an upload with its current state once.
    /// </summary>
    /// <returns>The upload, or <see langword="null"/> if it does not exist or has an outcome and <paramref name="withoutOutcome"/> is
    /// <see langword="true"/>, and whether its files stayed as they were listed while they were read. On the last attempt, a file that is gone or still
    /// being written is taken as it is: a run without a claim, no outcome yet, and a file whose last write is the upload's creation.</returns>
    private async ValueTask<(StoredUpload? Upload, bool IsConsistent)> TryReadAsync(
        IAbsoluteDirectoryPath directory, string? userId, string uploadId, bool withoutOutcome, bool isLastAttempt, CancellationToken cancellationToken)
    {
        HashSet<string> names;

        try
        {
            names = directory.GetChildEntries().Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        }
        catch (DirectoryNotFoundException)
        {
            return (null, true);
        }

        // An upload that 4.x finished is not kept. Its directory is removed at the first cleanup after the upgrade.
        // TODO 6.0: remove the check of its outcome.json.
        if (names.Contains(DeletedFileName) || !names.Contains(RecordFileName) || names.Contains(LegacyOutcomeFileName) ||
            (withoutOutcome && names.Contains(FinishedFileName)))
        {
            return (null, true);
        }

        RecordFile? recordFile;

        try
        {
            recordFile = await ReadFileAsync(
                directory.CombineFile(RecordFileName, PathOptions.None), DirectoryUploadStoreJsonContext.Default.RecordFile, cancellationToken);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "The record of the upload in {Directory} cannot be read.", directory.PathDisplay);
            return (null, true);
        }

        // The upload was deleted while it was read.
        if (recordFile is null)
            return (null, false);

        // File names are not case-sensitive on some file systems, so the record has the exact ID and user.
        if (recordFile.Id != uploadId || (userId is not null && recordFile.UserId != userId))
            return (null, true);

        var record = ToRecord(recordFile);

        // An upload that is being finished, or whose server stopped while it finished it, has its outcome in its directory.
        if (names.Contains(FinishedFileName))
        {
            var finishedFile = directory.CombineFile(FinishedFileName, PathOptions.None);
            var outcome = await ReadCreatedFileAsync(finishedFile, DirectoryUploadStoreJsonContext.Default.OutcomeFile, cancellationToken);

            switch (outcome.State)
            {
                case CreatedFileState.Complete:
                    return (new StoredUpload(record, record.CreatedUtc, outcome: outcome.Value!.Outcome, outcomeUtc: outcome.Value.StoredUtc), true);
                case CreatedFileState.Broken:
                    // The service reports an outcome that it cannot read, which the cleanup keeps as such in the user's outcomes.
                    _logger.LogWarning("The outcome of the upload in {Directory} cannot be read.", directory.PathDisplay);
                    return (new StoredUpload(record, record.CreatedUtc, outcome: string.Empty, outcomeUtc: outcome.WrittenUtc), true);
                case CreatedFileState.Missing:
                    return (null, false);
                case CreatedFileState.BeingWritten when !isLastAttempt:
                    return (null, false);
            }

            // The outcome is still being written, and the upload is taken as it was before.
        }

        CreatedFile<ClaimFile> claim = default;

        if (names.Contains(ClaimFileName))
        {
            var claimFile = directory.CombineFile(ClaimFileName, PathOptions.None);
            claim = await ReadCreatedFileAsync(claimFile, DirectoryUploadStoreJsonContext.Default.ClaimFile, cancellationToken);

            // The claim was released, or an outcome was stored, while the upload was read.
            if (claim.State is CreatedFileState.Missing && !isLastAttempt)
                return (null, false);
        }

        // The file's last write time is the time of the last chunk. The file is removed once an outcome is stored, and a handler can move it while its run
        // is claimed.
        var data = new FileInfo(directory.CombineFile(DataFileName, PathOptions.None).PathExport);

        if (!data.Exists && claim.State is CreatedFileState.Missing && !isLastAttempt)
            return (null, false);

        var lastWriteUtc = data.Exists ? data.LastWriteTimeUtc : record.CreatedUtc;

        switch (claim.State)
        {
            case CreatedFileState.Complete:
                // An outcome that was stored while the claim was read ends the run.
                if (Exists(directory.CombineFile(FinishedFileName, PathOptions.None)) && !isLastAttempt)
                    return (null, false);

                var state = claim.Value!.RenewedUtc + claim.Value.LeaseDuration < UtcNow ? UploadRunState.Lapsed : UploadRunState.Running;
                return (new StoredUpload(record, lastWriteUtc, state, claim.Value.StartedUtc), true);
            case CreatedFileState.BeingWritten:
                return (new StoredUpload(record, lastWriteUtc, UploadRunState.Running, claim.WrittenUtc), true);
            case CreatedFileState.Broken:
                // A claim that cannot be read is taken as lapsed, which ends the upload as interrupted.
                _logger.LogWarning("The claim of the run of the upload in {Directory} cannot be read.", directory.PathDisplay);
                return (new StoredUpload(record, lastWriteUtc, UploadRunState.Lapsed, claim.WrittenUtc), true);
        }

        return (new StoredUpload(record, lastWriteUtc), true);
    }

    /// <summary>
    /// The complete file of an upload, which is its <c>data</c> file until it is moved.
    /// </summary>
    private sealed class FileContent(IAbsoluteFilePath file) : UploadContent
    {
        private IAbsoluteFilePath _file = file;

        public override IAbsoluteFilePath? File => _file;

        public override Stream OpenReadStream()
        {
            // Deleting the upload, such as when it is aborted, removes the file while it is open.
            return _file.OpenAsyncStream(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }

        public override async Task MoveToAsync(IAbsoluteFilePath destination, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A file on the same file system is renamed, and one on another is copied, which can be cancelled, where a move would copy it synchronously.
            if (IsOnSameFileSystem(destination, _file))
            {
                _file.MoveTo(destination, overwrite: false);
            }
            else
            {
                await base.MoveToAsync(destination, cancellationToken);
                TryDeleteFile(_file);
            }

            _file = destination;
        }
    }
}
