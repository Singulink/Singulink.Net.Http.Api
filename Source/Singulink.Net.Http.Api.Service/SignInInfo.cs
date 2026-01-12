using System.Net;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Contains information about a sign-in request, such as device info, IP address, session expiry, and persistence.
/// </summary>
public class SignInInfo
{
    internal SignInInfo(string device, IPAddress? ipAddress, TimeSpan sessionExpiry, bool persistent)
    {
        Device = device;
        IpAddress = ipAddress;
        SessionExpiry = sessionExpiry;
        IsPersistent = persistent;
    }

    /// <summary>
    /// Gets the device making the sign-in request.
    /// </summary>
    public string Device { get; }

    /// <summary>
    /// Gets the IP address of the device making the sign-in request.
    /// </summary>
    public IPAddress? IpAddress { get; }

    /// <summary>
    /// Gets the duration after which the session will expire.
    /// </summary>
    public TimeSpan SessionExpiry { get; }

    /// <summary>
    /// Gets a value indicating whether the session should be persistent (i.e., it should survive browser or application restarts).
    /// </summary>
    public bool IsPersistent { get; }
}
