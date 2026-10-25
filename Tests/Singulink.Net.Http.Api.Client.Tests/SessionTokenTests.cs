namespace Singulink.Net.Http.Api.Client;

[PrefixTestClass]
public sealed class SessionTokenTests
{
    private const string ClearCookie = "__Host-session-token=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/; secure; httponly";

    // The cookies a service issues for persistent and non-persistent sessions.
    private const string PersistentCookie = "__Host-session-token=new-token; max-age=1209600; path=/; secure; samesite=none; httponly";
    private const string SessionOnlyCookie = "__Host-session-token=new-token; path=/; secure; samesite=none; httponly";

    private static FakeHttpMessageHandler CreateHandler(params string?[] setCookies)
    {
        int index = 0;

        return new FakeHttpMessageHandler(_ => {
            var response = Responses.NoContent();
            string? setCookie = index < setCookies.Length ? setCookies[index] : null;
            index++;

            return setCookie is null ? response : response.WithCookie(setCookie);
        });
    }

    [TestMethod]
    public async Task SetCookie_IsPickedUpAndSentWithNextRequest()
    {
        var handler = CreateHandler("__Host-session-token=abc123; path=/; secure; httponly; samesite=strict");
        var client = new TestApiClient(handler);

        await client.SendRequestAsync(client.CreatePostRequest("sign-in"));

        client.SessionToken.ShouldBe("abc123");

        await client.SendRequestAsync(client.CreateGetRequest("documents"));

        handler.Requests[0].Cookie.ShouldBeNull();
        handler.Requests[1].Cookie.ShouldBe("__Host-session-token=abc123");
    }

    [TestMethod]
    public async Task InitialToken_IsSentWithFirstRequest()
    {
        var handler = CreateHandler();
        var client = new TestApiClient(handler, "saved-token");

        await client.SendRequestAsync(client.CreateGetRequest("documents"));

        handler.SingleRequest.Cookie.ShouldBe("__Host-session-token=saved-token");
    }

    [TestMethod]
    public async Task RefreshedToken_ReplacesCurrentToken()
    {
        var handler = CreateHandler("__Host-session-token=refreshed");
        var client = new TestApiClient(handler, "saved-token");

        await client.SendRequestAsync(client.CreateGetRequest("documents"));
        await client.SendRequestAsync(client.CreateGetRequest("documents"));

        handler.Requests[0].Cookie.ShouldBe("__Host-session-token=saved-token");
        handler.Requests[1].Cookie.ShouldBe("__Host-session-token=refreshed");
    }

    [TestMethod]
    public async Task ClearedCookie_EndsSession()
    {
        var handler = CreateHandler(ClearCookie);
        var client = new TestApiClient(handler, "saved-token");

        await client.SendRequestAsync(client.CreatePostRequest("sign-out"));

        client.SessionToken.ShouldBeNull();

        await client.SendRequestAsync(client.CreateGetRequest("documents"));

        handler.Requests[1].Cookie.ShouldBeNull();
    }

    [TestMethod]
    public async Task OtherCookies_AreIgnored()
    {
        var handler = CreateHandler("other=1; path=/");
        var client = new TestApiClient(handler, "saved-token");

        await client.SendRequestAsync(client.CreateGetRequest("documents"));

        client.SessionToken.ShouldBe("saved-token");
    }

    [TestMethod]
    [DataRow("__Host-session-token=planted; path=/")]
    [DataRow(ClearCookie)]
    public async Task SetCookieFromOtherHost_IsIgnored(string setCookie)
    {
        var changes = new List<string?>();

        // A redirect changes the request's URI to the one the response comes from.
        var handler = new FakeHttpMessageHandler(request => {
            request.RequestUri = new Uri("https://other.example.com/v1/documents");
            return Responses.NoContent().WithCookie(setCookie);
        });

        var client = new TestApiClient(handler, "saved-token", changes.Add);

        await client.SendRequestAsync(client.CreateGetRequest("documents"));

        client.SessionToken.ShouldBe("saved-token");
        changes.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task SetCookieFromBaseHost_AfterRedirect_IsPickedUp()
    {
        var handler = new FakeHttpMessageHandler(request => {
            request.RequestUri = new Uri("https://API.example.com:8443/v2/documents");
            return Responses.NoContent().WithCookie("__Host-session-token=refreshed; path=/");
        });

        var client = new TestApiClient(handler, "saved-token");

        await client.SendRequestAsync(client.CreateGetRequest("documents"));

        client.SessionToken.ShouldBe("refreshed");
    }

    [TestMethod]
    public async Task SetCookieOnErrorResponse_IsPickedUp()
    {
        var handler = new FakeHttpMessageHandler(_ => Responses.Error(HttpStatusCode.Unauthorized, "Session expired.").WithCookie(ClearCookie));
        var client = new TestApiClient(handler, "saved-token");

        await Should.ThrowAsync<UnauthorizedApiException>(() => client.SendRequestAsync(client.CreateGetRequest("documents")));

        client.SessionToken.ShouldBeNull();
    }

    [TestMethod]
    public async Task PersistentSession_InvokesCallbackOnChanges()
    {
        var changes = new List<string?>();
        var client = new TestApiClient(CreateHandler("__Host-session-token=refreshed; max-age=1209600", ClearCookie), "saved-token", changes.Add);

        client.IsPersistentSession.ShouldBeTrue();

        await client.SendRequestAsync(client.CreateGetRequest("documents"));
        await client.SendRequestAsync(client.CreatePostRequest("sign-out"));

        changes.ShouldBe(["refreshed", null]);
        client.IsPersistentSession.ShouldBeFalse();
    }

    [TestMethod]
    public async Task CookieWithMaxAge_StartsPersistentSession()
    {
        var changes = new List<string?>();
        var client = new TestApiClient(CreateHandler(PersistentCookie), sessionToken: null, changes.Add);

        client.IsPersistentSession.ShouldBeFalse();

        await client.SendRequestAsync(client.CreatePostRequest("sign-in"));

        client.SessionToken.ShouldBe("new-token");
        client.IsPersistentSession.ShouldBeTrue();
        changes.ShouldBe(["new-token"]);
    }

    [TestMethod]
    public async Task CookieWithExpires_StartsPersistentSession()
    {
        var changes = new List<string?>();
        string cookie = $"__Host-session-token=new-token; expires={DateTimeOffset.UtcNow.AddDays(14):r}; path=/";
        var client = new TestApiClient(CreateHandler(cookie), sessionToken: null, changes.Add);

        await client.SendRequestAsync(client.CreatePostRequest("sign-in"));

        client.IsPersistentSession.ShouldBeTrue();
        changes.ShouldBe(["new-token"]);
    }

    [TestMethod]
    public async Task CookieWithoutExpiry_StartsSessionThatIsNotStored()
    {
        var changes = new List<string?>();
        var handler = CreateHandler(SessionOnlyCookie);
        var client = new TestApiClient(handler, sessionToken: null, changes.Add);

        await client.SendRequestAsync(client.CreatePostRequest("sign-in"));
        await client.SendRequestAsync(client.CreateGetRequest("documents"));

        client.SessionToken.ShouldBe("new-token");
        client.IsPersistentSession.ShouldBeFalse();
        changes.ShouldBeEmpty();
        handler.Requests[1].Cookie.ShouldBe("__Host-session-token=new-token");
    }

    [TestMethod]
    public async Task SessionOnlyCookie_ReplacingPersistentSession_DeletesStoredToken()
    {
        var changes = new List<string?>();
        var client = new TestApiClient(CreateHandler(SessionOnlyCookie), "saved-token", changes.Add);

        await client.SendRequestAsync(client.CreatePostRequest("sign-in"));

        client.SessionToken.ShouldBe("new-token");
        client.IsPersistentSession.ShouldBeFalse();
        changes.ShouldBe([null]);
    }

    [TestMethod]
    public async Task SignInRequiredWithoutSetCookie_EndsSession()
    {
        var changes = new List<string?>();
        var handler = new FakeHttpMessageHandler(_ => Responses.Error(HttpStatusCode.Unauthorized, ApiErrorCodes.SignInRequired, "User is not signed in."));
        var client = new TestApiClient(handler, "saved-token", changes.Add);

        await Should.ThrowAsync<UnauthorizedApiException>(() => client.SendRequestAsync(client.CreateGetRequest("documents")));

        client.SessionToken.ShouldBeNull();
        client.IsPersistentSession.ShouldBeFalse();
        changes.ShouldBe([null]);
    }

    [TestMethod]
    public async Task SignInRequiredInStream_EndsSession()
    {
        var changes = new List<string?>();
        var handler = new FakeHttpMessageHandler(_ => Responses.Stream(
            """{"item":{"id":1,"name":"a"}}""",
            """{"error":{"status":401,"code":"sign-in-required","message":"User is not signed in."}}"""));

        var client = new TestApiClient(handler, "saved-token", changes.Add);

        await Should.ThrowAsync<UnauthorizedApiException>(async () => {
            await foreach (var item in client.SendStreamingRequestAsync<TestItem>(client.CreateGetRequest("items")))
            {
            }
        });

        client.SessionToken.ShouldBeNull();
        changes.ShouldBe([null]);
    }

    [TestMethod]
    public async Task SignInRequiredForOldToken_KeepsNewToken()
    {
        var changes = new List<string?>();
        var handler = new FakeHttpMessageHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/sign-in", StringComparison.Ordinal)
            ? Responses.NoContent().WithCookie(PersistentCookie)
            : Responses.Error(HttpStatusCode.Unauthorized, ApiErrorCodes.SignInRequired, "User is not signed in."));

        var client = new TestApiClient(handler, "saved-token", changes.Add);

        // Created with the old token, then sent after a sign-in replaced it.
        var request = client.CreateGetRequest("documents");
        await client.SendRequestAsync(client.CreatePostRequest("sign-in"));

        await Should.ThrowAsync<UnauthorizedApiException>(() => client.SendRequestAsync(request));

        client.SessionToken.ShouldBe("new-token");
        changes.ShouldBe(["new-token"]);
    }

    [TestMethod]
    public async Task OtherUnauthorizedError_KeepsSession()
    {
        var handler = new FakeHttpMessageHandler(_ => Responses.Error(HttpStatusCode.Unauthorized, SignInErrorCodes.SignInCodeInvalid, "Invalid code."));
        var client = new TestApiClient(handler, "saved-token");

        await Should.ThrowAsync<UnauthorizedApiException>(() => client.SendRequestAsync(client.CreatePostRequest("sign-in")));

        client.SessionToken.ShouldBe("saved-token");
    }

    [TestMethod]
    public async Task FailedSignIn_KeepsStoredToken()
    {
        var changes = new List<string?>();
        var handler = new FakeHttpMessageHandler(_ => Responses.Error(HttpStatusCode.Unauthorized, "Wrong password."));
        var client = new TestApiClient(handler, "saved-token", changes.Add);

        await Should.ThrowAsync<UnauthorizedApiException>(() => client.SendRequestAsync(client.CreatePostRequest("sign-in")));

        client.SessionToken.ShouldBe("saved-token");
        client.IsPersistentSession.ShouldBeTrue();
        changes.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task SameToken_IsNotStoredAgain()
    {
        var changes = new List<string?>();
        var client = new TestApiClient(CreateHandler(PersistentCookie, PersistentCookie), sessionToken: null, changes.Add);

        await client.SendRequestAsync(client.CreatePostRequest("sign-in"));
        await client.SendRequestAsync(client.CreateGetRequest("documents"));

        changes.ShouldBe(["new-token"]);
    }

    [TestMethod]
    [DataRow("__Host-session-token=new-token; max-age=0")]
    [DataRow("__Host-session-token=new-token; Max-Age=-1; expires=Thu, 01 Jan 2099 00:00:00 GMT")]
    [DataRow("__Host-session-token=new-token; expires=Thu, 01 Jan 1970 00:00:00 GMT")]
    [DataRow("__Host-session-token=; max-age=1209600")]
    public async Task ExpiredOrEmptyCookie_EndsSession(string setCookie)
    {
        var changes = new List<string?>();
        var client = new TestApiClient(CreateHandler(setCookie), "saved-token", changes.Add);

        await client.SendRequestAsync(client.CreatePostRequest("sign-out"));

        client.SessionToken.ShouldBeNull();
        client.IsPersistentSession.ShouldBeFalse();
        changes.ShouldBe([null]);
    }

    [TestMethod]
    public async Task MaxAge_TakesPrecedenceOverExpires()
    {
        var changes = new List<string?>();
        var client = new TestApiClient(CreateHandler("__Host-session-token=new-token; expires=Thu, 01 Jan 1970 00:00:00 GMT; max-age=60"), null, changes.Add);

        await client.SendRequestAsync(client.CreatePostRequest("sign-in"));

        client.SessionToken.ShouldBe("new-token");
        changes.ShouldBe(["new-token"]);
    }

    [TestMethod]
    public async Task LastSessionCookie_Wins()
    {
        var handler = new FakeHttpMessageHandler(_ => Responses.NoContent().WithCookie(ClearCookie).WithCookie("other=1").WithCookie(PersistentCookie));
        var client = new TestApiClient(handler, "saved-token");

        await client.SendRequestAsync(client.CreatePostRequest("sign-in"));

        client.SessionToken.ShouldBe("new-token");
        client.IsPersistentSession.ShouldBeTrue();
    }

    [TestMethod]
    public async Task ClearSessionToken_DropsTokenAndDeletesStoredToken()
    {
        var changes = new List<string?>();
        var handler = CreateHandler();
        var client = new TestApiClient(handler, "saved-token", changes.Add);

        client.ClearToken();

        client.SessionToken.ShouldBeNull();
        client.IsPersistentSession.ShouldBeFalse();
        changes.ShouldBe([null]);

        await client.SendRequestAsync(client.CreateGetRequest("documents"));

        handler.SingleRequest.Cookie.ShouldBeNull();
    }

    [TestMethod]
    public async Task ClearSessionToken_DeletesStoredTokenOfAnySession()
    {
        var changes = new List<string?>();
        var client = new TestApiClient(CreateHandler(SessionOnlyCookie), sessionToken: null, changes.Add);
        var child = new TestApiClient(client);

        await client.SendRequestAsync(client.CreatePostRequest("sign-in"));
        child.ClearToken();
        child.ClearToken();

        client.SessionToken.ShouldBeNull();
        changes.ShouldBe([null, null]);
    }

    [TestMethod]
    public async Task ChildClient_SharesSession()
    {
        var handler = CreateHandler("__Host-session-token=from-parent", null, "__Host-session-token=from-child");
        var parent = new TestApiClient(handler);
        var child = new TestApiClient(parent);

        await parent.SendRequestAsync(parent.CreatePostRequest("sign-in"));
        await child.SendRequestAsync(child.CreateGetRequest("documents"));
        await child.SendRequestAsync(child.CreateGetRequest("documents"));

        handler.Requests[1].Cookie.ShouldBe("__Host-session-token=from-parent");
        parent.SessionToken.ShouldBe("from-child");
    }
}
