using System.Globalization;

namespace Singulink.Net.Http.Api.Client.Utilities;

/// <summary>
/// Formats values for request URLs: path values and query string parameter values.
/// </summary>
internal static class ApiValueFormatter
{
    /// <summary>
    /// Formats a value with the invariant culture. Returns <see langword="null"/> for a <see langword="null"/> value. Dates use the round-trip format
    /// unless a format is given, and byte arrays are base64 encoded.
    /// </summary>
    public static string? Format(object? value, string? format)
    {
        if (value is null)
            return null;

        if (value is string s)
            return s;

        if (value is byte[] bytes)
            return Convert.ToBase64String(bytes);

        if (value is IFormattable formattable)
        {
#if NET
            if (format is null && value is DateTime or DateTimeOffset or DateOnly or TimeOnly)
#else
            // .NET 6-9 apps use this build too, so their DateOnly and TimeOnly values get the same round-trip format as on .NET 10.
            if (format is null && (value is DateTime or DateTimeOffset || value.GetType().FullName is "System.DateOnly" or "System.TimeOnly"))
#endif
                format = "O";

            return formattable.ToString(format, CultureInfo.InvariantCulture);
        }

        return value.ToString();
    }

    /// <summary>
    /// Formats a path value like <see cref="Format(object?, string?)"/>, except that byte arrays are base64url encoded without padding: the <c>/</c> of
    /// base64 would be escaped as <c>%2F</c>, which ASP.NET Core leaves escaped in route values.
    /// </summary>
    public static string? FormatPathValue(object? value, string? format)
    {
        if (value is byte[] bytes)
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return Format(value, format);
    }
}
