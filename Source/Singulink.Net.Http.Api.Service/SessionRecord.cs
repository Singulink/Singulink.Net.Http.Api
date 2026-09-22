using System.Net;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Represents the values of a session record in the session store that are managed by the session handling service.
/// </summary>
/// <remarks>
/// Session store contexts return an instance of this class when a session is loaded, and receive an instance containing the updated values when a session
/// is refreshed. The refresh info (<see cref="RefreshedUtc"/>, <see cref="ValidFor"/> and <see cref="Generation"/>) is also what gets applied to session
/// tokens when they are created.
/// </remarks>
public class SessionRecord
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SessionRecord"/> class.
    /// </summary>
    /// <param name="device">The device that last refreshed the session.</param>
    /// <param name="ipAddress">The IP address that last refreshed the session, or <see langword="null"/> if it is not known.</param>
    /// <param name="refreshedUtc">The date and time when the session was last refreshed (in UTC).</param>
    /// <param name="validFor">The amount of time after the last refresh that the session remains valid.</param>
    /// <param name="generation">The generation of the session.</param>
    /// <param name="isPersistent">A value indicating whether the session is persistent.</param>
    public SessionRecord(string device, IPAddress? ipAddress, DateTime refreshedUtc, TimeSpan validFor, int generation, bool isPersistent)
    {
        ArgumentNullException.ThrowIfNull(device);

        Device = device;
        IpAddress = ipAddress;
        RefreshedUtc = refreshedUtc;
        ValidFor = validFor;
        Generation = generation;
        IsPersistent = isPersistent;
    }

    /// <summary>
    /// Gets the device that last refreshed the session.
    /// </summary>
    public string Device { get; }

    /// <summary>
    /// Gets the IP address that last refreshed the session, or <see langword="null"/> if it is not known.
    /// </summary>
    public IPAddress? IpAddress { get; }

    /// <summary>
    /// Gets the date and time when the session was last refreshed (in UTC).
    /// </summary>
    public DateTime RefreshedUtc { get; }

    /// <summary>
    /// Gets the amount of time after the last refresh that the session remains valid, after which it expires and can no longer be refreshed.
    /// </summary>
    public TimeSpan ValidFor { get; }

    /// <summary>
    /// Gets the generation of the session. This value is incremented each time the session is refreshed, and a token from the previous generation is
    /// accepted for a short grace period to allow for concurrent refresh requests.
    /// </summary>
    public int Generation { get; }

    /// <summary>
    /// Gets a value indicating whether the session is persistent (i.e. it survives browser or application restarts).
    /// </summary>
    public bool IsPersistent { get; }
}
