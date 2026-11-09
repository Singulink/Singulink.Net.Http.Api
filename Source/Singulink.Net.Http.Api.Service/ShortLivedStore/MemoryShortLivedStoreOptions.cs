namespace Singulink.Net.Http.Api.Service;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Provides options for <see cref="MemoryShortLivedStore"/>.
/// </summary>
/// <remarks>
/// The memory store checks the options when it takes them, after which they can't be changed, and their setters throw an
/// <see cref="InvalidOperationException"/>.
/// </remarks>
public sealed class MemoryShortLivedStoreOptions
{
    /// <summary>
    /// The default value of <see cref="MaxSize"/>: 256 MiB.
    /// </summary>
    public const long DefaultMaxSize = 256L * 1024 * 1024;

    /// <summary>
    /// The default value of <see cref="MaxSize"/> in the named options of a key of <see cref="ShortLivedStoreKeys"/>: 64 MiB.
    /// </summary>
    public const long DefaultKeyedMaxSize = 64L * 1024 * 1024;

    /// <summary>
    /// Gets or sets the approximate amount of memory, in bytes, that the store's values and counters can take up. Must be at least 1. Default is
    /// <see cref="DefaultMaxSize"/> (256 MiB), and <see cref="DefaultKeyedMaxSize"/> (64 MiB) in the named options of a key of
    /// <see cref="ShortLivedStoreKeys"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The size of an entry is estimated as 2 bytes per character of its key and value, plus about 120 bytes. Counters take up the same room as values.
    /// </para>
    /// <para>
    /// When adding a value or a counter would exceed the size, expired entries are removed first. If that does not make enough room, the operation throws
    /// a <see cref="ServiceUnavailableApiException"/> (<see cref="ApiErrorCodes.ShortLivedStoreFull"/>) and a warning is logged, at most once a minute.
    /// Reading, taking and removing values, and changing existing counters, always work.
    /// </para>
    /// <para>
    /// Everything that uses one store shares its size. While the app's store is the default memory store, each key of <see cref="ShortLivedStoreKeys"/> has a
    /// memory store of its own, with the named options of its key, such as
    /// <c>services.Configure&lt;MemoryShortLivedStoreOptions&gt;(ShortLivedStoreKeys.Uploads, o =&gt; o.MaxSize = ...)</c>, and the options without a name
    /// set the store of the app's own values: four memory stores, one of up to 256 MiB and three of up to 64 MiB by default.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public long MaxSize {
        get;
        set {
            EnsureNotFrozen();
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, 0);
            field = value;
        }
    } = DefaultMaxSize;

    /// <summary>
    /// Gets a value indicating whether the options are in use: the memory store checked them (see <see cref="Validate"/>) when it took them, and they can no
    /// longer be changed.
    /// </summary>
    public bool IsFrozen { get; private set; }

    /// <summary>
    /// Checks the options as the memory store does when it takes them.
    /// </summary>
    /// <remarks>
    /// Each value is checked when it is set, and no rule involves more than one of them, so this does not throw.
    /// </remarks>
    public void Validate()
    {
        var problems = GetProblems();

        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(" ", problems));
    }

    /// <summary>
    /// Checks the options (see <see cref="Validate"/>) and keeps them from being changed from then on, when the memory store takes them. Options that are
    /// frozen already stay as they are.
    /// </summary>
    /// <exception cref="InvalidOperationException">The options are not valid. The message lists every problem.</exception>
    internal void Freeze()
    {
        if (IsFrozen)
            return;

        Validate();
        IsFrozen = true;
    }

    /// <summary>
    /// Gets the problems of the options, which <see cref="Validate"/> reports.
    /// </summary>
    internal List<string> GetProblems()
    {
        return [];
    }

    private void EnsureNotFrozen()
    {
        if (IsFrozen)
            throw new InvalidOperationException($"The {nameof(MemoryShortLivedStoreOptions)} are in use, so they can no longer be changed.");
    }
}
