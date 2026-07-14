namespace Singulink.Net.Http.Api;

/// <summary>
/// The common subtype for all <see cref="ISupportsResponseException{TSelf}" /> instantiations.
/// </summary>
public interface ISupportsResponseException;

/// <summary>
/// Represents a type that can have enumerations made of it, such that the enumerations can automatically communicate response exceptions to the client.
/// </summary>
/// <remarks>
/// The type must be registered with <c>ApiExceptionEnumerationEndpointFilterOptions</c> on the server side in order for the enumerations to be handled
/// automatically.
/// </remarks>
public interface ISupportsResponseException<TSelf> : ISupportsResponseException where TSelf : ISupportsResponseException<TSelf>
{
    /// <summary>
    /// Creates a new instance of <typeparamref name="TSelf"/> (or a derived type) that represents a response exception based on the provided
    /// <paramref name="exceptionInfo" />.
    /// </summary>
    /// <remarks>
    /// The value returned from this type must implement <see cref="IStoresResponseException" /> (which will be checked by the caller), and should represent the
    /// provided <paramref name="exceptionInfo" />.
    /// </remarks>
    static abstract TSelf CreateResponseExceptionValue(string exceptionInfo);
}
