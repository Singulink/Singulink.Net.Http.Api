namespace Singulink.Net.Http.Api.Service.SignIn;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Provides options for <see cref="DeviceLinks"/>.
/// </summary>
/// <remarks>
/// <see cref="DeviceLinks"/> checks the options when it takes them, after which they can't be changed, and their setters throw an
/// <see cref="InvalidOperationException"/>.
/// </remarks>
public sealed class DeviceLinkOptions
{
    /// <summary>
    /// Gets or sets how long a device link waits for approval, which the started link reports as <see cref="DataContracts.DeviceLinkStart.Lifetime"/>, and
    /// how long an approver's wrong codes are remembered after their last lookup. Must be greater than zero. Default is 10 minutes.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public TimeSpan Lifetime {
        get;
        set {
            EnsureNotFrozen();
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            field = value;
        }
    } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Gets a value indicating whether the options are in use: <see cref="DeviceLinks"/> checked them (see <see cref="Validate"/>) when it took them, and they
    /// can no longer be changed.
    /// </summary>
    public bool IsFrozen { get; private set; }

    /// <summary>
    /// Checks the options as <see cref="DeviceLinks"/> does when it takes them.
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
    /// Checks the options (see <see cref="Validate"/>) and keeps them from being changed from then on, when <see cref="DeviceLinks"/> takes them. Options that
    /// are frozen already stay as they are.
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
            throw new InvalidOperationException($"The {nameof(DeviceLinkOptions)} are in use, so they can no longer be changed.");
    }
}
