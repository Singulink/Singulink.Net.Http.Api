namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Validates whether an origin is trusted.
/// </summary>
public interface IOriginValidator
{
    /// <summary>
    /// Determines whether the specified origin is trusted.
    /// </summary>
    bool IsTrusted(string origin);
}
