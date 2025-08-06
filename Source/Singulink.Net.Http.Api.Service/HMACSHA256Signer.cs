using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Signer that uses HMAC with the SHA-256 hashing algorithm and a secret key for signing data.
/// </summary>
public class HMACSHA256Signer : ISigner
{
    private readonly ImmutableArray<byte> _key;

    /// <summary>
    /// Initializes a new instance of the <see cref="HMACSHA256Signer"/> class with the specified secret signing key.
    /// </summary>
    public HMACSHA256Signer(string key)
    {
        _key = Encoding.UTF8.GetBytes("DDSigner" + key).ToImmutableArray();
    }

    /// <inheritdoc cref="ISigner.GetSignature(ReadOnlySpan{byte})"/>
    public byte[] GetSignature(ReadOnlySpan<byte> input)
    {
        return HMACSHA256.HashData(_key.AsSpan(), input);
    }

    /// <inheritdoc cref="ISigner.Verify(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>
    public bool Verify(ReadOnlySpan<byte> input, ReadOnlySpan<byte> signature)
    {
        byte[] computedSignature = GetSignature(input);
        return CryptographicOperations.FixedTimeEquals(computedSignature, signature);
    }
}
