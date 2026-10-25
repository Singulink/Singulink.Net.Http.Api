namespace Singulink.Net.Http.Api.Service;

[PrefixTestClass]
public sealed class OriginValidatorTests
{
    [TestMethod]
    [DataRow("https://example.com", true)]
    [DataRow("https://EXAMPLE.COM", true)]
    [DataRow("https://example.com:8443", true)]
    [DataRow("https://app.example.com", true)]
    [DataRow("https://deep.app.example.com", true)]
    [DataRow("https://localhost", true)]
    [DataRow("https://localhost:5000", true)]
    [DataRow("http://localhost:5000", true)]
    [DataRow("http://example.com", false)]
    [DataRow("http://app.example.com", false)]
    [DataRow("ftp://example.com", false)]
    [DataRow("wss://example.com", false)]
    [DataRow("https://example.com.evil.test", false)]
    [DataRow("https://notexample.com", false)]
    [DataRow("https://evil.test", false)]
    [DataRow("https://sub.localhost", false)]
    [DataRow("null", false)]
    [DataRow("not a uri", false)]
    [DataRow("", false)]
    public void HostPatterns_MatchHttpsAndLoopbackHttp(string origin, bool expected)
    {
        var validator = new OriginValidator("example.com", "*.example.com", "localhost");

        validator.IsAllowed(origin).ShouldBe(expected);
    }

    [TestMethod]
    [DataRow("127.0.0.1", "http://127.0.0.1:3000")]
    [DataRow("127.0.0.1", "https://127.0.0.1")]
    [DataRow("[::1]", "http://[::1]:3000")]
    [DataRow("[::1]", "https://[::1]")]
    [DataRow("LOCALHOST", "http://localhost")]
    public void LoopbackHostPatterns_MatchHttp(string pattern, string origin)
    {
        new OriginValidator(pattern).IsAllowed(origin).ShouldBeTrue();
    }

    [TestMethod]
    public void NonLoopbackIpAddress_MatchesHttpsOnly()
    {
        var validator = new OriginValidator("203.0.113.10");

        validator.IsAllowed("https://203.0.113.10").ShouldBeTrue();
        validator.IsAllowed("http://203.0.113.10").ShouldBeFalse();
    }

    [TestMethod]
    [DataRow("http://intranet.example.com", "http://intranet.example.com", true)]
    [DataRow("http://intranet.example.com", "http://intranet.example.com:80", true)]
    [DataRow("http://intranet.example.com", "http://intranet.example.com:8080", false)]
    [DataRow("http://intranet.example.com", "https://intranet.example.com", false)]
    [DataRow("HTTPS://example.com", "https://example.com", true)]
    [DataRow("https://example.com", "https://example.com:8443", false)]
    [DataRow("https://example.com", "http://example.com", false)]
    [DataRow("https://example.com:8443", "https://example.com:8443", true)]
    [DataRow("https://example.com:8443", "https://example.com", false)]
    [DataRow("http://*.example.com", "http://app.example.com", true)]
    [DataRow("http://*.example.com", "https://app.example.com", false)]
    [DataRow("http://localhost", "http://localhost:5000", false)]
    [DataRow("http://localhost:5000", "http://localhost:5000", true)]
    [DataRow("http://[::1]:5000", "http://[::1]:5000", true)]
    public void PatternWithScheme_MatchesThatSchemeAndPortOnly(string pattern, string origin, bool expected)
    {
        new OriginValidator(pattern).IsAllowed(origin).ShouldBe(expected);
    }

    [TestMethod]
    [DataRow("example.com:8443", "https://example.com:8443", true)]
    [DataRow("example.com:8443", "https://example.com", false)]
    [DataRow("example.com:8443", "http://example.com:8443", false)]
    [DataRow("localhost:5000", "http://localhost:5000", true)]
    [DataRow("localhost:5000", "https://localhost:5000", true)]
    [DataRow("localhost:5000", "http://localhost:5001", false)]
    [DataRow("[::1]:5000", "http://[::1]:5000", true)]
    public void PatternWithPort_MatchesThatPortOnly(string pattern, string origin, bool expected)
    {
        new OriginValidator(pattern).IsAllowed(origin).ShouldBe(expected);
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

    [TestMethod]
    [DataRow("")]
    [DataRow("*")]
    [DataRow("*.")]
    [DataRow("example.com/")]
    [DataRow("https://example.com/app")]
    [DataRow("ftp://example.com")]
    [DataRow("://example.com")]
    [DataRow("example.com:")]
    [DataRow("example.com:0")]
    [DataRow("example.com:65536")]
    [DataRow("example.com:http")]
    [DataRow("app.*.example.com")]
    [DataRow("::1")]
    public void InvalidPattern_Throws(string pattern)
    {
        Should.Throw<ArgumentException>(() => new OriginValidator(pattern)).ParamName.ShouldBe("allowedOrigins");
    }
}
