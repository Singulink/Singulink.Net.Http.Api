namespace Singulink.Net.Http.Api.Service;

[PrefixTestClass]
public sealed class CloudflareLocationTests
{
    private const string CloudflareIpv4 = "173.245.48.10";
    private const string DirectIp = "203.0.113.5";

    private static readonly (string Name, string Value)[] LocationHeaders = [
        ("CF-IPCountry", "CA"),
        ("CF-IPContinent", "NA"),
        ("CF-Region", "Ontario"),
        ("CF-Region-Code", "ON"),
        ("CF-IPCity", "Toronto"),
        ("CF-Postal-Code", "M5H"),
        ("CF-Metro-Code", "0"),
        ("CF-IPLatitude", "43.65070"),
        ("CF-IPLongitude", "-79.34720"),
        ("CF-Timezone", "America/Toronto"),
    ];

    [TestMethod]
    public async Task CloudflareConnection_ReturnsTheLocationHeaders()
    {
        await using var host = await CloudflareTestHost.StartAsync(new FakeCloudflareHandler());

        var context = await host.SendAsync(CloudflareIpv4, [("CF-Connecting-IP", "198.51.100.7"), .. LocationHeaders]);
        var location = context.GetClientLocation().ShouldNotBeNull();

        location.CountryCode.ShouldBe("CA");
        location.ContinentCode.ShouldBe("NA");
        location.Region.ShouldBe("Ontario");
        location.RegionCode.ShouldBe("ON");
        location.City.ShouldBe("Toronto");
        location.PostalCode.ShouldBe("M5H");
        location.MetroCode.ShouldBe("0");
        location.Latitude.ShouldBe("43.65070");
        location.Longitude.ShouldBe("-79.34720");
        location.TimeZone.ShouldBe("America/Toronto");
    }

    [TestMethod]
    public async Task ToString_IsCityRegionAndCountryCode()
    {
        await using var host = await CloudflareTestHost.StartAsync(new FakeCloudflareHandler());

        var full = await host.SendAsync(CloudflareIpv4, [("CF-Connecting-IP", "198.51.100.7"), .. LocationHeaders]);
        var countryOnly = await host.SendAsync(CloudflareIpv4, ("CF-IPCountry", "CA"));
        var unknownCountry = await host.SendAsync(CloudflareIpv4, ("CF-IPCountry", "XX"), ("CF-IPContinent", "EU"));
        var cityOnly = await host.SendAsync(CloudflareIpv4, ("CF-IPCountry", "XX"), ("CF-IPCity", "Toronto"));

        full.GetClientLocation().ShouldNotBeNull().ToString().ShouldBe("Toronto, Ontario, CA");
        countryOnly.GetClientLocation().ShouldNotBeNull().ToString().ShouldBe("CA");
        unknownCountry.GetClientLocation().ShouldNotBeNull().ToString().ShouldBeEmpty();
        cityOnly.GetClientLocation().ShouldNotBeNull().ToString().ShouldBe("Toronto");
    }

    [TestMethod]
    public async Task CloudflareConnection_WithCountryOnly_ReturnsTheCountry()
    {
        await using var host = await CloudflareTestHost.StartAsync(new FakeCloudflareHandler());

        var context = await host.SendAsync(CloudflareIpv4, ("CF-IPCountry", "XX"), ("CF-IPCity", string.Empty));
        var location = context.GetClientLocation().ShouldNotBeNull();

        location.CountryCode.ShouldBe("XX");
        location.City.ShouldBeNull();
        location.Region.ShouldBeNull();
    }

    [TestMethod]
    public async Task CloudflareConnection_WithoutLocationHeaders_ReturnsNull()
    {
        await using var host = await CloudflareTestHost.StartAsync(new FakeCloudflareHandler());

        var context = await host.SendAsync(CloudflareIpv4, ("CF-Connecting-IP", "198.51.100.7"), ("CF-Ray", "abc123"));

        context.GetClientLocation().ShouldBeNull();
    }

    [TestMethod]
    public async Task DirectConnection_ReturnsNull()
    {
        await using var host = await CloudflareTestHost.StartAsync(new FakeCloudflareHandler());

        var context = await host.SendAsync(DirectIp, LocationHeaders);

        context.GetClientLocation().ShouldBeNull();
    }

    [TestMethod]
    public async Task TrustedProxyConnection_ReturnsTheLocation()
    {
        await using var host = await CloudflareTestHost.StartAsync(new FakeCloudflareHandler(), o => o.TrustedProxyNetworks.Add("127.0.0.1"));

        var context = await host.SendAsync("127.0.0.1", LocationHeaders);

        context.GetClientLocation().ShouldNotBeNull().City.ShouldBe("Toronto");
    }

    [TestMethod]
    public void RequestWithoutTheMiddleware_ReturnsNull()
    {
        var context = new DefaultHttpContext();

        foreach (var (name, value) in LocationHeaders)
            context.Request.Headers[name] = value;

        context.GetClientLocation().ShouldBeNull();
    }
}
