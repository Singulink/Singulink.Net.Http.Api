using System.Collections.Immutable;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Validates whether an origin is trusted based on a list of trusted origins.
/// </summary>
public class OriginValidator : IOriginValidator
{
    private readonly ImmutableArray<string> _trustedOrigins;

    /// <summary>
    /// Initializes a new instance of the <see cref="OriginValidator"/> class with the specified trusted origins.
    /// </summary>
    /// <param name="trustedOrigins">The trusted origins to register. Can use a wildcard at the start of the origin to match subdomains (e.g.
    /// <c>*.example.com</c>).</param>
    public OriginValidator(params string[] trustedOrigins)
    {
        _trustedOrigins = trustedOrigins.ToImmutableArray();
    }

    /// <summary>
    /// Determines whether the specified origin is trusted.
    /// </summary>
    public bool IsTrusted(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri))
            return false;

        string host = uri.Host;

        foreach (string trustedOrigin in _trustedOrigins)
        {
            if (trustedOrigin.StartsWith('*'))
            {
                if (host.EndsWith(trustedOrigin[1..], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (host.Equals(trustedOrigin, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
