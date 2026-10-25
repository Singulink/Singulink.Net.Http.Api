namespace Singulink.Net.Http.Api.Client;

[PrefixTestClass]
public sealed class PathValidationTests
{
    private static readonly TestApiClient Client = new(new FakeHttpMessageHandler());

    [TestMethod]
    [DataRow("../documents")]
    [DataRow("documents/../../documents")]
    [DataRow("/documents")]
    [DataRow("//other.example.com/documents")]
    [DataRow("\\/other.example.com/documents")]
    [DataRow("https://other.example.com/v1/documents")]
    [DataRow("https://api.example.com/v2/documents")]
    public void PathOutsideBaseAddress_ThrowsArgumentException(string path)
    {
        Should.Throw<ArgumentException>(() => Client.GetUrl(path)).Message.ShouldStartWith($"Path '{path}' leads outside the base address.");
    }

    [TestMethod]
    [DataRow("mailto:someone@example.com")]
    [DataRow("http://api.example.com/v1/documents")]
    [DataRow("documents:batch")]
    public void AbsoluteUriOfOtherScheme_ThrowsArgumentException(string path)
    {
        Should.Throw<ArgumentException>(() => Client.GetUrl(path)).Message.ShouldStartWith($"Path '{path}' is an absolute URI outside the base address.");
    }

    [TestMethod]
    [DataRow("a:b")]
    [DataRow("c:batch")]
    public void InvalidUri_ThrowsArgumentException(string path)
    {
        var ex = Should.Throw<ArgumentException>(() => Client.GetUrl(path));

        ex.ShouldBeOfType<ArgumentException>();
        ex.Message.ShouldStartWith($"Path '{path}' is not a valid relative path or absolute URI.");
        ex.InnerException.ShouldBeOfType<UriFormatException>();
    }

    [TestMethod]
    [DataRow("go?next=https://other.example.com/", "https://api.example.com/v1/go?next=https://other.example.com/")]
    [DataRow("./a:b", "https://api.example.com/v1/a:b")]
    [DataRow("./documents:batch", "https://api.example.com/v1/documents:batch")]
    [DataRow("https://api.example.com/v1/documents", "https://api.example.com/v1/documents")]
    public void PathUnderBaseAddress_IsResolved(string path, string url)
    {
        Client.GetUrl(path).AbsoluteUri.ShouldBe(url);
    }

    [TestMethod]
    public void RawValueWithUrl_IsAccepted()
    {
        string next = "next=https://other.example.com/";

        Client.GetUrl($"go?{next:raw}").AbsoluteUri.ShouldBe("https://api.example.com/v1/go?next=https://other.example.com/");
    }

    [TestMethod]
    public void RawValueOutsideBaseAddress_ThrowsArgumentException()
    {
        string path = "../../other";

        Should.Throw<ArgumentException>(() => Client.GetUrl($"documents/{path:raw}"));
    }

    [TestMethod]
    public void PathWithDotSegmentsInside_IsResolved()
    {
        Client.GetUrl("documents/../folders/1").AbsoluteUri.ShouldBe("https://api.example.com/v1/folders/1");
    }
}
