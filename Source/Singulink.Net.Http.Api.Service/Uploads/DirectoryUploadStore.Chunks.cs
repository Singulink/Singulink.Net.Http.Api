using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Singulink.IO;

namespace Singulink.Net.Http.Api.Service.Uploads;

/// <content>
/// A chunk that is being written has a write marker, <c>chunks/{index}.{writeId}.writing</c>, which holds the time the write last showed that it is alive,
/// every 15 seconds, when the write also sets the time of the upload's last chunk if bytes of the chunk arrived since, so that the upload doesn't expire while
/// a chunk arrives, and does while one stalls. A write creates its marker and then checks for a claim, an outcome and a deletion, and a claim is created and
/// then checks for write markers that are alive, so of a write and a claim made at the same time, at least one sees the other and gives way. A marker that
/// cannot be opened is taken as alive. Markers are not flushed to the disk, since the write they show is gone after a power loss. A write that finds a claim
/// waits up to 2 seconds for it to back off, so a write that meets a claim that won takes 2 seconds to be closed. A claim that backs off and can't be removed
/// is kept renewed and removed again in the background, for up to 10 minutes, and looks like a running run meanwhile, so a write that meets it is closed.
/// </content>
internal sealed partial class DirectoryUploadStore
{
    private const string DataFileName = "data";
    private const string ChunksDirectoryName = "chunks";
    private const string WritingFileExtension = ".writing";

    // The size of the writes of a chunk to the file.
    private const int WriteBlockSize = 1 << 20;

    // The Win32 errors of a disk that has no room for a file (ERROR_HANDLE_DISK_FULL, ERROR_DISK_FULL), and of a file that is larger than the file system
    // allows (ERROR_FILE_TOO_LARGE, and ERROR_INVALID_PARAMETER for space reserved for a file), which are the lower 16 bits of an IOException's HResult on
    // Windows.
    private const int ErrorHandleDiskFull = 0x27;

    private const int ErrorInvalidParameter = 0x57;
    private const int ErrorDiskFull = 0x70;
    private const int ErrorFileTooLarge = 0xDF;

    // How often a write of a chunk shows that it is alive, and how long after it last did a claim takes it for stopped.
    private static readonly TimeSpan WriteHeartbeatInterval = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan WriteLease = TimeSpan.FromMinutes(1);

    // How much later than an upload's creation the time of its chunks is at the least, which is more than the resolution of a file system's times.
    private static readonly TimeSpan MinChunkTimeAfterCreation = TimeSpan.FromSeconds(2);

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

    public async ValueTask<ChunkWriteResult> WriteChunkAsync(UploadRecord upload, int index, Stream content, CancellationToken cancellationToken = default)
    {
        var directory = GetUploadDirectory(upload.UserId, upload.Id);
        var dataFile = directory.CombineFile(DataFileName, PathOptions.None);
        var chunksDirectory = directory.CombineDirectory(ChunksDirectoryName, PathOptions.None);
        string markerName = index.ToString(CultureInfo.InvariantCulture);
        var markerFile = chunksDirectory.CombineFile(markerName, PathOptions.None);
        var writingFile = chunksDirectory.CombineFile($"{markerName}.{CreateId()}{WritingFileExtension}", PathOptions.None);

        try
        {
            await WriteMarkerAsync(writingFile, cancellationToken);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException || (ex is IOException or UnauthorizedAccessException && IsBeingDeleted(directory)))
        {
            // An outcome removes the directory of the chunks. On Windows, a directory that is being removed cannot be written to for a moment.
            return GetEndedWriteResult(directory);
        }

        PeriodicTimer? heartbeatTimer = null;
        Task? heartbeat = null;
        bool isWritingData = false;

        try
        {
            // A claim, an outcome or a deletion made before the write marker existed wins. A claim made after it sees the marker.
            if (!Exists(directory.CombineFile(RecordFileName, PathOptions.None)) || Exists(directory.CombineFile(DeletedFileName, PathOptions.None)))
                return GetEndedWriteResult(directory);

            if (!await WaitWhileClaimedAsync(directory, cancellationToken))
                return ChunkWriteResult.Closed;

            // A chunk that was received is missing until it has been written again, so that a write that fails can't leave it reported as received.
            await DeleteFileAsync(markerFile, cancellationToken);

            // The upload is kept for as long as after its last chunk from now on, also while a slow chunk arrives.
            await RetryAsync(() => File.SetLastWriteTimeUtc(dataFile.PathExport, GetChunkTimeUtc(upload)), cancellationToken);

            var received = new StrongBox<long>();
            heartbeatTimer = new PeriodicTimer(WriteHeartbeatInterval, _timeProvider);
            heartbeat = KeepWriteAliveAsync(writingFile, dataFile, upload, heartbeatTimer, received);
            isWritingData = true;

            if (!await WriteDataAsync(dataFile, upload.GetChunkOffset(index), upload.GetChunkLength(index), content, received, cancellationToken))
                return ChunkWriteResult.NoRoom;

            isWritingData = false;

            await RetryAsync(() => File.SetLastWriteTimeUtc(dataFile.PathExport, GetChunkTimeUtc(upload)), cancellationToken);
            await RetryAsync(() => File.WriteAllBytes(markerFile.PathExport, []), cancellationToken);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException ||
            (ex is IOException or UnauthorizedAccessException && IsBeingDeleted(directory)))
        {
            return GetEndedWriteResult(directory);
        }
        catch (IOException ex) when (!isWritingData && IsOutOfSpace(ex, upload.GetChunkLength(index), isReservation: false))
        {
            // The marker of the chunk could not be created on a full disk. The content's own failures are not the disk's.
            _logger.LogWarning(ex, "A chunk of {Length} bytes could not be written in {Directory}.", upload.GetChunkLength(index), RootDirectory.PathDisplay);
            return ChunkWriteResult.NoRoom;
        }
        finally
        {
            // The heartbeat stops before the marker is removed, so that it can't put the marker back.
            heartbeatTimer?.Dispose();

            if (heartbeat is not null)
                await heartbeat;

            TryDeleteFile(writingFile);
        }

        return ChunkWriteResult.Written;
    }

    public ValueTask<IReadOnlyCollection<int>> GetReceivedChunksAsync(UploadRecord upload, CancellationToken cancellationToken = default)
    {
        var chunksDirectory = GetUploadDirectory(upload.UserId, upload.Id).CombineDirectory(ChunksDirectoryName, PathOptions.None);
        var received = new SortedSet<int>();

        try
        {
            // Write markers and temporary files have names that are not numbers.
            foreach (var file in chunksDirectory.GetChildFiles())
            {
                if (int.TryParse(file.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index < upload.ChunkCount)
                    received.Add(index);
            }
        }
        catch (DirectoryNotFoundException)
        {
        }

        return new(received);
    }

    /// <summary>
    /// Gets the result of a write of a chunk whose upload is no longer open, such as one that ended while the chunk was written: closed if the upload has
    /// an outcome, in its directory or in the user's outcomes, and not found otherwise.
    /// </summary>
    private static ChunkWriteResult GetEndedWriteResult(IAbsoluteDirectoryPath directory)
    {
        if (Exists(directory.CombineFile(FinishedFileName, PathOptions.None)))
            return ChunkWriteResult.Closed;

        var outcomesDirectory = directory.ParentDirectory!.CombineDirectory(OutcomesDirectoryName, PathOptions.None);
        return FindOutcomeFiles(outcomesDirectory, directory.Name).Any() ? ChunkWriteResult.Closed : ChunkWriteResult.NotFound;
    }

    /// <summary>
    /// Writes a chunk's content to the data file at its offset. Only a failed write of the file can be for a full disk: the content's failures are its own.
    /// </summary>
    /// <param name="dataFile">The data file.</param>
    /// <param name="offset">The offset of the chunk.</param>
    /// <param name="length">The length of the chunk.</param>
    /// <param name="content">The chunk's content.</param>
    /// <param name="received">The number of bytes of the content that have arrived so far, for the write's heartbeat.</param>
    /// <param name="cancellationToken">A token that cancels the write.</param>
    /// <returns><see langword="true"/> if the content was written, or <see langword="false"/> if the file system has no room for it.</returns>
    private async Task<bool> WriteDataAsync(
        IAbsoluteFilePath dataFile, long offset, int length, Stream content, StrongBox<long> received, CancellationToken cancellationToken)
    {
        // Each block is written through to the disk, so that a chunk is on the disk when its marker is created. That costs little with a write cache that
        // survives a power loss, such as a battery-backed RAID cache, and a disk write of each block without one.
        using var handle = File.OpenHandle(
            dataFile.PathExport, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous | FileOptions.WriteThrough);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(WriteBlockSize);

        try
        {
            while (true)
            {
                int read = await ReadBlockAsync(content, buffer, received, cancellationToken);

                if (read is 0)
                    break;

                try
                {
                    await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), offset, cancellationToken);
                }
                catch (IOException ex) when (IsOutOfSpace(ex, length, isReservation: false))
                {
                    // A file system that cannot reserve space for the file runs out of it as chunks are written.
                    _logger.LogWarning(ex, "A chunk of {Length} bytes could not be written in {Directory}.", length, RootDirectory.PathDisplay);
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
    /// Reads a block of a chunk's content into a buffer, or what is left of the content, and counts the bytes as they arrive.
    /// </summary>
    /// <returns>The number of bytes that were read, which is 0 at the end of the content.</returns>
    private static async ValueTask<int> ReadBlockAsync(Stream content, byte[] buffer, StrongBox<long> received, CancellationToken cancellationToken)
    {
        int length = 0;

        while (length < WriteBlockSize)
        {
            int read = await content.ReadAsync(buffer.AsMemory(length, WriteBlockSize - length), cancellationToken);

            if (read is 0)
                break;

            length += read;
            Interlocked.Add(ref received.Value, read);
        }

        return length;
    }

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
            return RootDirectory.AvailableFreeSpace < length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes the marker of a write of a chunk with the current time. The marker is not flushed to the disk: after a power loss, the write that it shows
    /// is gone too.
    /// </summary>
    private Task WriteMarkerAsync(IAbsoluteFilePath writingFile, CancellationToken cancellationToken)
    {
        var marker = new WritingFile { AliveUtc = UtcNow };
        return WriteFileAsync(writingFile, marker, DirectoryUploadStoreJsonContext.Default.WritingFile, cancellationToken, flushToDisk: false);
    }

    /// <summary>
    /// Writes the current time to the marker of a write of a chunk at each tick of a timer, until the timer is disposed, so that claims know that the write
    /// is alive. If bytes of the chunk have arrived since the last tick, it also sets the time of the upload's last chunk to it, so that the upload doesn't
    /// expire while a slow chunk arrives, and does expire while one that stalled doesn't. A renewal that fails is made again at the next tick.
    /// </summary>
    private async Task KeepWriteAliveAsync(
        IAbsoluteFilePath writingFile, IAbsoluteFilePath dataFile, UploadRecord upload, PeriodicTimer timer, StrongBox<long> received)
    {
        long lastReceived = 0;

        while (await timer.WaitForNextTickAsync())
        {
            try
            {
                await WriteMarkerAsync(writingFile, CancellationToken.None);
                long nowReceived = Interlocked.Read(ref received.Value);

                if (nowReceived != lastReceived)
                {
                    await RetryAsync(() => File.SetLastWriteTimeUtc(dataFile.PathExport, GetChunkTimeUtc(upload)), CancellationToken.None);
                    lastReceived = nowReceived;
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // The upload was deleted, and the write finds that out when it ends.
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(
                    ex, "A write of a chunk with the marker {Path} could not show that it is alive, and tries at the next tick.", writingFile.PathDisplay);
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether a chunk of an upload is being written: whether a write marker shows a write that is alive. Removes the markers of
    /// writes that stopped without removing them.
    /// </summary>
    private async Task<bool> HasLiveWritesAsync(IAbsoluteDirectoryPath directory, CancellationToken cancellationToken)
    {
        IAbsoluteFilePath[] files;

        try
        {
            files = [.. directory.CombineDirectory(ChunksDirectoryName, PathOptions.None).GetChildFiles("*" + WritingFileExtension)];
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }

        foreach (var file in files)
        {
            WritingFile? write;

            try
            {
                write = await ReadFileAsync(file, DirectoryUploadStoreJsonContext.Default.WritingFile, cancellationToken);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "The marker {Path} of a write of a chunk cannot be read, so it is removed.", file.PathDisplay);
                TryDeleteFile(file);
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

            TryDeleteFile(file);
        }

        return false;
    }
}
