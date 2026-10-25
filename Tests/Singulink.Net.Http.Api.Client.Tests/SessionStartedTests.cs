namespace Singulink.Net.Http.Api.Client;

[PrefixTestClass]
public sealed class SessionStartedTests
{
    private const string ClearCookie = "__Host-session-token=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/; secure; httponly";
    private const string PersistentCookie = "__Host-session-token=new-token; max-age=1209600; path=/; secure; samesite=none; httponly";
    private const string TemporaryCookie = "__Host-session-token=new-token; path=/; secure; samesite=none; httponly";

    [TestMethod]
    [DataRow(PersistentCookie)]
    [DataRow(TemporaryCookie)]
    public async Task CookieWithoutSession_StartsSession(string cookie)
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => Responses.NoContent().WithCookie(cookie)));
        var sessionStart = client.NextSessionStart;

        await client.SendRequestAsync(client.CreatePostRequest("sign-in"));

        sessionStart.IsCompleted.ShouldBeTrue();
        client.NextSessionStart.IsCompleted.ShouldBeFalse();
    }

    [TestMethod]
    public async Task RefreshedCookie_DoesNotStartSession()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => Responses.NoContent().WithCookie(PersistentCookie)), "saved-token");
        var sessionStart = client.NextSessionStart;

        await client.SendRequestAsync(client.CreateGetRequest("documents"));

        client.SessionToken.ShouldBe("new-token");
        sessionStart.IsCompleted.ShouldBeFalse();
    }

    [TestMethod]
    public async Task ClearedCookie_DoesNotStartSession()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => Responses.NoContent().WithCookie(ClearCookie)), "saved-token");
        var sessionStart = client.NextSessionStart;

        await client.SendRequestAsync(client.CreatePostRequest("sign-out"));

        client.SessionToken.ShouldBeNull();
        sessionStart.IsCompleted.ShouldBeFalse();
    }

    [TestMethod]
    public async Task SignInAfterSessionEnded_StartsSession()
    {
        var responses = new Queue<HttpResponseMessage>([
            Responses.Error(HttpStatusCode.Unauthorized, ApiErrorCodes.SignInRequired, "User is not signed in."),
            Responses.NoContent().WithCookie(PersistentCookie),
        ]);

        var client = new TestApiClient(new FakeHttpMessageHandler(_ => responses.Dequeue()), "saved-token");
        var sessionStart = client.NextSessionStart;

        await Should.ThrowAsync<UnauthorizedApiException>(() => client.SendRequestAsync(client.CreateGetRequest("documents")));
        sessionStart.IsCompleted.ShouldBeFalse();

        await client.SendRequestAsync(client.CreatePostRequest("sign-in"));

        client.SessionToken.ShouldBe("new-token");
        sessionStart.IsCompleted.ShouldBeTrue();
    }

    [TestMethod]
    public void NotifySessionStarted_StartsSessionOfAllClientsThatShareIt()
    {
        var parent = new TestApiClient(new FakeHttpMessageHandler(), "saved-token");
        var child = new TestApiClient(parent, userIdPrecondition: 5);
        var sessionStart = parent.NextSessionStart;

        child.NextSessionStart.ShouldBeSameAs(sessionStart);

        child.NotifyStarted();

        sessionStart.IsCompleted.ShouldBeTrue();
        parent.NextSessionStart.IsCompleted.ShouldBeFalse();
        child.NextSessionStart.ShouldBeSameAs(parent.NextSessionStart);
    }
}
