namespace Singulink.Net.Http.Api.Service.SignIn;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Provides options for <see cref="SignInCodes"/>.
/// </summary>
public sealed class SignInCodeOptions
{
    /// <summary>
    /// Gets or sets how long a sign-in code can be redeemed after it is created. Must be positive. Default is 10 minutes.
    /// </summary>
    /// <remarks>
    /// The lifetime covers the time between the client receiving the code and redeeming it, such as while the client asks the user whether to stay signed
    /// in. The sign-in codes of device links are valid for at least as long as the link can be polled (see <see cref="DeviceLinks.PollAsync"/>).
    /// </remarks>
    public TimeSpan Lifetime {
        get;
        set {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            field = value;
        }
    } = TimeSpan.FromMinutes(10);
}
