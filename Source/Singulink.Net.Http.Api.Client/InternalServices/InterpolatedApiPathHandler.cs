using System.Runtime.CompilerServices;
using System.Text;
using Singulink.Net.Http.Api.Client.Utilities;

namespace Singulink.Net.Http.Api.Client.InternalServices;

/// <summary>
/// Provides a custom interpolated string handler for API paths, which are relative to a client's base address.
/// </summary>
/// <remarks>
/// <para>
/// This type exists for the compiler's interpolated string support: an interpolated string passed as a path, such as <c>$"documents/{id}"</c>, is built
/// with it. The literal parts are used as written and each value is formatted with the invariant culture and then escaped as URI data, so characters such
/// as <c>/</c>, <c>?</c> and <c>#</c> in a value don't start a new segment, a query or a fragment. Values are formatted the same way as query string
/// parameter values, with dates in the round-trip format unless a format is given, except that byte arrays are base64url encoded without padding. The
/// <c>raw</c> format, as in <c>$"files/{path:raw}"</c>, skips the escaping. A value other than a <c>raw</c> one cannot be <c>.</c> or <c>..</c>, which
/// escaping leaves as they are, so that a value can't change which segments the path has.
/// </para>
/// <para>
/// ASP.NET Core unescapes route values except for <c>%2F</c>, so a <c>/</c> in a value reaches the service's endpoint as <c>%2F</c>.
/// </para>
/// <para>
/// A <see cref="string"/> converts to a path as is, without escaping.
/// </para>
/// </remarks>
[InterpolatedStringHandler]
public readonly struct InterpolatedApiPathHandler
{
    private const string RawFormat = "raw";

    private readonly StringBuilder? _builder;
    private readonly string? _path;

    /// <summary>
    /// Initializes a new instance of the <see cref="InterpolatedApiPathHandler"/> struct for an interpolated string.
    /// </summary>
    /// <param name="literalLength">The total length of the literal parts.</param>
    /// <param name="formattedCount">The number of values.</param>
    public InterpolatedApiPathHandler(int literalLength, int formattedCount)
    {
        _builder = new StringBuilder(literalLength + (formattedCount * 8));
    }

    private InterpolatedApiPathHandler(string path)
    {
        _path = path;
    }

    /// <summary>
    /// Converts a string to a path without escaping it.
    /// </summary>
    public static implicit operator InterpolatedApiPathHandler(string path) => new(path);

    /// <summary>
    /// Appends a literal part to the path as written.
    /// </summary>
    public void AppendLiteral(string value) => _builder!.Append(value);

    /// <summary>
    /// Appends a formatted and escaped value to the path.
    /// </summary>
    public void AppendFormatted<T>(T value) => AppendFormatted(value, null);

    /// <summary>
    /// Appends a value to the path with the specified format, escaped unless the format is <c>raw</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The format is not <c>raw</c> and the value is formatted as <c>.</c> or <c>..</c>.</exception>
    public void AppendFormatted<T>(T value, string? format)
    {
        if (format is RawFormat)
        {
            _builder!.Append(ApiValueFormatter.FormatPathValue(value, null));
            return;
        }

        if (ApiValueFormatter.FormatPathValue(value, format) is { Length: > 0 } formatted)
        {
            // Escaping leaves dots as they are, and a "." or ".." segment would remove segments from the path.
            if (formatted is "." or "..")
                throw new ArgumentException($"A value in a path cannot be '{formatted}'.", nameof(value));

            _builder!.Append(Uri.EscapeDataString(formatted));
        }
    }

    /// <summary>
    /// Returns the path.
    /// </summary>
    public override string ToString() => _builder?.ToString() ?? _path ?? string.Empty;
}
