namespace Singulink.Net.Http.Api;

/// <summary>
/// Defines a contract for a response type that can store opaque response side info (such as a response exception or ping) to be processed on the client side.
/// </summary>
public interface IStoresResponseSideInfo
{
    /// <summary>
    /// Gets the opaque side info string that this instance represents.
    /// </summary>
    string? SideInfo { get; }
}
