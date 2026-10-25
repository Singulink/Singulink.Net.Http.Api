using System.Globalization;

namespace Singulink.Net.Http.Api.Client.InternalServices;

[PrefixTestClass]
public sealed class InterpolatedApiPathHandlerTests
{
    [TestMethod]
    public void String_IsUsedAsIs()
    {
        InterpolatedApiPathHandler path = "items/a b/c?d=e#f";

        path.ToString().ShouldBe("items/a b/c?d=e#f");
    }

    [TestMethod]
    public void Default_IsEmpty()
    {
        default(InterpolatedApiPathHandler).ToString().ShouldBeEmpty();
    }

    [TestMethod]
    public void Literals_AreUsedAsWritten()
    {
        int id = 5;
        InterpolatedApiPathHandler path = $"items/{id}/sub items?sort=a b";

        path.ToString().ShouldBe("items/5/sub items?sort=a b");
    }

    [TestMethod]
    public void Values_AreEscaped()
    {
        string value = "a b/c?d#e%f&g=h+é";
        InterpolatedApiPathHandler path = $"items/{value}/x";

        path.ToString().ShouldBe("items/a%20b%2Fc%3Fd%23e%25f%26g%3Dh%2B%C3%A9/x");
    }

    [TestMethod]
    public void Values_AreFormattedInvariantly()
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("de-DE").Clone();
        culture.NumberFormat.NegativeSign = "~";

        var originalCulture = CultureInfo.CurrentCulture;
        string path;

        try
        {
            CultureInfo.CurrentCulture = culture;

            decimal amount = 1234.5m;
            double ratio = -0.25;
            var date = new DateTime(2026, 10, 20, 13, 45, 30, DateTimeKind.Utc);

            InterpolatedApiPathHandler apiPath = $"{amount}/{ratio}/{date}/{date:yyyy-MM-dd}/{amount:raw}/{ratio:N2}";
            path = apiPath.ToString();
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }

        path.ShouldBe("1234.5/-0.25/2026-10-20T13%3A45%3A30.0000000Z/2026-10-20/1234.5/-0.25");
    }

    [TestMethod]
    public void OtherValues_AreFormattedLikeQueryValues()
    {
        var guid = new Guid("5d1c27e4-0f2a-4b8e-9c1d-2f6a7b8c9d0e");
        InterpolatedApiPathHandler path = $"{DayOfWeek.Monday}/{guid}/{true}";

        path.ToString().ShouldBe("Monday/5d1c27e4-0f2a-4b8e-9c1d-2f6a7b8c9d0e/True");
    }

    [TestMethod]
    public void ByteArray_IsBase64UrlEncodedWithoutPadding()
    {
        byte[] bytes = [0xFB, 0xFF, 0xFE];
        byte[] padded = [0xFB, 0xFF, 0xFE, 0x01];
        InterpolatedApiPathHandler path = $"{bytes}/{padded}/{padded:raw}";

        path.ToString().ShouldBe("-__-/-__-AQ/-__-AQ");
    }

    [TestMethod]
    public void RawFormat_SkipsEscaping()
    {
        string folder = "a b/c";
        InterpolatedApiPathHandler path = $"files/{folder:raw}/{folder}";

        path.ToString().ShouldBe("files/a b/c/a%20b%2Fc");
    }

    [TestMethod]
    public void NullValue_IsEmpty()
    {
        string? value = null;
        InterpolatedApiPathHandler path = $"items/{value}/x/{value:raw}";

        path.ToString().ShouldBe("items//x/");
    }

    [TestMethod]
    public void ToString_CanBeCalledRepeatedly()
    {
        int id = 7;
        InterpolatedApiPathHandler path = $"items/{id}";

        path.ToString().ShouldBe("items/7");
        path.ToString().ShouldBe("items/7");
    }

    [TestMethod]
    public void EscapedValues_StayEscapedInRequestUrl()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler());
        string name = "a/b?c#d";

        client.GetUrl($"items/{name}", [("page", 2)]).AbsoluteUri.ShouldBe("https://api.example.com/v1/items/a%2Fb%3Fc%23d?page=2");
    }

    [TestMethod]
    [DataRow(".")]
    [DataRow("..")]
    public void DotSegmentValue_Throws(string value)
    {
        Should.Throw<ArgumentException>(() => {
            InterpolatedApiPathHandler path = $"items/{value}/x";
        });

        Should.Throw<ArgumentException>(() => {
            InterpolatedApiPathHandler path = $"items/{value:D}";
        });
    }

    [TestMethod]
    public void ValuesWithDots_AreKept()
    {
        string name = "...";
        string extension = ".txt";
        string range = "a..b";
        InterpolatedApiPathHandler path = $"files/{name}/a{extension}/{range}";

        path.ToString().ShouldBe("files/.../a.txt/a..b");
    }

    [TestMethod]
    public void DotSegmentRawValue_IsKept()
    {
        string parent = "..";
        InterpolatedApiPathHandler path = $"items/a/{parent:raw}/b";

        path.ToString().ShouldBe("items/a/../b");
    }
}
