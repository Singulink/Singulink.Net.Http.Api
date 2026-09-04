namespace Singulink.Net.Http.Api.Service;

[PrefixTestClass]
public sealed class OriginValidatorTests
{
    [TestMethod]
    [DataRow("https://example.com", true)]
    [DataRow("http://example.com", true)]
    [DataRow("https://EXAMPLE.COM", true)]
    [DataRow("https://example.com:8443", true)]
    [DataRow("https://app.example.com", true)]
    [DataRow("https://deep.app.example.com", true)]
    [DataRow("https://localhost", true)]
    [DataRow("https://localhost:5000", true)]
    [DataRow("https://example.com.evil.test", false)]
    [DataRow("https://notexample.com", false)]
    [DataRow("https://evil.test", false)]
    [DataRow("https://sub.localhost", false)]
    [DataRow("null", false)]
    [DataRow("not a uri", false)]
    [DataRow("", false)]
    public void IsAllowed(string origin, bool expected)
    {
        var validator = new OriginValidator("example.com", "*.example.com", "localhost");

        validator.IsAllowed(origin).ShouldBe(expected);
    }

    [TestMethod]
    public void Wildcard_DoesNotMatchBareDomain()
    {
        var validator = new OriginValidator("*.example.com");

        validator.IsAllowed("https://app.example.com").ShouldBeTrue();
        validator.IsAllowed("https://example.com").ShouldBeFalse();
    }

    [TestMethod]
    public void NoAllowedOrigins_RejectsEverything()
    {
        var validator = new OriginValidator();

        validator.IsAllowed("https://example.com").ShouldBeFalse();
    }
}
