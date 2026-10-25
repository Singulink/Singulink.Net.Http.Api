using System.Security.Cryptography;
using System.Text;

namespace Singulink.Net.Http.Api.Client;

// Runs on .NET Framework too, which covers the netstandard2.0 implementation. The tests that check the client's challenges against the service are in
// the service test project.
[PrefixTestClass]
public sealed class PkceTests
{
    [TestMethod]
    public void Create_ProducesRandomVerifierAndItsS256Challenge()
    {
        var pkce = Pkce.Create();

        pkce.Verifier.Length.ShouldBe(43);
        pkce.Verifier.ShouldAllBe(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_');
        pkce.Challenge.ShouldBe(ToBase64Url(Sha256(Encoding.ASCII.GetBytes(pkce.Verifier))));

        Pkce.Create().Verifier.ShouldNotBe(pkce.Verifier);
    }

    private static byte[] Sha256(byte[] data)
    {
        using var sha256 = SHA256.Create();
        return sha256.ComputeHash(data);
    }

    private static string ToBase64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
