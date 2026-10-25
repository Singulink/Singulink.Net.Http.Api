namespace Singulink.Net.Http.Api.Client;

[PrefixTestClass]
public sealed class SessionEndedTests
{
    private const string ClearCookie = "__Host-session-token=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/; secure; httponly";
    private const string PersistentCookie = "__Host-session-token=new-token; max-age=1209600; path=/; secure; samesite=none; httponly";

    private static HttpResponseMessage SignInRequired() =>
        Responses.Error(HttpStatusCode.Unauthorized, ApiErrorCodes.SignInRequired, "User is not signed in.");

    /// <summary>
    /// Answers a sign-in with a new session cookie at once, and other requests, once released, with a 401 that has the specified cookie if it deletes the
    /// cookie, or with a 204 that has it otherwise.
    /// </summary>
    private static async Task<HttpResponseMessage> RespondAfterSignInAsync(HttpRequestMessage request, Task release, string setCookie)
    {
        if (IsSignIn(request))
            return SignedIn();

        await release;
        return (setCookie == ClearCookie ? SignInRequired() : Responses.NoContent()).WithCookie(setCookie);
    }

    private static bool IsSignIn(HttpRequestMessage request) => request.RequestUri!.AbsolutePath.EndsWith("/sign-in", StringComparison.Ordinal);

    /// <summary>
    /// Gets the response of a sign-in: the new session's cookie, and the header that marks a sign-in's response.
    /// </summary>
    private static HttpResponseMessage SignedIn()
    {
        var response = Responses.NoContent().WithCookie(PersistentCookie);
        response.Headers.Add("Session-Change", "sign-in");

        return response;
    }

    /// <summary>
    /// Gets the response of a sign-out: the deletion of the session cookie, and the header that marks a sign-out's response.
    /// </summary>
    private static HttpResponseMessage SignedOut()
    {
        var response = Responses.NoContent().WithCookie(ClearCookie);
        response.Headers.Add("Session-Change", "sign-out");

        return response;
    }

    [TestMethod]
    public async Task ClearedCookie_EndsSession()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => Responses.NoContent().WithCookie(ClearCookie)), "saved-token");

        await client.SendRequestAsync(client.CreatePostRequest("sign-out"));
        await client.SendRequestAsync(client.CreatePostRequest("sign-out"));

        client.SessionEndedCount.ShouldBe(1);
    }

    [TestMethod]
    public async Task SignInRequired_EndsSessionOnce()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => SignInRequired()), "saved-token");

        await Should.ThrowAsync<UnauthorizedApiException>(() => client.SendRequestAsync(client.CreateGetRequest("documents")));
        await Should.ThrowAsync<UnauthorizedApiException>(() => client.SendRequestAsync(client.CreateGetRequest("documents")));

        client.SessionEndedCount.ShouldBe(1);
    }

    [TestMethod]
    public async Task SignInRequiredInStream_EndsSession()
    {
        var handler = new FakeHttpMessageHandler(_ => Responses.Stream(
            """{"error":{"status":401,"code":"sign-in-required","message":"User is not signed in."}}"""));

        var client = new TestApiClient(handler, "saved-token");

        await Should.ThrowAsync<UnauthorizedApiException>(async () => {
            await foreach (var item in client.SendStreamingRequestAsync<TestItem>(client.CreateGetRequest("items")))
            {
            }
        });

        client.SessionEndedCount.ShouldBe(1);
    }

    [TestMethod]
    public async Task SignInRequiredOfARequestThatBeganBeforeASessionStarted_EndsTheSessionWhoseTokenItHas()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var client = new TestApiClient(
            new FakeHttpMessageHandler(async (_, _) => {
                await release.Task;
                return SignInRequired();
            }),
            "saved-token");

        var request = client.SendRequestAsync(client.CreateGetRequest("documents"));

        // A session start is reported while the request is on its way. The device still has the request's token, which tells that the error concerns the
        // current session.
        client.NotifyStarted();
        release.SetResult(true);

        await Should.ThrowAsync<UnauthorizedApiException>(() => request);
        client.SessionEndedCount.ShouldBe(1);
        client.SessionToken.ShouldBeNull();
    }

    [TestMethod]
    public async Task RefreshOfTheSessionBeforeTheSignOutsResponse_EndsTheSession()
    {
        const string RefreshedCookie = "__Host-session-token=saved-token-refreshed; max-age=1209600; path=/; secure; samesite=none; httponly";
        var signOutResponse = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var handler = new FakeHttpMessageHandler(async (request, _) => {
            if (request.RequestUri!.AbsolutePath.EndsWith("/sign-out", StringComparison.Ordinal))
            {
                await signOutResponse.Task;
                return SignedOut();
            }

            return Responses.NoContent().WithCookie(RefreshedCookie);
        });

        var client = new TestApiClient(handler, "saved-token");

        // A sign-out is on its way while a request of the same session refreshes its cookie first.
        var signOut = client.SendRequestAsync(client.CreatePostRequest("sign-out"));
        await client.SendRequestAsync(client.CreateGetRequest("documents"));
        client.SessionToken.ShouldBe("saved-token-refreshed");

        signOutResponse.SetResult(true);
        await signOut;

        client.SessionToken.ShouldBeNull();
        client.SessionEndedCount.ShouldBe(1);
    }

    [TestMethod]
    [DataRow("refresh")]
    [DataRow("deletion")]
    [DataRow("sign-in-required")]
    public async Task ResponseOfTheReplacedSessionBeforeTheSignInsResponse_KeepsTheNewSession(string earlierResponse)
    {
        const string RefreshedCookie = "__Host-session-token=saved-token-refreshed; max-age=1209600; path=/; secure; samesite=none; httponly";
        var signInResponse = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? storedToken = "saved-token";

        var handler = new FakeHttpMessageHandler(async (request, _) => {
            if (IsSignIn(request))
            {
                await signInResponse.Task;
                return SignedIn();
            }

            return earlierResponse switch {
                "refresh" => Responses.NoContent().WithCookie(RefreshedCookie),
                "deletion" => SignInRequired().WithCookie(ClearCookie),
                _ => SignInRequired(),
            };
        });

        var client = new TestApiClient(handler, "saved-token", token => storedToken = token);

        // A sign-in is on its way while a request with the saved token, of the session that the sign-in replaces, gets its response first, which
        // refreshes the session's cookie, deletes it or ends the session.
        var signIn = client.SendRequestAsync(client.CreatePostRequest("sign-in"));
        var documents = client.SendRequestAsync(client.CreateGetRequest("documents"));

        if (earlierResponse is "refresh")
            await documents;
        else
            await Should.ThrowAsync<UnauthorizedApiException>(() => documents);

        signInResponse.SetResult(true);
        await signIn;

        client.SessionToken.ShouldBe("new-token");
        storedToken.ShouldBe("new-token");
    }

    [TestMethod]
    public async Task LateDeletionOfTheCookieOfASessionThatASignInReplaced_KeepsTheNewSession()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TestApiClient(new FakeHttpMessageHandler((request, _) => RespondAfterSignInAsync(request, release.Task, ClearCookie)), "saved-token");

        // A request with the saved token is on its way while a sign-in replaces the session, and then fails, deleting the cookie of the replaced session.
        var request = client.SendRequestAsync(client.CreateGetRequest("documents"));
        await client.SendRequestAsync(client.CreatePostRequest("sign-in"));
        release.SetResult(true);

        await Should.ThrowAsync<UnauthorizedApiException>(() => request);
        client.SessionToken.ShouldBe("new-token");
        client.SessionEndedCount.ShouldBe(0);
    }

    [TestMethod]
    public async Task LateReissueOfTheCookieOfASessionThatASignInReplaced_KeepsTheNewSession()
    {
        const string ReissuedCookie = "__Host-session-token=saved-token-reissued; max-age=1209600; path=/; secure; samesite=none; httponly";
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHttpMessageHandler((request, _) => RespondAfterSignInAsync(request, release.Task, ReissuedCookie));
        var client = new TestApiClient(handler, "saved-token");

        var request = client.SendRequestAsync(client.CreateGetRequest("documents"));
        await client.SendRequestAsync(client.CreatePostRequest("sign-in"));
        release.SetResult(true);

        await request;
        client.SessionToken.ShouldBe("new-token");
    }

    [TestMethod]
    public async Task Native_SignInsResponseThatReplacesTheSession_StartsASession()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => SignedIn()), "saved-token");
        var nextStart = client.SessionStartTask;

        await client.SendRequestAsync(client.CreatePostRequest("sign-in"));

        client.SessionToken.ShouldBe("new-token");
        nextStart.IsCompleted.ShouldBeTrue();
        client.SessionStartTask.IsCompleted.ShouldBeFalse();
    }

    [TestMethod]
    public async Task Native_RefreshThatChangesTheToken_DoesNotStartASession()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => Responses.NoContent().WithCookie(PersistentCookie)), "saved-token");
        var nextStart = client.SessionStartTask;

        await client.SendRequestAsync(client.CreateGetRequest("documents"));

        client.SessionToken.ShouldBe("new-token");
        nextStart.IsCompleted.ShouldBeFalse();
    }

    [TestMethod]
    public async Task Native_SignOutsResponse_DoesNotStartASession()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => SignedOut()), "saved-token");
        var nextStart = client.SessionStartTask;

        await client.SendRequestAsync(client.CreatePostRequest("sign-out"));

        client.SessionToken.ShouldBeNull();
        nextStart.IsCompleted.ShouldBeFalse();
    }

    [TestMethod]
    public async Task SignInsResponseFromAnotherHost_IsNotTaken()
    {
        // A handler of the app's followed a redirect to another host, which marks its response.
        var handler = new FakeHttpMessageHandler(_ => {
            var response = SignedIn();
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Post, "https://other.example.com/sign-in");

            return response;
        });

        var client = new TestApiClient(handler, "saved-token");
        var nextStart = client.SessionStartTask;

        await client.SendRequestAsync(client.CreatePostRequest("sign-in"));

        nextStart.IsCompleted.ShouldBeFalse();
        client.SessionToken.ShouldBe("saved-token");
    }

    [TestMethod]
    [DataRow("SIGN-IN", true)]
    [DataRow("sign-out", true)]
    [DataRow("1", false)]
    [DataRow("session-replaced", false)]
    public async Task SessionChangeValue_MarksTheResponseOnlyIfItIsASignInOrSignOut(string value, bool isMarked)
    {
        const string RefreshedCookie = "__Host-session-token=saved-token-refreshed; max-age=1209600; path=/; secure; samesite=none; httponly";
        var lateResponse = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var handler = new FakeHttpMessageHandler(async (request, _) => {
            if (!IsSignIn(request))
                return Responses.NoContent().WithCookie(RefreshedCookie);

            await lateResponse.Task;
            var response = Responses.NoContent().WithCookie(PersistentCookie);
            response.Headers.Add("Session-Change", value);

            return response;
        });

        var client = new TestApiClient(handler, "saved-token");
        var nextStart = client.SessionStartTask;

        // A response with the request's token refreshes it first, and the response with the header comes after it. A marked response's cookie is taken,
        // and starts a session.
        var marked = client.SendRequestAsync(client.CreatePostRequest("sign-in"));
        await client.SendRequestAsync(client.CreateGetRequest("documents"));
        lateResponse.SetResult(true);
        await marked;

        client.SessionToken.ShouldBe(isMarked ? "new-token" : "saved-token-refreshed");
        nextStart.IsCompleted.ShouldBe(isMarked);
    }

    [TestMethod]
    public void Browser_ReportOfARequestThatBeganBeforeASessionStarted_DoesNotEndSession()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler());
        var sessionStart = client.SessionStartTask;

        // A sign-in of another user starts a session after the request began.
        client.NotifyStarted();
        client.EndSessionOnPlatform(isBrowser: true, sessionToken: null, sessionStart);

        client.SessionEndedCount.ShouldBe(0);
    }

    [TestMethod]
    public void Browser_ReportOfARequestThatBeganAfterTheLastSessionStart_EndsSession()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler());
        client.NotifyStarted();

        client.EndSessionOnPlatform(isBrowser: true, sessionToken: null, client.SessionStartTask);

        client.SessionEndedCount.ShouldBe(1);
    }

    [TestMethod]
    public void Native_ReportWithTheCurrentToken_EndsSession_AlsoAfterASessionStart()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(), "saved-token");
        var sessionStart = client.SessionStartTask;
        client.NotifyStarted();

        client.EndSessionOnPlatform(isBrowser: false, "saved-token", sessionStart);

        client.SessionEndedCount.ShouldBe(1);
        client.SessionToken.ShouldBeNull();
    }

    [TestMethod]
    public void Native_ReportWithAnEarlierToken_DoesNotEndSession()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(), "saved-token");

        client.EndSessionOnPlatform(isBrowser: false, "earlier-token", client.SessionStartTask);

        client.SessionEndedCount.ShouldBe(0);
        client.SessionToken.ShouldBe("saved-token");
    }

    [TestMethod]
    public async Task SignInRequiredWithoutSession_DoesNotEndSession()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => SignInRequired()));

        await Should.ThrowAsync<UnauthorizedApiException>(() => client.SendRequestAsync(client.CreateGetRequest("documents")));

        client.SessionEndedCount.ShouldBe(0);
    }

    [TestMethod]
    public async Task NewSession_DoesNotEndSession()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => Responses.NoContent().WithCookie(PersistentCookie)), "saved-token");

        await client.SendRequestAsync(client.CreatePostRequest("sign-in"));

        client.SessionToken.ShouldBe("new-token");
        client.SessionEndedCount.ShouldBe(0);
    }

    [TestMethod]
    public async Task OtherUnauthorizedError_DoesNotEndSession()
    {
        var handler = new FakeHttpMessageHandler(_ => Responses.Error(HttpStatusCode.Unauthorized, "credentials-invalid", "Wrong password."));
        var client = new TestApiClient(handler, "saved-token");

        await Should.ThrowAsync<UnauthorizedApiException>(() => client.SendRequestAsync(client.CreatePostRequest("sign-in")));

        client.SessionEndedCount.ShouldBe(0);
    }

    [TestMethod]
    public void ClearSessionToken_EndsSessionThatHasToken()
    {
        var withToken = new TestApiClient(new FakeHttpMessageHandler(), "saved-token");
        var withoutToken = new TestApiClient(new FakeHttpMessageHandler());

        withToken.ClearToken();
        withToken.ClearToken();
        withoutToken.ClearToken();

        withToken.SessionEndedCount.ShouldBe(1);
        withoutToken.SessionEndedCount.ShouldBe(0);
    }

    [TestMethod]
    public async Task ChildRequest_EndsSessionOnParent()
    {
        var parent = new TestApiClient(new FakeHttpMessageHandler(_ => SignInRequired()), "saved-token");
        var child = new TestApiClient(new TestApiClient(parent, "repos/1/"), userIdPrecondition: "5");

        await Should.ThrowAsync<UnauthorizedApiException>(() => child.SendRequestAsync(child.CreateGetRequest("documents")));

        parent.SessionEndedCount.ShouldBe(1);
        child.SessionEndedCount.ShouldBe(0);
    }

    [TestMethod]
    public async Task CallbackException_SessionStillEnds()
    {
        var client = new TestApiClient(
            new FakeHttpMessageHandler(_ => SignInRequired()), "saved-token", _ => throw new InvalidOperationException("Deleting the token failed."));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => client.SendRequestAsync(client.CreateGetRequest("documents")));

        ex.Message.ShouldBe("Deleting the token failed.");
        client.SessionEndedCount.ShouldBe(1);
    }

    [TestMethod]
    public async Task OnSessionEndedRunsAfterCallback_AndItsExceptionPropagates()
    {
        var calls = new List<string>();
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => SignInRequired()), "saved-token", token => calls.Add($"callback {token}"));

        client.SessionEnded = () => {
            calls.Add("ended");
            throw new InvalidOperationException("Ended.");
        };

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => client.SendRequestAsync(client.CreateGetRequest("documents")));

        ex.Message.ShouldBe("Ended.");
        calls.ShouldBe(["callback ", "ended"]);
    }
}
