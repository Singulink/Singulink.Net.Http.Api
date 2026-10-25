using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Singulink.Net.Http.Api.Service.SignIn;

/// <summary>
/// Validates PKCE code challenges and checks code verifiers against them (RFC 7636 with the <c>S256</c> method, which is the only one supported).
/// </summary>
/// <remarks>
/// A client creates a random code verifier, sends the challenge derived from it when a flow starts, and proves it started the flow by presenting the verifier
/// when the flow finishes. Whoever intercepts something in between (a one-time code, a credential passed through a redirect) cannot finish the flow without
/// the verifier. <c>Singulink.Net.Http.Api.Client.Pkce</c> creates matching verifiers and challenges on the client.
/// </remarks>
public static class PkceChallenge
{
    /// <summary>
    /// The length of a valid code challenge: the 43 base64url characters (without padding) of a SHA-256 hash.
    /// </summary>
    public const int Length = 43;

    private const int MinVerifierLength = 43;
    private const int MaxVerifierLength = 128;

    private static readonly SearchValues<char> _base64UrlChars = SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_");
    private static readonly SearchValues<char> _verifierChars = SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~");

    // The last character of 32 bytes in base64url only carries 4 bits of data, so its 2 low bits must be zero.
    private static readonly SearchValues<char> _lastChallengeChars = SearchValues.Create("AEIMQUYcgkosw048");

    /// <summary>
    /// Gets a value indicating whether the specified value is a well-formed <c>S256</c> code challenge: <see cref="Length"/> base64url characters encoding a
    /// SHA-256 hash.
    /// </summary>
    public static bool IsValid([NotNullWhen(true)] string? codeChallenge)
    {
        return codeChallenge is { Length: Length } &&
            !codeChallenge.AsSpan().ContainsAnyExcept(_base64UrlChars) &&
            _lastChallengeChars.Contains(codeChallenge[^1]);
    }

    /// <summary>
    /// Gets a value indicating whether the code verifier matches the code challenge, i.e. the challenge is the base64url encoded SHA-256 hash of the
    /// verifier. Returns <see langword="false"/> if either value is malformed. The comparison takes the same time regardless of how much of the hash
    /// matches.
    /// </summary>
    public static bool IsMatch(string codeChallenge, string codeVerifier)
    {
        if (!IsValid(codeChallenge) || codeVerifier.Length is < MinVerifierLength or > MaxVerifierLength ||
            codeVerifier.AsSpan().ContainsAnyExcept(_verifierChars))
        {
            return false;
        }

        Span<byte> verifierBytes = stackalloc byte[codeVerifier.Length];
        Encoding.ASCII.GetBytes(codeVerifier, verifierBytes);

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(verifierBytes, hash);

        Span<byte> challenge = stackalloc byte[SHA256.HashSizeInBytes];

        return Base64Url.TryDecodeFromChars(codeChallenge, challenge, out int length) &&
            length == challenge.Length &&
            CryptographicOperations.FixedTimeEquals(hash, challenge);
    }

    /// <summary>
    /// Throws a <see cref="BadRequestApiException"/> if the value is not a well-formed code challenge (see <see cref="IsValid"/>), so that a request can be
    /// checked up front with the same rules the sign-in building blocks apply.
    /// </summary>
    /// <param name="codeChallenge">The code challenge from the client.</param>
    /// <exception cref="BadRequestApiException">The code challenge is missing or malformed (<see cref="SignInErrorCodes.CodeChallengeInvalid"/>).
    /// </exception>
    public static void EnsureValid([NotNull] string? codeChallenge)
    {
        if (!IsValid(codeChallenge))
            throw new BadRequestApiException("The code challenge is not valid.") { ErrorCode = SignInErrorCodes.CodeChallengeInvalid };
    }
}
