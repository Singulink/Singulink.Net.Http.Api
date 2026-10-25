using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;

namespace Singulink.Net.Http.Api.Service.SignIn;

/// <summary>
/// Generates, normalizes and formats short codes that users read on one screen and type on another, such as device link codes or setup codes: 8 characters
/// from Crockford's base 32 (about 10^12 codes), displayed as <c>K7PX-M3QR</c>.
/// </summary>
/// <remarks>
/// <para>
/// The alphabet has the digits and the letters without I, L, O and U, so codes avoid characters that are easily confused. Typing is forgiving: case does not
/// matter, O is read as 0, I and L are read as 1, and spaces and dashes are ignored.
/// </para>
/// <para>
/// Short codes can be guessed far more easily than random keys: whatever a code unlocks is exposed for as long as the code is valid, to as many wrong
/// attempts as are allowed. There are few enough codes that a plain hash of one can be reversed by hashing every code, while a keyed hash (see <see
/// cref="ComputeHash(string, ReadOnlySpan{byte})"/>) cannot be without its key.
/// </para>
/// </remarks>
public static class ShortCode
{
    /// <summary>
    /// The number of characters in a normalized code.
    /// </summary>
    public const int Length = 8;

    /// <summary>
    /// The minimum length of the key of <see cref="ComputeHash(string, ReadOnlySpan{byte})"/>, in bytes.
    /// </summary>
    public const int MinHashKeyLength = 32;

    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>
    /// Generates a new random code in normalized form (<see cref="Length"/> characters, without the dash).
    /// </summary>
    public static string Generate() => RandomNumberGenerator.GetString(Alphabet, Length);

    /// <summary>
    /// Normalizes a code typed by a user, or returns <see langword="null"/> if the input cannot be a code. The input is case-insensitive, O is read as 0, I
    /// and L are read as 1, and spaces and dashes are ignored.
    /// </summary>
    public static string? Normalize(string input)
    {
        Span<char> code = stackalloc char[Length];
        int length = 0;

        foreach (char c in input)
        {
            if (char.IsWhiteSpace(c) || char.GetUnicodeCategory(c) is UnicodeCategory.DashPunctuation)
                continue;

            char normalized = char.ToUpperInvariant(c) switch {
                'O' => '0',
                'I' or 'L' => '1',
                var other => other,
            };

            if (length is Length || !Alphabet.Contains(normalized))
                return null;

            code[length++] = normalized;
        }

        return length is Length ? new string(code) : null;
    }

    /// <summary>
    /// Formats a code for display, for example <c>K7PX-M3QR</c>. The code is normalized first, so typed input is accepted too.
    /// </summary>
    /// <exception cref="ArgumentException">The value is not a valid code.</exception>
    public static string Format(string code)
    {
        string normalized = Normalize(code) ?? throw new ArgumentException("The value is not a valid code.", nameof(code));
        return string.Concat(normalized.AsSpan(0, 4), "-", normalized.AsSpan(4));
    }

    /// <summary>
    /// Computes the keyed hash (HMAC-SHA256) of a code, as 43 base64url characters. The code is normalized first, so typed input gives the same hash as the
    /// generated code.
    /// </summary>
    /// <param name="code">The code.</param>
    /// <param name="key">The secret key of the hash, at least <see cref="MinHashKeyLength"/> bytes long. Only someone who has the key can tell which code a
    /// hash belongs to.</param>
    /// <exception cref="ArgumentException">The value is not a valid code, or the key is shorter than <see cref="MinHashKeyLength"/> bytes.</exception>
    public static string ComputeHash(string code, ReadOnlySpan<byte> key)
    {
        if (key.Length < MinHashKeyLength)
            throw new ArgumentException($"The key must be at least {MinHashKeyLength} bytes long.", nameof(key));

        string normalized = Normalize(code) ?? throw new ArgumentException("The value is not a valid code.", nameof(code));

        // Normalized codes are ASCII, so each character is one byte.
        Span<byte> codeBytes = stackalloc byte[Length];

        for (int i = 0; i < Length; i++)
            codeBytes[i] = (byte)normalized[i];

        Span<byte> hash = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(key, codeBytes, hash);

        return Base64Url.EncodeToString(hash);
    }
}
