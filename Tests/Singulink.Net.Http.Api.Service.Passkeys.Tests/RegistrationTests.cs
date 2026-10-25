using System.Buffers.Text;
using Singulink.Net.Http.Api.DataContracts;
using Singulink.Net.Http.Api.Service.Passkeys.Infrastructure;
using Singulink.Net.Http.Api.Service.Testing;

namespace Singulink.Net.Http.Api.Service.Passkeys;

/// <summary>
/// Passkey registration tests, run with ceremonies kept in the memory store and in a shared store, where every request could run on a different server.
/// </summary>
public abstract class RegistrationTests
{
    protected abstract StoreKind StoreKind { get; }

    protected PasskeyFixture CreateFixture(Action<PasskeyOptions>? configure = null) => new(configure, StoreKind);

    private static readonly Guid ICloudKeychain = new("fbfc3007-154e-4ecc-8c0b-6e020557d7bd");

    [TestMethod]
    public async Task RegistersAPasskeyThatCanSignIn()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin) { AaGuid = ICloudKeychain, IsBackedUp = true };

        var ceremony = await fixture.CreateService().BeginRegistrationAsync(PasskeyFixture.User());
        string credential = authenticator.Create(ceremony.OptionsJson);
        var result = await fixture.CreateService().FinishRegistrationAsync("user-1", ceremony.Id, credential, "IOS (Safari 18.0)");

        var passkey = result.Passkey;
        passkey.UserId.ShouldBe("user-1");
        passkey.CredentialId.ShouldBe(authenticator.CredentialId);
        passkey.PublicKey.ShouldNotBeEmpty();
        passkey.SignCount.ShouldBe(0u);
        passkey.Transports.ShouldBe(["internal", "hybrid"]);
        passkey.IsBackupEligible.ShouldBeTrue();
        passkey.IsBackedUp.ShouldBeTrue();
        passkey.AaGuid.ShouldBe(ICloudKeychain);
        result.SuggestedName.ShouldBe("iCloud Keychain (iOS)");
        result.Attachment.ShouldBe(PasskeyAttachment.Platform);

        // Registration does not store the passkey: the app does.
        fixture.Store.Passkeys.ShouldBeEmpty();
        fixture.Store.Add(passkey);

        var verified = await fixture.SignInAsync(authenticator);
        verified.UserId.ShouldBe("user-1");
    }

    [TestMethod]
    public async Task WithSessionToken_RegistersForItsUser()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        var user = new TestUserToken("user-1");

        var ceremony = await fixture.CreateService().BeginRegistrationAsync(new PasskeyUser(user, "alex@example.com", "Alex Smith"));
        var result = await fixture.CreateService().FinishRegistrationAsync(user, ceremony.Id, authenticator.Create(ceremony.OptionsJson));

        result.Passkey.UserId.ShouldBe("user-1");
        authenticator.UserHandle.ShouldBe("user-1"u8.ToArray());
    }

    [TestMethod]
    public async Task RegistrationWithCodeChallenge_ContinuesWithASignInCode()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        var pkce = PasskeyFixture.CreatePkce();

        var ceremony = await fixture.CreateService().BeginRegistrationAsync(PasskeyFixture.User(), pkce.Challenge);
        var result = await fixture.CreateService().FinishRegistrationAsync("user-1", ceremony.Id, authenticator.Create(ceremony.OptionsJson));
        fixture.Store.Add(result.Passkey);

        result.CodeChallenge.ShouldBe(pkce.Challenge);

        string code = await fixture.CreateSignInCodes().CreateAsync(result);

        (await fixture.CreateSignInCodes().RedeemAsync(code, PasskeyFixture.CreatePkce().Verifier)).ShouldBeNull();

        string otherCode = await fixture.CreateSignInCodes().CreateAsync(result, appData: "v2");
        var redeemed = (await fixture.CreateSignInCodes().RedeemAsync(otherCode, pkce.Verifier)).ShouldNotBeNull();
        redeemed.UserId.ShouldBe("user-1");
        redeemed.AppData.ShouldBe("v2");
    }

    [TestMethod]
    public async Task RegistrationWithoutCodeChallenge_CannotContinueWithASignInCode()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);

        var result = await fixture.RegisterAsync(authenticator);

        result.CodeChallenge.ShouldBeNull();
        await Should.ThrowAsync<InvalidOperationException>(() => fixture.CreateSignInCodes().CreateAsync(result));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("not-a-challenge")]
    [DataRow("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB")]
    public async Task RegistrationWithMalformedCodeChallenge_IsRejected(string codeChallenge)
    {
        using var fixture = CreateFixture();

        var ex = await Should.ThrowAsync<BadRequestApiException>(() => fixture.CreateService().BeginRegistrationAsync(PasskeyFixture.User(), codeChallenge));

        ex.ErrorCode.ShouldBe(SignInErrorCodes.CodeChallengeInvalid);
    }

    [TestMethod]
    public async Task SecurityKey_IsNamedAsOne()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin) {
            Transports = ["usb", "nfc"], Attachment = PasskeyAttachment.CrossPlatform, IsBackupEligible = false, IsBackedUp = false,
        };

        var result = await fixture.RegisterAsync(authenticator, device: "Windows (Chrome 130.0)");

        result.SuggestedName.ShouldBe("Security key");
        result.Attachment.ShouldBe(PasskeyAttachment.CrossPlatform);
        result.Passkey.IsBackupEligible.ShouldBeFalse();
    }

    [TestMethod]
    public async Task AttachmentNotReported_IsUnknown()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin) { Attachment = PasskeyAttachment.Unknown };

        var result = await fixture.RegisterAsync(authenticator);

        result.Attachment.ShouldBe(PasskeyAttachment.Unknown);
        result.SuggestedName.ShouldBe("Passkey");
    }

    [TestMethod]
    public async Task AlreadyRegisteredPasskey_IsRejected()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(authenticator);

        var ceremony = await fixture.CreateService().BeginRegistrationAsync(PasskeyFixture.User());

        var ex = await Should.ThrowAsync<ConflictApiException>(() =>
            fixture.CreateService().FinishRegistrationAsync("user-1", ceremony.Id, authenticator.Create(ceremony.OptionsJson)));

        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.AlreadyRegistered);
    }

    [TestMethod]
    public async Task FinishingForAnotherUser_IsRejected()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);

        var ceremony = await fixture.CreateService().BeginRegistrationAsync(PasskeyFixture.User("user-1"));

        var ex = await Should.ThrowAsync<ValidationApiException>(() =>
            fixture.CreateService().FinishRegistrationAsync("user-2", ceremony.Id, authenticator.Create(ceremony.OptionsJson)));

        ex.StatusCode.ShouldBe((HttpStatusCode)422);
        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.CeremonyExpired);
    }

    [TestMethod]
    public async Task Ceremony_IsSingleUseAndExpires()
    {
        using var fixture = CreateFixture();
        using var first = new SoftwareAuthenticator(PasskeyFixture.Origin);
        using var second = new SoftwareAuthenticator(PasskeyFixture.Origin);

        var ceremony = await fixture.CreateService().BeginRegistrationAsync(PasskeyFixture.User());
        string credential = first.Create(ceremony.OptionsJson);
        await fixture.CreateService().FinishRegistrationAsync("user-1", ceremony.Id, credential);

        var ex = await Should.ThrowAsync<ValidationApiException>(() => fixture.CreateService().FinishRegistrationAsync("user-1", ceremony.Id, credential));
        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.CeremonyExpired);

        var expired = await fixture.CreateService().BeginRegistrationAsync(PasskeyFixture.User());
        string expiredCredential = second.Create(expired.OptionsJson);
        fixture.Time.Advance(TimeSpan.FromMinutes(5));

        ex = await Should.ThrowAsync<ValidationApiException>(() => fixture.CreateService().FinishRegistrationAsync("user-1", expired.Id, expiredCredential));
        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.CeremonyExpired);
    }

    [TestMethod]
    public async Task SignInCeremony_CannotFinishARegistration()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);

        var registration = await fixture.CreateService().BeginRegistrationAsync(PasskeyFixture.User());
        string credential = authenticator.Create(registration.OptionsJson);
        var signIn = await fixture.CreateService().BeginSignInAsync(codeChallenge: fixture.Pkce.Challenge);

        var ex = await Should.ThrowAsync<ValidationApiException>(() => fixture.CreateService().FinishRegistrationAsync("user-1", signIn.Id, credential));
        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.CeremonyExpired);
    }

    [TestMethod]
    public async Task OriginNotAllowed_IsRejected()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin) { Origin = "https://evil.example" };

        var ceremony = await fixture.CreateService().BeginRegistrationAsync(PasskeyFixture.User());

        var ex = await Should.ThrowAsync<ValidationApiException>(() =>
            fixture.CreateService().FinishRegistrationAsync("user-1", ceremony.Id, authenticator.Create(ceremony.OptionsJson)));

        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.VerificationFailed);

        // Fido2NetLib's description of the failure is logged rather than sent to the client.
        ex.Message.ShouldBe("The passkey could not be verified.");
        fixture.Logs.Entries.ShouldContain(e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Exception is Fido2NetLib.Fido2VerificationException);
    }

    [TestMethod]
    public async Task UserVerificationMissing_IsRejected()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin) { VerifiesUser = false };

        var ceremony = await fixture.CreateService().BeginRegistrationAsync(PasskeyFixture.User());

        var ex = await Should.ThrowAsync<ValidationApiException>(() =>
            fixture.CreateService().FinishRegistrationAsync("user-1", ceremony.Id, authenticator.Create(ceremony.OptionsJson)));

        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.VerificationFailed);
    }

    [TestMethod]
    public async Task AssertionCredential_IsRejected()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(authenticator);

        var signIn = await fixture.CreateService().BeginSignInAsync(codeChallenge: fixture.Pkce.Challenge);
        string assertion = authenticator.Get(signIn.OptionsJson);
        var registration = await fixture.CreateService().BeginRegistrationAsync(PasskeyFixture.User());

        await Should.ThrowAsync<BadRequestApiException>(() => fixture.CreateService().FinishRegistrationAsync("user-1", registration.Id, assertion));
    }

    [TestMethod]
    public async Task UnknownTransports_AreKept()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin) { Transports = ["internal", "teleport", "INVALID", "internal"] };

        var result = await fixture.RegisterAsync(authenticator);

        result.Passkey.Transports.ShouldBe(["internal", "teleport"]);
    }

    [TestMethod]
    public async Task UserId_FormattedDifferentlyLater_DoesNotSignIn()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);

        var ceremony = await fixture.CreateService().BeginRegistrationAsync(PasskeyFixture.User("1"));
        var result = await fixture.CreateService().FinishRegistrationAsync("1", ceremony.Id, authenticator.Create(ceremony.OptionsJson));

        // As if the app stored the user ID in another format than it registered the passkey with.
        fixture.Store.Add(new PasskeyRecord {
            UserId = "01",
            CredentialId = result.Passkey.CredentialId,
            PublicKey = result.Passkey.PublicKey,
            Transports = result.Passkey.Transports,
        });

        var ex = await Should.ThrowAsync<UnauthorizedApiException>(() => fixture.SignInAsync(authenticator));
        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.VerificationFailed);
    }

    [TestMethod]
    public async Task UserHandle_IsTheUtf8UserId()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        const string UserId = "ünïcode-user";

        var ceremony = await fixture.CreateService().BeginRegistrationAsync(PasskeyFixture.User(UserId));
        var result = await fixture.CreateService().FinishRegistrationAsync(UserId, ceremony.Id, authenticator.Create(ceremony.OptionsJson));
        fixture.Store.Add(result.Passkey);

        Base64Url.EncodeToString(authenticator.UserHandle!).ShouldBe(Base64Url.EncodeToString("ünïcode-user"u8));

        var verified = await fixture.SignInAsync(authenticator);
        verified.UserId.ShouldBe(UserId);
    }
}

[PrefixTestClass]
public sealed class RegistrationTestsWithMemoryStore : RegistrationTests
{
    protected override StoreKind StoreKind => StoreKind.Memory;
}

[PrefixTestClass]
public sealed class RegistrationTestsWithSharedStore : RegistrationTests
{
    protected override StoreKind StoreKind => StoreKind.Shared;
}
