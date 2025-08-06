namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Represents a signer used for generating and verifying signatures.
/// </summary>
public interface ISigner
{
    /// <summary>
    /// Generates a signature for the given input data.
    /// </summary>
    byte[] GetSignature(ReadOnlySpan<byte> input);

    /// <summary>
    /// Verifies the signature for the given input data.
    /// </summary>
    public bool Verify(ReadOnlySpan<byte> input, ReadOnlySpan<byte> signature);
}
