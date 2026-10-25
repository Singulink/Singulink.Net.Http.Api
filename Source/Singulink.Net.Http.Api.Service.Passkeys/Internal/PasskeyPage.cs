using System.Buffers.Text;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Primitives;

namespace Singulink.Net.Http.Api.Service.Passkeys.Internal;

/// <summary>
/// Serves the passkey hand-off page. The request is checked on the server, and the page script only runs what the server wrote into the page.
/// </summary>
internal sealed class PasskeyPage
{
    public const string ScriptFileName = "passkey-page.js";
    public const string SharedScriptFileName = "passkeys.js";
    public const string StyleFileName = "passkey.css";

    private const int MaxStateLength = 256;

    // The page needs nothing but its own script and stylesheet, and never connects anywhere: the credential leaves it only through the redirect.
    private const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'; " +
        "require-trusted-types-for 'script'";

    private static readonly HtmlEncoder Encoder = HtmlEncoder.Default;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // Schemes that browsers handle themselves rather than handing them to an app.
    private static readonly HashSet<string> ReservedSchemes = new(StringComparer.OrdinalIgnoreCase) {
        "about", "blob", "chrome", "chrome-extension", "data", "edge", "file", "filesystem", "ftp", "http", "https", "intent", "javascript", "mailto",
        "sms", "tel", "vbscript", "view-source", "ws", "wss",
    };

    private readonly string _appName;
    private readonly HashSet<string> _redirectSchemes;
    private readonly bool _allowLoopbackRedirects;
    private readonly PageTemplate _template;

    public PasskeyPage(PasskeyPageOptions options, string appName, PageTemplate template)
    {
        _redirectSchemes = new(StringComparer.OrdinalIgnoreCase);

        foreach (string configuredScheme in options.RedirectSchemes)
        {
            string scheme = configuredScheme.TrimEnd(':');

            if (!IsValidScheme(scheme) || ReservedSchemes.Contains(scheme))
                throw new ArgumentException($"'{configuredScheme}' is not allowed as a redirect scheme: it must be a custom URI scheme.", nameof(options));

            _redirectSchemes.Add(scheme);
        }

        if (_redirectSchemes.Count is 0 && !options.AllowLoopbackRedirects)
            throw new ArgumentException("The passkey page needs at least one redirect scheme or loopback redirects allowed.", nameof(options));

        _allowLoopbackRedirects = options.AllowLoopbackRedirects;
        _appName = appName;
        _template = template;
    }

    public static Task WriteFileAsync(HttpContext context, string contentType, byte[] content)
    {
        return WriteAsync(context, StatusCodes.Status200OK, contentType, content);
    }

    public Task WriteAsync(HttpContext context)
    {
        var query = context.Request.Query;
        var data = new List<(string Name, string Value)> { ("app-name", _appName) };

        string heading, message;
        bool isReady = false;
        int statusCode = StatusCodes.Status400BadRequest;

        if (GetSingle(query["redirect"]) is not { } redirectValue || GetAllowedRedirect(redirectValue) is not { } redirect ||
            query["state"] is { Count: > 1 } || GetSingle(query["state"]) is { Length: > MaxStateLength })
        {
            // Without an allowed callback address the page never navigates away, so the credential cannot be sent anywhere else.
            heading = "This link is not valid";
            message = $"Go back to {_appName} and try again.";
        }
        else
        {
            data.Add(("redirect", redirect));

            if (GetSingle(query["state"]) is { } state)
                data.Add(("state", state));

            string? op = GetSingle(query["op"]);
            string? options = GetSingle(query["options"]) is { } encoded ? DecodeOptions(encoded) : null;

            if (op is not ("get" or "create"))
            {
                // The script returns the error to the app as soon as it loads.
                data.Add(("error-name", "NotSupportedError"));
                data.Add(("error-message", "The passkey operation is not supported."));
                heading = _appName;
                message = $"Returning to {_appName}...";
            }
            else if (options is null)
            {
                data.Add(("error-name", "UnknownError"));
                data.Add(("error-message", "The passkey options are not valid."));
                heading = _appName;
                message = $"Returning to {_appName}...";
            }
            else
            {
                data.Add(("op", op));
                data.Add(("options", options));
                isReady = true;
                statusCode = StatusCodes.Status200OK;

                if (op is "create")
                {
                    heading = "Create a passkey";
                    message = GetUserName(options) is { } userName ?
                        $"Create a passkey for {userName} to sign in to {_appName} with your fingerprint, face or screen lock." :
                        $"Create a passkey to sign in to {_appName} with your fingerprint, face or screen lock.";
                }
                else
                {
                    heading = "Use your passkey";
                    message = $"Continue to use your passkey for {_appName}.";
                }
            }
        }

        string basePath = context.Request.PathBase.Add(context.Request.Path).ToUriComponent().TrimEnd('/');

        var values = new Dictionary<string, string> {
            ["title"] = Encoder.Encode(_appName),
            ["style"] = Encoder.Encode($"{basePath}/{StyleFileName}"),
            ["script"] = Encoder.Encode($"{basePath}/{ScriptFileName}"),
            ["data"] = string.Join(' ', data.Select(d => $"data-{d.Name}=\"{Encoder.Encode(d.Value)}\"")),
            ["heading"] = Encoder.Encode(heading),
            ["message"] = Encoder.Encode(message),
            ["buttons-hidden"] = isReady ? string.Empty : " hidden",
        };

        return WriteAsync(context, statusCode, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(_template.Render(values)));
    }

    /// <summary>
    /// Gets the redirect address to use if the value is an allowed callback address, otherwise <see langword="null"/>.
    /// </summary>
    private string? GetAllowedRedirect(string value)
    {
        // Whitespace, control characters and backslashes are rejected outright, since browsers and .NET may disagree on what such addresses mean.
        if (value.Any(c => c <= ' ' || c is '\\' or '\x7F') || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0)
            return null;

        int schemeEnd = value.IndexOf(':', StringComparison.Ordinal);

        if (_redirectSchemes.Contains(uri.Scheme) && value.AsSpan(0, schemeEnd).Equals(uri.Scheme, StringComparison.OrdinalIgnoreCase))
            return value;

        if (_allowLoopbackRedirects && uri.Scheme is "http" &&
            ((uri.HostNameType is UriHostNameType.IPv4 && uri.Host is "127.0.0.1") ||
             (uri.HostNameType is UriHostNameType.IPv6 && IPAddress.TryParse(uri.DnsSafeHost, out var address) && address.Equals(IPAddress.IPv6Loopback))))
        {
            // The address is passed on as .NET normalized it, so the browser navigates to exactly the host that was checked.
            return uri.AbsoluteUri;
        }

        return null;
    }

    private static string? GetSingle(StringValues values) => values.Count is 1 ? values[0] : null;

    private static string? DecodeOptions(string encoded)
    {
        try
        {
            string json = StrictUtf8.GetString(Base64Url.DecodeFromChars(encoded));
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind is JsonValueKind.Object &&
                document.RootElement.TryGetProperty("challenge", out var challenge) && challenge.ValueKind is JsonValueKind.String)
            {
                return json;
            }
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or JsonException)
        {
            // Not base64url, UTF-8 or JSON.
        }

        return null;
    }

    private static string? GetUserName(string creationOptionsJson)
    {
        using var document = JsonDocument.Parse(creationOptionsJson);

        if (document.RootElement.TryGetProperty("user", out var user) && user.ValueKind is JsonValueKind.Object &&
            user.TryGetProperty("name", out var name) && name.ValueKind is JsonValueKind.String && name.GetString() is { Length: > 0 } value)
        {
            return value;
        }

        return null;
    }

    // RFC 3986 section 3.1: a letter followed by letters, digits, "+", "-" or ".".
    private static bool IsValidScheme(string scheme)
    {
        return scheme.Length > 0 && char.IsAsciiLetter(scheme[0]) && scheme.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.');
    }

    private static Task WriteAsync(HttpContext context, int statusCode, string contentType, byte[] content)
    {
        var response = context.Response;

        response.StatusCode = statusCode;
        response.ContentType = contentType;
        response.ContentLength = content.Length;

        var headers = response.Headers;

        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.CacheControl = "no-store";
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";

        return response.Body.WriteAsync(content, context.RequestAborted).AsTask();
    }
}
