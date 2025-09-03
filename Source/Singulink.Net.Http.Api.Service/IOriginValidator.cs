namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Provides validation to determine whether an origin is allowed.
/// </summary>
public interface IOriginValidator
{
    /// <summary>
    /// Determines whether the specified origin is allowed.
    /// </summary>
    bool IsAllowed(string origin);
}
