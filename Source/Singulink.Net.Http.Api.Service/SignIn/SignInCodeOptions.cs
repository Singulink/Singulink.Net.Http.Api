namespace Singulink.Net.Http.Api.Service.SignIn;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Provides options for <see cref="SignInCodes"/>.
/// </summary>
/// <remarks>
/// <see cref="SignInCodes"/> checks the options when it takes them, after which they can't be changed, and their setters throw an
/// <see cref="InvalidOperationException"/>.
/// </remarks>
public sealed class SignInCodeOptions
{
    /// <summary>
    /// Gets or sets how long a sign-in code can be redeemed after it is created. Must be greater than zero. Default is 10 minutes.
    /// </summary>
    /// <remarks>
    /// The lifetime covers the time between the client receiving the code and redeeming it, such as while the client asks the user whether to stay signed
    /// in. The sign-in codes of device links are valid for at least as long as the link can be polled (see <see cref="DeviceLinks.PollAsync"/>).
    /// </remarks>
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
    /// Gets a value indicating whether the options are in use: <see cref="SignInCodes"/> checked them (see <see cref="Validate"/>) when it took them, and they
    /// can no longer be changed.
    /// </summary>
    public bool IsFrozen { get; private set; }

    /// <summary>
    /// Checks the options as <see cref="SignInCodes"/> does when it takes them.
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
    /// Checks the options (see <see cref="Validate"/>) and keeps them from being changed from then on, when <see cref="SignInCodes"/> takes them. Options that
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
            throw new InvalidOperationException($"The {nameof(SignInCodeOptions)} are in use, so they can no longer be changed.");
    }
}
