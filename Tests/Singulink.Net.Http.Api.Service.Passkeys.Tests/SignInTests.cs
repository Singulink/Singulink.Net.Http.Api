using System.Text;
using Microsoft.Extensions.Logging;
using Singulink.Net.Http.Api.DataContracts;
using Singulink.Net.Http.Api.Service.Passkeys.Infrastructure;
using Singulink.Net.Http.Api.Service.Testing;

namespace Singulink.Net.Http.Api.Service.Passkeys;

/// <summary>
/// Passkey sign-in tests, run with ceremonies kept in the memory store and in a shared store, where every request could run on a different server.
/// </summary>
public abstract class SignInTests
{
    protected abstract StoreKind StoreKind { get; }

    protected PasskeyFixture CreateFixture(Action<PasskeyOptions>? configure = null) => new(configure, StoreKind);

    [TestMethod]
    public async Task Discoverable_SignsInAndSavesUse()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(authenticator);
        fixture.Time.Advance(TimeSpan.FromHours(1));

        var verified = await fixture.SignInAsync(authenticator);

        verified.UserId.ShouldBe("user-1");
        verified.Passkey.CredentialId.ShouldBe(authenticator.CredentialId);
        verified.Attachment.ShouldBe(PasskeyAttachment.Platform);

        var stored = fixture.Store.Get(authenticator.CredentialId);
        stored.LastUsedUtc.ShouldBe(fixture.Time.GetUtcNow().UtcDateTime);
        stored.LastUsedUtc!.Value.Kind.ShouldBe(DateTimeKind.Utc);
        fixture.Store.UpdateCount.ShouldBe(1);
    }

    [TestMethod]
    public async Task ForUser_SignsIn()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin) { Attachment = PasskeyAttachment.CrossPlatform };
        await fixture.RegisterAsync(authenticator);

        var ceremony = await fixture.CreateService().BeginSignInAsync(fixture.Pkce.Challenge, "user-1");
        string credential = authenticator.Get(ceremony.OptionsJson, includeUserHandle: false);
        var verified = await fixture.CreateService().FinishSignInAsync(ceremony.Id, credential, fixture.Pkce.Verifier);

        verified.UserId.ShouldBe("user-1");
        verified.Attachment.ShouldBe(PasskeyAttachment.CrossPlatform);
    }

    [TestMethod]
    public async Task ForUser_PasskeyOfAnotherUser_IsRejectedAsWrongAccount()
    {
        using var fixture = CreateFixture();
        using var mine = new SoftwareAuthenticator(PasskeyFixture.Origin);
        using var theirs = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(mine, "user-1");
        await fixture.RegisterAsync(theirs, "user-2");

        var ceremony = await fixture.CreateService().BeginSignInAsync(fixture.Pkce.Challenge, "user-1");

        var ex = await Should.ThrowAsync<UnauthorizedApiException>(() =>
            fixture.CreateService().FinishSignInAsync(ceremony.Id, theirs.Get(ceremony.OptionsJson), fixture.Pkce.Verifier));

        ex.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.WrongAccount);
    }

    [TestMethod]
    public async Task SignCount_IsSavedAndARegressionIsRejected()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin) { CountsSignatures = true };
        await fixture.RegisterAsync(authenticator);

        await fixture.SignInAsync(authenticator);
        var verified = await fixture.SignInAsync(authenticator);

        verified.Passkey.SignCount.ShouldBe(2u);
        fixture.Store.Get(authenticator.CredentialId).Record.SignCount.ShouldBe(2u);

        // A clone of the authenticator reports a counter that has not moved past the stored one.
        authenticator.SignCount = 0;

        var ex = await Should.ThrowAsync<UnauthorizedApiException>(() => fixture.SignInAsync(authenticator));
        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.VerificationFailed);
        fixture.Store.Get(authenticator.CredentialId).Record.SignCount.ShouldBe(2u);
    }

    [TestMethod]
    public async Task BackupState_IsSaved()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin) { IsBackedUp = false };
        var registration = await fixture.RegisterAsync(authenticator);
        registration.Passkey.IsBackedUp.ShouldBeFalse();

        authenticator.IsBackedUp = true;
        var verified = await fixture.SignInAsync(authenticator);

        verified.Passkey.IsBackedUp.ShouldBeTrue();
        fixture.Store.Get(authenticator.CredentialId).Record.IsBackedUp.ShouldBeTrue();
    }

    [TestMethod]
    public async Task Ceremony_IsSingleUse()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(authenticator);

        var ceremony = await fixture.CreateService().BeginSignInAsync(codeChallenge: fixture.Pkce.Challenge);
        string credential = authenticator.Get(ceremony.OptionsJson);

        await fixture.CreateService().FinishSignInAsync(ceremony.Id, credential, fixture.Pkce.Verifier);

        var ex = await Should.ThrowAsync<UnauthorizedApiException>(() =>
            fixture.CreateService().FinishSignInAsync(ceremony.Id, credential, fixture.Pkce.Verifier));

        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.CeremonyExpired);
    }

    [TestMethod]
    public async Task Ceremony_IsSingleUseUnderConcurrency()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(authenticator);

        var ceremony = await fixture.CreateService().BeginSignInAsync(codeChallenge: fixture.Pkce.Challenge);
        string credential = authenticator.Get(ceremony.OptionsJson);
        var services = Enumerable.Range(0, 16).Select(_ => fixture.CreateService()).ToList();

        var results = await Task.WhenAll(services.Select(service => Task.Run(async () => {
            try
            {
                await service.FinishSignInAsync(ceremony.Id, credential, fixture.Pkce.Verifier);
                return true;
            }
            catch (UnauthorizedApiException ex) when (ex.ErrorCode is PasskeyErrorCodes.CeremonyExpired)
            {
                return false;
            }
        })));

        results.Count(r => r).ShouldBe(1);
    }

    [TestMethod]
    [DataRow("not json")]
    [DataRow("""{"kind":"SignIn","userId":null,"codeChallenge":null,"optionsJson":"not json"}""")]
    [DataRow("""{"kind":"Unknown","userId":null,"codeChallenge":null,"optionsJson":"{}"}""")]
    [DataRow("""{"kind":"SignIn"}""")]
    public async Task UnreadableCeremony_IsTreatedAsExpiredAndLogged(string storedValue)
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(authenticator);

        var ceremony = await fixture.CreateService().BeginSignInAsync(codeChallenge: fixture.Pkce.Challenge);
        string credential = authenticator.Get(ceremony.OptionsJson);

        // As if the ceremony were stored by a version of the service or of Fido2NetLib that kept it differently.
        string key = ToolkitKeys.PasskeyCeremony + ceremony.Id;
        await fixture.ShortLivedStore.TakeAsync(key);
        await fixture.ShortLivedStore.TryAddAsync(key, storedValue, TimeSpan.FromMinutes(5));

        var ex = await Should.ThrowAsync<UnauthorizedApiException>(() =>
            fixture.CreateService().FinishSignInAsync(ceremony.Id, credential, fixture.Pkce.Verifier));

        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.CeremonyExpired);
        fixture.Logs.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("could not be read", StringComparison.Ordinal));
        fixture.Store.UpdateCount.ShouldBe(0);
    }

    [TestMethod]
    public async Task Ceremony_Expires()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(authenticator);

        var stillValid = await fixture.CreateService().BeginSignInAsync(codeChallenge: fixture.Pkce.Challenge);
        var expired = await fixture.CreateService().BeginSignInAsync(codeChallenge: fixture.Pkce.Challenge);
        string stillValidCredential = authenticator.Get(stillValid.OptionsJson);
        string expiredCredential = authenticator.Get(expired.OptionsJson);

        fixture.Time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        await fixture.CreateService().FinishSignInAsync(stillValid.Id, stillValidCredential, fixture.Pkce.Verifier);

        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        var ex = await Should.ThrowAsync<UnauthorizedApiException>(() =>
            fixture.CreateService().FinishSignInAsync(expired.Id, expiredCredential, fixture.Pkce.Verifier));

        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.CeremonyExpired);
    }

    [TestMethod]
    public async Task Ceremony_LifetimeIsConfigurable()
    {
        using var fixture = CreateFixture(o => o.CeremonyLifetime = TimeSpan.FromMinutes(10));
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(authenticator);

        var ceremony = await fixture.CreateService().BeginSignInAsync(codeChallenge: fixture.Pkce.Challenge);
        fixture.Time.Advance(TimeSpan.FromMinutes(9));

        await fixture.CreateService().FinishSignInAsync(ceremony.Id, authenticator.Get(ceremony.OptionsJson), fixture.Pkce.Verifier);
    }

    [TestMethod]
    public async Task UnknownCeremony_IsRejected()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(authenticator);

        var ceremony = await fixture.CreateService().BeginSignInAsync(codeChallenge: fixture.Pkce.Challenge);

        var ex = await Should.ThrowAsync<UnauthorizedApiException>(() =>
            fixture.CreateService().FinishSignInAsync("unknown", authenticator.Get(ceremony.OptionsJson), fixture.Pkce.Verifier));

        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.CeremonyExpired);
    }

    [TestMethod]
    public async Task VerificationCeremony_CannotFinishASignIn()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(authenticator);

        var ceremony = await fixture.CreateService().BeginVerificationAsync("user-1");

        var ex = await Should.ThrowAsync<UnauthorizedApiException>(() =>
            fixture.CreateService().FinishSignInAsync(ceremony.Id, authenticator.Get(ceremony.OptionsJson), fixture.Pkce.Verifier));

        ex.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.CeremonyExpired);
    }

    [TestMethod]
    public async Task UnregisteredPasskey_IsRejected()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin) { UserHandle = Encoding.UTF8.GetBytes("user-1") };

        var ex = await Should.ThrowAsync<UnauthorizedApiException>(() => fixture.SignInAsync(authenticator));
        ex.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.NotRegistered);
    }

    [TestMethod]
    public async Task UserHandleOfAnotherUser_IsRejected()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(authenticator);

        authenticator.UserHandle = Encoding.UTF8.GetBytes("user-2");

        var ex = await Should.ThrowAsync<UnauthorizedApiException>(() => fixture.SignInAsync(authenticator));
        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.VerificationFailed);
        fixture.Store.UpdateCount.ShouldBe(0);
    }

    [TestMethod]
    public async Task OriginNotAllowed_IsRejected()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(authenticator);

        authenticator.Origin = "https://evil.example";

        var ex = await Should.ThrowAsync<UnauthorizedApiException>(() => fixture.SignInAsync(authenticator));
        ex.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.VerificationFailed);

        // Fido2NetLib's description of the failure is logged rather than sent to the client.
        ex.Message.ShouldBe("The passkey could not be verified.");
        fixture.Logs.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Exception is Fido2NetLib.Fido2VerificationException);
    }

    [TestMethod]
    public async Task OtherRelyingParty_IsRejected()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(authenticator);

        authenticator.RelyingPartyId = "evil.example";

        var ex = await Should.ThrowAsync<UnauthorizedApiException>(() => fixture.SignInAsync(authenticator));
        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.VerificationFailed);
    }

    [TestMethod]
    [DataRow("http://localhost:5173")]
    [DataRow("https://localhost:7001")]
    [DataRow("https://localhost")]
    public async Task LocalhostOrigin_IsOnlyAllowedWhenEnabled(string origin)
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(authenticator);
        authenticator.Origin = origin;

        await Should.ThrowAsync<UnauthorizedApiException>(() => fixture.SignInAsync(authenticator));

        using var developmentFixture = CreateFixture(o => o.AllowLocalhostOrigins = true);
        using var developmentAuthenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await developmentFixture.RegisterAsync(developmentAuthenticator);
        developmentAuthenticator.Origin = origin;

        await developmentFixture.SignInAsync(developmentAuthenticator);
    }

    [TestMethod]
    [DataRow("http://127.0.0.1:5173")]
    [DataRow("http://localhost.evil.example")]
    [DataRow("http://sub.localhost:5173")]
    public async Task LocalhostOption_DoesNotAllowOtherOrigins(string origin)
    {
        using var fixture = CreateFixture(o => o.AllowLocalhostOrigins = true);
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(authenticator);
        authenticator.Origin = origin;

        await Should.ThrowAsync<UnauthorizedApiException>(() => fixture.SignInAsync(authenticator));
    }

    [TestMethod]
    public async Task AndroidAppOrigin_IsAllowedWhenConfigured()
    {
        const string AndroidOrigin = "android:apk-key-hash:47DEQpj8HBSa-_TImW-5JCeuQeRkm5NMpJWZG3hSuFU";

        using var fixture = CreateFixture(o => o.AllowedOrigins.Add(AndroidOrigin));
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(authenticator);
        authenticator.Origin = AndroidOrigin;

        await fixture.SignInAsync(authenticator);
    }

    [TestMethod]
    public async Task UserVerification_IsRequiredByDefault()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(authenticator);
        authenticator.VerifiesUser = false;

        var ex = await Should.ThrowAsync<UnauthorizedApiException>(() => fixture.SignInAsync(authenticator));
        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.VerificationFailed);

        using var relaxedFixture = CreateFixture(o => o.RequireUserVerification = false);
        using var relaxedAuthenticator = new SoftwareAuthenticator(PasskeyFixture.Origin) { VerifiesUser = false };
        await relaxedFixture.RegisterAsync(relaxedAuthenticator);

        await relaxedFixture.SignInAsync(relaxedAuthenticator);
    }

    [TestMethod]
    [DataRow("not json")]
    [DataRow("[]")]
    [DataRow("{}")]
    [DataRow("""{"id":"AAAA","rawId":"AAAA","type":"public-key","response":{}}""")]
    [DataRow("""{"id":"AAAA","rawId":"not base64url!","type":"public-key","response":{"clientDataJSON":"","authenticatorData":"","signature":""}}""")]
    public async Task MalformedCredential_IsRejected(string credentialJson)
    {
        using var fixture = CreateFixture();
        var ceremony = await fixture.CreateService().BeginSignInAsync(codeChallenge: fixture.Pkce.Challenge);

        await Should.ThrowAsync<BadRequestApiException>(() => fixture.CreateService().FinishSignInAsync(ceremony.Id, credentialJson, fixture.Pkce.Verifier));
    }

    [TestMethod]
    public async Task Pkce_RightVerifier_SignsIn()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(authenticator);
        var (verifier, challenge) = PasskeyFixture.CreatePkce();

        var ceremony = await fixture.CreateService().BeginSignInAsync(codeChallenge: challenge);
        var verified = await fixture.CreateService().FinishSignInAsync(ceremony.Id, authenticator.Get(ceremony.OptionsJson), verifier);

        verified.UserId.ShouldBe("user-1");
    }

    [TestMethod]
    public async Task Pkce_MissingVerifier_IsRejectedAndUsesUpTheCeremony()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(authenticator);
        var (verifier, challenge) = PasskeyFixture.CreatePkce();

        var ceremony = await fixture.CreateService().BeginSignInAsync(codeChallenge: challenge);
        string credential = authenticator.Get(ceremony.OptionsJson);

        var ex = await Should.ThrowAsync<UnauthorizedApiException>(() => fixture.CreateService().FinishSignInAsync(ceremony.Id, credential));
        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.CodeVerifierMismatch);

        ex = await Should.ThrowAsync<UnauthorizedApiException>(() => fixture.CreateService().FinishSignInAsync(ceremony.Id, credential, verifier));
        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.CeremonyExpired);
        fixture.Store.UpdateCount.ShouldBe(0);
    }

    [TestMethod]
    public async Task Pkce_WrongVerifier_IsRejectedAndUsesUpTheCeremony()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(authenticator);
        var (verifier, challenge) = PasskeyFixture.CreatePkce();

        var ceremony = await fixture.CreateService().BeginSignInAsync(codeChallenge: challenge);
        string credential = authenticator.Get(ceremony.OptionsJson);

        var ex = await Should.ThrowAsync<UnauthorizedApiException>(() =>
            fixture.CreateService().FinishSignInAsync(ceremony.Id, credential, PasskeyFixture.CreatePkce().Verifier));

        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.CodeVerifierMismatch);

        ex = await Should.ThrowAsync<UnauthorizedApiException>(() => fixture.CreateService().FinishSignInAsync(ceremony.Id, credential, verifier));
        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.CeremonyExpired);
    }

    [TestMethod]
    [DataRow("short")]
    [DataRow("has spaces in it, which no verifier may have ...")]
    public async Task Pkce_MalformedVerifier_IsRejected(string verifier)
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(authenticator);

        var ceremony = await fixture.CreateService().BeginSignInAsync(codeChallenge: PasskeyFixture.CreatePkce().Challenge);

        var ex = await Should.ThrowAsync<UnauthorizedApiException>(() =>
            fixture.CreateService().FinishSignInAsync(ceremony.Id, authenticator.Get(ceremony.OptionsJson), verifier));

        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.CodeVerifierMismatch);
    }

    [TestMethod]
    public async Task Pkce_VerifierWithoutChallenge_IsRejected()
    {
        using var fixture = CreateFixture(o => o.RequireCodeChallenge = false);
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(authenticator);

        var ceremony = await fixture.CreateService().BeginSignInAsync(codeChallenge: null);

        await Should.ThrowAsync<BadRequestApiException>(() =>
            fixture.CreateService().FinishSignInAsync(ceremony.Id, authenticator.Get(ceremony.OptionsJson), PasskeyFixture.CreatePkce().Verifier));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("too-short")]
    [DataRow("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-c")] // 42 characters
    [DataRow("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM=")] // padded
    [DataRow("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw+cM")] // base64, not base64url
    public async Task Pkce_MalformedChallenge_IsRejected(string challenge)
    {
        using var fixture = CreateFixture();
        using var relaxedFixture = CreateFixture(o => o.RequireCodeChallenge = false);

        var ex = await Should.ThrowAsync<BadRequestApiException>(() => fixture.CreateService().BeginSignInAsync(codeChallenge: challenge));
        ex.ErrorCode.ShouldBe(SignInErrorCodes.CodeChallengeInvalid);
        ex.Message.ShouldBe("The code challenge is not valid.");

        // A challenge that is given must be valid even when none is required.
        ex = await Should.ThrowAsync<BadRequestApiException>(() => relaxedFixture.CreateService().BeginSignInAsync(codeChallenge: challenge));
        ex.ErrorCode.ShouldBe(SignInErrorCodes.CodeChallengeInvalid);
    }

    [TestMethod]
    public async Task Pkce_ChallengeIsRequiredByDefault()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(authenticator);

        var ex = await Should.ThrowAsync<BadRequestApiException>(() => fixture.CreateService().BeginSignInAsync(codeChallenge: null));
        ex.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        ex.ErrorCode.ShouldBe(SignInErrorCodes.CodeChallengeInvalid);
        ex.ErrorCode.ShouldBe("code-challenge-invalid");
        ex.Message.ShouldBe("A code challenge is required to sign in.");

        ex = await Should.ThrowAsync<BadRequestApiException>(() => fixture.CreateService().BeginSignInAsync(codeChallenge: null, "user-1"));
        ex.ErrorCode.ShouldBe(SignInErrorCodes.CodeChallengeInvalid);
    }

    [TestMethod]
    public async Task Pkce_ChallengeCanBeLeftOutWhenNotRequired()
    {
        using var fixture = CreateFixture(o => o.RequireCodeChallenge = false);
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(authenticator);

        var ceremony = await fixture.CreateService().BeginSignInAsync(codeChallenge: null);
        var verified = await fixture.CreateService().FinishSignInAsync(ceremony.Id, authenticator.Get(ceremony.OptionsJson));

        verified.UserId.ShouldBe("user-1");
        verified.CodeChallenge.ShouldBeNull();

        // Without a challenge, the sign-in cannot continue with a sign-in code.
        await Should.ThrowAsync<InvalidOperationException>(() => fixture.CreateSignInCodes().CreateAsync(verified));
    }

    [TestMethod]
    public async Task Pkce_ChallengeOfSignIn_ContinuesWithASignInCode()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(authenticator);
        var (verifier, challenge) = PasskeyFixture.CreatePkce();

        // The challenge sent when the sign-in began binds the sign-in code too, so the client redeems it with the same verifier.
        var ceremony = await fixture.CreateService().BeginSignInAsync(codeChallenge: challenge);
        var verified = await fixture.CreateService().FinishSignInAsync(ceremony.Id, authenticator.Get(ceremony.OptionsJson), verifier);

        verified.CodeChallenge.ShouldBe(challenge);

        string code = await fixture.CreateSignInCodes().CreateAsync(verified);
        string otherCode = await fixture.CreateSignInCodes().CreateAsync(verified);

        (await fixture.CreateSignInCodes().RedeemAsync(otherCode, PasskeyFixture.CreatePkce().Verifier)).ShouldBeNull();
        (await fixture.CreateSignInCodes().RedeemAsync(code, verifier)).ShouldNotBeNull().UserId.ShouldBe("user-1");

        string codeWithAppData = await fixture.CreateSignInCodes().CreateAsync(verified, appData: "v2");
        (await fixture.CreateSignInCodes().RedeemAsync(codeWithAppData, verifier)).ShouldNotBeNull().AppData.ShouldBe("v2");
    }
}

[PrefixTestClass]
public sealed class SignInTestsWithMemoryStore : SignInTests
{
    protected override StoreKind StoreKind => StoreKind.Memory;
}

[PrefixTestClass]
public sealed class SignInTestsWithSharedStore : SignInTests
{
    protected override StoreKind StoreKind => StoreKind.Shared;
}
