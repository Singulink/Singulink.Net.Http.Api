using System.Buffers.Text;
using System.Text;
using Singulink.Net.Http.Api.Service.Passkeys.Infrastructure;
using Singulink.Net.Http.Api.Service.Testing;

namespace Singulink.Net.Http.Api.Service.Passkeys;

[PrefixTestClass]
public sealed class CeremonyOptionsTests
{
    [TestMethod]
    public async Task SignIn_Discoverable_RequestOptionsShape()
    {
        using var fixture = new PasskeyFixture();

        var ceremony = await fixture.CreateService().BeginSignInAsync(codeChallenge: fixture.Pkce.Challenge);

        ceremony.Id.Length.ShouldBe(43);

        using var options = JsonDocument.Parse(ceremony.OptionsJson);
        var root = options.RootElement;

        Base64Url.DecodeFromChars(root.GetProperty("challenge").GetString()).Length.ShouldBe(32);
        root.GetProperty("timeout").GetInt32().ShouldBe(180_000);
        root.GetProperty("rpId").GetString().ShouldBe(PasskeyFixture.RpId);
        root.GetProperty("allowCredentials").GetArrayLength().ShouldBe(0);
        root.GetProperty("userVerification").GetString().ShouldBe("required");
    }

    [TestMethod]
    public async Task SignIn_EachCeremonyHasItsOwnIdAndChallenge()
    {
        using var fixture = new PasskeyFixture();

        var first = await fixture.CreateService().BeginSignInAsync(codeChallenge: fixture.Pkce.Challenge);
        var second = await fixture.CreateService().BeginSignInAsync(codeChallenge: fixture.Pkce.Challenge);

        first.Id.ShouldNotBe(second.Id);
        GetChallenge(first.OptionsJson).ShouldNotBe(GetChallenge(second.OptionsJson));
    }

    [TestMethod]
    public async Task SignIn_ForUser_AllowsOnlyTheirPasskeys()
    {
        using var fixture = new PasskeyFixture();
        using var mine = new SoftwareAuthenticator(PasskeyFixture.Origin);
        using var theirs = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(mine, "user-1");
        await fixture.RegisterAsync(theirs, "user-2");

        var ceremony = await fixture.CreateService().BeginSignInAsync(fixture.Pkce.Challenge, "user-1");

        using var options = JsonDocument.Parse(ceremony.OptionsJson);
        var allowed = options.RootElement.GetProperty("allowCredentials").EnumerateArray().ToList();

        allowed.Count.ShouldBe(1);
        allowed[0].GetProperty("type").GetString().ShouldBe("public-key");
        allowed[0].GetProperty("id").GetString().ShouldBe(Base64Url.EncodeToString(mine.CredentialId));
        allowed[0].GetProperty("transports").EnumerateArray().Select(t => t.GetString()).ShouldBe(["internal", "hybrid"]);
    }

    [TestMethod]
    public async Task SignIn_ForUser_SendsOnlyKnownTransports()
    {
        using var fixture = new PasskeyFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin) { Transports = ["hybrid", "teleport", "internal"] };
        var registration = await fixture.RegisterAsync(authenticator);

        registration.Passkey.Transports.ShouldBe(["hybrid", "teleport", "internal"]);

        var ceremony = await fixture.CreateService().BeginSignInAsync(fixture.Pkce.Challenge, "user-1");

        using var options = JsonDocument.Parse(ceremony.OptionsJson);
        var transports = options.RootElement.GetProperty("allowCredentials")[0].GetProperty("transports");
        transports.EnumerateArray().Select(t => t.GetString()).ShouldBe(["hybrid", "internal"]);
    }

    [TestMethod]
    public async Task SignIn_ForUserWithoutPasskeys_Throws()
    {
        using var fixture = new PasskeyFixture();

        var ex = await Should.ThrowAsync<UnauthorizedApiException>(() => fixture.CreateService().BeginSignInAsync(fixture.Pkce.Challenge, "user-1"));
        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.NoPasskeys);
    }

    [TestMethod]
    public async Task Verification_WithoutPasskeys_Throws()
    {
        using var fixture = new PasskeyFixture();

        var ex = await Should.ThrowAsync<ConflictApiException>(() => fixture.CreateService().BeginVerificationAsync("user-1"));
        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.NoPasskeys);
    }

    [TestMethod]
    public async Task Verification_AllowsOnlyTheUsersPasskeys()
    {
        using var fixture = new PasskeyFixture();
        using var mine = new SoftwareAuthenticator(PasskeyFixture.Origin);
        using var theirs = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(mine, "user-1");
        await fixture.RegisterAsync(theirs, "user-2");

        var ceremony = await fixture.CreateService().BeginVerificationAsync("user-1");

        using var options = JsonDocument.Parse(ceremony.OptionsJson);
        var allowed = options.RootElement.GetProperty("allowCredentials").EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToList();
        allowed.ShouldBe([Base64Url.EncodeToString(mine.CredentialId)]);
        options.RootElement.GetProperty("userVerification").GetString().ShouldBe("required");
    }

    [TestMethod]
    public async Task Registration_CreationOptionsShape()
    {
        using var fixture = new PasskeyFixture();
        using var existing = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(existing, "user-1");

        var ceremony = await fixture.CreateService().BeginRegistrationAsync(new PasskeyUser("user-1", "alex@example.com", "Alex Smith"));

        using var options = JsonDocument.Parse(ceremony.OptionsJson);
        var root = options.RootElement;

        root.GetProperty("rp").GetProperty("id").GetString().ShouldBe(PasskeyFixture.RpId);
        root.GetProperty("rp").GetProperty("name").GetString().ShouldBe("Example");

        var user = root.GetProperty("user");
        Base64Url.DecodeFromChars(user.GetProperty("id").GetString()).ShouldBe(Encoding.UTF8.GetBytes("user-1"));
        user.GetProperty("name").GetString().ShouldBe("alex@example.com");
        user.GetProperty("displayName").GetString().ShouldBe("Alex Smith");

        Base64Url.DecodeFromChars(root.GetProperty("challenge").GetString()).Length.ShouldBe(32);
        root.GetProperty("timeout").GetInt32().ShouldBe(180_000);
        root.GetProperty("attestation").GetString().ShouldBe("none");

        var algorithms = root.GetProperty("pubKeyCredParams").EnumerateArray().Select(p => p.GetProperty("alg").GetInt32()).ToList();
        algorithms.ShouldContain(-7); // ES256
        algorithms.ShouldContain(-257); // RS256

        var selection = root.GetProperty("authenticatorSelection");
        selection.GetProperty("residentKey").GetString().ShouldBe("required");
        selection.GetProperty("requireResidentKey").GetBoolean().ShouldBeTrue();
        selection.GetProperty("userVerification").GetString().ShouldBe("required");
        selection.TryGetProperty("authenticatorAttachment", out _).ShouldBeFalse();

        var excluded = root.GetProperty("excludeCredentials").EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToList();
        excluded.ShouldBe([Base64Url.EncodeToString(existing.CredentialId)]);

        (root.TryGetProperty("extensions", out var extensions) && extensions.TryGetProperty("credProps", out _)).ShouldBeFalse();
    }

    [TestMethod]
    public async Task Registration_NotExcludingExistingPasskeys_ListsNone()
    {
        using var fixture = new PasskeyFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(authenticator, "user-1");

        var ceremony = await fixture.CreateService().BeginRegistrationAsync(PasskeyFixture.User("user-1"), excludeExistingPasskeys: false);

        using var options = JsonDocument.Parse(ceremony.OptionsJson);
        int excluded = options.RootElement.TryGetProperty("excludeCredentials", out var list) ? list.GetArrayLength() : 0;
        excluded.ShouldBe(0);
    }

    [TestMethod]
    public async Task Options_FollowConfiguredRequirementsAndTimeout()
    {
        using var fixture = new PasskeyFixture(o => {
            o.RequireUserVerification = false;
            o.RequireDiscoverableCredentials = false;
            o.CeremonyTimeout = TimeSpan.FromMinutes(2);
        });

        var signIn = await fixture.CreateService().BeginSignInAsync(codeChallenge: fixture.Pkce.Challenge);
        var registration = await fixture.CreateService().BeginRegistrationAsync(PasskeyFixture.User());

        using var signInOptions = JsonDocument.Parse(signIn.OptionsJson);
        signInOptions.RootElement.GetProperty("userVerification").GetString().ShouldBe("preferred");
        signInOptions.RootElement.GetProperty("timeout").GetInt32().ShouldBe(120_000);

        using var registrationOptions = JsonDocument.Parse(registration.OptionsJson);
        var selection = registrationOptions.RootElement.GetProperty("authenticatorSelection");
        selection.GetProperty("residentKey").GetString().ShouldBe("preferred");
        selection.GetProperty("userVerification").GetString().ShouldBe("preferred");
        registrationOptions.RootElement.GetProperty("timeout").GetInt32().ShouldBe(120_000);
    }

    [TestMethod]
    public void User_IdIsLimitedTo64Utf8Bytes()
    {
        _ = new PasskeyUser(new string('a', 64), "name", "Name");
        _ = new PasskeyUser(new string('é', 32), "name", "Name");

        Should.Throw<ArgumentException>(() => new PasskeyUser(new string('a', 65), "name", "Name"));
        Should.Throw<ArgumentException>(() => new PasskeyUser(new string('é', 33), "name", "Name"));
        Should.Throw<ArgumentException>(() => new PasskeyUser(string.Empty, "name", "Name"));
        Should.Throw<ArgumentException>(() => new PasskeyUser("user-1", " ", "Name"));
        Should.Throw<ArgumentException>(() => new PasskeyUser("user-1", "name", string.Empty));
    }

    private static string GetChallenge(string optionsJson)
    {
        using var options = JsonDocument.Parse(optionsJson);
        return options.RootElement.GetProperty("challenge").GetString()!;
    }
}
