using System.Diagnostics.CodeAnalysis;
using Singulink.IO;

namespace Singulink.Net.Http.Api.Service.Uploads;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Provides options for the directory store of chunked uploads (see <see cref="ServiceCollectionExtensions.AddDirectoryUploadStore"/>): the directory, and
/// whether other processes use it too.
/// </summary>
/// <remarks>
/// <para>
/// The store checks and freezes the options when the application starts, after which they can't be changed, and their setters throw an
/// <see cref="InvalidOperationException"/>.
/// </para>
/// <para>
/// The members that are <see langword="required"/> have no default. The compiler requires them when the options are created in code, but not when they are
/// bound from a configuration, so they read as not set until they are set, in any order, and the application fails to start, with a message that names
/// them, if one is still not set then.
/// </para>
/// <para>
/// The options can be bound from a configuration section with <c>services.Configure&lt;DirectoryUploadStoreOptions&gt;(section)</c>. The reflection-based
/// configuration binder binds <see cref="Directory"/> from a string through the type converter of Singulink.IO.FileSystem, which reads it as a path of the
/// platform the app runs on, with <see cref="PathOptions.None"/>, and the directory then must have friendly names. The configuration binding source
/// generator doesn't use type converters and leaves <see cref="Directory"/> unset, so an app that uses it sets the directory in the action passed to
/// <see cref="ServiceCollectionExtensions.AddDirectoryUploadStore"/>, from a path that it parses, such as with
/// <see cref="DirectoryPath.ParseAbsolute(ReadOnlySpan{char}, PathOptions)"/>.
/// </para>
/// </remarks>
public sealed class DirectoryUploadStoreOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DirectoryUploadStoreOptions"/> class.
    /// </summary>
    public DirectoryUploadStoreOptions()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DirectoryUploadStoreOptions"/> class with the directory and whether other processes use it too.
    /// </summary>
    /// <param name="directory">The directory that the store keeps uploads in (see <see cref="Directory"/>).</param>
    /// <param name="sharingMode">Whether other processes use the directory too (see <see cref="SharingMode"/>).</param>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is not a path with friendly names.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sharingMode"/> is <see cref="StoreSharingMode.Unspecified"/> or not a value of
    /// <see cref="StoreSharingMode"/>.</exception>
    [SetsRequiredMembers]
    public DirectoryUploadStoreOptions(IAbsoluteDirectoryPath directory, StoreSharingMode sharingMode)
    {
        CheckDirectory(directory, nameof(directory));

        // The setter accepts Unspecified while the value is not set, which it still is here.
        CheckSharingMode(sharingMode, nameof(sharingMode));

        Directory = directory;
        SharingMode = sharingMode;
    }

    /// <summary>
    /// Gets or sets the directory that the store keeps uploads in, or <see langword="null"/> while it is not set.
    /// </summary>
    /// <remarks>
    /// Servers that share uploads use the same directory, such as a network share mounted at the same path. The directory must be a path with friendly
    /// names, one that is valid for <see cref="PathOptions.NoUnfriendlyNames"/>, on every platform. A path bound from a configuration is read with
    /// <see cref="PathOptions.None"/>, so a typo such as a trailing space or dot would otherwise make the store create a directory with that name, which
    /// many tools handle badly.
    /// </remarks>
    /// <exception cref="ArgumentException">The value is not a path with friendly names. The message names the path and its problem.</exception>
    [MaybeNull]
    public required IAbsoluteDirectoryPath Directory {
        get => field ?? (IsFrozen ? throw NotSet(nameof(Directory)) : null);
        set {
            EnsureNotFrozen();
            CheckDirectory(value, nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// Gets or sets whether other processes use <see cref="Directory"/> too, such as the other servers of a service that share it
    /// (<see cref="StoreSharingMode.Shared"/>), or only this process does (<see cref="StoreSharingMode.ProcessLocal"/>). It is
    /// <see cref="StoreSharingMode.Unspecified"/> while it is not set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With <see cref="StoreSharingMode.ProcessLocal"/>, the store is process-local (see <see cref="IUploadStore.SharingMode"/>). It holds a lock file in the
    /// directory, <c>uploads.lock</c>, open without sharing from the start of the application until it stops, and the application fails to start, without
    /// waiting, if another process holds it. The process of an overlapped restart, such as of an IIS application pool with overlapped recycling, then fails
    /// to start while the previous process runs.
    /// </para>
    /// <para>
    /// With <see cref="StoreSharingMode.Shared"/>, the store takes no lock, and the <see cref="IShortLivedStore"/> must be shared too (see
    /// <see cref="IShortLivedStore.SharingMode"/>), or the application fails to start.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is <see cref="StoreSharingMode.Unspecified"/>, other than while it is not set, or not a value
    /// of <see cref="StoreSharingMode"/>.</exception>
    public required StoreSharingMode SharingMode {
        get => field is StoreSharingMode.Unspecified && IsFrozen ? throw NotSet(nameof(SharingMode)) : field;
        set {
            EnsureNotFrozen();

            // The binder of a configuration that lacks the value writes back what it read, which leaves the value as it was.
            if (value == field)
                return;

            CheckSharingMode(value, nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// Gets a value indicating whether the options are in use: the directory store checked them (see <see cref="Validate"/>) when it took them, and they can
    /// no longer be changed.
    /// </summary>
    [MemberNotNullWhen(true, nameof(Directory))]
    public bool IsFrozen { get; private set; }

    /// <summary>
    /// Checks the options as the directory store does when it takes them. The required <see cref="Directory"/> and <see cref="SharingMode"/> are set.
    /// </summary>
    /// <exception cref="InvalidOperationException">The options are not valid. The message lists every problem.</exception>
    [MemberNotNull(nameof(Directory))]
    public void Validate()
    {
        var problems = GetProblems();

        if (problems.Count > 0 || Directory is null)
            throw new InvalidOperationException(string.Join(" ", problems));
    }

    /// <summary>
    /// Gets the problems of the options, which <see cref="Validate"/> reports.
    /// </summary>
    internal List<string> GetProblems()
    {
        List<string> problems = [];

        if (Directory is null)
            problems.Add(NotSetMessage(nameof(Directory)));

        if (SharingMode is StoreSharingMode.Unspecified)
            problems.Add(NotSetMessage(nameof(SharingMode)));

        return problems;
    }

    /// <summary>
    /// Checks the options (see <see cref="Validate"/>) and keeps them from being changed from then on, when the directory store takes them. Options that are
    /// frozen already stay as they are.
    /// </summary>
    /// <exception cref="InvalidOperationException">The options are not valid. The message lists every problem.</exception>
    [MemberNotNull(nameof(Directory))]
    internal void Freeze()
    {
        if (IsFrozen)
            return;

        Validate();
        IsFrozen = true;
    }

    private static string NotSetMessage(string property) => $"{nameof(DirectoryUploadStoreOptions)}.{property} is not set. It is required, and has no default.";

    private static InvalidOperationException NotSet(string property) => new(NotSetMessage(property));

    private static void CheckDirectory(IAbsoluteDirectoryPath value, string paramName)
    {
        if (!value.IsValidFor(PathOptions.NoUnfriendlyNames, out string? errorMessage))
        {
            throw new ArgumentException(
                $"{nameof(DirectoryUploadStoreOptions)}.{nameof(Directory)} '{value.PathDisplay}' must be a path with friendly names: {errorMessage}",
                paramName);
        }
    }

    private static void CheckSharingMode(StoreSharingMode value, string paramName)
    {
        if (value is not (StoreSharingMode.ProcessLocal or StoreSharingMode.Shared))
        {
            throw new ArgumentOutOfRangeException(
                paramName, value, $"The value is not {nameof(StoreSharingMode.ProcessLocal)} or {nameof(StoreSharingMode.Shared)}.");
        }
    }

    private void EnsureNotFrozen()
    {
        if (IsFrozen)
            throw new InvalidOperationException($"The {nameof(DirectoryUploadStoreOptions)} are in use, so they can no longer be changed.");
    }
}
