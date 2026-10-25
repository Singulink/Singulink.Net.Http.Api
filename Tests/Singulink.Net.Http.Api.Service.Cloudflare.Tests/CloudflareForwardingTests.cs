using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service;

[PrefixTestClass]
public sealed class CloudflareForwardingTests
{
    private const string CloudflareIpv4 = "173.245.48.10";
    private const string CloudflareIpv6 = "2400:cb00::1";
    private const string DirectIp = "203.0.113.5";
    private const string ClientIp = "198.51.100.7";

    [TestMethod]
    public async Task CloudflareConnection_AppliesForwardedAddressAndScheme()
    {
        await using var host = await CloudflareTestHost.StartAsync(new FakeCloudflareHandler());

        var context = await host.SendAsync(
            CloudflareIpv4,
            ("CF-Connecting-IP", ClientIp),
            ("X-Forwarded-Proto", "https"),
            ("CF-IPCountry", "CA"));

        context.Connection.RemoteIpAddress.ShouldBe(IPAddress.Parse(ClientIp));
        context.Request.Scheme.ShouldBe("https");
        context.Request.Headers["CF-IPCountry"].ToString().ShouldBe("CA");
        context.Request.Headers["CF-Connecting-IP"].ToString().ShouldBe(ClientIp);
    }

    [TestMethod]
    public async Task CloudflareIpv6Connection_AppliesForwardedAddress()
    {
        await using var host = await CloudflareTestHost.StartAsync(new FakeCloudflareHandler());

        var context = await host.SendAsync(CloudflareIpv6, ("CF-Connecting-IP", "2001:db8::5"), ("X-Forwarded-Proto", "http"));

        context.Connection.RemoteIpAddress.ShouldBe(IPAddress.Parse("2001:db8::5"));
        context.Request.Scheme.ShouldBe("http");
    }

    [TestMethod]
    public async Task CloudflareConnection_WithoutForwardedHeaders_IsUnchanged()
    {
        await using var host = await CloudflareTestHost.StartAsync(new FakeCloudflareHandler());

        var context = await host.SendAsync(CloudflareIpv4);

        context.Connection.RemoteIpAddress.ShouldBe(IPAddress.Parse(CloudflareIpv4));
        context.Request.Scheme.ShouldBe("http");
    }

    [TestMethod]
    public async Task DirectConnection_RemovesCloudflareHeadersAndKeepsAddressAndScheme()
    {
        await using var host = await CloudflareTestHost.StartAsync(new FakeCloudflareHandler());

        var context = await host.SendAsync(
            DirectIp,
            ("CF-Connecting-IP", ClientIp),
            ("CF-IPCountry", "CA"),
            ("cf-ray", "abc123"),
            ("X-Forwarded-Proto", "https"),
            ("X-Custom", "kept"));

        context.Connection.RemoteIpAddress.ShouldBe(IPAddress.Parse(DirectIp));
        context.Request.Scheme.ShouldBe("http");

        var headers = context.Request.Headers;
        headers.Keys.ShouldNotContain(k => k.StartsWith("CF-", StringComparison.OrdinalIgnoreCase));
        headers["X-Custom"].ToString().ShouldBe("kept");
        headers["X-Forwarded-Proto"].ToString().ShouldBe("https");
    }

    [TestMethod]
    public async Task Ipv4MappedCloudflareAddress_IsTrusted()
    {
        await using var host = await CloudflareTestHost.StartAsync(new FakeCloudflareHandler());

        var context = await host.SendAsync("::ffff:" + CloudflareIpv4, ("CF-Connecting-IP", ClientIp), ("CF-IPCountry", "CA"));

        context.Connection.RemoteIpAddress.ShouldBe(IPAddress.Parse(ClientIp));
        context.Request.Headers["CF-IPCountry"].ToString().ShouldBe("CA");
    }

    [TestMethod]
    public async Task Ipv4MappedDirectAddress_IsUnchanged()
    {
        await using var host = await CloudflareTestHost.StartAsync(new FakeCloudflareHandler());

        var context = await host.SendAsync("::ffff:" + DirectIp, ("CF-Connecting-IP", ClientIp), ("CF-IPCountry", "CA"));

        context.Connection.RemoteIpAddress.ShouldBe(IPAddress.Parse("::ffff:" + DirectIp));
        context.Request.Headers.ContainsKey("CF-IPCountry").ShouldBeFalse();
    }

    [TestMethod]
    [DataRow("127.0.0.1", "127.0.0.1")]
    [DataRow("127.0.0.0/8", "127.10.20.30")]
    [DataRow("::1", "::1")]
    [DataRow("127.0.0.1", "::ffff:127.0.0.1")]
    [DataRow("::ffff:127.0.0.1", "127.0.0.1")]
    [DataRow("::ffff:10.0.0.0/104", "10.1.2.3")]
    [DataRow("::ffff:10.0.0.0/104", "::ffff:10.1.2.3")]
    [DataRow("::ffff:0.0.0.0/96", "203.0.113.9")]
    [DataRow(" 10.0.0.0/8 ", "10.1.2.3")]
    [DataRow("2001:db8::/32", "2001:db8::5")]
    public async Task TrustedProxyConnection_IsTrusted(string network, string proxyIp)
    {
        await using var host = await CloudflareTestHost.StartAsync(new FakeCloudflareHandler(), o => o.TrustedProxyNetworks.Add(network));

        var context = await host.SendAsync(proxyIp, ("CF-Connecting-IP", ClientIp), ("X-Forwarded-Proto", "https"), ("CF-IPCountry", "CA"));

        context.Connection.RemoteIpAddress.ShouldBe(IPAddress.Parse(ClientIp));
        context.Request.Scheme.ShouldBe("https");
        context.Request.Headers["CF-IPCountry"].ToString().ShouldBe("CA");
    }

    [TestMethod]
    [DataRow("127.0.0.1")]
    [DataRow("::1")]
    public async Task LoopbackConnection_IsDirect_ByDefault(string loopbackIp)
    {
        await using var host = await CloudflareTestHost.StartAsync(new FakeCloudflareHandler());

        var context = await host.SendAsync(loopbackIp, ("CF-Connecting-IP", ClientIp), ("X-Forwarded-Proto", "https"), ("CF-IPCountry", "CA"));

        context.Connection.RemoteIpAddress.ShouldBe(IPAddress.Parse(loopbackIp));
        context.Request.Scheme.ShouldBe("http");
        context.Request.Headers.ContainsKey("CF-IPCountry").ShouldBeFalse();
    }

    [TestMethod]
    public async Task OtherDirectConnections_StayDirect_WhenProxyNetworksAreTrusted()
    {
        await using var host = await CloudflareTestHost.StartAsync(new FakeCloudflareHandler(), o => o.TrustedProxyNetworks.Add("127.0.0.1"));

        var context = await host.SendAsync("10.0.0.5", ("CF-Connecting-IP", ClientIp), ("CF-IPCountry", "CA"));

        context.Connection.RemoteIpAddress.ShouldBe(IPAddress.Parse("10.0.0.5"));
        context.Request.Headers.ContainsKey("CF-IPCountry").ShouldBeFalse();
    }

    [TestMethod]
    [DataRow("not-an-ip")]
    [DataRow("")]
    [DataRow("198.51.100.7, 198.51.100.8")]
    public async Task InvalidConnectingIp_IsIgnored(string connectingIp)
    {
        await using var host = await CloudflareTestHost.StartAsync(new FakeCloudflareHandler());

        var context = await host.SendAsync(CloudflareIpv4, ("CF-Connecting-IP", connectingIp), ("X-Forwarded-Proto", "https"));

        context.Connection.RemoteIpAddress.ShouldBe(IPAddress.Parse(CloudflareIpv4));
        context.Request.Scheme.ShouldBe("https");
    }

    [TestMethod]
    public async Task RepeatedConnectingIpHeader_IsIgnored()
    {
        await using var host = await CloudflareTestHost.StartAsync(new FakeCloudflareHandler());

        var context = await host.SendAsync(CloudflareIpv4, ("CF-Connecting-IP", ClientIp), ("CF-Connecting-IP", "198.51.100.8"));

        context.Connection.RemoteIpAddress.ShouldBe(IPAddress.Parse(CloudflareIpv4));
    }

    [TestMethod]
    [DataRow("ftp")]
    [DataRow("https, http")]
    public async Task UnsupportedForwardedProto_IsIgnored(string proto)
    {
        await using var host = await CloudflareTestHost.StartAsync(new FakeCloudflareHandler());

        var context = await host.SendAsync(CloudflareIpv4, ("CF-Connecting-IP", ClientIp), ("X-Forwarded-Proto", proto));

        context.Connection.RemoteIpAddress.ShouldBe(IPAddress.Parse(ClientIp));
        context.Request.Scheme.ShouldBe("http");
    }

    [TestMethod]
    [DataRow("not an address")]
    [DataRow("10.0.0.0/33")]
    [DataRow("")]
    public async Task InvalidTrustedProxyNetwork_FailsStartup(string entry)
    {
        var ex = await Should.ThrowAsync<OptionsValidationException>(
            () => CloudflareTestHost.StartAsync(new FakeCloudflareHandler(), o => o.TrustedProxyNetworks.Add(entry)));

        ex.Message.ShouldContain(nameof(CloudflareForwardingOptions.TrustedProxyNetworks));
    }

    [TestMethod]
    public async Task DirectConnectionForwardingAnotherAddress_IsLoggedOncePerAddress()
    {
        await using var host = await CloudflareTestHost.StartAsync(new FakeCloudflareHandler());

        await host.SendAsync("127.0.0.1", ("CF-Connecting-IP", ClientIp));
        await host.SendAsync("127.0.0.1", ("CF-Connecting-IP", "198.51.100.99"));
        await host.SendAsync(DirectIp, ("CF-Connecting-IP", ClientIp));

        var warnings = host.Logs.Where(l => l.Level is LogLevel.Warning && l.Message.Contains("CF-Connecting-IP", StringComparison.Ordinal)).ToList();
        warnings.Count.ShouldBe(2);

        warnings[0].Message.ShouldContain("from 127.0.0.1");
        warnings[0].Message.ShouldContain(ClientIp);
        warnings[0].Message.ShouldContain(nameof(CloudflareForwardingOptions.TrustedProxyNetworks));
        warnings[1].Message.ShouldContain("from " + DirectIp);
    }

    [TestMethod]
    public async Task DirectConnectionsForwardingAnotherAddress_AreLoggedForAtMost100Addresses()
    {
        await using var host = await CloudflareTestHost.StartAsync(new FakeCloudflareHandler());

        for (int i = 1; i <= 110; i++)
            await host.SendAsync($"10.0.0.{i}", ("CF-Connecting-IP", ClientIp));

        host.Logs.Count(l => l.Level is LogLevel.Warning && l.Message.Contains("CF-Connecting-IP", StringComparison.Ordinal)).ShouldBe(100);
    }

    [TestMethod]
    public async Task DirectConnectionsForwardingAnotherAddress_AreLoggedAgainAnHourLater()
    {
        var clock = new ManualClock();
        await using var host = await CloudflareTestHost.StartAsync(
            new FakeCloudflareHandler(), configureServices: s => s.AddSingleton<TimeProvider>(clock));

        int CountWarnings() => host.Logs.Count(l => l.Level is LogLevel.Warning && l.Message.Contains("CF-Connecting-IP", StringComparison.Ordinal));

        for (int i = 1; i <= 110; i++)
            await host.SendAsync($"10.0.0.{i}", ("CF-Connecting-IP", ClientIp));

        clock.Advance(TimeSpan.FromMinutes(59));
        await host.SendAsync("10.0.0.1", ("CF-Connecting-IP", ClientIp));
        await host.SendAsync(DirectIp, ("CF-Connecting-IP", ClientIp));
        CountWarnings().ShouldBe(100);

        // A proxy that starts forwarding later, and one that keeps forwarding, are reported in the next hour.
        clock.Advance(TimeSpan.FromMinutes(1));
        await host.SendAsync(DirectIp, ("CF-Connecting-IP", ClientIp));
        await host.SendAsync("10.0.0.1", ("CF-Connecting-IP", ClientIp));
        await host.SendAsync("10.0.0.1", ("CF-Connecting-IP", ClientIp));
        CountWarnings().ShouldBe(102);
    }

    [TestMethod]
    public async Task DirectConnectionForwardingItsOwnAddressOrNone_IsNotLogged()
    {
        await using var host = await CloudflareTestHost.StartAsync(new FakeCloudflareHandler(), o => o.TrustedProxyNetworks.Add("10.0.0.0/8"));

        await host.SendAsync(DirectIp, ("CF-Connecting-IP", DirectIp));
        await host.SendAsync(DirectIp, ("CF-IPCountry", "CA"));
        await host.SendAsync(DirectIp, ("CF-Connecting-IP", "not an address"));
        await host.SendAsync(CloudflareIpv4, ("CF-Connecting-IP", ClientIp));
        await host.SendAsync("10.0.0.5", ("CF-Connecting-IP", ClientIp));

        host.Logs.ShouldNotContain(l => l.Message.Contains("CF-Connecting-IP", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task TrustedProxyNetworks_CanBeSetThroughConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Cloudflare:TrustedProxyNetworks:0"] = "127.0.0.1" })
            .Build();

        await using var host = await CloudflareTestHost.StartAsync(new FakeCloudflareHandler(), o => configuration.GetSection("Cloudflare").Bind(o));

        var context = await host.SendAsync("127.0.0.1", ("CF-Connecting-IP", ClientIp));
        context.Connection.RemoteIpAddress.ShouldBe(IPAddress.Parse(ClientIp));
    }

    [TestMethod]
    public void UseWithoutRegistration_Throws()
    {
        var app = WebApplication.CreateSlimBuilder().Build();

        Should.Throw<InvalidOperationException>(() => app.UseCloudflareForwarding()).Message.ShouldContain("AddCloudflareForwarding");
    }

    [TestMethod]
    public void Options_RejectInvalidValues()
    {
        var options = new CloudflareForwardingOptions();

        Should.Throw<ArgumentOutOfRangeException>(() => options.RefreshInterval = TimeSpan.Zero);
        Should.Throw<ArgumentOutOfRangeException>(() => options.RetryInterval = TimeSpan.FromSeconds(-1));
        Should.Throw<ArgumentOutOfRangeException>(() => options.InitialRetryInterval = TimeSpan.Zero);
        Should.Throw<ArgumentOutOfRangeException>(() => options.DownloadTimeout = TimeSpan.Zero);
        Should.Throw<ArgumentOutOfRangeException>(() => options.StaleWarningAge = TimeSpan.FromDays(-1));
        Should.Throw<ArgumentException>(() => options.SavedRangesPath = " ");

        options.SavedRangesPath = "ranges.txt";
        options.SavedRangesPath = null;
        options.SavedRangesPath.ShouldBeNull();
    }

    [TestMethod]
    public void Options_Defaults()
    {
        var options = new CloudflareForwardingOptions();

        options.SavedRangesPath.ShouldBeNull();
        options.DownloadRanges.ShouldBeTrue();
        options.TrustedProxyNetworks.ShouldBeEmpty();
        options.RefreshInterval.ShouldBe(TimeSpan.FromDays(1));
        options.RetryInterval.ShouldBe(TimeSpan.FromMinutes(5));
        options.InitialRetryInterval.ShouldBe(TimeSpan.FromSeconds(10));
        options.DownloadTimeout.ShouldBe(TimeSpan.FromSeconds(10));
        options.StaleWarningAge.ShouldBe(TimeSpan.FromDays(7));
    }

    [TestMethod]
    public void AddCloudflareForwarding_CanBeCalledTwice()
    {
        var services = new ServiceCollection();
        services.AddCloudflareForwarding();
        services.AddCloudflareForwarding();

        services.Count(d => d.ServiceType.Name is "CloudflareNetworks").ShouldBe(1);
        services.Count(d => d.ImplementationType?.Name is "CloudflareRangeRefresher").ShouldBe(1);
        services.Count(d => d.ImplementationType?.Name is "CloudflareForwardingOptionsValidator").ShouldBe(1);
    }

    [TestMethod]
    public async Task MissingSavedRangesPath_FailsStartup()
    {
        var handler = new FakeCloudflareHandler();

        var ex = await Should.ThrowAsync<OptionsValidationException>(() => CloudflareTestHost.StartAsync(handler, o => o.SavedRangesPath = null));

        ex.Failures.ShouldHaveSingleItem().ShouldStartWith("Cloudflare forwarding requires CloudflareForwardingOptions.SavedRangesPath to be set.");
        handler.RequestCount.ShouldBe(0);
    }

    [TestMethod]
    public async Task MissingSavedRangesPath_WithDownloadsTurnedOff_FailsStartup()
    {
        var ex = await Should.ThrowAsync<OptionsValidationException>(() => CloudflareTestHost.StartAsync(new FakeCloudflareHandler(), o => {
            o.SavedRangesPath = null;
            o.DownloadRanges = false;
        }));

        ex.Failures.ShouldHaveSingleItem().ShouldBe(
            "Cloudflare forwarding requires CloudflareForwardingOptions.SavedRangesPath to be set. Downloads are turned off, so Cloudflare's IP ranges are " +
            "read from this file.");
    }

    [TestMethod]
    public async Task SavedRangesPath_CanBeSetThroughConfiguration()
    {
        string contentRoot = CloudflareTestHost.CreateContentRoot();

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = contentRoot });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection([new("Cloudflare:SavedRangesPath", "configured/ranges.txt")]);

        builder.Services.AddCloudflareForwarding();
        builder.Services.Configure<CloudflareForwardingOptions>(builder.Configuration.GetSection("Cloudflare"));
        builder.Services.AddHttpClient(CloudflareForwardingOptions.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeCloudflareHandler());

        await using (var app = builder.Build())
        {
            await app.StartAsync();
            File.Exists(Path.Combine(contentRoot, "configured", "ranges.txt")).ShouldBeTrue();
        }

        Directory.Delete(contentRoot, recursive: true);
    }
}
