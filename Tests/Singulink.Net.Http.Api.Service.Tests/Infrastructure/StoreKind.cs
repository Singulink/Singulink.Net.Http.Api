namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The short-lived stores that the sign-in, passkey and rate limit tests run against.
/// </summary>
public enum StoreKind
{
    /// <summary>
    /// The default <see cref="MemoryShortLivedStore"/>, with one instance of each service, as on a single server.
    /// </summary>
    Memory,

    /// <summary>
    /// A <see cref="SharedShortLivedStore"/>, with a new instance of each service for every operation, as if each request ran on a different server.
    /// </summary>
    Shared,
}
