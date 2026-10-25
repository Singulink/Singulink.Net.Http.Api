using Singulink.Net.Http.Api.DataContracts;

namespace Singulink.Net.Http.Api.Client.Passkeys;

[PrefixTestClass]
public sealed class PasskeyCredentialTests
{
    [TestMethod]
    [DataRow("""{"id":"Y3JlZA","authenticatorAttachment":"platform"}""", PasskeyAttachment.Platform)]
    [DataRow("""{"id":"Y3JlZA","authenticatorAttachment":"cross-platform"}""", PasskeyAttachment.CrossPlatform)]
    [DataRow("""{"id":"Y3JlZA","authenticatorAttachment":null}""", PasskeyAttachment.Unknown)]
    [DataRow("""{"id":"Y3JlZA","authenticatorAttachment":"hybrid"}""", PasskeyAttachment.Unknown)]
    [DataRow("""{"id":"Y3JlZA","authenticatorAttachment":1}""", PasskeyAttachment.Unknown)]
    [DataRow("""{"id":"Y3JlZA"}""", PasskeyAttachment.Unknown)]
    [DataRow("""["platform"]""", PasskeyAttachment.Unknown)]
    [DataRow("not json", PasskeyAttachment.Unknown)]
    public void FromJson_ReadsAttachmentAndKeepsJson(string json, PasskeyAttachment attachment)
    {
        var credential = PasskeyCredential.FromJson(json);

        credential.Json.ShouldBe(json);
        credential.Attachment.ShouldBe(attachment);
    }

    [TestMethod]
    public void Constructor_SetsValues()
    {
        var credential = new PasskeyCredential("{}", PasskeyAttachment.CrossPlatform);

        credential.Json.ShouldBe("{}");
        credential.Attachment.ShouldBe(PasskeyAttachment.CrossPlatform);
    }
}
