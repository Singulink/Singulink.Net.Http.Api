using System;

namespace Singulink.Net.Http.Api;

/// <summary>
/// Defines a contract for a response type that can store an <see cref="ResponseExceptionInfo" /> instance, representing an exception to be thrown on
/// the client side.
/// </summary>
public interface IStoresResponseException
{
    /// <summary>
    /// Gets the <see cref="ResponseExceptionInfo" /> that this instance represents.
    /// </summary>
    ResponseExceptionInfo Info { get; }
}
