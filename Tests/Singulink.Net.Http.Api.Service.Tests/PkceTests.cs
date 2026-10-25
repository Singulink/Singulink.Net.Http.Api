using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The client's <see cref="Client.Pkce"/> and the service's <see cref="PkceChallenge"/> working together. The client's own tests, which also run on .NET
/// Framework, are in the client test project.
/// </summary>
[PrefixTestClass]
public sealed class PkceTests
{
    // RFC 7636, appendix B.
    private const string RfcVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string RfcChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    [TestMethod]
    public void ClientCreate_ProducesRandomVerifierAndItsS256Challenge()
    {
        var pkce = Client.Pkce.Create();

        pkce.Verifier.Length.ShouldBe(43);
        Base64Url.IsValid(pkce.Verifier, out int verifierLength).ShouldBeTrue();
        verifierLength.ShouldBe(32);

        pkce.Challenge.ShouldBe(Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(pkce.Verifier))));

        Client.Pkce.Create().Verifier.ShouldNotBe(pkce.Verifier);
    }

    [TestMethod]
    public void ClientCreate_IsAcceptedByService()
    {
        for (int i = 0; i < 100; i++)
        {
            var pkce = Client.Pkce.Create();

            PkceChallenge.IsValid(pkce.Challenge).ShouldBeTrue();
            PkceChallenge.IsMatch(pkce.Challenge, pkce.Verifier).ShouldBeTrue();
        }
    }

    [TestMethod]
    public void IsMatch_RfcExample()
    {
        PkceChallenge.IsValid(RfcChallenge).ShouldBeTrue();
        PkceChallenge.IsMatch(RfcChallenge, RfcVerifier).ShouldBeTrue();
    }

    [TestMethod]
    public void IsMatch_WrongOrMalformedVerifier_ReturnsFalse()
    {
        string otherVerifier = Client.Pkce.Create().Verifier;

        PkceChallenge.IsMatch(RfcChallenge, otherVerifier).ShouldBeFalse();
        PkceChallenge.IsMatch(RfcChallenge, RfcVerifier[..^1]).ShouldBeFalse();
        PkceChallenge.IsMatch(RfcChallenge, RfcVerifier + "A").ShouldBeFalse();
        PkceChallenge.IsMatch(RfcChallenge, string.Empty).ShouldBeFalse();
        PkceChallenge.IsMatch(RfcChallenge, new string('a', 42)).ShouldBeFalse();
        PkceChallenge.IsMatch(RfcChallenge, new string('a', 129)).ShouldBeFalse();
        PkceChallenge.IsMatch(RfcChallenge, RfcVerifier[..^1] + "+").ShouldBeFalse();
        PkceChallenge.IsMatch(RfcChallenge, RfcVerifier[..^1] + (char)0xE9).ShouldBeFalse(); // Non-ASCII
    }

    [TestMethod]
    public void IsMatch_LongVerifierWithAllAllowedCharacters()
    {
        string verifier = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~" + new string('~', 62);
        verifier.Length.ShouldBe(128);

        string challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        PkceChallenge.IsMatch(challenge, verifier).ShouldBeTrue();
    }

    [TestMethod]
    public void IsMatch_MalformedChallenge_ReturnsFalse()
    {
        PkceChallenge.IsMatch(RfcChallenge[..^1], RfcVerifier).ShouldBeFalse();

        // Decodes to the same hash, but with non-zero trailing bits.
        PkceChallenge.IsMatch(RfcChallenge[..^1] + "N", RfcVerifier).ShouldBeFalse();
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-c")]
    [DataRow("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cMA")]
    [DataRow("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw+cM")]
    [DataRow("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw/cM")]
    [DataRow("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-c=")]
    [DataRow("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cN")] // Non-zero trailing bits: not what any encoder produces.
    [DataRow("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw cM")]
    public void IsValid_MalformedChallenge_ReturnsFalse(string? challenge)
    {
        PkceChallenge.IsValid(challenge).ShouldBeFalse();
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cN")]
    public void EnsureValid_MalformedChallenge_ThrowsBadRequestWithErrorCode(string? challenge)
    {
        var ex = Should.Throw<BadRequestApiException>(() => PkceChallenge.EnsureValid(challenge));

        ex.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        ex.ErrorCode.ShouldBe(SignInErrorCodes.CodeChallengeInvalid);
    }

    [TestMethod]
    public void EnsureValid_ValidChallenge_DoesNotThrow()
    {
        PkceChallenge.EnsureValid(RfcChallenge);
        PkceChallenge.EnsureValid(Client.Pkce.Create().Challenge);
    }
}
