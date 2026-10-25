using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Singulink.Net.Http.Api.Service;

[PrefixTestClass]
public sealed class ShortCodeTests
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    [TestMethod]
    public void Generate_ReturnsNormalizedCodesFromAlphabet()
    {
        var codes = Enumerable.Range(0, 1000).Select(_ => ShortCode.Generate()).ToList();

        foreach (string code in codes)
        {
            code.Length.ShouldBe(ShortCode.Length);
            code.ShouldAllBe(c => Alphabet.Contains(c));
            ShortCode.Normalize(code).ShouldBe(code);
        }

        // 1000 codes out of 32^8 should not collide, and should use the whole alphabet.
        codes.Distinct().Count().ShouldBe(codes.Count);
        codes.SelectMany(c => c).Distinct().Count().ShouldBe(Alphabet.Length);
    }

    [TestMethod]
    [DataRow("K7PXM3QR", "K7PXM3QR")]
    [DataRow("K7PX-M3QR", "K7PXM3QR")]
    [DataRow("k7px-m3qr", "K7PXM3QR")]
    [DataRow(" k7px m3qr ", "K7PXM3QR")]
    [DataRow("K-7-P-X-M-3-Q-R", "K7PXM3QR")]
    [DataRow("-K7PX-M3QR-", "K7PXM3QR")]
    [DataRow("K7PX\tM3QR", "K7PXM3QR")]
    [DataRow("K7PX\u2013M3QR", "K7PXM3QR")] // En dash, as some keyboards substitute.
    [DataRow("oO00-iIlL", "00001111")]
    [DataRow("0123-4567", "01234567")]
    [DataRow("89ab-cdef", "89ABCDEF")]
    [DataRow("ghjk-mnpq", "GHJKMNPQ")]
    [DataRow("rstv-wxyz", "RSTVWXYZ")]
    public void Normalize_AcceptsTypedVariations(string input, string expected)
    {
        ShortCode.Normalize(input).ShouldBe(expected);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("K7PX-M3Q")]
    [DataRow("K7PX-M3QRS")]
    [DataRow("K7PX-M3QU")]
    [DataRow("K7PX_M3QR")]
    [DataRow("K7PX.M3QR")]
    [DataRow("K7PX-M3Q*")]
    [DataRow("K7PX-M3Q\u00C9")]
    public void Normalize_RejectsInvalidInput(string input)
    {
        ShortCode.Normalize(input).ShouldBeNull();
    }

    [TestMethod]
    public void Format_InsertsDash()
    {
        ShortCode.Format("K7PXM3QR").ShouldBe("K7PX-M3QR");
        ShortCode.Format("k7px m3qr").ShouldBe("K7PX-M3QR");
        ShortCode.Format("K7PX-M3QR").ShouldBe("K7PX-M3QR");
    }

    [TestMethod]
    public void ComputeHash_IsTheHmacOfTheNormalizedCode()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        string expected = Base64Url.EncodeToString(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes("K7PXM3QR")));

        ShortCode.ComputeHash("K7PXM3QR", key).ShouldBe(expected);
        ShortCode.ComputeHash(" k7px-m3qr ", key).ShouldBe(expected);
        ShortCode.ComputeHash("K7PXM3QR", key).Length.ShouldBe(43);
    }

    [TestMethod]
    public void ComputeHash_DependsOnTheKey()
    {
        string code = ShortCode.Generate();

        ShortCode.ComputeHash(code, RandomNumberGenerator.GetBytes(32)).ShouldNotBe(ShortCode.ComputeHash(code, RandomNumberGenerator.GetBytes(32)));
        ShortCode.ComputeHash(code, new byte[64]).ShouldNotBe(ShortCode.ComputeHash(ShortCode.Generate(), new byte[64]));
    }

    [TestMethod]
    public void ComputeHash_InvalidCodeOrShortKey_Throws()
    {
        Should.Throw<ArgumentException>(() => ShortCode.ComputeHash("K7PX", new byte[32])).ParamName.ShouldBe("code");
        Should.Throw<ArgumentException>(() => ShortCode.ComputeHash("K7PXM3QR", new byte[31])).ParamName.ShouldBe("key");
        ShortCode.MinHashKeyLength.ShouldBe(32);
    }

    [TestMethod]
    public void Format_InvalidCode_Throws()
    {
        Should.Throw<ArgumentException>(() => ShortCode.Format("K7PX"));
        Should.Throw<ArgumentException>(() => ShortCode.Format("K7PX-M3QU"));
    }
}
