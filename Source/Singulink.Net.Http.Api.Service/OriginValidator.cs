using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Validates origins against a list of allowed host patterns.
/// </summary>
public class OriginValidator : IOriginValidator
{
    private readonly ImmutableArray<HostPattern> _patterns;

    /// <summary>
    /// Initializes a new instance of the <see cref="OriginValidator"/> class with the specified allowed host patterns.
    /// </summary>
    /// <param name="allowedOrigins">The allowed host patterns. A host (<c>example.com</c>) or a host with a leading wildcard that matches its subdomains
    /// (<c>*.example.com</c>) matches <c>https</c> origins with that host on any port, and also <c>http</c> origins if the host is a loopback host
    /// (<c>localhost</c>, an address in <c>127.0.0.0/8</c> or <c>[::1]</c>). A pattern that includes a scheme (<c>http://intranet.example.com</c>) only
    /// matches origins with that scheme, on its default port unless the pattern includes a port. A pattern that includes a port (<c>example.com:8443</c>)
    /// only matches origins with that port.</param>
    /// <exception cref="ArgumentException">A pattern is not a valid host pattern.</exception>
    public OriginValidator(params string[] allowedOrigins)
    {
        var patterns = ImmutableArray.CreateBuilder<HostPattern>(allowedOrigins.Length);

        foreach (string allowedOrigin in allowedOrigins)
        {
            if (HostPattern.TryParse(allowedOrigin, out var pattern, out string? error))
                patterns.Add(pattern);
            else
                throw new ArgumentException($"Allowed origin '{allowedOrigin}' is not a valid host pattern. {error}", nameof(allowedOrigins));
        }

        _patterns = patterns.MoveToImmutable();
    }

    /// <summary>
    /// Gets a value indicating whether a pattern matches origins on a loopback host: <c>localhost</c>, an address in <c>127.0.0.0/8</c> or <c>[::1]</c>.
    /// </summary>
    internal bool AllowsLoopbackHosts => _patterns.Any(p => p.IsLoopback);

    /// <summary>
    /// Determines whether the specified origin is allowed.
    /// </summary>
    public bool IsAllowed(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return false;

        foreach (var pattern in _patterns)
        {
            if (pattern.IsMatch(uri))
                return true;
        }

        return false;
    }

    private static bool IsLoopbackHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        if (host.StartsWith('[') && host.EndsWith(']'))
            host = host[1..^1];

        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }

    private sealed class HostPattern
    {
        private readonly string? _scheme;
        private readonly string _host;
        private readonly bool _isWildcard;
        private readonly int? _port;

        private HostPattern(string? scheme, string host, bool isWildcard, int? port)
        {
            _scheme = scheme;
            _host = host;
            _isWildcard = isWildcard;
            _port = port;
        }

        public bool IsLoopback => !_isWildcard && IsLoopbackHost(_host);

        public static bool TryParse(string value, [NotNullWhen(true)] out HostPattern? pattern, [NotNullWhen(false)] out string? error)
        {
            pattern = null;
            string rest = value;
            string? scheme = null;
            int schemeEnd = rest.IndexOf("://", StringComparison.Ordinal);

            if (schemeEnd >= 0)
            {
                scheme = rest[..schemeEnd].ToLowerInvariant();

                if (scheme != Uri.UriSchemeHttps && scheme != Uri.UriSchemeHttp)
                {
                    error = "The scheme must be 'http' or 'https'.";
                    return false;
                }

                rest = rest[(schemeEnd + 3)..];
            }

            int? port = null;
            int portStart = rest.StartsWith('[') ? rest.IndexOf("]:", StringComparison.Ordinal) + 1 : rest.LastIndexOf(':');

            if (portStart > 0)
            {
                if (!int.TryParse(rest.AsSpan(portStart + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int portValue) ||
                    portValue is < 1 or > 65535)
                {
                    error = "The port must be a number from 1 to 65535.";
                    return false;
                }

                port = portValue;
                rest = rest[..portStart];
            }

            bool isWildcard = rest.StartsWith("*.", StringComparison.Ordinal);
            string host = isWildcard ? rest[1..] : rest;
            string hostName = isWildcard ? host[1..] : host;

            if (hostName.StartsWith('[') && hostName.EndsWith(']'))
                hostName = hostName[1..^1];

            if (Uri.CheckHostName(hostName) is UriHostNameType.Unknown)
            {
                error = "The host is not valid.";
                return false;
            }

            pattern = new HostPattern(scheme, host, isWildcard, port);
            error = null;
            return true;
        }

        public bool IsMatch(Uri origin)
        {
            string host = origin.Host;

            if (_isWildcard ? !host.EndsWith(_host, StringComparison.OrdinalIgnoreCase) : !host.Equals(_host, StringComparison.OrdinalIgnoreCase))
                return false;

            if (_scheme is not null)
            {
                if (origin.Scheme != _scheme)
                    return false;
            }
            else if (origin.Scheme != Uri.UriSchemeHttps && !IsLoopbackHost(host))
            {
                return false;
            }

            if (_port is { } port)
                return origin.Port == port;

            return _scheme is null || origin.IsDefaultPort;
        }
    }
}
