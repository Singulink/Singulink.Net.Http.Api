using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Singulink.Net.Http.Api.Client.Passkeys;

/// <summary>
/// The passkey page protocol: the request that opens the page and the response it returns to the app's callback, plus the mapping of the browser's
/// errors (DOMException names) to <see cref="PasskeyErrorReason"/> values, which the WebAssembly path shares.
/// </summary>
/// <remarks>
/// Request: <c>{pageUri}?op={get|create}&amp;options={o}&amp;redirect={r}&amp;state={s}</c>, where <c>o</c> is the base64url (no padding) of the UTF-8
/// options JSON. Response: the callback with <c>state={s}&amp;credential={c}</c>, where <c>c</c> is the base64url of the UTF-8 credential JSON, or
/// <c>state={s}&amp;error={name}&amp;message={text}</c>.
/// </remarks>
internal static class PasskeyPageProtocol
{
    public const string GetOperation = "get";
    public const string CreateOperation = "create";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Creates a random state value (32 bytes, base64url) to tie the page's response to the request.
    /// </summary>
    public static string CreateState() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Builds the address that opens the passkey page. Query parameters already in <paramref name="pageUri"/> are kept.
    /// </summary>
    public static Uri BuildRequestUri(Uri pageUri, string operation, string optionsJson, Uri redirectUri, string state)
    {
        string options = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(optionsJson));
        string query = $"op={operation}&options={options}&redirect={Uri.EscapeDataString(redirectUri.OriginalString)}&state={Uri.EscapeDataString(state)}";

        string address = pageUri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
        string separator = !address.Contains('?') ? "?" : address.EndsWith('?') ? string.Empty : "&";

        return new Uri(address + separator + query + pageUri.Fragment);
    }

    /// <summary>
    /// Reads the passkey page's response from the callback address it returned to. Returns the credential JSON, or throws a
    /// <see cref="PasskeyAuthenticatorException"/> for an error response or a response that doesn't match the request.
    /// </summary>
    public static string ParseResponse(string? responseUri, string expectedState)
    {
        var values = ParseQuery(responseUri);

        if (!values.TryGetValue("state", out string? state) || state != expectedState)
            throw new PasskeyAuthenticatorException(PasskeyErrorReason.Unknown, "The passkey page returned a response that does not belong to this request.");

        if (values.TryGetValue("error", out string? error))
            throw CreateException(error, values.GetValueOrDefault("message"));

        if (!values.TryGetValue("credential", out string? encodedCredential) || encodedCredential.Length is 0)
            throw new PasskeyAuthenticatorException(PasskeyErrorReason.Unknown, "The passkey page returned no credential.");

        try
        {
            return StrictUtf8.GetString(Base64Url.DecodeFromChars(encodedCredential));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new PasskeyAuthenticatorException(PasskeyErrorReason.Unknown, "The passkey page returned a malformed credential.", ex);
        }
    }

    /// <summary>
    /// Creates the exception for an error reported by its DOMException name and message.
    /// </summary>
    public static PasskeyAuthenticatorException CreateException(string? errorName, string? message)
    {
        var reason = GetReason(errorName);

        if (string.IsNullOrEmpty(message))
            message = null;
        else if (reason is PasskeyErrorReason.Unknown && errorName is { Length: > 0 } and not "UnknownError")
            message = $"{errorName}: {message}";

        return new PasskeyAuthenticatorException(reason, message);
    }

    /// <summary>
    /// Creates the exception for an error that the passkeys script reported as <c>"{DOMException name}: {message}"</c>.
    /// </summary>
    public static PasskeyAuthenticatorException CreateScriptException(string scriptMessage)
    {
        // The JS error can arrive in its string form, which has the "Error: " prefix.
        const string ErrorPrefix = "Error: ";
        string text = scriptMessage.StartsWith(ErrorPrefix, StringComparison.Ordinal) ? scriptMessage[ErrorPrefix.Length..] : scriptMessage;
        int separatorIndex = text.IndexOf(": ", StringComparison.Ordinal);

        if (separatorIndex > 0 && IsErrorName(text.AsSpan(0, separatorIndex)))
            return CreateException(text[..separatorIndex], text[(separatorIndex + 2)..]);

        return new PasskeyAuthenticatorException(PasskeyErrorReason.Unknown, text.Length > 0 ? text : null);

        static bool IsErrorName(ReadOnlySpan<char> name)
        {
            if (!name.EndsWith("Error", StringComparison.Ordinal))
                return false;

            foreach (char c in name)
            {
                if (!char.IsAsciiLetter(c))
                    return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Gets the reason for a DOMException name.
    /// </summary>
    public static PasskeyErrorReason GetReason(string? errorName) => errorName switch {
        "AbortError" => PasskeyErrorReason.Cancelled,
        "NotAllowedError" => PasskeyErrorReason.NotAllowed,
        "InvalidStateError" => PasskeyErrorReason.AlreadyOnAuthenticator,

        "NotSupportedError" => PasskeyErrorReason.NotSupported,

        // The authenticator can't satisfy the options, e.g. user verification or a discoverable credential.
        "ConstraintError" => PasskeyErrorReason.RequirementsNotMet,
        "SecurityError" => PasskeyErrorReason.Security,
        _ => PasskeyErrorReason.Unknown,
    };

    /// <summary>
    /// Parses the query of an address into its decoded values. The first value of a repeated name wins.
    /// </summary>
    internal static Dictionary<string, string> ParseQuery(string? uri)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        int queryIndex = uri?.IndexOf('?') ?? -1;

        if (queryIndex < 0)
            return values;

        int fragmentIndex = uri!.IndexOf('#', queryIndex);
        var query = uri.AsSpan(queryIndex + 1, (fragmentIndex < 0 ? uri.Length : fragmentIndex) - queryIndex - 1);

        foreach (var range in query.Split('&'))
        {
            var pair = query[range];

            if (pair.IsEmpty)
                continue;

            int equalsIndex = pair.IndexOf('=');
            string name = Decode(equalsIndex < 0 ? pair : pair[..equalsIndex]);
            string value = equalsIndex < 0 ? string.Empty : Decode(pair[(equalsIndex + 1)..]);

            values.TryAdd(name, value);
        }

        return values;

        // Form decoding ('+' is a space), which is how the page's URLSearchParams encodes values.
        static string Decode(ReadOnlySpan<char> value) => WebUtility.UrlDecode(value.ToString());
    }
}
