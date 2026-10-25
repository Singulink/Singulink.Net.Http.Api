using System.Security.Cryptography;
using System.Text;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// A PKCE code verifier and its <c>S256</c> code challenge (RFC 7636). The client sends the challenge when it starts a sign-in flow and presents the verifier
/// when it finishes, so whatever the service returns in between (such as a one-time sign-in code) is useless to anyone who intercepts it.
/// </summary>
/// <remarks>
/// Each instance has its own random verifier. The challenge reveals nothing about the verifier, which proves that the party that finishes a flow is the
/// one that started it only as long as no one else knows it.
/// </remarks>
public sealed class Pkce
{
    private const int VerifierByteCount = 32;

    private Pkce(string verifier, string challenge)
    {
        Verifier = verifier;
        Challenge = challenge;
    }

    /// <summary>
    /// Gets the code verifier: 32 random bytes, base64url encoded without padding (43 characters).
    /// </summary>
    public string Verifier { get; }

    /// <summary>
    /// Gets the code challenge: the SHA-256 hash of the verifier's ASCII characters, base64url encoded without padding (43 characters).
    /// </summary>
    public string Challenge { get; }

    /// <summary>
    /// Creates a new random code verifier and its code challenge.
    /// </summary>
    public static Pkce Create()
    {
        string verifier = ToBase64Url(GetRandomBytes(VerifierByteCount));
        string challenge = ToBase64Url(ComputeSha256(Encoding.ASCII.GetBytes(verifier)));

        return new Pkce(verifier, challenge);
    }

    private static byte[] GetRandomBytes(int count)
    {
#if NET6_0_OR_GREATER
        return RandomNumberGenerator.GetBytes(count);
#else
        byte[] bytes = new byte[count];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);

        return bytes;
#endif
    }

    private static byte[] ComputeSha256(byte[] data)
    {
#if NET6_0_OR_GREATER
        return SHA256.HashData(data);
#else
        using var sha256 = SHA256.Create();
        return sha256.ComputeHash(data);
#endif
    }

    private static string ToBase64Url(byte[] bytes)
    {
#if NET9_0_OR_GREATER
        return System.Buffers.Text.Base64Url.EncodeToString(bytes);
#else
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
#endif
    }
}
