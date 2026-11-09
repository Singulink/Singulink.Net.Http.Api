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
        var file = DirectoryPath.GetTemp().CombineFile("ranges.txt", PathOptions.None);
        var options = new CloudflareForwardingOptions(file);

        Should.Throw<ArgumentOutOfRangeException>(() => options.RefreshInterval = TimeSpan.Zero);
        Should.Throw<ArgumentOutOfRangeException>(() => options.RetryInterval = TimeSpan.FromSeconds(-1));
        Should.Throw<ArgumentOutOfRangeException>(() => options.InitialRetryInterval = TimeSpan.Zero);
        Should.Throw<ArgumentOutOfRangeException>(() => options.DownloadTimeout = TimeSpan.Zero);
        Should.Throw<ArgumentOutOfRangeException>(() => options.StaleWarningAge = TimeSpan.FromDays(-1));
        Should.Throw<ArgumentNullException>(() => options.SavedRangesFile = null!);

        options.SavedRangesFile.ShouldBeSameAs(file);
    }

    [TestMethod]
    [DataRow(null, "ranges.txt ", "Entry name ends with a space.")]
    [DataRow(null, "ranges.txt.", "Entry name ends with a dot.")]
    [DataRow(null, " ranges.txt", "Entry name starts with a space.")]
    [DataRow("data.", "ranges.txt", "Entry name ends with a dot.")]
    public void SavedRangesFile_WithAnUnfriendlyName_IsRejected(string? directoryName, string fileName, string error)
    {
        var directory = directoryName is null ? DirectoryPath.GetTemp() : DirectoryPath.GetTemp().CombineDirectory(directoryName, PathOptions.None);
        var unfriendly = directory.CombineFile(fileName, PathOptions.None);
        string message =
            $"CloudflareForwardingOptions.SavedRangesFile '{unfriendly.PathDisplay}' must be a path with friendly names: {error} (Parameter 'value')";

        var friendly = DirectoryPath.GetTemp().CombineFile("ranges.txt", PathOptions.None);
        var options = new CloudflareForwardingOptions(friendly);

        var ex = Should.Throw<ArgumentException>(() => options.SavedRangesFile = unfriendly);
        (ex.ParamName, ex.Message).ShouldBe(("value", message));
        options.SavedRangesFile.ShouldBeSameAs(friendly);

        // The constructor names its own parameter.
        ex = Should.Throw<ArgumentException>(() => new CloudflareForwardingOptions(unfriendly));
        ex.ParamName.ShouldBe("savedRangesFile");
        ex.Message.ShouldBe(
            $"CloudflareForwardingOptions.SavedRangesFile '{unfriendly.PathDisplay}' must be a path with friendly names: {error} " +
            "(Parameter 'savedRangesFile')");

        // A value that was rejected leaves the options without one, so they still fail validation as not set.
        var bound = Activator.CreateInstance<CloudflareForwardingOptions>();
        Should.Throw<ArgumentException>(() => bound.SavedRangesFile = unfriendly);
        bound.SavedRangesFile.ShouldBeNull();
        Should.Throw<InvalidOperationException>(bound.Validate).Message.ShouldStartWith("CloudflareForwardingOptions.SavedRangesFile is not set.");
    }

    [TestMethod]
    public void SavedRangesFile_WithAReservedDeviceName_IsRejectedOnWindows_AndAcceptedElsewhere()
    {
        var file = DirectoryPath.GetTemp().CombineFile("con.txt", PathOptions.None);

        if (OperatingSystem.IsWindows())
        {
            Should.Throw<ArgumentException>(() => new CloudflareForwardingOptions(file)).Message.ShouldBe(
                $"CloudflareForwardingOptions.SavedRangesFile '{file.PathDisplay}' must be a path with friendly names: Invalid reserved device name in " +
                "entry name 'con'. (Parameter 'savedRangesFile')");
        }
        else
        {
            new CloudflareForwardingOptions(file).SavedRangesFile.ShouldBeSameAs(file);
        }
    }

    [TestMethod]
    public void SavedRangesFile_WithFriendlyNames_IsAccepted()
    {
        var file = DirectoryPath.GetTemp().CombineDirectory("cloudflare data", PathOptions.None).CombineFile("ranges.v2.txt", PathOptions.None);
        var options = Activator.CreateInstance<CloudflareForwardingOptions>();

        options.SavedRangesFile = file;

        options.SavedRangesFile.ShouldBeSameAs(file);
        options.Validate();
    }

    [TestMethod]
    public async Task Options_AreFrozen_WhenForwardingTakesThem()
    {
        await using var host = await CloudflareTestHost.StartAsync(new FakeCloudflareHandler(), o => o.TrustedProxyNetworks.Add("10.0.0.0/8"));
        var options = host.App.Services.GetRequiredService<IOptions<CloudflareForwardingOptions>>().Value;

        OptionsAssertions.ShouldBeFrozen(options);
        options.TrustedProxyNetworks.ShouldBe(["10.0.0.0/8"]);

        // Frozen options refuse a path with unfriendly names as they refuse any other value.
        Should.Throw<InvalidOperationException>(() => options.SavedRangesFile = DirectoryPath.GetTemp().CombineFile("ranges.txt ", PathOptions.None))
            .Message.ShouldBe("The CloudflareForwardingOptions are in use, so they can no longer be changed.");

        // The nullable warning of this read fails the build unless IsFrozen tells the compiler that the value is not null when it is true.
        if (options.IsFrozen)
            options.SavedRangesFile.PathDisplay.ShouldBe(host.SavedRangesPath);
    }

    [TestMethod]
    public void Validate_ListsEveryProblem_WithoutFreezing()
    {
        // As the options are created when they are bound from a configuration, without their required values.
        var options = Activator.CreateInstance<CloudflareForwardingOptions>();
        options.TrustedProxyNetworks.Add("not-a-network");

        var ex = Should.Throw<InvalidOperationException>(options.Validate);
        ex.Message.ShouldStartWith(
            "'not-a-network' in CloudflareForwardingOptions.TrustedProxyNetworks is not a valid IP address or network in CIDR notation (for example " +
            "127.0.0.1 or 10.0.0.0/8). CloudflareForwardingOptions.SavedRangesFile is not set.");
        options.IsFrozen.ShouldBeFalse();

        // The nullable warning of this read fails the build unless Validate tells the compiler that the value is not null afterwards.
        options.TrustedProxyNetworks.Clear();
        options.SavedRangesFile = DirectoryPath.GetTemp().CombineFile("ranges.txt", PathOptions.None);
        options.Validate();
        options.SavedRangesFile.Name.ShouldBe("ranges.txt");
    }

    [TestMethod]
    public void Options_Defaults()
    {
        // As the options are created when they are bound from a configuration, without their required values.
        var options = Activator.CreateInstance<CloudflareForwardingOptions>();

        options.SavedRangesFile.ShouldBeNull();
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
    [DataRow(true)]
    [DataRow(false)]
    public async Task MissingSavedRangesFile_FailsStartup(bool downloadRanges)
    {
        var handler = new FakeCloudflareHandler();

        var ex = await Should.ThrowAsync<OptionsValidationException>(
            () => CloudflareTestHost.StartAsync(handler, o => o.DownloadRanges = downloadRanges, setsSavedRangesFile: false));

        ex.Failures.ShouldHaveSingleItem().ShouldStartWith(
            "CloudflareForwardingOptions.SavedRangesFile is not set. It is required, and has no default: Cloudflare's IP ranges are saved to this file");
        handler.RequestCount.ShouldBe(0);
    }

    [TestMethod]
    public async Task SavedRangesFile_IsBoundFromAConfiguration()
    {
        string contentRoot = CloudflareTestHost.CreateContentRoot();
        string path = Path.Combine(contentRoot, "configured", "ranges.txt");

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = contentRoot });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection([new("Cloudflare:SavedRangesFile", path), new("Cloudflare:DownloadTimeout", "00:00:07")]);

        // The reflection-based binder creates the path from the string with the type converter of Singulink.IO.FileSystem.
        builder.Services.AddCloudflareForwarding();
        builder.Services.Configure<CloudflareForwardingOptions>(builder.Configuration.GetSection("Cloudflare"));
        builder.Services.AddHttpClient(CloudflareForwardingOptions.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeCloudflareHandler());

        await using (var app = builder.Build())
        {
            await app.StartAsync();

            var options = app.Services.GetRequiredService<IOptions<CloudflareForwardingOptions>>().Value;
            options.DownloadTimeout.ShouldBe(TimeSpan.FromSeconds(7));
            options.SavedRangesFile.ShouldBe(FilePath.ParseAbsolute(path, PathOptions.None));
            File.Exists(path).ShouldBeTrue();
        }

        Directory.Delete(contentRoot, recursive: true);
    }

    [TestMethod]
    public void SavedRangesFile_BoundFromAConfiguration_WithAnUnfriendlyName_FailsWithTheMessageOfTheSetter()
    {
        var file = DirectoryPath.GetTemp().CombineFile("ranges.txt ", PathOptions.None);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SavedRangesFile"] = file.PathDisplay })
            .Build();

        // The binder sets the property with reflection, which wraps the exception of the setter.
        var ex = Should.Throw<System.Reflection.TargetInvocationException>(() => configuration.Get<CloudflareForwardingOptions>());
        ex.InnerException.ShouldBeOfType<ArgumentException>().Message.ShouldBe(
            $"CloudflareForwardingOptions.SavedRangesFile '{file.PathDisplay}' must be a path with friendly names: Entry name ends with a space. " +
            "(Parameter 'value')");
    }
}
