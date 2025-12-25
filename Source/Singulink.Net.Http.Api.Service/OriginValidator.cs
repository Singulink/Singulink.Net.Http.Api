using System.Collections.Immutable;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Validates origins against a list of allowed origins.
/// </summary>
public class OriginValidator : IOriginValidator
{
    private readonly ImmutableArray<string> _allowedOrigins;

    /// <summary>
    /// Initializes a new instance of the <see cref="OriginValidator"/> class with the specified allowed origins.
    /// </summary>
    /// <param name="allowedOrigins">The allowed origins to register. Can use a wildcard at the start of the origin to match subdomains (e.g.
    /// <c>*.example.com</c>).</param>
    public OriginValidator(params string[] allowedOrigins)
    {
        _allowedOrigins = allowedOrigins.ToImmutableArray();
    }

    /// <summary>
    /// Determines whether the specified origin is allowed.
    /// </summary>
    public bool IsAllowed(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri))
            return false;

        string host = uri.Host;

        foreach (string allowedOrigin in _allowedOrigins)
        {
            if (allowedOrigin.StartsWith("*."))
            {
                if (host.EndsWith(allowedOrigin[1..], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (host.Equals(allowedOrigin, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
