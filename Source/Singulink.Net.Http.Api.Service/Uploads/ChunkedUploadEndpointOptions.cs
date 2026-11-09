namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// Provides the options of an upload endpoint mapped with <c>MapChunkedUpload</c>.
/// </summary>
/// <remarks>
/// The upload endpoint checks the options when it takes them, after which they can't be changed, and their setters throw an
/// <see cref="InvalidOperationException"/>.
/// </remarks>
public sealed class ChunkedUploadEndpointOptions
{
    /// <summary>
    /// Gets or sets the largest upload of the endpoint, in bytes, or <see langword="null"/> for <see cref="ChunkedUploadOptions.MaxSize"/>. Must be at
    /// least zero. Default is <see langword="null"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public long? MaxSize {
        get;
        set {
            EnsureNotFrozen();
            if (value is { } size)
                ArgumentOutOfRangeException.ThrowIfNegative(size, nameof(value));

            field = value;
        }
    }

    /// <summary>
    /// Gets a value indicating whether the options are in use: the upload endpoint checked them (see <see cref="Validate"/>) when it took them, and they can no
    /// longer be changed.
    /// </summary>
    public bool IsFrozen { get; private set; }

    /// <summary>
    /// Checks the options as the upload endpoint does when it takes them.
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
    /// Checks the options (see <see cref="Validate"/>) and keeps them from being changed from then on, when the upload endpoint takes them. Options that are
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
            throw new InvalidOperationException($"The {nameof(ChunkedUploadEndpointOptions)} are in use, so they can no longer be changed.");
    }
}
