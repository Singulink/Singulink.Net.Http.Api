using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Contains information about a sign-in request: the device and IP address making it, and the starting values of the session it creates.
/// </summary>
/// <remarks>
/// Session handling issues the session cookie with these values, so the session record that the application creates stores the same values. The session
/// is created and refreshed at <see cref="SessionRecord.CreatedUtc"/>, with generation 0.
/// </remarks>
public class SignInInfo : SessionRecord
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SignInInfo"/> class. The session is created and refreshed at <paramref name="createdUtc"/>, with
    /// generation 0.
    /// </summary>
    /// <param name="device">The device making the sign-in request.</param>
    /// <param name="ipAddress">The IP address of the device making the sign-in request, or <see langword="null"/> if it is not known.</param>
    /// <param name="createdUtc">The date and time when the session is created (in UTC).</param>
    /// <param name="expiresUtc">The date and time when the session expires unless it is refreshed (in UTC). Session handling uses the configured expiry for
    /// the session's kind after its creation, capped at the end of the configured maximum session lifetime (see
    /// <see cref="SessionHandlingOptions.MaxTempSessionLifetime"/> and <see cref="SessionHandlingOptions.MaxPersistentSessionLifetime"/>).</param>
    /// <param name="isPersistent">A value indicating whether the session is persistent.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="expiresUtc"/> is not after <paramref name="createdUtc"/>.</exception>
    [SetsRequiredMembers]
    public SignInInfo(string device, IPAddress? ipAddress, DateTime createdUtc, DateTime expiresUtc, bool isPersistent)
    {
        if (expiresUtc <= createdUtc)
            throw new ArgumentOutOfRangeException(nameof(expiresUtc), "The expiry time must be after the creation time.");

        Device = device;
        IpAddress = ipAddress;
        CreatedUtc = createdUtc;
        RefreshedUtc = createdUtc;
        ExpiresUtc = expiresUtc;
        Generation = 0;
        IsPersistent = isPersistent;
    }
}
