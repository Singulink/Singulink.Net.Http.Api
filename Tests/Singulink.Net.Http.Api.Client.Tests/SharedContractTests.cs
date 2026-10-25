namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Tests for the shared contracts whose validation differs between the .NET and .NET Standard builds.
/// </summary>
[PrefixTestClass]
public sealed class SharedContractTests
{
    private static HubMessage<string> Echo { get; } = new();

    [TestMethod]
    public void HubDefinitionName_DefaultsToMemberName()
    {
        Echo.Name.ShouldBe(nameof(Echo));
    }

    [TestMethod]
    public void HubDefinitionName_Empty_ThrowsArgumentException()
    {
        Should.Throw<ArgumentException>(() => new HubMessage(string.Empty)).ParamName.ShouldBe("name");
        Should.Throw<ArgumentException>(() => new HubMethod<int, int>(string.Empty)).ParamName.ShouldBe("name");
        Should.Throw<ArgumentException>(() => new HubStream<int>(string.Empty)).ParamName.ShouldBe("name");
    }

    [TestMethod]
    public void ValidationApiException_WithInnerException_KeepsIt()
    {
        var inner = new FormatException("bad");
        var ex = new ValidationApiException("Invalid.", inner);

        ex.StatusCode.ShouldBe((HttpStatusCode)422);
        ex.Message.ShouldBe("Invalid.");
        ex.InnerException.ShouldBeSameAs(inner);
    }

    [TestMethod]
    public void ErrorCode_ValidChars_AreAccepted()
    {
        new ApiException(HttpStatusCode.Conflict, "message") { ErrorCode = "Valid_code-123" }.ErrorCode.ShouldBe("Valid_code-123");
    }

    [TestMethod]
    public void ErrorCode_Empty_IsNormalizedToNull()
    {
        new ApiException(HttpStatusCode.Conflict, "message") { ErrorCode = string.Empty }.ErrorCode.ShouldBeNull();
    }

    [TestMethod]
    [DataRow("has space")]
    [DataRow("bracket]")]
    [DataRow("café")]
    public void ErrorCode_InvalidChars_ThrowsArgumentException(string errorCode)
    {
        Should.Throw<ArgumentException>(() => new ApiException(HttpStatusCode.Conflict, "message") { ErrorCode = errorCode });
    }

    [TestMethod]
    public void RetryAfter_Negative_ThrowsArgumentOutOfRangeException()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new ApiException(HttpStatusCode.ServiceUnavailable, "message") { RetryAfter = TimeSpan.FromTicks(-1) });
    }

    [TestMethod]
    public void RetryAfter_ZeroPositiveOrNull_IsKept()
    {
        new ApiException(HttpStatusCode.ServiceUnavailable, "message") { RetryAfter = TimeSpan.Zero }.RetryAfter.ShouldBe(TimeSpan.Zero);
        new ApiException(HttpStatusCode.ServiceUnavailable, "message") { RetryAfter = TimeSpan.FromSeconds(5) }.RetryAfter.ShouldBe(TimeSpan.FromSeconds(5));
        new ApiException(HttpStatusCode.ServiceUnavailable, "message") { RetryAfter = null }.RetryAfter.ShouldBeNull();
    }

    [TestMethod]
    public void StatusCodesMissingFromNetStandard_AreMapped()
    {
        new ValidationApiException("message").StatusCode.ShouldBe((HttpStatusCode)422);
        new UserRequiredApiException("message").StatusCode.ShouldBe((HttpStatusCode)428);
        new TooManyRequestsApiException("message").StatusCode.ShouldBe((HttpStatusCode)429);
    }

    [TestMethod]
    public void ToolkitErrorCodes_HaveTheirWireValues()
    {
        ApiErrorCodes.SignInRequired.ShouldBe("sign-in-required");
        ApiErrorCodes.OriginBlocked.ShouldBe("origin-blocked");
        ApiErrorCodes.UserAgentRequired.ShouldBe("user-agent-required");
        ApiErrorCodes.RateLimited.ShouldBe("rate-limited");
        ApiErrorCodes.ShortLivedStoreFull.ShouldBe("short-lived-store-full");
        ApiErrorCodes.CloudflareRangesUnavailable.ShouldBe("cloudflare-ranges-unavailable");

        SignInErrorCodes.SignInCodeInvalid.ShouldBe("sign-in-code-invalid");
        SignInErrorCodes.DeviceLinkTooManyWrongCodes.ShouldBe("device-link-too-many-wrong-codes");
        SignInErrorCodes.DeviceLinkExpired.ShouldBe("device-link-expired");
        SignInErrorCodes.CodeChallengeInvalid.ShouldBe("code-challenge-invalid");

        PasskeyErrorCodes.CeremonyExpired.ShouldBe("passkey-ceremony-expired");
        PasskeyErrorCodes.CodeVerifierMismatch.ShouldBe("passkey-code-verifier-mismatch");
        PasskeyErrorCodes.NoPasskeys.ShouldBe("passkey-none");
        PasskeyErrorCodes.NotRegistered.ShouldBe("passkey-not-registered");
        PasskeyErrorCodes.WrongAccount.ShouldBe("passkey-wrong-account");
        PasskeyErrorCodes.VerificationFailed.ShouldBe("passkey-verification-failed");
        PasskeyErrorCodes.AlreadyRegistered.ShouldBe("passkey-already-registered");
    }

    [TestMethod]
    public void ToolkitErrorCodes_AreValidAndDistinct()
    {
        string[] codes = [.. new[] { typeof(ApiErrorCodes), typeof(SignInErrorCodes), typeof(PasskeyErrorCodes) }
            .SelectMany(t => t.GetFields())
            .Select(f => (string)f.GetRawConstantValue()!)];

        codes.Length.ShouldBe(17);
        codes.ShouldBeUnique();

        foreach (string code in codes)
            new ApiException(HttpStatusCode.Conflict, "message") { ErrorCode = code }.ErrorCode.ShouldBe(code);
    }
}
