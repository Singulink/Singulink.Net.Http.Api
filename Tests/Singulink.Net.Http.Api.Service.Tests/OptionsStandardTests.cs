using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Tests of the standard that the options classes follow: they are checked as a whole and frozen once they are in use, and required values read as not set
/// until they are set.
/// </summary>
[PrefixTestClass]
public sealed class OptionsStandardTests
{
    [TestMethod]
    public void Freeze_KeepsEveryOptionFromChanging()
    {
        var chunkedUploads = new ChunkedUploadOptions();
        chunkedUploads.Freeze();
        OptionsAssertions.ShouldBeFrozen(chunkedUploads);

        var endpoint = new ChunkedUploadEndpointOptions();
        endpoint.Freeze();
        OptionsAssertions.ShouldBeFrozen(endpoint);

        var directoryStore = new DirectoryUploadStoreOptions(DirectoryPath.GetTemp(), StoreSharingMode.ProcessLocal);
        directoryStore.Freeze();
        OptionsAssertions.ShouldBeFrozen(directoryStore);

        var memoryStore = new MemoryShortLivedStoreOptions();
        memoryStore.Freeze();
        OptionsAssertions.ShouldBeFrozen(memoryStore);

        var signInCodes = new SignInCodeOptions();
        signInCodes.Freeze();
        OptionsAssertions.ShouldBeFrozen(signInCodes);

        var deviceLinks = new DeviceLinkOptions();
        deviceLinks.Freeze();
        OptionsAssertions.ShouldBeFrozen(deviceLinks);

        var sessionHandling = new SessionHandlingOptions();
        sessionHandling.Freeze();
        OptionsAssertions.ShouldBeFrozen(sessionHandling);

        var rateLimit = new ClientRateLimitOptions { PermitLimit = 10, ExemptNetworks = { "10.0.0.0/8" } };
        rateLimit.Freeze();
        OptionsAssertions.ShouldBeFrozen(rateLimit);
        rateLimit.ExemptNetworks.ShouldBe(["10.0.0.0/8"]);
    }

    [TestMethod]
    public void Validate_ListsEveryProblem_WithoutFreezing()
    {
        var options = new SessionHandlingOptions {
            TempSessionExpiry = TimeSpan.FromMinutes(5),
            PersistentSessionExpiry = TimeSpan.FromMinutes(10),
            CookieDomain = "example.com",
        };

        var problems = options.GetProblems();
        problems.Count.ShouldBe(3);
        problems[0].ShouldStartWith("SessionHandlingOptions.TempSessionExpiry (00:05:00) must be longer than SessionHandlingOptions.RefreshAfter");
        problems[1].ShouldStartWith("SessionHandlingOptions.PersistentSessionExpiry (00:10:00) must be longer than SessionHandlingOptions.RefreshAfter");
        problems[2].ShouldStartWith("SessionHandlingOptions.SessionCookieName '__Host-session-token' starts with '__Host-'");

        string message = string.Join(" ", problems);
        Should.Throw<InvalidOperationException>(options.Validate).Message.ShouldBe(message);
        Should.Throw<InvalidOperationException>(options.Freeze).Message.ShouldBe(message);
        options.IsFrozen.ShouldBeFalse();

        // The options were not frozen, so the problems can be fixed.
        options.TempSessionExpiry = TimeSpan.FromDays(1);
        options.PersistentSessionExpiry = TimeSpan.FromDays(30);
        options.CookieDomain = null;
        options.SessionCookieName = "session-token";
        options.CookieDomain = "example.com";

        options.Validate();
        options.IsFrozen.ShouldBeFalse();
        options.Freeze();
        options.IsFrozen.ShouldBeTrue();
    }

    [TestMethod]
    public void RequiredPermitLimit_ReadsAsZeroUntilItIsSet_AndKeepsTheOptionsFromFreezing()
    {
        // As the options are created when they are bound from a configuration, without their required values.
        var options = Activator.CreateInstance<ClientRateLimitOptions>();
        options.PermitLimit.ShouldBe(0);

        const string message = "ClientRateLimitOptions.PermitLimit is not set. It is required, and has no default.";
        options.GetProblems().ShouldBe([message]);
        Should.Throw<InvalidOperationException>(options.Freeze).Message.ShouldBe(message);
        options.IsFrozen.ShouldBeFalse();

        options.PermitLimit = 5;
        options.Freeze();
        options.PermitLimit.ShouldBe(5);
    }

    [TestMethod]
    public void PermitLimitOfZero_LeavesItNotSet_WhileItIsNotSet_AndThrowsOnceItIsSet()
    {
        var options = Activator.CreateInstance<ClientRateLimitOptions>();
        options.PermitLimit = 0;
        options.PermitLimit.ShouldBe(0);
        options.GetProblems().ShouldBe(["ClientRateLimitOptions.PermitLimit is not set. It is required, and has no default."]);

        options.PermitLimit = 5;
        Should.Throw<ArgumentOutOfRangeException>(() => options.PermitLimit = 0);
        Should.Throw<ArgumentOutOfRangeException>(() => options.PermitLimit = -1);
        options.PermitLimit.ShouldBe(5);
    }

    [TestMethod]
    public void TheServicesFreezeTheirOptions_WhenTheyAreCreated()
    {
        var services = new ServiceCollection();
        services.AddDeviceLinks();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<MemoryShortLivedStoreOptions>>().Value.IsFrozen.ShouldBeFalse();
        provider.GetRequiredService<IOptions<SignInCodeOptions>>().Value.IsFrozen.ShouldBeFalse();
        provider.GetRequiredService<IOptions<DeviceLinkOptions>>().Value.IsFrozen.ShouldBeFalse();

        _ = provider.GetRequiredService<IShortLivedStore>();
        _ = provider.GetRequiredService<SignInCodes>();
        _ = provider.GetRequiredService<DeviceLinks>();

        provider.GetRequiredService<IOptions<MemoryShortLivedStoreOptions>>().Value.IsFrozen.ShouldBeTrue();
        provider.GetRequiredService<IOptions<SignInCodeOptions>>().Value.IsFrozen.ShouldBeTrue();
        provider.GetRequiredService<IOptions<DeviceLinkOptions>>().Value.IsFrozen.ShouldBeTrue();

        var sessions = new SessionTestHost();
        sessions.Options.IsFrozen.ShouldBeFalse();
        _ = sessions.Services.GetRequiredService<SessionHandler<TestSessionToken>>();
        sessions.Options.IsFrozen.ShouldBeTrue();
    }

    [TestMethod]
    public void DirectoryStoreOptions_ValidateAndIsFrozen_TellTheCompilerThatTheDirectoryIsSet()
    {
        // The nullable warnings of these reads fail the build (TreatWarningsAsErrors), so this compiles only because Validate and IsFrozen tell the compiler,
        // with MemberNotNull and MemberNotNullWhen, that Directory is not null afterwards.
        var validated = Activator.CreateInstance<DirectoryUploadStoreOptions>();
        validated.Directory = DirectoryPath.GetTemp().CombineDirectory("uploads", PathOptions.None);
        validated.SharingMode = StoreSharingMode.Shared;
        validated.Validate();
        validated.Directory.Name.ShouldBe("uploads");

        var frozen = new DirectoryUploadStoreOptions(DirectoryPath.GetTemp().CombineDirectory("other", PathOptions.None), StoreSharingMode.Shared);
        frozen.Freeze();
        frozen.IsFrozen.ShouldBeTrue();

        if (frozen.IsFrozen)
            frozen.Directory.Name.ShouldBe("other");
    }
}
