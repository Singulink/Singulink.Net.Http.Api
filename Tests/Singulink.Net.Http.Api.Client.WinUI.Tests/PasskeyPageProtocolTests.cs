namespace Singulink.Net.Http.Api.Client.Passkeys;

[PrefixTestClass]
public sealed class PasskeyPageProtocolTests
{
    private const string OptionsJson = """{"challenge":"Y2hhbGxlbmdl","rpId":"example.com","userVerification":"required"}""";
    private const string CredentialJson =
        """{"id":"Y3JlZA","rawId":"Y3JlZA","type":"public-key","response":{"clientDataJSON":"e30"},"clientExtensionResults":{}}""";

    private static readonly Uri PageUri = new("https://example.com/passkey");
    private static readonly Uri CustomSchemeRedirect = new("myapp://auth");

    [TestMethod]
    public void BuildRequestUri_AddsProtocolParameters()
    {
        var uri = PasskeyPageProtocol.BuildRequestUri(PageUri, PasskeyPageProtocol.GetOperation, OptionsJson, CustomSchemeRedirect, "state-1");

        uri.GetLeftPart(UriPartial.Path).ShouldBe("https://example.com/passkey");

        var query = PasskeyPageProtocol.ParseQuery(uri.OriginalString);
        query.Keys.ShouldBe(["op", "options", "redirect", "state"], ignoreOrder: true);
        query["op"].ShouldBe("get");
        DecodeBase64Url(query["options"]).ShouldBe(OptionsJson);
        query["redirect"].ShouldBe("myapp://auth");
        query["state"].ShouldBe("state-1");
    }

    [TestMethod]
    public void BuildRequestUri_CreateOperation()
    {
        var uri = PasskeyPageProtocol.BuildRequestUri(PageUri, PasskeyPageProtocol.CreateOperation, OptionsJson, CustomSchemeRedirect, "s");

        PasskeyPageProtocol.ParseQuery(uri.OriginalString)["op"].ShouldBe("create");
    }

    [TestMethod]
    public void BuildRequestUri_EncodesOptionsAsUnpaddedBase64Url()
    {
        // Characters whose UTF-8 bytes produce '+', '/' and '=' in standard base64.
        const string json = """{"name":"ÿþ>>>???","displayName":"Zoë 🎉"}""";

        var uri = PasskeyPageProtocol.BuildRequestUri(PageUri, PasskeyPageProtocol.CreateOperation, json, CustomSchemeRedirect, "s");
        string rawOptions = GetRawQueryValue(uri.OriginalString, "options");

        rawOptions.ShouldMatch("^[A-Za-z0-9_-]+$");
        DecodeBase64Url(rawOptions).ShouldBe(json);
    }

    [TestMethod]
    public void BuildRequestUri_EscapesRedirectAndState()
    {
        var redirect = new Uri("http://127.0.0.1:5000/callback?x=1&y=2");

        var uri = PasskeyPageProtocol.BuildRequestUri(PageUri, PasskeyPageProtocol.GetOperation, OptionsJson, redirect, "a b&c");

        GetRawQueryValue(uri.OriginalString, "redirect").ShouldBe("http%3A%2F%2F127.0.0.1%3A5000%2Fcallback%3Fx%3D1%26y%3D2");
        GetRawQueryValue(uri.OriginalString, "state").ShouldBe("a%20b%26c");

        var query = PasskeyPageProtocol.ParseQuery(uri.OriginalString);
        query["redirect"].ShouldBe("http://127.0.0.1:5000/callback?x=1&y=2");
        query["state"].ShouldBe("a b&c");
    }

    [TestMethod]
    public void BuildRequestUri_KeepsRedirectAsConfigured()
    {
        // Uri normalization would add a trailing slash to "myapp://auth"; the redirect must match what the app registered.
        var uri = PasskeyPageProtocol.BuildRequestUri(PageUri, PasskeyPageProtocol.GetOperation, OptionsJson, new Uri("myapp://auth"), "s");

        PasskeyPageProtocol.ParseQuery(uri.OriginalString)["redirect"].ShouldBe("myapp://auth");
    }

    [TestMethod]
    public void BuildRequestUri_KeepsExistingQueryAndFragment()
    {
        var uri = PasskeyPageProtocol.BuildRequestUri(
            new Uri("https://example.com:8443/app/passkey?lang=de#top"), PasskeyPageProtocol.GetOperation, OptionsJson, CustomSchemeRedirect, "s");

        uri.OriginalString.ShouldStartWith("https://example.com:8443/app/passkey?lang=de&op=get&options=");
        uri.OriginalString.ShouldEndWith("&state=s#top");
    }

    [TestMethod]
    public void BuildRequestUri_EmptyExistingQuery()
    {
        var uri = PasskeyPageProtocol.BuildRequestUri(
            new Uri("https://example.com/passkey?"), PasskeyPageProtocol.GetOperation, OptionsJson, CustomSchemeRedirect, "s");

        uri.OriginalString.ShouldStartWith("https://example.com/passkey?op=get&");
    }

    [TestMethod]
    public void CreateState_IsRandomUnpaddedBase64Url()
    {
        string state1 = PasskeyPageProtocol.CreateState();
        string state2 = PasskeyPageProtocol.CreateState();

        state1.ShouldMatch("^[A-Za-z0-9_-]{43}$");
        state2.ShouldMatch("^[A-Za-z0-9_-]{43}$");
        state1.ShouldNotBe(state2);
    }

    [TestMethod]
    public void ParseResponse_ReturnsCredential()
    {
        string response = $"myapp://auth?state=s1&credential={EncodeBase64Url(CredentialJson)}";

        PasskeyPageProtocol.ParseResponse(response, "s1").ShouldBe(CredentialJson);
    }

    [TestMethod]
    public void ParseResponse_DecodesNonAsciiCredential()
    {
        const string json = """{"id":"x","name":"Zoë ✓ 🎉"}""";

        PasskeyPageProtocol.ParseResponse($"myapp://auth?state=s1&credential={EncodeBase64Url(json)}", "s1").ShouldBe(json);
    }

    [TestMethod]
    public void ParseResponse_IgnoresOtherParametersAndFragment()
    {
        string response = $"http://127.0.0.1:5000/callback?x=1&state=s1&credential={EncodeBase64Url(CredentialJson)}#fragment";

        PasskeyPageProtocol.ParseResponse(response, "s1").ShouldBe(CredentialJson);
    }

    [TestMethod]
    public void ParseResponse_NullResponse_Throws()
    {
        var ex = Should.Throw<PasskeyAuthenticatorException>(() => PasskeyPageProtocol.ParseResponse(null, "s1"));

        ex.Reason.ShouldBe(PasskeyErrorReason.Unknown);
    }

    [TestMethod]
    [DataRow("myapp://auth")]
    [DataRow("myapp://auth?credential=e30")]
    [DataRow("myapp://auth?state=other&credential=e30")]
    [DataRow("myapp://auth?state=S1&credential=e30")]
    [DataRow("myapp://auth?state=&credential=e30")]
    [DataRow("myapp://auth?state=other&error=NotAllowedError&message=x")]
    public void ParseResponse_StateMismatch_Throws(string response)
    {
        var ex = Should.Throw<PasskeyAuthenticatorException>(() => PasskeyPageProtocol.ParseResponse(response, "s1"));

        ex.Reason.ShouldBe(PasskeyErrorReason.Unknown);
        ex.Message.ShouldContain("does not belong to this request");
    }

    [TestMethod]
    [DataRow("AbortError", PasskeyErrorReason.Cancelled)]
    [DataRow("NotAllowedError", PasskeyErrorReason.NotAllowed)]
    [DataRow("InvalidStateError", PasskeyErrorReason.AlreadyOnAuthenticator)]
    [DataRow("NotSupportedError", PasskeyErrorReason.NotSupported)]
    [DataRow("ConstraintError", PasskeyErrorReason.RequirementsNotMet)]
    [DataRow("SecurityError", PasskeyErrorReason.Security)]
    [DataRow("UnknownError", PasskeyErrorReason.Unknown)]
    [DataRow("TypeError", PasskeyErrorReason.Unknown)]
    public void ParseResponse_Error_ThrowsWithReason(string errorName, PasskeyErrorReason expectedReason)
    {
        var ex = Should.Throw<PasskeyAuthenticatorException>(() =>
            PasskeyPageProtocol.ParseResponse($"myapp://auth?state=s1&error={errorName}&message=Some+message%21", "s1"));

        ex.Reason.ShouldBe(expectedReason);
        ex.Message.ShouldEndWith("Some message!");
    }

    [TestMethod]
    public void ParseResponse_UnmappedError_KeepsNameInMessage()
    {
        string response = "myapp://auth?state=s1&error=TypeError&message=Bad";
        var ex = Should.Throw<PasskeyAuthenticatorException>(() => PasskeyPageProtocol.ParseResponse(response, "s1"));

        ex.Message.ShouldBe("TypeError: Bad");
    }

    [TestMethod]
    public void ParseResponse_ErrorWithoutMessage_UsesDefaultMessage()
    {
        var ex = Should.Throw<PasskeyAuthenticatorException>(() => PasskeyPageProtocol.ParseResponse("myapp://auth?state=s1&error=AbortError", "s1"));

        ex.Reason.ShouldBe(PasskeyErrorReason.Cancelled);
        ex.Message.ShouldBe(new PasskeyAuthenticatorException(PasskeyErrorReason.Cancelled, null).Message);
    }

    [TestMethod]
    public void ParseResponse_ErrorTakesPrecedenceOverCredential()
    {
        string response = $"myapp://auth?state=s1&credential={EncodeBase64Url(CredentialJson)}&error=NotAllowedError";

        Should.Throw<PasskeyAuthenticatorException>(() => PasskeyPageProtocol.ParseResponse(response, "s1")).Reason.ShouldBe(PasskeyErrorReason.NotAllowed);
    }

    [TestMethod]
    [DataRow("myapp://auth?state=s1")]
    [DataRow("myapp://auth?state=s1&credential=")]
    public void ParseResponse_MissingCredential_Throws(string response)
    {
        var ex = Should.Throw<PasskeyAuthenticatorException>(() => PasskeyPageProtocol.ParseResponse(response, "s1"));

        ex.Reason.ShouldBe(PasskeyErrorReason.Unknown);
        ex.Message.ShouldContain("no credential");
    }

    [TestMethod]
    [DataRow("not*base64url")]
    [DataRow("_w")] // base64url of 0xFF, which isn't valid UTF-8
    public void ParseResponse_MalformedCredential_Throws(string encodedCredential)
    {
        string response = $"myapp://auth?state=s1&credential={encodedCredential}";
        var ex = Should.Throw<PasskeyAuthenticatorException>(() => PasskeyPageProtocol.ParseResponse(response, "s1"));

        ex.Reason.ShouldBe(PasskeyErrorReason.Unknown);
        ex.Message.ShouldContain("malformed");
        ex.InnerException.ShouldNotBeNull();
    }

    [TestMethod]
    [DataRow("NotAllowedError: The operation either timed out or was not allowed.", PasskeyErrorReason.NotAllowed,
        "The operation either timed out or was not allowed.")]
    [DataRow("Error: AbortError: The person cancelled.", PasskeyErrorReason.Cancelled, "The person cancelled.")]
    [DataRow("InvalidStateError: Already registered: yes.", PasskeyErrorReason.AlreadyOnAuthenticator, "Already registered: yes.")]
    [DataRow("ConstraintError: User verification is not available.", PasskeyErrorReason.RequirementsNotMet, "User verification is not available.")]
    [DataRow("NotSupportedError: This browser does not support passkeys.", PasskeyErrorReason.NotSupported, "This browser does not support passkeys.")]
    [DataRow("SecurityError: The RP ID is invalid.", PasskeyErrorReason.Security, "The RP ID is invalid.")]
    [DataRow("TypeError: Failed to parse.", PasskeyErrorReason.Unknown, "TypeError: Failed to parse.")]
    [DataRow("Something went wrong", PasskeyErrorReason.Unknown, "Something went wrong")]
    [DataRow("Not an error name: text", PasskeyErrorReason.Unknown, "Not an error name: text")]
    [DataRow("Bad_Error: text", PasskeyErrorReason.Unknown, "Bad_Error: text")]
    public void CreateScriptException_ParsesNameAndMessage(string scriptMessage, PasskeyErrorReason expectedReason, string expectedMessage)
    {
        var ex = PasskeyPageProtocol.CreateScriptException(scriptMessage);

        ex.Reason.ShouldBe(expectedReason);
        ex.Message.ShouldBe(expectedMessage);
    }

    [TestMethod]
    public void CreateScriptException_EmptyMessage_UsesDefaultMessage()
    {
        var ex = PasskeyPageProtocol.CreateScriptException("NotAllowedError: ");

        ex.Reason.ShouldBe(PasskeyErrorReason.NotAllowed);
        ex.Message.ShouldBe(new PasskeyAuthenticatorException(PasskeyErrorReason.NotAllowed, null).Message);
    }

    [TestMethod]
    public void ParseQuery_DecodesFormEncodingAndKeepsFirstValue()
    {
        var query = PasskeyPageProtocol.ParseQuery("x://y?a=1+2&b=%2B%26%3D&flag&&a=second#c=3");

        query.Count.ShouldBe(3);
        query["a"].ShouldBe("1 2");
        query["b"].ShouldBe("+&=");
        query["flag"].ShouldBe(string.Empty);
    }

    [TestMethod]
    [DataRow("x://y")]
    [DataRow("x://y?")]
    [DataRow("x://y#a=1")]
    public void ParseQuery_NoQuery_ReturnsEmpty(string uri)
    {
        PasskeyPageProtocol.ParseQuery(uri).ShouldBeEmpty();
    }

    private static string EncodeBase64Url(string value) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(value));

    private static string DecodeBase64Url(string value) => Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value));

    private static string GetRawQueryValue(string uri, string name)
    {
        string query = uri[(uri.IndexOf('?') + 1)..];
        int fragmentIndex = query.IndexOf('#');

        if (fragmentIndex >= 0)
            query = query[..fragmentIndex];

        return query.Split('&').Select(p => p.Split('=', 2)).Single(p => p[0] == name)[1];
    }
}
