using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Singulink.Net.Http.Api.Service.Passkeys.Infrastructure;

namespace Singulink.Net.Http.Api.Service.Passkeys;

[PrefixTestClass]
public sealed class PasskeyOptionsTests
{
    [TestMethod]
    public void Defaults()
    {
        var options = new PasskeyOptions();

        options.AllowLocalhostOrigins.ShouldBeFalse();
        options.CeremonyTimeout.ShouldBe(TimeSpan.FromSeconds(180));
        options.CeremonyLifetime.ShouldBe(TimeSpan.FromMinutes(5));
        options.RequireUserVerification.ShouldBeTrue();
        options.RequireDiscoverableCredentials.ShouldBeTrue();
        options.RequireCodeChallenge.ShouldBeTrue();
    }

    [TestMethod]
    public void BindsFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["Passkeys:RelyingPartyId"] = "example.com",
                ["Passkeys:RelyingPartyName"] = "Example",
                ["Passkeys:AllowedOrigins:0"] = "https://app.example.com",
                ["Passkeys:AllowedOrigins:1"] = "https://api.example.com",
                ["Passkeys:AllowLocalhostOrigins"] = "true",
                ["Passkeys:CeremonyTimeout"] = "00:02:00",
                ["Passkeys:CeremonyLifetime"] = "00:10:00",
                ["Passkeys:RequireUserVerification"] = "false",
                ["Passkeys:RequireDiscoverableCredentials"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddPasskeys<InMemoryPasskeyStore>(o => configuration.GetSection("Passkeys").Bind(o));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<PasskeyOptions>>().Value;

        options.RelyingPartyId.ShouldBe("example.com");
        options.RelyingPartyName.ShouldBe("Example");
        options.AllowedOrigins.ShouldBe(["https://app.example.com", "https://api.example.com"]);
        options.AllowLocalhostOrigins.ShouldBeTrue();
        options.CeremonyTimeout.ShouldBe(TimeSpan.FromMinutes(2));
        options.CeremonyLifetime.ShouldBe(TimeSpan.FromMinutes(10));
        options.RequireUserVerification.ShouldBeFalse();
        options.RequireDiscoverableCredentials.ShouldBeFalse();
    }

    [TestMethod]
    public void AddPasskeys_RegistersSignInCodesOnce()
    {
        var afterServices = new ServiceCollection();
        afterServices.AddPasskeys<InMemoryPasskeyStore>(o => o.RelyingPartyName = "Example");
        afterServices.AddSignInCodes(o => o.Lifetime = TimeSpan.FromMinutes(5));

        var beforeServices = new ServiceCollection();
        beforeServices.AddSignInCodes(o => o.Lifetime = TimeSpan.FromMinutes(5));
        beforeServices.AddPasskeys<InMemoryPasskeyStore>(o => o.RelyingPartyName = "Example");

        foreach (var services in new[] { afterServices, beforeServices })
        {
            services.Count(d => d.ServiceType == typeof(SignInCodes)).ShouldBe(1);
            services.Count(d => d.ServiceType == typeof(IShortLivedStore)).ShouldBe(1);

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<SignInCodes>().ShouldBeSameAs(provider.GetRequiredService<SignInCodes>());
            provider.GetRequiredService<IOptions<SignInCodeOptions>>().Value.Lifetime.ShouldBe(TimeSpan.FromMinutes(5));
        }
    }

    [TestMethod]
    public void AddPasskeys_Alone_ProvidesSignInCodes()
    {
        var services = new ServiceCollection();
        services.AddPasskeys<InMemoryPasskeyStore>(o => o.RelyingPartyName = "Example");

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<SignInCodes>().ShouldNotBeNull();
        provider.GetRequiredService<IOptions<SignInCodeOptions>>().Value.Lifetime.ShouldBe(TimeSpan.FromMinutes(10));
    }

    [TestMethod]
    public void ValidOptions_PassValidation()
    {
        Validate(o => { }).ShouldBeEmpty();
        Validate(o => SetOrigins(o, "https://app.example.com/", "https://example.com", "https://a.b.EXAMPLE.com:8443", "android:apk-key-hash:abc"))
            .ShouldBeEmpty();
        Validate(o => {
            SetOrigins(o, "http://localhost:5000");
            o.AllowLocalhostOrigins = true;
            o.RelyingPartyId = "localhost";
        }).ShouldBeEmpty();
    }

    [TestMethod]
    public void InvalidOptions_FailValidation()
    {
        Validate(o => o.RelyingPartyId = string.Empty).ShouldHaveSingleItem().ShouldContain(nameof(PasskeyOptions.RelyingPartyId));
        Validate(o => o.RelyingPartyId = "https://example.com").ShouldHaveSingleItem().ShouldContain(nameof(PasskeyOptions.RelyingPartyId));
        Validate(o => o.RelyingPartyId = "127.0.0.1").ShouldHaveSingleItem().ShouldContain(nameof(PasskeyOptions.RelyingPartyId));
        Validate(o => o.RelyingPartyName = " ").ShouldHaveSingleItem().ShouldContain(nameof(PasskeyOptions.RelyingPartyName));
        Validate(o => SetOrigins(o)).ShouldHaveSingleItem().ShouldContain(nameof(PasskeyOptions.AllowedOrigins));
        Validate(o => SetOrigins(o, "https://app.example.com/path")).ShouldHaveSingleItem().ShouldContain("https://app.example.com/path");
        Validate(o => SetOrigins(o, "app.example.com")).ShouldHaveSingleItem().ShouldContain("app.example.com");
        Validate(o => SetOrigins(o, "ftp://app.example.com")).ShouldHaveSingleItem().ShouldContain("ftp://app.example.com");
        Validate(o => SetOrigins(o, "android:apk-key-hash:")).ShouldHaveSingleItem();
        Validate(o => o.CeremonyLifetime = TimeSpan.FromMinutes(1)).ShouldHaveSingleItem().ShouldContain(nameof(PasskeyOptions.CeremonyLifetime));
    }

    [TestMethod]
    [DataRow("http://localhost:5000")]
    [DataRow("https://example.org")]
    [DataRow("https://notexample.com")]
    [DataRow("https://example.com.evil.test")]
    [DataRow("https://203.0.113.7")]
    public void OriginNotOnRelyingPartyId_FailsValidation(string origin)
    {
        Validate(o => SetOrigins(o, "https://app.example.com", origin)).ShouldHaveSingleItem().ShouldContain($"'{origin}' is not on the relying party ID");
    }

    [TestMethod]
    [DataRow("example.com", true)]
    [DataRow("localhost", false)]
    public void LocalhostOrigins_WithAnotherRelyingPartyId_AreLoggedAsWarning(string rpId, bool warned)
    {
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(logs));

        services.AddPasskeys<InMemoryPasskeyStore>(o => {
            o.RelyingPartyId = rpId;
            o.RelyingPartyName = "Example";
            o.AllowLocalhostOrigins = true;
        });

        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IOptions<PasskeyOptions>>().Value;

        logs.Entries.Any(e => e.Level is LogLevel.Warning && e.Message.Contains(nameof(PasskeyOptions.AllowLocalhostOrigins))).ShouldBe(warned);
    }

    [TestMethod]
    public void CeremonyTimes_OutOfRange_Throw()
    {
        var options = new PasskeyOptions();

        Should.Throw<ArgumentOutOfRangeException>(() => options.CeremonyTimeout = TimeSpan.Zero);
        Should.Throw<ArgumentOutOfRangeException>(() => options.CeremonyTimeout = TimeSpan.FromMilliseconds(uint.MaxValue + 1.0));
        Should.Throw<ArgumentOutOfRangeException>(() => options.CeremonyLifetime = TimeSpan.Zero);
        Should.Throw<ArgumentOutOfRangeException>(() => options.CeremonyLifetime = TimeSpan.FromMinutes(-1));

        options.CeremonyTimeout = TimeSpan.FromMilliseconds(uint.MaxValue);
        options.CeremonyTimeout.ShouldBe(TimeSpan.FromMilliseconds(uint.MaxValue));
    }

    [TestMethod]
    public void InvalidOptions_FailWhenFirstUsed()
    {
        var services = new ServiceCollection();
        services.AddPasskeys<InMemoryPasskeyStore>(o => o.RelyingPartyName = "Example");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Should.Throw<OptionsValidationException>(() => scope.ServiceProvider.GetRequiredService<PasskeyService>());
    }

    [TestMethod]
    public async Task InvalidOptions_FailWhenTheAppStarts()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddPasskeys<InMemoryPasskeyStore>(o => o.RelyingPartyName = "Example");

        await using var app = builder.Build();

        await Should.ThrowAsync<OptionsValidationException>(() => app.StartAsync());
    }

    private static void SetOrigins(PasskeyOptions options, params string[] origins)
    {
        options.AllowedOrigins.Clear();

        foreach (string origin in origins)
            options.AllowedOrigins.Add(origin);
    }

    private static List<string> Validate(Action<PasskeyOptions> configure)
    {
        var services = new ServiceCollection();

        services.AddPasskeys<InMemoryPasskeyStore>(o => {
            o.RelyingPartyId = "example.com";
            o.RelyingPartyName = "Example";
            o.AllowedOrigins.Add("https://app.example.com");
            configure(o);
        });

        using var provider = services.BuildServiceProvider();

        try
        {
            _ = provider.GetRequiredService<IOptions<PasskeyOptions>>().Value;
            return [];
        }
        catch (OptionsValidationException ex)
        {
            return ex.Failures.ToList();
        }
    }
}
