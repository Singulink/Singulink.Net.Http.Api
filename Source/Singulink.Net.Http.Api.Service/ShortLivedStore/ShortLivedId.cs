using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Checks the random IDs of short-lived state that the toolkit hands out, such as the IDs that
/// <see cref="ShortLivedStoreExtensions.AddAsync(IShortLivedStore, string, string, TimeSpan, CancellationToken)"/> returns, sign-in codes, device link
/// IDs and passkey ceremony IDs.
/// </summary>
public static class ShortLivedId
{
    /// <summary>
    /// The length of an ID: 256 random bits as base64url characters without padding.
    /// </summary>
    public const int Length = 43;

    private static readonly SearchValues<char> Base64UrlChars = SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_");

    /// <summary>
    /// Gets a value indicating whether the value has the form of an ID: <see cref="Length"/> base64url characters.
    /// </summary>
    public static bool IsWellFormed([NotNullWhen(true)] string? value)
    {
        return value is { Length: Length } && !value.AsSpan().ContainsAnyExcept(Base64UrlChars);
    }

    /// <summary>
    /// Creates a new ID: 32 random bytes, base64url encoded without padding.
    /// </summary>
    internal static string Create() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Hashes a value (such as a user ID) for use in a key, which gives it a fixed length and keeps it out of the store.
    /// </summary>
    internal static string Hash(string value) => Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>
    /// Compares two secrets in time that does not depend on how much of them matches.
    /// </summary>
    internal static bool FixedTimeEquals(string secret, string other)
    {
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(other));
    }
}
