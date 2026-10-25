using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service.Uploads;

#pragma warning disable SA1402 // File may only contain a single type

/// <summary>
/// The default <see cref="IUploadStore"/>, which keeps uploads in a directory (<see cref="ChunkedUploadOptions.Directory"/>) on a local disk or a share
/// that several servers mount.
/// </summary>
/// <remarks>
/// <para>
/// Each upload has a directory, <c>{Directory}/{userKey}/{uploadId}</c>, where <c>userKey</c> is a hash of the user ID. It holds <c>upload.json</c>, the
/// upload's record, and <c>data</c>, the file, which is created with the upload's length and, where the file system can, space reserved for it. Each chunk
/// is written at its offset through to the disk, and only then is an empty marker, <c>chunks/{index}</c>, created for it, so a chunk whose write was
/// interrupted is missing. A write of a chunk that was received removes its marker first.
/// </para>
/// <para>
/// A chunk that is being written has a write marker, <c>chunks/{index}.{writeId}.writing</c>, which holds the time the write last showed that it is
/// alive. A write creates its marker and then checks for a claim, an outcome and a deletion, and a claim is created and then checks for write markers
/// that are alive, so of a write and a claim made at the same time, at least one sees the other and gives way. A marker that cannot be opened is taken
/// as alive. A write that finds a claim waits up to 2 seconds for it to back off, so a write that meets a claim that won takes 2 seconds to be closed. A
/// claim that backs off and can't be removed is kept renewed and removed again in the background, for up to 10 minutes, and looks like a running run
/// meanwhile, so a write that meets it is closed.
/// </para>
/// <para>
/// The files that only one request may create, the <c>claim</c> of a run, the <c>outcome.json</c> of an upload and the reservation of an idempotency key,
/// a file in <c>{Directory}/{userKey}/keys</c> named by a hash of the key, are created in one step that fails if the file exists
/// (<see cref="FileMode.CreateNew"/>), which the file system, also that of a file server, decides for all of its clients, and their content is then
/// written. A reader can find such a file empty or partly written for a moment: it takes a file that it cannot read as being written for a minute after
/// the file's last write time, by the file system's clock, and as broken after that. A server that stalls for longer than that while it creates such a
/// file can find it taken over, and two servers then have the claim or the reservation. The other files, such as the record, the write markers and the
/// claims that are renewed, are written under a temporary name and moved to their name, replacing a file of that name, so they are complete whenever they
/// can be read. A release of a reservation creates a marker of its own, <c>{key}.{reservationId}.released</c>, which only one release can do, and then
/// removes the reservation. A deleted upload gets a <c>deleted</c> marker before its files are removed, so it is gone even if some of them cannot be
/// removed yet.
/// </para>
/// <para>
/// The checks for files that another server may have created open them rather than ask whether they exist, which clients of file shares can answer from
/// caches of their own. The times that tell whether a claim or a write is alive are in the content of their files rather than in the file system's times.
/// </para>
/// </remarks>
internal sealed class DirectoryUploadStore : IUploadStore, IDisposable
{
    private const string RecordFileName = "upload.json";
    private const string DataFileName = "data";
    private const string ChunksDirectoryName = "chunks";
    private const string ClaimFileName = "claim";
    private const string OutcomeFileName = "outcome.json";
    private const string DeletedFileName = "deleted";
    private const string KeysDirectoryName = "keys";
    private const string TemporaryFileExtension = ".tmp";
    private const string WritingFileExtension = ".writing";
    private const string ReleasedFileExtension = ".released";

    // The size of the writes of a chunk to the file.
    private const int WriteBlockSize = 1 << 20;

    // How many times an operation is tried that fails because another process has the file open for a moment, such as a virus scanner.
    private const int Attempts = 10;

    // The Win32 errors of a disk that has no room for a file (ERROR_HANDLE_DISK_FULL, ERROR_DISK_FULL), and of a file that is larger than the file system
    // allows (ERROR_FILE_TOO_LARGE, and ERROR_INVALID_PARAMETER for space reserved for a file), which are the lower 16 bits of an IOException's HResult on
    // Windows.
    private const int ErrorHandleDiskFull = 0x27;
    private const int ErrorInvalidParameter = 0x57;
    private const int ErrorDiskFull = 0x70;
    private const int ErrorFileTooLarge = 0xDF;

    // How old the files of a creation that failed, temporary files, released reservations and the markers of writes that stopped must be before they are
    // removed, which leaves time for the creation, the write or the release to finish.
    private static readonly TimeSpan StrayDataAge = TimeSpan.FromHours(1);

    // How long a file that is created with its content, and that cannot be read yet, is taken as being written.
    private static readonly TimeSpan IncompleteFileAge = TimeSpan.FromMinutes(1);

    // How often a write of a chunk shows that it is alive, and how long after it last did a claim takes it for stopped.
    private static readonly TimeSpan WriteHeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WriteLease = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    // How much later than an upload's creation the time of its chunks is at the least, which is more than the resolution of a file system's times.
    private static readonly TimeSpan MinChunkTimeAfterCreation = TimeSpan.FromSeconds(2);

    // How long a read of an upload whose files changed while it was read waits before it reads them again.
    private static readonly TimeSpan ReadAgainDelay = TimeSpan.FromMilliseconds(20);

    // How long a release of a key's reservation waits for another release of it to remove it.
    private static readonly TimeSpan ReleaseWait = TimeSpan.FromSeconds(2);

    // How long a write of a chunk waits for a claim made at the same time to back off.
    private static readonly TimeSpan ClaimBackOffWait = TimeSpan.FromSeconds(2);

    // How often a claim that backed off and could not be removed is removed again, and for how long.
    private static readonly TimeSpan ClaimRemovalInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxClaimRemovalTime = TimeSpan.FromMinutes(10);

    // How often a release of a key's reservation that could not remove it removes it again, and for how long.
    private static readonly TimeSpan ReservationRemovalInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxReservationRemovalTime = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    // Stops the removals in the background once the store is disposed.
    private readonly CancellationTokenSource _disposed = new();

    public DirectoryUploadStore(IOptions<ChunkedUploadOptions> options, IHostEnvironment environment, TimeProvider timeProvider, ILoggerFactory loggerFactory)
    {
        string directory = options.Value.Directory ?? throw new InvalidOperationException(
            $"Chunked uploads require {nameof(ChunkedUploadOptions)}.{nameof(ChunkedUploadOptions.Directory)} to be set, unless the app registers an " +
            $"{nameof(IUploadStore)} of its own. Use a location that survives restarts and redeployments, for example \"../data/uploads\".");

        RootDirectory = Path.GetFullPath(directory, environment.ContentRootPath);
        _timeProvider = timeProvider;
        _logger = loggerFactory.CreateLogger(typeof(DirectoryUploadStore).FullName!);
    }

    /// <summary>
    /// Specifies the state of a file that is created with its content (see <see cref="TryCreateFileAsync"/>).
    /// </summary>
    private enum CreatedFileState
    {
        Missing,
        Complete,
        BeingWritten,
        Broken,
    }

    public string RootDirectory { get; }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    // The token source is only cancelled, not disposed, so that the removals still running can read their token.
    public void Dispose() => _disposed.Cancel();

    /// <summary>
    /// Gets the time of a chunk of an upload: now, and always later than the upload's creation, so that an upload that has received a chunk can be told
    /// apart from one that has not (see <see cref="StoredUpload.LastWriteUtc"/>), at least 2 seconds later, so that it is also later on file systems whose
    /// times have whole seconds or two.
    /// </summary>
    private DateTime GetChunkTimeUtc(UploadRecord upload)
    {
        var nowUtc = UtcNow;
        var earliestUtc = upload.CreatedUtc + MinChunkTimeAfterCreation;

        return nowUtc > earliestUtc ? nowUtc : earliestUtc;
    }

    public async ValueTask<UploadKeyReservation> ReserveKeyAsync(string userId, string key, string uploadId, CancellationToken cancellationToken = default)
    {
        string path = GetKeyPath(userId, key);

        for (int attempt = 1; ; attempt++)
        {
            var reservation = new KeyFile { ReservationId = CreateId(), UploadId = uploadId, ReservedUtc = UtcNow };

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                // A creation that stalled for long enough before its content arrived, such as on a share whose server failed over, can find its file
                // taken for broken and released, and another reservation in its place.
                if (await TryCreateFileAsync(path, reservation, DirectoryUploadStoreJsonContext.Default.KeyFile, cancellationToken) &&
                    (await ReadCreatedFileAsync(path, DirectoryUploadStoreJsonContext.Default.KeyFile, cancellationToken)).Value?.ReservationId ==
                    reservation.ReservationId)
                {
                    return new UploadKeyReservation(reservation.UploadId, reservation.ReservedUtc);
                }
            }
            catch (DirectoryNotFoundException) when (attempt < Attempts)
            {
                // The cleanup removed the directory of the keys while it was empty.
                continue;
            }

            var existing = await ReadCreatedFileAsync(path, DirectoryUploadStoreJsonContext.Default.KeyFile, cancellationToken);

            switch (existing.State)
            {
                case CreatedFileState.Complete:
                    return new UploadKeyReservation(existing.Value!.UploadId, existing.Value.ReservedUtc);
                case CreatedFileState.Broken:
                    _logger.LogWarning("The reservation of an upload key in {Path} cannot be read, so it is released.", path);
                    await ReleaseReservationAsync(path, GetBrokenReservationId(existing.WrittenUtc), cancellationToken);
                    break;
                case CreatedFileState.BeingWritten when attempt >= Attempts:
                    throw new IOException($"The reservation of an upload key in {path} is still being written.");
                case CreatedFileState.BeingWritten:
                    await Task.Delay(RetryDelay, cancellationToken);
                    break;
            }

            // A reservation that was released in the meantime is made again.
        }
    }

    public async ValueTask ReleaseKeyAsync(string userId, string key, string uploadId, CancellationToken cancellationToken = default)
    {
        string path = GetKeyPath(userId, key);
        var reservation = await ReadCreatedFileAsync(path, DirectoryUploadStoreJsonContext.Default.KeyFile, cancellationToken);

        if (reservation.Value?.UploadId == uploadId)
            await ReleaseReservationAsync(path, reservation.Value.ReservationId, cancellationToken);
    }

    public async ValueTask<bool> TryCreateAsync(UploadRecord upload, CancellationToken cancellationToken = default)
    {
        string directory = GetUploadDirectory(upload.UserId, upload.Id);

        try
        {
            string dataPath = Path.Combine(directory, DataFileName);

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
                        Directory.CreateDirectory(directory);

                        using var data = new FileStream(dataPath, dataOptions);
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
                _logger.LogWarning(ex, "An upload of {Length} bytes could not be created in {Directory}.", upload.Length, RootDirectory);
                TryDeleteDirectory(directory);
                return false;
            }

            // The file's last write is the upload's creation until a chunk arrives.
            await RetryAsync(() => File.SetLastWriteTimeUtc(dataPath, upload.CreatedUtc), cancellationToken);
            Directory.CreateDirectory(Path.Combine(directory, ChunksDirectoryName));

            var record = new RecordFile {
                Id = upload.Id,
                UserId = upload.UserId,
                Length = upload.Length,
                ChunkSize = upload.ChunkSize,
                CreatedUtc = upload.CreatedUtc,
                Metadata = upload.Metadata,
                IdempotencyKey = upload.IdempotencyKey,
            };

            // The record is written last: an upload without one does not exist yet.
            await WriteFileAsync(Path.Combine(directory, RecordFileName), record, DirectoryUploadStoreJsonContext.Default.RecordFile, cancellationToken);
            return true;
        }
        catch
        {
            TryDeleteDirectory(directory);
            throw;
        }
    }

    public async ValueTask<StoredUpload?> GetAsync(string userId, string uploadId, CancellationToken cancellationToken = default)
    {
        if (!ShortLivedId.IsWellFormed(uploadId))
            return null;

        return await ReadAsync(GetUploadDirectory(userId, uploadId), userId, uploadId, withoutOutcome: false, cancellationToken);
    }

    public async IAsyncEnumerable<StoredUpload> ListAsync(
        string? userId = null, bool withoutOutcomes = false, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The directories are listed up front, so that uploads that are removed while they are listed don't disturb the listing.
        string[] userDirectories = userId is null ? GetDirectories(RootDirectory) : [GetUserDirectory(userId)];

        foreach (string userDirectory in userDirectories)
        {
            string userKey = Path.GetFileName(userDirectory);

            foreach (string uploadDirectory in GetDirectories(userDirectory))
            {
                string uploadId = Path.GetFileName(uploadDirectory);

                if (!ShortLivedId.IsWellFormed(uploadId))
                    continue;

                var upload = await ReadAsync(uploadDirectory, userId, uploadId, withoutOutcomes, cancellationToken);

                if (upload is not null && (userId is not null || ShortLivedId.Hash(upload.Record.UserId) == userKey))
                    yield return upload;
            }
        }
    }

    public async ValueTask<ChunkWriteResult> WriteChunkAsync(UploadRecord upload, int index, Stream content, CancellationToken cancellationToken = default)
    {
        string directory = GetUploadDirectory(upload.UserId, upload.Id);
        string dataPath = Path.Combine(directory, DataFileName);
        string markerPath = Path.Combine(directory, ChunksDirectoryName, index.ToString(CultureInfo.InvariantCulture));
        string writingPath = $"{markerPath}.{CreateId()}{WritingFileExtension}";

        try
        {
            await WriteFileAsync(writingPath, new WritingFile { AliveUtc = UtcNow }, DirectoryUploadStoreJsonContext.Default.WritingFile, cancellationToken);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException || (ex is IOException or UnauthorizedAccessException && IsBeingDeleted(directory)))
        {
            // An outcome removes the directory of the chunks. On Windows, a directory that is being removed cannot be written to for a moment.
            return GetEndedWriteResult(directory);
        }

        using var heartbeatStop = new CancellationTokenSource();
        Task? heartbeat = null;
        bool isWritingData = false;

        try
        {
            // A claim, an outcome or a deletion made before the write marker existed wins. A claim made after it sees the marker.
            if (!Exists(Path.Combine(directory, RecordFileName)) || Exists(Path.Combine(directory, DeletedFileName)))
                return ChunkWriteResult.NotFound;

            if (Exists(Path.Combine(directory, OutcomeFileName)) || !await WaitWhileClaimedAsync(directory, cancellationToken))
                return ChunkWriteResult.Closed;

            // A chunk that was received is missing until it has been written again, so that a write that fails can't leave it reported as received.
            await DeleteFileAsync(markerPath, cancellationToken);

            // The upload is kept for as long as after its last chunk from now on, also while a slow chunk arrives.
            await RetryAsync(() => File.SetLastWriteTimeUtc(dataPath, GetChunkTimeUtc(upload)), cancellationToken);

            heartbeat = KeepWriteAliveAsync(writingPath, heartbeatStop.Token);
            isWritingData = true;

            if (!await WriteDataAsync(dataPath, upload.GetChunkOffset(index), upload.GetChunkLength(index), content, cancellationToken))
                return ChunkWriteResult.NoRoom;

            isWritingData = false;

            await RetryAsync(() => File.SetLastWriteTimeUtc(dataPath, GetChunkTimeUtc(upload)), cancellationToken);
            await RetryAsync(() => File.WriteAllBytes(markerPath, []), cancellationToken);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException ||
            (ex is IOException or UnauthorizedAccessException && IsBeingDeleted(directory)))
        {
            return GetEndedWriteResult(directory);
        }
        catch (IOException ex) when (!isWritingData && IsOutOfSpace(ex, upload.GetChunkLength(index), isReservation: false))
        {
            // The marker of the chunk could not be created on a full disk. The content's own failures are not the disk's.
            _logger.LogWarning(ex, "A chunk of {Length} bytes could not be written in {Directory}.", upload.GetChunkLength(index), RootDirectory);
            return ChunkWriteResult.NoRoom;
        }
        finally
        {
            // The heartbeat stops before the marker is removed, so that it can't put the marker back.
            heartbeatStop.Cancel();

            if (heartbeat is not null)
                await heartbeat;

            TryDeleteFile(writingPath);
        }

        return ChunkWriteResult.Written;
    }

    public ValueTask<IReadOnlyCollection<int>> GetReceivedChunksAsync(UploadRecord upload, CancellationToken cancellationToken = default)
    {
        string chunksDirectory = Path.Combine(GetUploadDirectory(upload.UserId, upload.Id), ChunksDirectoryName);
        var received = new SortedSet<int>();

        try
        {
            // Write markers and temporary files have names that are not numbers.
            foreach (string path in Directory.EnumerateFiles(chunksDirectory))
            {
                if (int.TryParse(Path.GetFileName(path), NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index < upload.ChunkCount)
                    received.Add(index);
            }
        }
        catch (DirectoryNotFoundException)
        {
        }

        return new(received);
    }

    public async ValueTask<IUploadRunClaim?> TryClaimRunAsync(UploadRecord upload, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        string directory = GetUploadDirectory(upload.UserId, upload.Id);

        if (!Exists(Path.Combine(directory, RecordFileName)) || HasOutcomeOrIsDeleted(directory))
            return null;

        string claimPath = Path.Combine(directory, ClaimFileName);
        var nowUtc = UtcNow;
        var claim = new ClaimFile { ClaimId = CreateId(), LeaseDuration = leaseDuration, StartedUtc = nowUtc, RenewedUtc = nowUtc };

        try
        {
            if (!await TryCreateFileAsync(claimPath, claim, DirectoryUploadStoreJsonContext.Default.ClaimFile, cancellationToken))
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
            var current = await ReadCreatedFileAsync(claimPath, DirectoryUploadStoreJsonContext.Default.ClaimFile, cancellationToken);

            if (current.Value?.ClaimId != claim.ClaimId)
                return null;

            // An outcome or a deletion that came in the meantime wins, and so does a write of a chunk that is alive.
            if (!HasOutcomeOrIsDeleted(directory) && !await HasLiveWritesAsync(directory, cancellationToken))
                return created;
        }
        catch
        {
            await RemoveClaimAsync(created, claimPath, leaseDuration);
            throw;
        }

        await RemoveClaimAsync(created, claimPath, leaseDuration);
        return null;
    }

    /// <summary>
    /// Waits for a moment while the upload's run is claimed, for a claim that was made at the same time as a write of a chunk: a claim that sees the write's
    /// marker backs off, and is removed within a moment.
    /// </summary>
    /// <returns><see langword="true"/> if the run is not claimed, or <see langword="false"/> if it still is, or the upload has an outcome.</returns>
    private static async Task<bool> WaitWhileClaimedAsync(string directory, CancellationToken cancellationToken)
    {
        string claimPath = Path.Combine(directory, ClaimFileName);
        long started = Stopwatch.GetTimestamp();

        while (Exists(claimPath))
        {
            if (Stopwatch.GetElapsedTime(started) > ClaimBackOffWait)
                return false;

            await Task.Delay(ReadAgainDelay, cancellationToken);
        }

        return !Exists(Path.Combine(directory, OutcomeFileName));
    }

    /// <summary>
    /// Removes a claim that backs off. A claim that is left would lapse, which ends the upload as interrupted, so one that can't be removed, such as while
    /// another process has it open, is kept renewed and removed again in the background, for up to 10 minutes.
    /// </summary>
    private async Task RemoveClaimAsync(Claim claim, string claimPath, TimeSpan leaseDuration)
    {
        try
        {
            await claim.RemoveAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The claim {Path} that backed off could not be removed, and is removed again in the background.", claimPath);
            _ = KeepRemovingClaimAsync(claim, claimPath, leaseDuration);
        }
    }

    private async Task KeepRemovingClaimAsync(Claim claim, string claimPath, TimeSpan leaseDuration)
    {
        long started = _timeProvider.GetTimestamp();
        long renewed = started;

        try
        {
            while (_timeProvider.GetElapsedTime(started) < MaxClaimRemovalTime)
            {
                await Task.Delay(ClaimRemovalInterval, _timeProvider, _disposed.Token);

                try
                {
                    await claim.RemoveAsync(CancellationToken.None);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Removed again at the next interval.
                }

                if (_timeProvider.GetElapsedTime(renewed) >= leaseDuration / 4)
                {
                    try
                    {
                        if (!await claim.RenewAsync(CancellationToken.None))
                            return;

                        renewed = _timeProvider.GetTimestamp();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }

            _logger.LogError("The claim {Path} that backed off could not be removed for 10 minutes, so it lapses.", claimPath);
        }
        catch (OperationCanceledException) when (_disposed.IsCancellationRequested)
        {
            // The store is disposed, and the claim lapses.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The claim {Path} that backed off could not be removed, so it lapses.", claimPath);
        }
    }

    public ValueTask<UploadContent> GetContentAsync(UploadRecord upload, CancellationToken cancellationToken = default)
    {
        string dataPath = Path.Combine(GetUploadDirectory(upload.UserId, upload.Id), DataFileName);
        return new(new FileContent(dataPath));
    }

    public async ValueTask<bool> TrySetOutcomeAsync(UploadRecord upload, string outcome, CancellationToken cancellationToken = default)
    {
        string directory = GetUploadDirectory(upload.UserId, upload.Id);

        if (!Exists(Path.Combine(directory, RecordFileName)) || Exists(Path.Combine(directory, DeletedFileName)))
            return false;

        var outcomeFile = new OutcomeFile { Outcome = outcome, StoredUtc = UtcNow };
        string outcomePath = Path.Combine(directory, OutcomeFileName);

        try
        {
            if (!await TryCreateFileAsync(outcomePath, outcomeFile, DirectoryUploadStoreJsonContext.Default.OutcomeFile, cancellationToken))
                return false;
        }
        catch (DirectoryNotFoundException)
        {
            // The upload was deleted in the meantime.
            return false;
        }

        TryDeleteFile(Path.Combine(directory, DataFileName));
        TryDeleteDirectory(Path.Combine(directory, ChunksDirectoryName));
        TryDeleteFile(Path.Combine(directory, ClaimFileName));

        return true;
    }

    public async ValueTask DeleteAsync(UploadRecord upload, CancellationToken cancellationToken = default)
    {
        string directory = GetUploadDirectory(upload.UserId, upload.Id);
        string recordPath = Path.Combine(directory, RecordFileName);

        try
        {
            await RetryAsync(() => File.WriteAllBytes(Path.Combine(directory, DeletedFileName), []), cancellationToken);
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && !Exists(recordPath))
        {
            // Another deletion of the upload at the same time, such as the end of its run after it was aborted, removed the record and is removing the
            // directory.
            return;
        }

        // The record goes first, so that the upload stays gone even if the marker is removed before files that cannot be removed yet.
        TryDeleteFile(recordPath);

        // Another deletion of the upload at the same time can make one attempt fail.
        for (int attempt = 1; ; attempt++)
        {
            TryDeleteDirectory(directory);

            if (!Directory.Exists(directory) || attempt is 3)
                break;

            await Task.Delay(50, CancellationToken.None);
        }

        if (upload.IdempotencyKey is { } key)
            await ReleaseKeyAsync(upload.UserId, key, upload.Id, cancellationToken);
    }

    public async ValueTask CleanUpAsync(CancellationToken cancellationToken = default)
    {
        // Reservations hold times of the time provider in their content, while the file system sets the times of files and directories by the system's
        // clock, which they are compared with, so that a time provider ahead of the system's clock can't make a file that is being written look stray.
        var strayBeforeUtc = UtcNow - StrayDataAge;
        var strayFilesBeforeUtc = TimeProvider.System.GetUtcNow().UtcDateTime - StrayDataAge;

        foreach (string userDirectory in GetDirectories(RootDirectory))
        {
            await CleanUpKeysAsync(userDirectory, strayBeforeUtc, strayFilesBeforeUtc, cancellationToken);

            foreach (string uploadDirectory in GetDirectories(userDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    bool isDeleted = Exists(Path.Combine(uploadDirectory, DeletedFileName));
                    bool hasRecord = Exists(Path.Combine(uploadDirectory, RecordFileName));

                    if (isDeleted || (!hasRecord && Directory.GetCreationTimeUtc(uploadDirectory) < strayFilesBeforeUtc))
                    {
                        TryDeleteDirectory(uploadDirectory);
                        continue;
                    }

                    RemoveStrayFiles(uploadDirectory, TemporaryFileExtension, strayFilesBeforeUtc);
                    RemoveStrayFiles(Path.Combine(uploadDirectory, ChunksDirectoryName), TemporaryFileExtension, strayFilesBeforeUtc);
                    RemoveStrayFiles(Path.Combine(uploadDirectory, ChunksDirectoryName), WritingFileExtension, strayFilesBeforeUtc);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "The leftovers of uploads in {Directory} could not be removed.", uploadDirectory);
                }
            }

            try
            {
                // A creation of an upload creates the user's directory before the upload's directory in it, so a directory that changed lately is kept.
                if (Directory.GetLastWriteTimeUtc(userDirectory) < strayFilesBeforeUtc && !Directory.EnumerateFileSystemEntries(userDirectory).Any())
                    Directory.Delete(userDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A new upload of the user was created in the meantime, or the directory is in use.
            }
        }
    }

    /// <summary>
    /// Gets the mount point of the file system that a full path is on: the longest of the root directories of the system's drives that the path is in, or
    /// the root of the path if it is in none of them.
    /// </summary>
    internal static string GetMountPoint(string fullPath, IEnumerable<string> driveRoots)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string? mountPoint = null;

        foreach (string root in driveRoots)
        {
            string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            bool isInRoot = root.Length > 0 &&
                (fullPath.StartsWith(prefix, comparison) || fullPath.Equals(Path.TrimEndingDirectorySeparator(root), comparison));

            if (isInRoot && (mountPoint is null || root.Length > mountPoint.Length))
                mountPoint = root;
        }

        return mountPoint ?? Path.GetPathRoot(fullPath) ?? string.Empty;
    }

    /// <summary>
    /// Gets a value indicating whether two full paths are on the same file system, by the mount points of the system's drives (see
    /// <see cref="GetMountPoint"/>).
    /// </summary>
    internal static bool IsOnSameFileSystem(string path, string otherPath)
    {
        string[] driveRoots;

        try
        {
            driveRoots = [.. DriveInfo.GetDrives().Select(d => d.RootDirectory.FullName)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            driveRoots = [];
        }

        return string.Equals(GetMountPoint(path, driveRoots), GetMountPoint(otherPath, driveRoots), StringComparison.Ordinal);
    }

    private static string[] GetDirectories(string path)
    {
        try
        {
            return Directory.GetDirectories(path);
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static string CreateId() => Guid.NewGuid().ToString("N");

    private static string GetBrokenReservationId(DateTime writtenUtc) => "broken-" + writtenUtc.Ticks.ToString(CultureInfo.InvariantCulture);

    private static bool HasOutcomeOrIsDeleted(string directory)
    {
        return Exists(Path.Combine(directory, OutcomeFileName)) || Exists(Path.Combine(directory, DeletedFileName));
    }

    /// <summary>
    /// Gets a value indicating whether an upload has been deleted or is being deleted: a deletion creates the <c>deleted</c> marker first and then removes
    /// the record before the other files.
    /// </summary>
    private static bool IsBeingDeleted(string directory)
    {
        return Exists(Path.Combine(directory, DeletedFileName)) || !Exists(Path.Combine(directory, RecordFileName));
    }

    /// <summary>
    /// Gets the result of a write of a chunk whose upload ended while it was written: closed if the upload has an outcome, which removes the chunks, and
    /// not found otherwise.
    /// </summary>
    private static ChunkWriteResult GetEndedWriteResult(string directory)
    {
        return Exists(Path.Combine(directory, OutcomeFileName)) ? ChunkWriteResult.Closed : ChunkWriteResult.NotFound;
    }

    /// <summary>
    /// Gets a value indicating whether a file exists by opening it, which asks the file system, also that of a file server, rather than a cache of a client
    /// of a share. A file that exists but cannot be opened, such as one that another process holds or that is being deleted, is taken as existing.
    /// </summary>
    private static bool Exists(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static FileStream OpenRead(string path)
    {
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
    }

    /// <summary>
    /// Reads a file that is written under a temporary name and moved to its name (see <see cref="WriteFileAsync"/>), again after a short wait while another
    /// process has it open for a moment, such as a virus scanner.
    /// </summary>
    /// <returns>The file's content, or <see langword="null"/> if it does not exist.</returns>
    private static async Task<T?> ReadFileAsync<T>(string path, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
        where T : class
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var stream = OpenRead(path);
                return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < Attempts)
            {
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Reads a file that is created with its content (see <see cref="TryCreateFileAsync"/>), which can be empty or partly written for a moment, again
    /// after a short wait while another process has it open for a moment, such as a virus scanner.
    /// </summary>
    private async Task<CreatedFile<T>> ReadCreatedFileAsync<T>(string path, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
        where T : class
    {
        var file = await ReadCreatedFileOnceAsync(path, typeInfo, cancellationToken);

        if (file.State is not CreatedFileState.Broken)
            return file;

        // A file that looks broken is read again a moment later, and is broken only if it is still the same, since the times of a file that is being
        // created can be passing ones for a moment, such as the verifier of an exclusive creation over NFS.
        await Task.Delay(RetryDelay, cancellationToken);
        var again = await ReadCreatedFileOnceAsync(path, typeInfo, cancellationToken);

        if (again.State is CreatedFileState.Broken && again.WrittenUtc != file.WrittenUtc)
            return again with { State = CreatedFileState.BeingWritten };

        return again;
    }

    private async Task<CreatedFile<T>> ReadCreatedFileOnceAsync<T>(string path, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
        where T : class
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var stream = OpenRead(path);

                try
                {
                    if (await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken) is { } value)
                        return new CreatedFile<T>(CreatedFileState.Complete, value, default);
                }
                catch (JsonException)
                {
                }

                // The file system sets the file's times by its own clock, which is the file server's on a share, so the file's age is measured by it.
                var writtenUtc = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
                bool isBeingWritten = writtenUtc + IncompleteFileAge > GetFileSystemUtcNow(Path.GetDirectoryName(path)!);

                return new CreatedFile<T>(isBeingWritten ? CreatedFileState.BeingWritten : CreatedFileState.Broken, null, writtenUtc);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return new CreatedFile<T>(CreatedFileState.Missing, null, default);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < Attempts)
            {
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Gets the current time of the file system of a directory: the last write time of a file that is created there, which the file server sets on a share,
    /// and which is removed again. A file that can't be created gives the system's time, which is logged.
    /// </summary>
    private DateTime GetFileSystemUtcNow(string directory)
    {
        try
        {
            string path = Path.Combine(directory, CreateId() + TemporaryFileExtension);
            using var handle = File.OpenHandle(path, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.DeleteOnClose);

            return File.GetLastWriteTimeUtc(handle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "The time of the file system in {Directory} could not be read, so the system's time is used.", directory);
            return TimeProvider.System.GetUtcNow().UtcDateTime;
        }
    }

    /// <summary>
    /// Creates a file that must not exist yet, with its content, in one step that fails if the file exists, which the file system decides for all of its
    /// clients, so that of several creations at the same time, also on several servers, one creates the file. Readers can find the file empty or partly
    /// written while it is written (see <see cref="ReadCreatedFileAsync"/>).
    /// </summary>
    /// <returns><see langword="true"/> if the file was created, or <see langword="false"/> if it exists.</returns>
    private static async Task<bool> TryCreateFileAsync<T>(string path, T value, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        FileStream stream;

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete, bufferSize: 0, useAsync: true);
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && ex is not DirectoryNotFoundException &&
                IsCreationRaced(path, attempt))
            {
                if (Exists(path))
                    return false;

                // The file existed and was removed in the meantime, or is being removed, and is created again in a moment.
                await Task.Delay(ReadAgainDelay, cancellationToken);
            }
        }

        try
        {
            await using (stream)
            {
                await JsonSerializer.SerializeAsync(stream, value, typeInfo, cancellationToken);

                // Without this, the file can reach the disk before its content does, and a power loss would leave it empty or partly written.
                stream.Flush(flushToDisk: true);
            }
        }
        catch
        {
            TryDeleteFile(path);
            throw;
        }

        return true;
    }

    /// <summary>
    /// Creates an empty file that must not exist yet, in one step that fails if the file exists (see <see cref="TryCreateFileAsync"/>).
    /// </summary>
    /// <returns><see langword="true"/> if the file was created, or <see langword="false"/> if it exists.</returns>
    private static async Task<bool> TryCreateEmptyFileAsync(string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete).Dispose();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && ex is not DirectoryNotFoundException &&
                IsCreationRaced(path, attempt))
            {
                if (Exists(path))
                    return false;

                await Task.Delay(ReadAgainDelay);
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether a creation of a file that failed is handled: because the file exists, or, before the last attempt, because it
    /// existed and was removed in the meantime, after which the creation is made again. Otherwise, its exception is thrown.
    /// </summary>
    private static bool IsCreationRaced(string path, int attempt) => attempt < Attempts || Exists(path);

    /// <summary>
    /// Writes a file under a temporary name and then moves it to its name, replacing a file of that name if there is one, so that the file is complete
    /// whenever it can be read.
    /// </summary>
    private static async Task WriteFileAsync<T>(string path, T value, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        string temporaryPath = await WriteTemporaryFileAsync(path, value, typeInfo, cancellationToken);

        try
        {
            // On Windows, a file that a reader has open for a moment cannot be replaced.
            await RetryAsync(() => File.Move(temporaryPath, path, overwrite: true), cancellationToken);
        }
        catch
        {
            TryDeleteFile(temporaryPath);
            throw;
        }
    }

    /// <summary>
    /// Writes the content of a file under a temporary name next to it, and flushes it to the disk.
    /// </summary>
    /// <returns>The path of the temporary file.</returns>
    private static async Task<string> WriteTemporaryFileAsync<T>(string path, T value, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        // Unique, so that servers that write the same file at the same time never write to the same temporary file.
        string temporaryPath = $"{path}.{CreateId()}{TemporaryFileExtension}";

        try
        {
            await using var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 0, useAsync: true);
            await JsonSerializer.SerializeAsync(stream, value, typeInfo, cancellationToken);

            // Without this, the move can reach the disk before the content does, and a power loss would leave an empty or partial file.
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            TryDeleteFile(temporaryPath);
            throw;
        }

        return temporaryPath;
    }

    /// <summary>
    /// Writes a chunk's content to the data file at its offset. Only a failed write of the file can be for a full disk: the content's failures are its own.
    /// </summary>
    /// <returns><see langword="true"/> if the content was written, or <see langword="false"/> if the file system has no room for it.</returns>
    private async Task<bool> WriteDataAsync(string dataPath, long offset, int length, Stream content, CancellationToken cancellationToken)
    {
        using var handle = File.OpenHandle(
            dataPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous | FileOptions.WriteThrough);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(WriteBlockSize);

        try
        {
            while (true)
            {
                int read = await content.ReadAtLeastAsync(buffer.AsMemory(0, WriteBlockSize), WriteBlockSize, throwOnEndOfStream: false, cancellationToken);

                if (read is 0)
                    break;

                try
                {
                    await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), offset, cancellationToken);
                }
                catch (IOException ex) when (IsOutOfSpace(ex, length, isReservation: false))
                {
                    // A file system that cannot reserve space for the file runs out of it as chunks are written.
                    _logger.LogWarning(ex, "A chunk of {Length} bytes could not be written in {Directory}.", length, RootDirectory);
                    return false;
                }

                offset += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return true;
    }

    /// <summary>
    /// Runs an operation on files, again after a short wait when it fails because another process has a file open for a moment, such as a virus scanner.
    /// </summary>
    private static async Task RetryAsync(Action operation, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                operation();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && ex is not (FileNotFoundException or DirectoryNotFoundException) &&
                attempt < Attempts)
            {
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Deletes a file, again after a short wait while another process has it open for a moment, such as a virus scanner. Throws if it cannot.
    /// </summary>
    private static Task DeleteFileAsync(string path, CancellationToken cancellationToken)
    {
        return RetryAsync(
            () => {
                try
                {
                    File.Delete(path);
                }
                catch (DirectoryNotFoundException)
                {
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// Releases a reservation of an idempotency key, unless it was released in the meantime: creates the reservation's marker, which only one release
    /// can do, and then removes the reservation, which no other release can have removed, and which can't have been reserved again, since a reservation
    /// is only made once the file of the last one is removed. The marker stays until the cleanup removes it, so that a release that read the reservation
    /// before then finds it rather than removing a newer reservation. A release that finds the marker waits a moment for the other release to remove the
    /// reservation, so that the reservation is gone once a release returns. The release that made the marker is the only one that can remove the
    /// reservation, so if it can't, such as while another process has the file open, it removes it again in the background, for up to 10 minutes.
    /// </summary>
    private async Task ReleaseReservationAsync(string path, string reservationId, CancellationToken cancellationToken)
    {
        try
        {
            if (!await TryCreateEmptyFileAsync($"{path}.{reservationId}{ReleasedFileExtension}"))
            {
                await WaitWhileReservedAsync(path, reservationId, cancellationToken);
                return;
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Released, and the directory of the keys removed since.
            return;
        }

        try
        {
            await DeleteFileAsync(path, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The reservation of an upload key in {Path} could not be removed, and is removed again in the background.", path);
            _ = KeepRemovingReservationAsync(path, reservationId);
        }
    }

    private async Task KeepRemovingReservationAsync(string path, string reservationId)
    {
        long started = _timeProvider.GetTimestamp();

        try
        {
            while (_timeProvider.GetElapsedTime(started) < MaxReservationRemovalTime)
            {
                await Task.Delay(ReservationRemovalInterval, _timeProvider, _disposed.Token);

                try
                {
                    if (await IsReservedAsync(path, reservationId, _disposed.Token))
                        await DeleteFileAsync(path, _disposed.Token);

                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Removed again at the next interval.
                }
            }

            _logger.LogError("The reservation of an upload key in {Path} could not be removed for 10 minutes.", path);
        }
        catch (OperationCanceledException) when (_disposed.IsCancellationRequested)
        {
            // The store is disposed, and the reservation is taken over once the cleanup has removed its marker.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The reservation of an upload key in {Path} could not be removed.", path);
        }
    }

    /// <summary>
    /// Waits for up to 2 seconds while a key's file holds a reservation that another release is removing.
    /// </summary>
    private async Task WaitWhileReservedAsync(string path, string reservationId, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();

        while (Stopwatch.GetElapsedTime(started) < ReleaseWait && await IsReservedAsync(path, reservationId, cancellationToken))
            await Task.Delay(ReadAgainDelay, cancellationToken);
    }

    /// <summary>
    /// Gets a value indicating whether a key's file holds the specified reservation, also one that is broken.
    /// </summary>
    private async Task<bool> IsReservedAsync(string path, string reservationId, CancellationToken cancellationToken)
    {
        var current = await ReadCreatedFileOnceAsync(path, DirectoryUploadStoreJsonContext.Default.KeyFile, cancellationToken);

        return current.State switch {
            CreatedFileState.Missing => false,
            CreatedFileState.Complete => current.Value!.ReservationId == reservationId,
            _ => GetBrokenReservationId(current.WrittenUtc) == reservationId,
        };
    }

    private static void RemoveStrayFiles(string directory, string extension, DateTime strayBeforeUtc)
    {
        try
        {
            foreach (string path in Directory.EnumerateFiles(directory, "*" + extension))
            {
                if (File.GetLastWriteTimeUtc(path) < strayBeforeUtc)
                    TryDeleteFile(path);
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Removed by a later cleanup.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Removed by a later cleanup.
        }
    }

    private string GetUserDirectory(string userId) => Path.Combine(RootDirectory, ShortLivedId.Hash(userId));

    private string GetKeyPath(string userId, string key) => Path.Combine(GetUserDirectory(userId), KeysDirectoryName, ShortLivedId.Hash(key));

    private string GetUploadDirectory(string userId, string uploadId) => Path.Combine(GetUserDirectory(userId), uploadId);

    /// <summary>
    /// Gets a value indicating whether an exception of a write of the specified length is because there is no room for it: on Windows, its error says
    /// so, and elsewhere, the volume of the directory has less space available than the length.
    /// </summary>
    /// <param name="exception">The exception of the write.</param>
    /// <param name="length">The length of the write.</param>
    /// <param name="isReservation">Whether the write reserved the space of a file, which on Windows also fails for a file that is larger than the file
    /// system allows.</param>
    private bool IsOutOfSpace(IOException exception, long length, bool isReservation)
    {
        if (OperatingSystem.IsWindows())
        {
            int error = exception.HResult & 0xFFFF;
            return error is ErrorHandleDiskFull or ErrorDiskFull || (isReservation && error is ErrorInvalidParameter or ErrorFileTooLarge);
        }

        try
        {
            return new DriveInfo(RootDirectory).AvailableFreeSpace < length;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes the current time to the marker of a write of a chunk at an interval, until it is stopped, so that claims know that the write is alive. A
    /// renewal that fails is made again at the next interval.
    /// </summary>
    private async Task KeepWriteAliveAsync(string writingPath, CancellationToken stop)
    {
        try
        {
            using var timer = new PeriodicTimer(WriteHeartbeatInterval, _timeProvider);

            while (await timer.WaitForNextTickAsync(stop))
            {
                try
                {
                    await WriteFileAsync(writingPath, new WritingFile { AliveUtc = UtcNow }, DirectoryUploadStoreJsonContext.Default.WritingFile, stop);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "The marker {Path} of a write of a chunk could not be renewed, and is renewed at the next interval.", writingPath);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Gets a value indicating whether a chunk of an upload is being written: whether a write marker shows a write that is alive. Removes the markers of
    /// writes that stopped without removing them.
    /// </summary>
    private async Task<bool> HasLiveWritesAsync(string directory, CancellationToken cancellationToken)
    {
        string[] paths;

        try
        {
            paths = Directory.GetFiles(Path.Combine(directory, ChunksDirectoryName), "*" + WritingFileExtension);
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }

        foreach (string path in paths)
        {
            WritingFile? write;

            try
            {
                write = await ReadFileAsync(path, DirectoryUploadStoreJsonContext.Default.WritingFile, cancellationToken);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "The marker {Path} of a write of a chunk cannot be read, so it is removed.", path);
                TryDeleteFile(path);
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // On Windows, a marker that a write that ends is removing cannot be opened for a moment, so the write is taken as alive, and the run is
                // claimed later.
                return true;
            }

            if (write is null)
                continue;

            if (write.AliveUtc + WriteLease > UtcNow)
                return true;

            TryDeleteFile(path);
        }

        return false;
    }

    /// <summary>
    /// Removes the reservations of keys whose uploads were never created or that cannot be read, the markers of reservations that were released, and
    /// temporary files that are left.
    /// </summary>
    private async Task CleanUpKeysAsync(string userDirectory, DateTime strayBeforeUtc, DateTime strayFilesBeforeUtc, CancellationToken cancellationToken)
    {
        string keysDirectory = Path.Combine(userDirectory, KeysDirectoryName);
        string[] paths;

        try
        {
            paths = Directory.GetFiles(keysDirectory);
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        foreach (string path in paths)
        {
            if (path.EndsWith(TemporaryFileExtension, StringComparison.Ordinal) || path.EndsWith(ReleasedFileExtension, StringComparison.Ordinal))
            {
                if (File.GetLastWriteTimeUtc(path) < strayFilesBeforeUtc)
                    TryDeleteFile(path);

                continue;
            }

            try
            {
                var reservation = await ReadCreatedFileAsync(path, DirectoryUploadStoreJsonContext.Default.KeyFile, cancellationToken);

                if (reservation.State is CreatedFileState.Broken)
                {
                    _logger.LogWarning("The reservation of an upload key in {Path} cannot be read, so it is released.", path);
                    await ReleaseReservationAsync(path, GetBrokenReservationId(reservation.WrittenUtc), cancellationToken);
                }
                else if (reservation.Value is { } key && key.ReservedUtc < strayBeforeUtc && ShortLivedId.IsWellFormed(key.UploadId) &&
                    !Exists(Path.Combine(userDirectory, key.UploadId, RecordFileName)))
                {
                    await ReleaseReservationAsync(path, key.ReservationId, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "The reservation of an upload key in {Path} could not be cleaned up.", path);
            }
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(keysDirectory).Any())
                Directory.Delete(keysDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A key was reserved in the meantime.
        }
    }

    /// <summary>
    /// Reads an upload with its current state. Files can be added and removed while it is read, such as when its claim is released or its outcome is
    /// stored, so a read that finds a file gone that it listed starts again a moment later.
    /// </summary>
    private async ValueTask<StoredUpload?> ReadAsync(string directory, string? userId, string uploadId, bool withoutOutcome, CancellationToken cancellationToken)
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
        string directory, string? userId, string uploadId, bool withoutOutcome, bool isLastAttempt, CancellationToken cancellationToken)
    {
        HashSet<string> names;

        try
        {
            names = Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal)!;
        }
        catch (DirectoryNotFoundException)
        {
            return (null, true);
        }

        if (names.Contains(DeletedFileName) || !names.Contains(RecordFileName) || (withoutOutcome && names.Contains(OutcomeFileName)))
            return (null, true);

        RecordFile? recordFile;

        try
        {
            recordFile = await ReadFileAsync(Path.Combine(directory, RecordFileName), DirectoryUploadStoreJsonContext.Default.RecordFile, cancellationToken);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "The record of the upload in {Directory} cannot be read.", directory);
            return (null, true);
        }

        // The upload was deleted while it was read.
        if (recordFile is null)
            return (null, false);

        // File names are not case-sensitive on some file systems, so the record has the exact ID and user.
        if (recordFile.Id != uploadId || (userId is not null && recordFile.UserId != userId))
            return (null, true);

        var record = new UploadRecord(
            recordFile.Id, recordFile.UserId, recordFile.Length, recordFile.ChunkSize, recordFile.CreatedUtc, recordFile.Metadata, recordFile.IdempotencyKey);

        if (names.Contains(OutcomeFileName))
        {
            string outcomePath = Path.Combine(directory, OutcomeFileName);
            var outcome = await ReadCreatedFileAsync(outcomePath, DirectoryUploadStoreJsonContext.Default.OutcomeFile, cancellationToken);

            switch (outcome.State)
            {
                case CreatedFileState.Complete:
                    return (new StoredUpload(record, record.CreatedUtc, outcome: outcome.Value!.Outcome, outcomeUtc: outcome.Value.StoredUtc), true);
                case CreatedFileState.Broken:
                    // The service reports an outcome that it cannot read, and the upload is removed when the outcome's file is no longer kept.
                    _logger.LogWarning("The outcome of the upload in {Directory} cannot be read.", directory);
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
            claim = await ReadCreatedFileAsync(Path.Combine(directory, ClaimFileName), DirectoryUploadStoreJsonContext.Default.ClaimFile, cancellationToken);

            // The claim was released, or an outcome was stored, while the upload was read.
            if (claim.State is CreatedFileState.Missing && !isLastAttempt)
                return (null, false);
        }

        // The file's last write time is the time of the last chunk. The file is removed once an outcome is stored, and a handler can move it while its run
        // is claimed.
        var data = new FileInfo(Path.Combine(directory, DataFileName));

        if (!data.Exists && claim.State is CreatedFileState.Missing && !isLastAttempt)
            return (null, false);

        var lastWriteUtc = data.Exists ? data.LastWriteTimeUtc : record.CreatedUtc;

        switch (claim.State)
        {
            case CreatedFileState.Complete:
                // An outcome that was stored while the claim was read ends the run.
                if (Exists(Path.Combine(directory, OutcomeFileName)) && !isLastAttempt)
                    return (null, false);

                var state = claim.Value!.RenewedUtc + claim.Value.LeaseDuration < UtcNow ? UploadRunState.Lapsed : UploadRunState.Running;
                return (new StoredUpload(record, lastWriteUtc, state, claim.Value.StartedUtc), true);
            case CreatedFileState.BeingWritten:
                return (new StoredUpload(record, lastWriteUtc, UploadRunState.Running, claim.WrittenUtc), true);
            case CreatedFileState.Broken:
                // A claim that cannot be read is taken as lapsed, which ends the upload as interrupted.
                _logger.LogWarning("The claim of the run of the upload in {Directory} cannot be read.", directory);
                return (new StoredUpload(record, lastWriteUtc, UploadRunState.Lapsed, claim.WrittenUtc), true);
        }

        return (new StoredUpload(record, lastWriteUtc), true);
    }

    /// <summary>
    /// A file that is created with its content: its state, its content once it is complete, and the time it was last written while it cannot be read.
    /// </summary>
    private readonly record struct CreatedFile<T>(CreatedFileState State, T? Value, DateTime WrittenUtc)
        where T : class;

    /// <summary>
    /// A claim of a run, kept in the upload's <c>claim</c> file.
    /// </summary>
    private sealed class Claim(DirectoryUploadStore store, string directory, string claimId, UploadRecord upload) : IUploadRunClaim
    {
        private string ClaimPath => Path.Combine(directory, ClaimFileName);

        public async ValueTask<bool> RenewAsync(CancellationToken cancellationToken = default)
        {
            if (HasOutcomeOrIsDeleted(directory) || await ReadCurrentAsync(cancellationToken) is not { } claim)
                return false;

            var renewed = new ClaimFile {
                ClaimId = claim.ClaimId, LeaseDuration = claim.LeaseDuration, StartedUtc = claim.StartedUtc, RenewedUtc = store.UtcNow,
            };

            await WriteFileAsync(ClaimPath, renewed, DirectoryUploadStoreJsonContext.Default.ClaimFile, cancellationToken);
            return true;
        }

        public async ValueTask ReleaseAsync(CancellationToken cancellationToken = default)
        {
            if (await ReadCurrentAsync(cancellationToken) is null)
                return;

            // The upload has all its chunks again, and is kept for as long as after its last chunk.
            try
            {
                await RetryAsync(() => File.SetLastWriteTimeUtc(Path.Combine(directory, DataFileName), store.GetChunkTimeUtc(upload)), cancellationToken);
            }
            catch (FileNotFoundException)
            {
            }

            // A claim that is left would lapse, which ends the upload as interrupted, so a claim that can't be removed fails the release.
            await DeleteFileAsync(ClaimPath, cancellationToken);
        }

        /// <summary>
        /// Removes the claim, if it is still this one, without touching the upload.
        /// </summary>
        public async Task RemoveAsync(CancellationToken cancellationToken)
        {
            if (await ReadCurrentAsync(cancellationToken) is not null)
                await DeleteFileAsync(ClaimPath, cancellationToken);
        }

        private async Task<ClaimFile?> ReadCurrentAsync(CancellationToken cancellationToken)
        {
            var claim = await ReadFileAsync(ClaimPath, DirectoryUploadStoreJsonContext.Default.ClaimFile, cancellationToken);
            return claim?.ClaimId == claimId ? claim : null;
        }
    }

    /// <summary>
    /// The complete file of an upload, which is its <c>data</c> file until it is moved.
    /// </summary>
    private sealed class FileContent(string path) : UploadContent
    {
        private string _path = path;

        public override string? FilePath => _path;

        public override Stream OpenReadStream()
        {
            // Deleting the upload, such as when it is aborted, removes the file while it is open.
            return new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        }

        public override async Task MoveToAsync(string destinationPath, CancellationToken cancellationToken = default)
        {
            string fullPath = Path.GetFullPath(destinationPath);
            cancellationToken.ThrowIfCancellationRequested();

            // A file on the same file system is renamed, and one on another is copied, which can be cancelled, where File.Move would copy it synchronously.
            if (IsOnSameFileSystem(fullPath, _path))
            {
                File.Move(_path, fullPath, overwrite: false);
            }
            else
            {
                await base.MoveToAsync(fullPath, cancellationToken);
                TryDeleteFile(_path);
            }

            _path = fullPath;
        }
    }
}

/// <summary>
/// The record of an upload in its <c>upload.json</c> file.
/// </summary>
internal sealed class RecordFile
{
    public required string Id { get; init; }

    public required string UserId { get; init; }

    public required long Length { get; init; }

    public required int ChunkSize { get; init; }

    public required DateTime CreatedUtc { get; init; }

    public required string Metadata { get; init; }

    public string? IdempotencyKey { get; init; }
}

/// <summary>
/// The claim of an upload's run in its <c>claim</c> file, with the time it was last renewed.
/// </summary>
internal sealed class ClaimFile
{
    public required string ClaimId { get; init; }

    public required TimeSpan LeaseDuration { get; init; }

    public required DateTime StartedUtc { get; init; }

    public required DateTime RenewedUtc { get; init; }
}

/// <summary>
/// The marker of a write of a chunk, with the time the write last showed that it is alive.
/// </summary>
internal sealed class WritingFile
{
    public required DateTime AliveUtc { get; init; }
}

/// <summary>
/// The reservation of an idempotency key in its file in the <c>keys</c> directory of the user.
/// </summary>
internal sealed class KeyFile
{
    /// <summary>
    /// Gets the ID of the reservation, which names the marker of its release.
    /// </summary>
    public required string ReservationId { get; init; }

    public required string UploadId { get; init; }

    public required DateTime ReservedUtc { get; init; }
}

/// <summary>
/// The outcome of an upload in its <c>outcome.json</c> file.
/// </summary>
internal sealed class OutcomeFile
{
    public required string Outcome { get; init; }

    public required DateTime StoredUtc { get; init; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(RecordFile))]
[JsonSerializable(typeof(ClaimFile))]
[JsonSerializable(typeof(WritingFile))]
[JsonSerializable(typeof(OutcomeFile))]
[JsonSerializable(typeof(KeyFile))]
internal sealed partial class DirectoryUploadStoreJsonContext : JsonSerializerContext;
