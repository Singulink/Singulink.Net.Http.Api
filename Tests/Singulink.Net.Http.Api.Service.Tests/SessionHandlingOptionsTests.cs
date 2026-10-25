using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service;

[PrefixTestClass]
public sealed class SessionHandlingOptionsTests
{
    [TestMethod]
    public void Defaults()
    {
        var options = new SessionHandlingOptions();

        options.SessionCookieName.ShouldBe("__Host-session-token");
        options.CookieDomain.ShouldBeNull();
        options.CookiePath.ShouldBe("/");
        options.DataProtectionPurpose.ShouldBeNull();
        options.UserIdPreconditionQueryName.ShouldBe("if-userId");
        options.ForcedAccessOptions.ShouldBe(SessionAccessOptions.None);
        options.RefreshAfter.ShouldBe(TimeSpan.FromMinutes(10));
        options.MaxDeviceLength.ShouldBe(512);
        options.PreviousGenerationGracePeriod.ShouldBe(TimeSpan.FromMinutes(2));
        options.TempSessionExpiry.ShouldBe(TimeSpan.FromDays(1));
        options.PersistentSessionExpiry.ShouldBe(TimeSpan.FromDays(30));
        options.MaxTempSessionLifetime.ShouldBeNull();
        options.MaxPersistentSessionLifetime.ShouldBeNull();
    }

    [TestMethod]
    public void EmptyNames_Throw()
    {
        var options = new SessionHandlingOptions();

        Should.Throw<ArgumentException>(() => options.SessionCookieName = string.Empty);
        Should.Throw<ArgumentException>(() => options.CookiePath = string.Empty);
        Should.Throw<ArgumentException>(() => options.UserIdPreconditionQueryName = string.Empty);
        Should.Throw<ArgumentException>(() => options.DataProtectionPurpose = string.Empty);

        options.DataProtectionPurpose = "Custom";
        options.DataProtectionPurpose = null;
        options.DataProtectionPurpose.ShouldBeNull();
    }

    [TestMethod]
    public void UndefinedForcedAccessOptions_Throw()
    {
        var options = new SessionHandlingOptions();

        Should.Throw<ArgumentException>(() => options.ForcedAccessOptions = (SessionAccessOptions)64);
        options.ForcedAccessOptions.ShouldBe(SessionAccessOptions.None);

        options.ForcedAccessOptions = SessionAccessOptions.ForceValidate | SessionAccessOptions.AllowAllOrigins;
        options.ForcedAccessOptions.ShouldBe(SessionAccessOptions.ForceValidate | SessionAccessOptions.AllowAllOrigins);
    }

    [TestMethod]
    public void RefreshAfter_ZeroOrNegative_Throws()
    {
        var options = new SessionHandlingOptions();

        Should.Throw<ArgumentOutOfRangeException>(() => options.RefreshAfter = TimeSpan.Zero);
        Should.Throw<ArgumentOutOfRangeException>(() => options.RefreshAfter = TimeSpan.FromTicks(-1));
        options.RefreshAfter.ShouldBe(TimeSpan.FromMinutes(10));

        options.RefreshAfter = TimeSpan.FromSeconds(1);
        options.RefreshAfter.ShouldBe(TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public void MaxDeviceLength_ZeroOrNegative_Throws()
    {
        var options = new SessionHandlingOptions();

        Should.Throw<ArgumentOutOfRangeException>(() => options.MaxDeviceLength = 0);
        Should.Throw<ArgumentOutOfRangeException>(() => options.MaxDeviceLength = -1);
        options.MaxDeviceLength.ShouldBe(512);

        options.MaxDeviceLength = 1;
        options.MaxDeviceLength.ShouldBe(1);
    }

    [TestMethod]
    public void MaxSessionLifetimes_ZeroOrNegative_Throw()
    {
        var options = new SessionHandlingOptions();

        Should.Throw<ArgumentOutOfRangeException>(() => options.TempSessionExpiry = TimeSpan.Zero);
        Should.Throw<ArgumentOutOfRangeException>(() => options.PersistentSessionExpiry = TimeSpan.FromTicks(-1));
        Should.Throw<ArgumentOutOfRangeException>(() => options.PreviousGenerationGracePeriod = TimeSpan.FromTicks(-1));
        options.PreviousGenerationGracePeriod = TimeSpan.Zero;
        Should.Throw<ArgumentOutOfRangeException>(() => options.MaxTempSessionLifetime = TimeSpan.Zero);
        Should.Throw<ArgumentOutOfRangeException>(() => options.MaxTempSessionLifetime = TimeSpan.FromTicks(-1));
        Should.Throw<ArgumentOutOfRangeException>(() => options.MaxPersistentSessionLifetime = TimeSpan.Zero);
        Should.Throw<ArgumentOutOfRangeException>(() => options.MaxPersistentSessionLifetime = TimeSpan.FromDays(-1));

        options.MaxTempSessionLifetime.ShouldBeNull();
        options.MaxPersistentSessionLifetime.ShouldBeNull();
    }

    [TestMethod]
    public void MaxSessionLifetimes_AcceptPositiveValuesAndNull()
    {
        var options = new SessionHandlingOptions();

        options.MaxTempSessionLifetime = TimeSpan.FromTicks(1);
        options.MaxTempSessionLifetime.ShouldBe(TimeSpan.FromTicks(1));
        options.MaxTempSessionLifetime = TimeSpan.FromHours(12);
        options.MaxTempSessionLifetime.ShouldBe(TimeSpan.FromHours(12));
        options.MaxTempSessionLifetime = null;
        options.MaxTempSessionLifetime.ShouldBeNull();

        options.MaxPersistentSessionLifetime = TimeSpan.MaxValue;
        options.MaxPersistentSessionLifetime.ShouldBe(TimeSpan.MaxValue);
        options.MaxPersistentSessionLifetime = null;
        options.MaxPersistentSessionLifetime.ShouldBeNull();
    }

    [TestMethod]
    public void RegisteredDefaults_MatchNewInstance()
    {
        var host = new SessionTestHost();

        host.Options.PreviousGenerationGracePeriod.ShouldBe(TimeSpan.FromMinutes(2));
        host.Options.SessionCookieName.ShouldBe("__Host-session-token");
    }

    [TestMethod]
    [DataRow(false, 5)]
    [DataRow(false, 10)]
    [DataRow(true, 10)]
    public void ExpiryNotLongerThanRefreshAfter_FailsValidation(bool persistent, int expiryMinutes)
    {
        var expiry = TimeSpan.FromMinutes(expiryMinutes);

        var ex = Should.Throw<OptionsValidationException>(() => new SessionTestHost(o => {
            if (persistent)
                o.PersistentSessionExpiry = expiry;
            else
                o.TempSessionExpiry = expiry;
        }));

        ex.Message.ShouldContain(persistent ? nameof(SessionHandlingOptions.PersistentSessionExpiry) : nameof(SessionHandlingOptions.TempSessionExpiry));
        ex.Message.ShouldContain(nameof(SessionHandlingOptions.RefreshAfter));
    }

    [TestMethod]
    [DataRow("__Host-session-token", ".example.com", "/")]
    [DataRow("__Host-session-token", null, "/api")]
    [DataRow("__host-sid", ".example.com", "/")]
    public void HostPrefixedCookieName_WithDomainOrPath_FailsValidation(string name, string? domain, string path)
    {
        var ex = Should.Throw<OptionsValidationException>(() => new SessionTestHost(o => {
            o.SessionCookieName = name;
            o.CookieDomain = domain;
            o.CookiePath = path;
        }));

        ex.Message.ShouldContain("__Host-");
        ex.Message.ShouldContain(domain is null ? nameof(SessionHandlingOptions.CookiePath) : nameof(SessionHandlingOptions.CookieDomain));
    }

    [TestMethod]
    public void OtherCookieName_WithDomainAndPath_PassesValidation()
    {
        var host = new SessionTestHost(o => {
            o.SessionCookieName = "session-token";
            o.CookieDomain = ".example.com";
            o.CookiePath = "/api";
        });

        host.Options.CookieDomain.ShouldBe(".example.com");
    }

    [TestMethod]
    public void ExpiryLongerThanRefreshAfter_PassesValidation()
    {
        var host = new SessionTestHost(o => {
            o.RefreshAfter = TimeSpan.FromMinutes(1);
            o.TempSessionExpiry = TimeSpan.FromMinutes(5);
            o.PersistentSessionExpiry = TimeSpan.FromMinutes(2);
        });

        host.Options.TempSessionExpiry.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [TestMethod]
    public void WithoutDataProtection_RegistersIt()
    {
        var services = new ServiceCollection();
        services.AddAllowedOrigins("example.com");
        services.AddHttpSessionHandling<TestSessionToken, ScopedInMemorySessionStore>();

        using var provider = services.BuildServiceProvider();

        provider.GetService<IDataProtectionProvider>().ShouldNotBeNull();
        provider.GetRequiredService<IOptions<SessionHandlingOptions>>().Value.ShouldNotBeNull();
    }

    [TestMethod]
    public void AppDataProtectionProvider_IsKept()
    {
        var services = new ServiceCollection();
        var appProvider = new EphemeralDataProtectionProvider();
        services.AddSingleton<IDataProtectionProvider>(appProvider);
        services.AddAllowedOrigins("example.com");
        services.AddHttpSessionHandling<TestSessionToken, ScopedInMemorySessionStore>();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IDataProtectionProvider>().ShouldBeSameAs(appProvider);
    }

    [TestMethod]
    public void AppDataProtectionProvider_DefaultKeyRingIsNotSetUp()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddAllowedOrigins("example.com");
        services.AddHttpSessionHandling<TestSessionToken, ScopedInMemorySessionStore>();

        using var provider = services.BuildServiceProvider();

        provider.GetService<IKeyManager>().ShouldBeNull();
        provider.GetServices<IHostedService>().ShouldBeEmpty();
    }

    [TestMethod]
    public async Task WithoutAllowedOrigins_FailsStartup()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddHttpSessionHandling<TestSessionToken, ScopedInMemorySessionStore>();

        await using var app = builder.Build();

        var ex = await Should.ThrowAsync<OptionsValidationException>(() => app.StartAsync());
        ex.Message.ShouldContain("AddAllowedOrigins");
        ex.Message.ShouldNotContain("AddDataProtection");
    }

    [TestMethod]
    public void WithDataProtectionAndAllowedOrigins_PassesValidation()
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        services.AddAllowedOrigins("example.com");
        services.AddHttpSessionHandling<TestSessionToken, ScopedInMemorySessionStore>();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<SessionHandlingOptions>>().Value.ShouldNotBeNull();
    }

    [TestMethod]
    public async Task InvalidOptions_FailStartup()
    {
        await Should.ThrowAsync<OptionsValidationException>(() => TestWebHost.StartAsync(
            _ => { },
            configureServices: s => s.AddHttpSessionHandling<TestSessionToken, ScopedInMemorySessionStore>(
                o => o.TempSessionExpiry = TimeSpan.FromMinutes(5))));
    }
}
