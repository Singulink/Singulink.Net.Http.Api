using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Singulink.IO;

namespace Singulink.Net.Http.Api.Service.Uploads;

/// <content>
/// <para>
/// The files that only one request may create, the <c>claim</c> of a run, the <c>finished.json</c> of an upload and the reservation of an idempotency key,
/// a file in <c>{Directory}/{userKey}/keys</c> named by a hash of the key, are created in one step that fails if the file exists
/// (<see cref="FileMode.CreateNew"/>), which the file system, also that of a file server, decides for all of its clients, and their content is then
/// written. A reader can find such a file empty or partly written for a moment: it takes a file that it cannot read as being written for a minute after
/// the file's last write time, by the file system's clock, and as broken after that. A server that stalls for longer than that while it creates such a
/// file can find it taken over, and two servers then have the claim or the reservation. The other files, such as the record, the write markers and the
/// claims that are renewed, are written under a temporary name and moved to their name, replacing a file of that name, so they are complete whenever they
/// can be read. A release of a reservation creates a marker of its own, <c>{key}.{reservationId}.released</c>, which only one release can do, and then
/// removes the reservation. A deleted upload gets a <c>deleted</c> marker, which only one deletion can create, before its files are removed, so it is gone even
/// if some of them cannot be removed yet. The directory of a finished upload gets none: its record is removed first, and then the rest, so that a store of an
/// outcome that meets the marker knows that the upload was deleted rather than finished. A store of an outcome looks for the marker again once it has created
/// <c>finished.json</c>, and stores nothing if a deletion has marked the upload or removed its record. It looks again once it has copied the outcome, and
/// removes the copy if it finds the marker. A server that moves a finished upload on looks for the marker before and after it copies the outcome in the same
/// way. A deletion that has removed the whole directory by then, having found <c>finished.json</c>, leaves the store to succeed, and removes the upload's
/// outcomes after it has removed the directory: a copy written before then is removed, so the upload reads as gone, and one written after stays, so the upload
/// reads as finished, with the outcome of its handler, which did finish.
/// </para>
/// <para>
/// The checks for files that another server may have created open them rather than ask whether they exist, which clients of file shares can answer from
/// caches of their own. The times that tell whether a claim or a write is alive are in the content of their files rather than in the file system's times.
/// </para>
/// </content>
internal sealed partial class DirectoryUploadStore
{
    private const string TemporaryFileExtension = ".tmp";

    // How many times an operation is tried that fails because another process has the file open for a moment, such as a virus scanner.
    private const int Attempts = 10;

    // How many times the directory of an upload is removed when another removal at the same time makes an attempt fail.
    private const int DirectoryRemovalAttempts = 3;

    // How long a file that is created with its content, and that cannot be read yet, is taken as being written.
    private static readonly TimeSpan IncompleteFileAge = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    // How long apart the directory of an upload is removed when another removal at the same time makes an attempt fail.
    private static readonly TimeSpan DirectoryRemovalDelay = TimeSpan.FromMilliseconds(50);

    // How long a read of an upload whose files changed while it was read waits before it reads them again.
    private static readonly TimeSpan ReadAgainDelay = TimeSpan.FromMilliseconds(20);

    // How long a write of a chunk waits for a claim made at the same time to back off, and a release of a key's reservation for another release to remove it.
    private static readonly TimeSpan RemovalWait = TimeSpan.FromSeconds(2);

    // How often a claim that backed off, or the reservation of a key that a release released, that could not be removed is removed again, and for how long.
    private static readonly TimeSpan BackgroundRemovalInterval = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan MaxBackgroundRemovalTime = TimeSpan.FromMinutes(10);

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

    /// <summary>
    /// Removes a file that could not be removed again at an interval, in the background, for up to 10 minutes or until the store is disposed. A failure is
    /// followed by another attempt, and the last one is logged when the removal gives up: the file is then left to expire, as a claim lapses and a reservation
    /// is taken over once the cleanup has removed its marker.
    /// </summary>
    /// <param name="what">What is removed, for the log.</param>
    /// <param name="file">The file.</param>
    /// <param name="remove">Removes the file.</param>
    /// <param name="renew">Keeps the file valid between attempts, if it needs that, and returns <see langword="false"/> if it is gone.</param>
    /// <param name="renewInterval">How often to renew the file.</param>
    private async Task KeepRemovingAsync(
        string what, IAbsoluteFilePath file, Func<Task> remove, Func<Task<bool>>? renew = null, TimeSpan renewInterval = default)
    {
        long started = _timeProvider.GetTimestamp();
        long renewed = started;
        Exception? failure = null;

        try
        {
            while (_timeProvider.GetElapsedTime(started) < MaxBackgroundRemovalTime)
            {
                await Task.Delay(BackgroundRemovalInterval, _timeProvider, _disposed.Token);

                try
                {
                    await remove();
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failure = ex;
                }

                if (renew is not null && _timeProvider.GetElapsedTime(renewed) >= renewInterval)
                {
                    try
                    {
                        if (!await renew())
                            return;

                        renewed = _timeProvider.GetTimestamp();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        failure = ex;
                    }
                }
            }

            _logger.LogError(failure, "{What} in {Path} could not be removed for 10 minutes, so it is left to expire.", what, file.PathDisplay);
        }
        catch (OperationCanceledException) when (_disposed.IsCancellationRequested)
        {
            // The store is disposed, and the file is left to expire.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{What} in {Path} could not be removed, so it is left to expire.", what, file.PathDisplay);
        }
    }

    /// <summary>
    /// Removes the directory of an upload whose record is removed, again a moment later if another removal at the same time makes an attempt fail.
    /// </summary>
    private static async Task RemoveDirectoryAsync(IAbsoluteDirectoryPath directory)
    {
        for (int attempt = 1; ; attempt++)
        {
            TryDeleteDirectory(directory);

            if (!directory.Exists || attempt == DirectoryRemovalAttempts)
                break;

            await Task.Delay(DirectoryRemovalDelay, CancellationToken.None);
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
    /// Gets a value indicating whether two paths are on the same file system, by the mount points of the system's drives (see
    /// <see cref="GetMountPoint"/>).
    /// </summary>
    internal static bool IsOnSameFileSystem(IAbsolutePath path, IAbsolutePath otherPath)
    {
        string[] driveRoots;

        try
        {
            driveRoots = Environment.GetLogicalDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            driveRoots = [];
        }

        // The paths are compared in their display form, which is the form of the mount points.
        string mountPoint = GetMountPoint(path.PathDisplay, driveRoots);
        return string.Equals(mountPoint, GetMountPoint(otherPath.PathDisplay, driveRoots), StringComparison.Ordinal);
    }

    private static IAbsoluteDirectoryPath[] GetDirectories(IAbsoluteDirectoryPath directory)
    {
        try
        {
            return [.. directory.GetChildDirectories()];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }

    /// <summary>
    /// Enumerates the files of a directory with their times, which listing the directory gives on Windows, or nothing if the directory doesn't exist. Files
    /// that are created or removed meanwhile can be left out or not.
    /// </summary>
    private static IEnumerable<CachedFileInfo> EnumerateFiles(IAbsoluteDirectoryPath directory)
    {
        using var files = directory.GetChildFilesInfo().GetEnumerator();

        while (true)
        {
            try
            {
                if (!files.MoveNext())
                    yield break;
            }
            catch (DirectoryNotFoundException)
            {
                yield break;
            }

            yield return files.Current;
        }
    }

    /// <summary>
    /// Removes a directory if it is empty. A file that is created in it at the same time keeps it, or is created again by its creator.
    /// </summary>
    private static void TryDeleteEmptyDirectory(IAbsoluteDirectoryPath directory)
    {
        try
        {
            if (!directory.GetChildEntries().Any())
                directory.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file was created in the meantime, or the directory is gone or in use.
        }
    }

    /// <summary>
    /// Gets a value indicating whether a file exists by opening it, which asks the file system, also that of a file server, rather than a cache of a client
    /// of a share: <see cref="File.Exists(string?)"/> reads the file's attributes, which an SMB client can answer from its cache for a few seconds. A file
    /// that exists but cannot be opened, such as one that another process holds or that is being deleted, is taken as existing.
    /// </summary>
    private static bool Exists(IAbsoluteFilePath file)
    {
        try
        {
            using var stream = file.OpenStream(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
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

    /// <summary>
    /// Gets a value indicating whether a file exists, as <see cref="Exists"/> does, but opens it again after a short wait while it can't be opened, such as
    /// while the directory that it is in is being removed, which can't be told apart from a file that another process holds until the directory is gone. A
    /// file that still can't be opened is taken as existing.
    /// </summary>
    private static async Task<bool> ExistsAsync(IAbsoluteFilePath file, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = file.OpenStream(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
                return true;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= Attempts)
                    return true;

                await Task.Delay(RetryDelay, cancellationToken);
            }
        }
    }

    private static FileStream OpenRead(IAbsoluteFilePath file)
    {
        return file.OpenAsyncStream(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }

    /// <summary>
    /// Reads a file that is written under a temporary name and moved to its name (see <see cref="WriteFileAsync"/>), again after a short wait while another
    /// process has it open for a moment, such as a virus scanner.
    /// </summary>
    /// <returns>The file's content, or <see langword="null"/> if it does not exist.</returns>
    private static async Task<T?> ReadFileAsync<T>(IAbsoluteFilePath file, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
        where T : class
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var stream = OpenRead(file);
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
    private async Task<CreatedFile<T>> ReadCreatedFileAsync<T>(IAbsoluteFilePath file, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
        where T : class
    {
        var read = await ReadCreatedFileOnceAsync(file, typeInfo, cancellationToken);

        if (read.State is not CreatedFileState.Broken)
            return read;

        // A file that looks broken is read again a moment later, and is broken only if it is still the same, since the times of a file that is being
        // created can be passing ones for a moment, such as the verifier of an exclusive creation over NFS.
        await Task.Delay(RetryDelay, cancellationToken);
        var again = await ReadCreatedFileOnceAsync(file, typeInfo, cancellationToken);

        if (again.State is CreatedFileState.Broken && again.WrittenUtc != read.WrittenUtc)
            return again with { State = CreatedFileState.BeingWritten };

        return again;
    }

    private async Task<CreatedFile<T>> ReadCreatedFileOnceAsync<T>(IAbsoluteFilePath file, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
        where T : class
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var stream = OpenRead(file);

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
                bool isBeingWritten = writtenUtc + IncompleteFileAge > GetFileSystemUtcNow(file.ParentDirectory);

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
    private DateTime GetFileSystemUtcNow(IAbsoluteDirectoryPath directory)
    {
        try
        {
            var file = directory.CombineFile(CreateId() + TemporaryFileExtension, PathOptions.None);
            using var handle = File.OpenHandle(file.PathExport, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.DeleteOnClose);

            return File.GetLastWriteTimeUtc(handle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "The time of the file system in {Directory} could not be read, so the system's time is used.", directory.PathDisplay);
            return TimeProvider.System.GetUtcNow().UtcDateTime;
        }
    }

    /// <summary>
    /// Creates a file that must not exist yet, with its content, in one step that fails if the file exists, which the file system decides for all of its
    /// clients, so that of several creations at the same time, also on several servers, one creates the file. Readers can find the file empty or partly
    /// written while it is written (see <see cref="ReadCreatedFileAsync"/>).
    /// </summary>
    /// <returns><see langword="true"/> if the file was created, or <see langword="false"/> if it exists.</returns>
    private static async Task<bool> TryCreateFileAsync<T>(IAbsoluteFilePath file, T value, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        FileStream stream;

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                stream = file.OpenAsyncStream(FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete, bufferSize: 0);
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && ex is not DirectoryNotFoundException &&
                IsCreationRaced(file, attempt))
            {
                if (Exists(file))
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
            TryDeleteFile(file);
            throw;
        }

        return true;
    }

    /// <summary>
    /// Creates an empty file that must not exist yet, in one step that fails if the file exists (see <see cref="TryCreateFileAsync"/>).
    /// </summary>
    /// <returns><see langword="true"/> if the file was created, or <see langword="false"/> if it exists.</returns>
    private static async Task<bool> TryCreateEmptyFileAsync(IAbsoluteFilePath file)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                file.OpenStream(FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete).Dispose();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && ex is not DirectoryNotFoundException &&
                IsCreationRaced(file, attempt))
            {
                if (Exists(file))
                    return false;

                await Task.Delay(ReadAgainDelay);
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether a creation of a file that failed is handled: because the file exists, or, before the last attempt, because it
    /// existed and was removed in the meantime, after which the creation is made again. Otherwise, its exception is thrown.
    /// </summary>
    private static bool IsCreationRaced(IAbsoluteFilePath file, int attempt) => attempt < Attempts || Exists(file);

    /// <summary>
    /// Writes a file under a temporary name and then moves it to its name, replacing a file of that name if there is one, so that the file is complete
    /// whenever it can be read.
    /// </summary>
    /// <param name="file">The file.</param>
    /// <param name="value">The content of the file.</param>
    /// <param name="typeInfo">The type info of the content.</param>
    /// <param name="cancellationToken">A token that cancels the write.</param>
    /// <param name="flushToDisk">Whether the content reaches the disk before the file is moved to its name, so that the file is complete after a power
    /// loss too.</param>
    private static async Task WriteFileAsync<T>(
        IAbsoluteFilePath file, T value, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken, bool flushToDisk = true)
    {
        var temporaryFile = await WriteTemporaryFileAsync(file, value, typeInfo, flushToDisk, cancellationToken);

        try
        {
            // On Windows, a file that a reader has open for a moment cannot be replaced.
            await RetryAsync(() => temporaryFile.MoveTo(file, overwrite: true), cancellationToken);
        }
        catch
        {
            TryDeleteFile(temporaryFile);
            throw;
        }
    }

    /// <summary>
    /// Writes the content of a file under a temporary name next to it, and flushes it to the disk unless <paramref name="flushToDisk"/> is
    /// <see langword="false"/>.
    /// </summary>
    /// <returns>The temporary file.</returns>
    private static async Task<IAbsoluteFilePath> WriteTemporaryFileAsync<T>(
        IAbsoluteFilePath file, T value, JsonTypeInfo<T> typeInfo, bool flushToDisk, CancellationToken cancellationToken)
    {
        // Unique, so that servers that write the same file at the same time never write to the same temporary file.
        var temporaryFile = file.ParentDirectory.CombineFile($"{file.Name}.{CreateId()}{TemporaryFileExtension}", PathOptions.None);

        try
        {
            await using var stream = temporaryFile.OpenAsyncStream(FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 0);
            await JsonSerializer.SerializeAsync(stream, value, typeInfo, cancellationToken);

            // Without this, the move can reach the disk before the content does, and a power loss would leave an empty or partial file.
            if (flushToDisk)
                stream.Flush(flushToDisk: true);
        }
        catch
        {
            TryDeleteFile(temporaryFile);
            throw;
        }

        return temporaryFile;
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
    private static Task DeleteFileAsync(IAbsoluteFilePath file, CancellationToken cancellationToken)
    {
        return RetryAsync(
            () => {
                try
                {
                    file.Delete();
                }
                catch (DirectoryNotFoundException)
                {
                }
            },
            cancellationToken);
    }

    private static void TryDeleteFile(IAbsoluteFilePath file)
    {
        try
        {
            file.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Removed by a later cleanup.
        }
    }

    private static void TryDeleteDirectory(IAbsoluteDirectoryPath directory)
    {
        try
        {
            directory.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Removed by a later cleanup.
        }
    }

    /// <summary>
    /// A file that is created with its content: its state, its content once it is complete, and the time it was last written while it cannot be read.
    /// </summary>
    private readonly record struct CreatedFile<T>(CreatedFileState State, T? Value, DateTime WrittenUtc)
        where T : class;
}
