using System.Globalization;

namespace Singulink.Net.Http.Api.Client;

[PrefixTestClass]
public sealed class QueryStringTests
{
    private static readonly TestApiClient Client = new(new FakeHttpMessageHandler());

    [TestMethod]
    public void NoParams_HasNoQueryString()
    {
        Client.GetUrl("documents").AbsoluteUri.ShouldBe("https://api.example.com/v1/documents");
    }

    [TestMethod]
    public void Params_AreEscapedAndNullsOmitted()
    {
        var url = Client.GetUrl("documents", [("folderId", 12), ("skip", null), ("q", "a b&c=d/é"), ("sort by", "name")]);

        url.AbsoluteUri.ShouldBe("https://api.example.com/v1/documents?folderId=12&q=a%20b%26c%3Dd%2F%C3%A9&sort%20by=name");
    }

    [TestMethod]
    public void Values_AreFormattedInvariantly()
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("de-DE").Clone();
        culture.NumberFormat.NegativeSign = "~";

        var originalCulture = CultureInfo.CurrentCulture;
        Uri url;

        try
        {
            CultureInfo.CurrentCulture = culture;

            url = Client.GetUrl(
                "values",
                [
                    ("decimal", 1234.5m),
                    ("double", -0.25),
                    ("int", -5),
                    ("bool", true),
                    ("enum", DayOfWeek.Monday),
                    ("guid", new Guid("5d1c27e4-0f2a-4b8e-9c1d-2f6a7b8c9d0e")),
                    ("timeSpan", TimeSpan.FromMinutes(90)),
                ]);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }

        url.Query.ShouldBe(
            "?decimal=1234.5&double=-0.25&int=-5&bool=True&enum=Monday&guid=5d1c27e4-0f2a-4b8e-9c1d-2f6a7b8c9d0e&timeSpan=01%3A30%3A00");
    }

    [TestMethod]
    public void Dates_UseRoundTripFormat()
    {
        var url = Client.GetUrl(
            "values",
            [
                ("dateTime", new DateTime(2026, 10, 20, 13, 45, 30, 123, DateTimeKind.Utc)),
                ("dateTimeOffset", new DateTimeOffset(2026, 10, 20, 13, 45, 30, 123, TimeSpan.FromHours(2))),
            ]);

        url.Query.ShouldBe("?dateTime=2026-10-20T13%3A45%3A30.1230000Z&dateTimeOffset=2026-10-20T13%3A45%3A30.1230000%2B02%3A00");
    }

#if NET
    [TestMethod]
    public void DateOnlyAndTimeOnly_UseRoundTripFormat()
    {
        var url = Client.GetUrl("values", [("date", new DateOnly(2026, 10, 20)), ("time", new TimeOnly(13, 45, 30, 123))]);

        url.Query.ShouldBe("?date=2026-10-20&time=13%3A45%3A30.1230000");
    }
#endif

    [TestMethod]
    public void ByteArray_IsBase64Encoded()
    {
        var url = Client.GetUrl("values", [("bytes", new byte[] { 0xFB, 0xFF, 0xFE })]);

        url.Query.ShouldBe("?bytes=%2B%2F%2F%2B");
    }

    [TestMethod]
    public void UserIdPrecondition_IsAppendedAfterCallParams()
    {
        var client = new TestApiClient(new TestApiClient(new FakeHttpMessageHandler()), userIdPrecondition: "5");

        client.GetUrl("documents", [("page", 2)]).Query.ShouldBe("?page=2&if-userId=5");
        client.GetUrl("documents").Query.ShouldBe("?if-userId=5");
    }

    [TestMethod]
    public void UserIdPrecondition_ValueIsFormattedInvariantly()
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("de-DE").Clone();
        culture.NumberFormat.NegativeSign = "~";
        var originalCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = culture;
            var parent = new TestApiClient(new FakeHttpMessageHandler());

            new TestApiClient(parent, userIdPrecondition: -42).GetUrl("documents").Query.ShouldBe("?if-userId=-42");
            new TestApiClient(parent, userIdPrecondition: new Guid("5d1c27e4-0f2a-4b8e-9c1d-2f6a7b8c9d0e")).GetUrl("documents").Query
                .ShouldBe("?if-userId=5d1c27e4-0f2a-4b8e-9c1d-2f6a7b8c9d0e");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [TestMethod]
    public void UserIdPrecondition_IsEscaped()
    {
        var client = new TestApiClient(new TestApiClient(new FakeHttpMessageHandler()), userIdPrecondition: "a b&c");

        client.GetUrl("documents").Query.ShouldBe("?if-userId=a%20b%26c");
    }

    [TestMethod]
    public void UserIdPrecondition_IsOverriddenByCallParamIgnoringCase()
    {
        var client = new TestApiClient(new TestApiClient(new FakeHttpMessageHandler()), userIdPrecondition: "5");

        client.GetUrl("documents", [("IF-USERID", 7), ("page", 2)]).Query.ShouldBe("?IF-USERID=7&page=2");
    }

    [TestMethod]
    public void UserIdPrecondition_OverriddenByNullCallParam_IsOmitted()
    {
        var client = new TestApiClient(new TestApiClient(new FakeHttpMessageHandler()), userIdPrecondition: "5");

        client.GetUrl("sign-in", [("if-userId", null)]).AbsoluteUri.ShouldBe("https://api.example.com/v1/sign-in");
    }

    [TestMethod]
    public void UserIdPrecondition_IsNotAddedWithoutOne()
    {
        var parent = new TestApiClient(new FakeHttpMessageHandler());
        var child = new TestApiClient(parent);

        parent.GetUrl("documents").Query.ShouldBeEmpty();
        child.GetUrl("documents").Query.ShouldBeEmpty();
    }

    [TestMethod]
    public void PathWithQuery_KeepsExistingQuery()
    {
        Client.GetUrl("documents?sort=name", [("page", 2)]).AbsoluteUri.ShouldBe("https://api.example.com/v1/documents?sort=name&page=2");
    }

    [TestMethod]
    public async Task Request_IsSentToApiUrl()
    {
        var handler = new FakeHttpMessageHandler();
        var client = new TestApiClient(new TestApiClient(handler), userIdPrecondition: "5");

        await client.SendRequestAsync(client.CreateGetRequest("documents", [("folderId", 12)]));

        handler.SingleRequest.Uri.AbsoluteUri.ShouldBe("https://api.example.com/v1/documents?folderId=12&if-userId=5");
    }
}
