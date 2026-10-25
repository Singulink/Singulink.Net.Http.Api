namespace Singulink.Net.Http.Api.Client.Passkeys;

[PrefixTestClass]
public sealed class PasskeySignalsTests
{
    private const string CredentialJson = """{"id":"Y3JlZA","rawId":"Y3JlZA","type":"public-key"}""";

    [TestMethod]
    public void ReadsCredentialIdAndRelyingPartyId()
    {
        bool result = PasskeySignals.TryGetUnknownCredentialArguments(
            CredentialJson, """{"challenge":"Y2hhbA","rpId":"example.com"}""", out string? rpId, out string? credentialId);

        result.ShouldBeTrue();
        credentialId.ShouldBe("Y3JlZA");
        rpId.ShouldBe("example.com");
    }

    [TestMethod]
    [DataRow("""{"challenge":"Y2hhbA"}""")]
    [DataRow("""{"challenge":"Y2hhbA","rpId":""}""")]
    [DataRow("""{"rpId":5}""")]
    [DataRow("not json")]
    public void OptionsWithoutRelyingPartyId_GiveNull(string optionsJson)
    {
        PasskeySignals.TryGetUnknownCredentialArguments(CredentialJson, optionsJson, out string? rpId, out string? credentialId).ShouldBeTrue();

        credentialId.ShouldBe("Y3JlZA");
        rpId.ShouldBeNull();
    }

    [TestMethod]
    [DataRow("""{"rawId":"Y3JlZA"}""")]
    [DataRow("""{"id":""}""")]
    [DataRow("""{"id":1}""")]
    [DataRow("""["Y3JlZA"]""")]
    [DataRow("not json")]
    public void CredentialWithoutId_ReturnsFalse(string credentialJson)
    {
        PasskeySignals.TryGetUnknownCredentialArguments(credentialJson, """{"rpId":"example.com"}""", out string? rpId, out string? credentialId)
            .ShouldBeFalse();

        credentialId.ShouldBeNull();
        rpId.ShouldBeNull();
    }
}
