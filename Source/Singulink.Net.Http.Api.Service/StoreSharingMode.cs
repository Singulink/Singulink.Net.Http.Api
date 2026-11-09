namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Specifies who sees the data of a store: only the process that uses it, or every process that uses the same storage, such as the other servers of a
/// service.
/// </summary>
public enum StoreSharingMode
{
    /// <summary>
    /// Not specified: the value of an option that was never set. Passing it to a constructor, or setting it once a value is set, throws; setting it while no
    /// value is set leaves the option not set. A store that reports it makes the application fail to start.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// Only this process sees the store's data, such as a store in its memory, or in a directory that only it uses.
    /// </summary>
    ProcessLocal = 1,

    /// <summary>
    /// Every process that uses the same storage sees the store's data, such as the servers of a service that share a database or a network share.
    /// </summary>
    Shared = 2,
}
