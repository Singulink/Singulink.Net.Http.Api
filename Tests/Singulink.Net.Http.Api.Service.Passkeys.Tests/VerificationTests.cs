using Singulink.Net.Http.Api.Service.Passkeys.Infrastructure;
using Singulink.Net.Http.Api.Service.Testing;

namespace Singulink.Net.Http.Api.Service.Passkeys;

/// <summary>
/// Passkey verification tests, run with ceremonies kept in the memory store and in a shared store, where every request could run on a different server.
/// </summary>
public abstract class VerificationTests
{
    protected abstract StoreKind StoreKind { get; }

    protected PasskeyFixture CreateFixture(Action<PasskeyOptions>? configure = null) => new(configure, StoreKind);

    [TestMethod]
    public async Task VerifiesTheUserAndSavesUse()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin) { CountsSignatures = true };
        await fixture.RegisterAsync(authenticator);

        var ceremony = await fixture.CreateService().BeginVerificationAsync("user-1");
        var verified = await fixture.CreateService().FinishVerificationAsync("user-1", ceremony.Id, authenticator.Get(ceremony.OptionsJson));

        verified.UserId.ShouldBe("user-1");
        verified.Passkey.SignCount.ShouldBe(1u);
        fixture.Store.Get(authenticator.CredentialId).LastUsedUtc.ShouldBe(fixture.Time.GetUtcNow().UtcDateTime);
    }

    [TestMethod]
    public async Task WithSessionToken_VerifiesItsUser()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(authenticator);
        var user = new TestUserToken("user-1");

        var ceremony = await fixture.CreateService().BeginVerificationAsync(user);
        var verified = await fixture.CreateService().FinishVerificationAsync(user, ceremony.Id, authenticator.Get(ceremony.OptionsJson));

        verified.UserId.ShouldBe("user-1");

        var other = await fixture.CreateService().BeginVerificationAsync(user);

        var ex = await Should.ThrowAsync<ValidationApiException>(() =>
            fixture.CreateService().FinishVerificationAsync(new TestUserToken("user-2"), other.Id, authenticator.Get(other.OptionsJson)));

        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.CeremonyExpired);
    }

    [TestMethod]
    public async Task FinishingForAnotherUser_IsRejected()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(authenticator);

        var ceremony = await fixture.CreateService().BeginVerificationAsync("user-1");

        var ex = await Should.ThrowAsync<ValidationApiException>(() =>
            fixture.CreateService().FinishVerificationAsync("user-2", ceremony.Id, authenticator.Get(ceremony.OptionsJson)));

        ex.StatusCode.ShouldBe((HttpStatusCode)422);
        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.CeremonyExpired);
    }

    [TestMethod]
    public async Task PasskeyOfAnotherUser_IsRejectedAsWrongAccount()
    {
        using var fixture = CreateFixture();
        using var mine = new SoftwareAuthenticator(PasskeyFixture.Origin);
        using var theirs = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(mine, "user-1");
        await fixture.RegisterAsync(theirs, "user-2");

        var ceremony = await fixture.CreateService().BeginVerificationAsync("user-1");

        var ex = await Should.ThrowAsync<ValidationApiException>(() =>
            fixture.CreateService().FinishVerificationAsync("user-1", ceremony.Id, theirs.Get(ceremony.OptionsJson)));

        ex.StatusCode.ShouldBe((HttpStatusCode)422);
        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.WrongAccount);
        fixture.Store.UpdateCount.ShouldBe(0);
    }

    [TestMethod]
    public async Task UnregisteredPasskey_IsAValidationError()
    {
        using var fixture = CreateFixture();
        using var mine = new SoftwareAuthenticator(PasskeyFixture.Origin);
        using var unregistered = new SoftwareAuthenticator(PasskeyFixture.Origin) { UserHandle = System.Text.Encoding.UTF8.GetBytes("user-1") };
        await fixture.RegisterAsync(mine);

        var ceremony = await fixture.CreateService().BeginVerificationAsync("user-1");

        var ex = await Should.ThrowAsync<ValidationApiException>(() =>
            fixture.CreateService().FinishVerificationAsync("user-1", ceremony.Id, unregistered.Get(ceremony.OptionsJson)));

        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.NotRegistered);
    }

    [TestMethod]
    public async Task FailedVerification_IsAValidationErrorWithAPlainMessage()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(authenticator);
        authenticator.Origin = "https://evil.example";

        var ceremony = await fixture.CreateService().BeginVerificationAsync("user-1");

        var ex = await Should.ThrowAsync<ValidationApiException>(() =>
            fixture.CreateService().FinishVerificationAsync("user-1", ceremony.Id, authenticator.Get(ceremony.OptionsJson)));

        ex.StatusCode.ShouldBe((HttpStatusCode)422);
        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.VerificationFailed);

        // Fido2NetLib's description of the failure is logged rather than sent to the client.
        ex.Message.ShouldBe("The passkey could not be verified.");
        fixture.Logs.Entries.ShouldContain(e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Exception is Fido2NetLib.Fido2VerificationException);
    }

    [TestMethod]
    public async Task ExpiredCeremony_IsAValidationError()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(authenticator);

        var ceremony = await fixture.CreateService().BeginVerificationAsync("user-1");
        string credential = authenticator.Get(ceremony.OptionsJson);
        fixture.Time.Advance(TimeSpan.FromMinutes(5));

        var ex = await Should.ThrowAsync<ValidationApiException>(() => fixture.CreateService().FinishVerificationAsync("user-1", ceremony.Id, credential));
        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.CeremonyExpired);
    }

    [TestMethod]
    public async Task Verification_HasNoCodeChallenge()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(authenticator);

        var ceremony = await fixture.CreateService().BeginVerificationAsync("user-1");
        var verified = await fixture.CreateService().FinishVerificationAsync("user-1", ceremony.Id, authenticator.Get(ceremony.OptionsJson));

        verified.CodeChallenge.ShouldBeNull();

        // A verification cannot sign anyone in.
        await Should.ThrowAsync<InvalidOperationException>(() => fixture.CreateSignInCodes().CreateAsync(verified));
    }

    [TestMethod]
    public async Task SignInCeremony_CannotFinishAVerification()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(authenticator);

        var ceremony = await fixture.CreateService().BeginSignInAsync(fixture.Pkce.Challenge, "user-1");

        var ex = await Should.ThrowAsync<ValidationApiException>(() =>
            fixture.CreateService().FinishVerificationAsync("user-1", ceremony.Id, authenticator.Get(ceremony.OptionsJson)));

        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.CeremonyExpired);
    }

    [TestMethod]
    public async Task Ceremony_IsSingleUse()
    {
        using var fixture = CreateFixture();
        using var authenticator = new SoftwareAuthenticator(PasskeyFixture.Origin);
        await fixture.RegisterAsync(authenticator);

        var ceremony = await fixture.CreateService().BeginVerificationAsync("user-1");
        string credential = authenticator.Get(ceremony.OptionsJson);
        await fixture.CreateService().FinishVerificationAsync("user-1", ceremony.Id, credential);

        var ex = await Should.ThrowAsync<ValidationApiException>(() => fixture.CreateService().FinishVerificationAsync("user-1", ceremony.Id, credential));
        ex.ErrorCode.ShouldBe(PasskeyErrorCodes.CeremonyExpired);
    }
}

[PrefixTestClass]
public sealed class VerificationTestsWithMemoryStore : VerificationTests
{
    protected override StoreKind StoreKind => StoreKind.Memory;
}

[PrefixTestClass]
public sealed class VerificationTestsWithSharedStore : VerificationTests
{
    protected override StoreKind StoreKind => StoreKind.Shared;
}
