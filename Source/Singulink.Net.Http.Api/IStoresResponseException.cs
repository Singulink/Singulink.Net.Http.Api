namespace Singulink.Net.Http.Api;

/// <summary>
/// Defines a contract for a response type that can store exception information, representing an exception to be thrown on the client side.
/// </summary>
public interface IStoresResponseException
{
    /// <summary>
    /// Gets the opaque exception information string that this instance represents.
    /// </summary>
    string ExceptionInfo { get; }
}
