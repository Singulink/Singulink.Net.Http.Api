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
        // As the options are created when they are bound from a configuration, without their required values.
        var options = Activator.CreateInstance<PasskeyOptions>();

        options.RelyingPartyId.ShouldBeNull();
        options.RelyingPartyName.ShouldBeNull();
        options.AllowLocalhostOrigins.ShouldBeFalse();
        options.CeremonyTimeout.ShouldBe(TimeSpan.FromSeconds(180));
        options.CeremonyLifetime.ShouldBe(TimeSpan.FromMinutes(5));
        options.UserVerification.ShouldBe(PasskeyRequirement.Required);
        options.DiscoverableCredentials.ShouldBe(PasskeyRequirement.Required);
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
                ["Passkeys:UserVerification"] = "Preferred",
                ["Passkeys:DiscoverableCredentials"] = "Discouraged",
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
        options.UserVerification.ShouldBe(PasskeyRequirement.Preferred);
        options.DiscoverableCredentials.ShouldBe(PasskeyRequirement.Discouraged);
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
            services.Count(d => d.ServiceType == typeof(IShortLivedStore) && !d.IsKeyedService).ShouldBe(1);
            services.Count(d => d.ServiceType == typeof(IShortLivedStore) && d.IsKeyedService).ShouldBe(1);

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
    public void EmptyRelyingParty_Throws()
    {
        var options = new PasskeyOptions("example.com", "Example");

        Should.Throw<ArgumentException>(() => options.RelyingPartyId = string.Empty).ParamName.ShouldBe("value");
        Should.Throw<ArgumentException>(() => options.RelyingPartyName = " ").ParamName.ShouldBe("value");
        (options.RelyingPartyId, options.RelyingPartyName).ShouldBe(("example.com", "Example"));
    }

    [TestMethod]
    public void EmptyRelyingParty_InTheConstructor_ThrowsNamingTheParameter()
    {
        Should.Throw<ArgumentException>(() => new PasskeyOptions(string.Empty, "Example")).ParamName.ShouldBe("relyingPartyId");
        Should.Throw<ArgumentException>(() => new PasskeyOptions("example.com", " ")).ParamName.ShouldBe("relyingPartyName");
    }

    [TestMethod]
    public void InvalidOptions_FailValidation()
    {
        Validate(o => o.RelyingPartyId = "https://example.com").ShouldHaveSingleItem().ShouldContain(nameof(PasskeyOptions.RelyingPartyId));
        Validate(o => o.RelyingPartyId = "127.0.0.1").ShouldHaveSingleItem().ShouldContain(nameof(PasskeyOptions.RelyingPartyId));
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
        var options = new PasskeyOptions("example.com", "Example");

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

    [TestMethod]
    public void Options_AreFrozen_WhenTheServiceTakesThem()
    {
        PasskeyOptions? options = null;
        using var fixture = new PasskeyFixture(o => options = o);

        _ = fixture.CreateService();

        OptionsAssertions.ShouldBeFrozen(options.ShouldNotBeNull());
        options.AllowedOrigins.ShouldBe([PasskeyFixture.Origin]);
    }

    [TestMethod]
    public async Task PageOptions_AreFrozen_WhenThePageIsMapped()
    {
        PasskeyPageOptions? options = null;
        await using var host = await PageHost.StartAsync(o => {
            PageHost.ConfigureDefault(o);
            options = o;
        });

        OptionsAssertions.ShouldBeFrozen(options.ShouldNotBeNull());
        options.RedirectSchemes.ShouldBe(["myapp"]);
    }

    [TestMethod]
    public void Validate_ListsEveryProblem_WithoutFreezing()
    {
        // As the options are created when they are bound from a configuration, without their required values.
        var options = Activator.CreateInstance<PasskeyOptions>();
        options.CeremonyLifetime = TimeSpan.FromMinutes(1);

        var ex = Should.Throw<InvalidOperationException>(options.Validate);
        ex.Message.ShouldBe(
            "PasskeyOptions.RelyingPartyId is not set. It is required, and has no default. " +
            "PasskeyOptions.RelyingPartyName is not set. It is required, and has no default. " +
            "At least one origin is required in PasskeyOptions.AllowedOrigins. " +
            "PasskeyOptions.CeremonyLifetime must be at least CeremonyTimeout.");
        options.IsFrozen.ShouldBeFalse();

        // The options can still be set, and Validate and IsFrozen tell the compiler that the required values are not null afterwards: the nullable warnings
        // of these reads fail the build otherwise.
        options.RelyingPartyId = "example.com";
        options.RelyingPartyName = "Example";
        options.AllowedOrigins.Add("https://app.example.com");
        options.CeremonyLifetime = TimeSpan.FromMinutes(5);
        options.Validate();
        (options.RelyingPartyId.Length + options.RelyingPartyName.Length).ShouldBe(18);
        options.IsFrozen.ShouldBeFalse();
    }

    [TestMethod]
    public void IsFrozen_TellsTheCompilerThatTheRequiredValuesAreSet()
    {
        PasskeyOptions? options = null;
        using var fixture = new PasskeyFixture(o => options = o);
        _ = fixture.CreateService();

        var frozen = options.ShouldNotBeNull();
        frozen.IsFrozen.ShouldBeTrue();

        // The nullable warnings of these reads fail the build unless IsFrozen tells the compiler that the values are not null when it is true.
        if (frozen.IsFrozen)
            (frozen.RelyingPartyId.Length + frozen.RelyingPartyName.Length).ShouldBe(18);
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
