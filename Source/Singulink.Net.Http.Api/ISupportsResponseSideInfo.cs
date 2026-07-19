namespace Singulink.Net.Http.Api;

/// <summary>
/// The common subtype for all <see cref="ISupportsResponseSideInfo{TSelf}" /> instantiations.
/// </summary>
public interface ISupportsResponseSideInfo;

/// <summary>
/// Represents a type that can have enumerations made of it, such that the enumerations can automatically communicate response side info (such as response
/// exceptions and pings) to the client.
/// </summary>
/// <remarks>
/// The type must be registered with <c>ResponseSideInfoEnumerationEndpointFilterOptions</c> on the server side in order for the enumerations to be handled
/// automatically.
/// </remarks>
public interface ISupportsResponseSideInfo<TSelf> : ISupportsResponseSideInfo where TSelf : ISupportsResponseSideInfo<TSelf>
{
    /// <summary>
    /// Creates a new instance of <typeparamref name="TSelf"/> (or a derived type) that represents the provided opaque <paramref name="sideInfo" />.
    /// </summary>
    /// <remarks>
    /// The value returned from this type must implement <see cref="IStoresResponseSideInfo" /> (which will be checked by the caller), and should represent the
    /// provided <paramref name="sideInfo" />.
    /// </remarks>
    static abstract TSelf CreateResponseSideInfoValue(string? sideInfo);
}
