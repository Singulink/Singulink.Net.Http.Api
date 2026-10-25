namespace Singulink.Net.Http.Api.DataContracts;

/// <summary>
/// Represents a device link that was just started: the code that the new device shows, the ID and poll secret it checks the link's status with, and how
/// long the link waits for approval. Everything in it goes to the new device only.
/// </summary>
public sealed class DeviceLinkStart
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceLinkStart"/> class.
    /// </summary>
    /// <param name="id">The ID of the link.</param>
    /// <param name="code">The short code that the new device shows.</param>
    /// <param name="pollSecret">The secret that the new device checks the link's status with.</param>
    /// <param name="lifetime">How long the link waits for approval after it started.</param>
    public DeviceLinkStart(string id, string code, string pollSecret, TimeSpan lifetime)
    {
        Id = id;
        Code = code;
        PollSecret = pollSecret;
        Lifetime = lifetime;
    }

    /// <summary>
    /// Gets the ID of the link, which the new device presents together with <see cref="PollSecret"/> to check the link's status.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the short code that the new device shows and the user types on a device where they are signed in, such as <c>K7PX-M3QR</c>.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Gets the secret that the new device presents together with <see cref="Id"/> to check the link's status.
    /// </summary>
    public string PollSecret { get; }

    /// <summary>
    /// Gets how long the link waits for approval after the service started it. Counted from when the client receives the link, it ends later than on the
    /// service by the time the response took to arrive, and does not depend on the client's clock.
    /// </summary>
    public TimeSpan Lifetime { get; }
}
