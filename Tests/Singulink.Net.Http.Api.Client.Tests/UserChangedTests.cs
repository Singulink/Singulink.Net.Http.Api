namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// The <see cref="UserChangedApiException.ExpectedUserId"/> of the exceptions for the <c>412</c> errors of requests: the user ID precondition that the
/// request sent, which the error itself doesn't carry.
/// </summary>
[PrefixTestClass]
public sealed class UserChangedTests
{
    private static HttpResponseMessage UserChanged() => Responses.Error(HttpStatusCode.PreconditionFailed, "The user changed.");

    [TestMethod]
    public async Task RequestOfAChildClient_CarriesItsUserIdPrecondition_AsDoTheRequestsOfItsChildren()
    {
        var parent = new TestApiClient(new FakeHttpMessageHandler(_ => UserChanged()));
        var child = new TestApiClient(parent, userIdPrecondition: 5);
        var grandchild = new TestApiClient(child, "repos/42/");

        var ex = await Should.ThrowAsync<UserChangedApiException>(() => child.SendRequestAsync(child.CreateGetRequest("items")));
        var grandchildEx = await Should.ThrowAsync<UserChangedApiException>(() => grandchild.SendRequestAsync(grandchild.CreateGetRequest("issues")));

        ex.ExpectedUserId.ShouldBe("5");
        ex.Message.ShouldBe("The user changed.");
        grandchildEx.ExpectedUserId.ShouldBe("5");
    }

    [TestMethod]
    public async Task RequestWithoutAUserIdPrecondition_HasNone()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => UserChanged()));

        var ex = await Should.ThrowAsync<UserChangedApiException>(() => client.SendRequestAsync(client.CreateGetRequest("items")));

        ex.ExpectedUserId.ShouldBeNull();
    }

    [TestMethod]
    public async Task UserIdPreconditionOfTheRequestsQuery_IsTheOneCarried()
    {
        var parent = new TestApiClient(new FakeHttpMessageHandler(_ => UserChanged())) { PreconditionName = "uid" };
        var child = new TestApiClient(parent, userIdPrecondition: 5);

        var replaced = await Should.ThrowAsync<UserChangedApiException>(
            () => child.SendRequestAsync(child.CreateGetRequest("items", [("uid", "a b&c")])));

        var ofTheParent = await Should.ThrowAsync<UserChangedApiException>(
            () => parent.SendRequestAsync(parent.CreateGetRequest("items", [("UID", 7)])));

        var leftOut = await Should.ThrowAsync<UserChangedApiException>(
            () => child.SendRequestAsync(child.CreateGetRequest("items", [("uid", null)])));

        replaced.ExpectedUserId.ShouldBe("a b&c");
        ofTheParent.ExpectedUserId.ShouldBe("7");
        leftOut.ExpectedUserId.ShouldBeNull();
    }

    [TestMethod]
    public async Task ResponseNotInTheToolkitsFormat_CarriesTheUserIdPreconditionToo()
    {
        var parent = new TestApiClient(new FakeHttpMessageHandler(_ =>
            Responses.Content(HttpStatusCode.PreconditionFailed, "<html>Precondition Failed</html>", "text/html")));

        var child = new TestApiClient(parent, userIdPrecondition: "user-5");

        var ex = await Should.ThrowAsync<UserChangedApiException>(() => child.SendRequestAsync<TestItem>(child.CreateGetRequest("items/1")));

        ex.ExpectedUserId.ShouldBe("user-5");
        ex.ErrorContent.ShouldNotBeNull().Content.ShouldBe("<html>Precondition Failed</html>");
    }

    [TestMethod]
    public async Task StreamingResponse_ErrorResponseAndErrorRecord_CarryTheUserIdPrecondition()
    {
        var refused = new TestApiClient(new TestApiClient(new FakeHttpMessageHandler(_ => UserChanged())), userIdPrecondition: 5);

        var broken = new TestApiClient(
            new TestApiClient(new FakeHttpMessageHandler(_ => Responses.Stream(
                """{"item":{"id":1,"name":"a"}}""",
                """{"error":{"status":412,"message":"The user changed."}}"""))),
            userIdPrecondition: 5);

        var refusedEx = await Should.ThrowAsync<UserChangedApiException>(() => ReadAllAsync(refused));
        var brokenEx = await Should.ThrowAsync<UserChangedApiException>(() => ReadAllAsync(broken));

        refusedEx.ExpectedUserId.ShouldBe("5");
        brokenEx.ExpectedUserId.ShouldBe("5");
        brokenEx.Message.ShouldBe("The user changed.");
    }

    [TestMethod]
    public async Task StreamingErrorRecordOfARequestWithoutAUserIdPrecondition_HasNone()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => Responses.Stream("""{"error":{"status":412,"message":"The user changed."}}""")));

        var ex = await Should.ThrowAsync<UserChangedApiException>(() => ReadAllAsync(client));

        ex.ExpectedUserId.ShouldBeNull();
    }

    [TestMethod]
    public async Task ExpectedUserIdOfTheServicesException_IsNotSent_AndTheClientCarriesItsOwn()
    {
        var handler = new FakeHttpMessageHandler(_ => ApiErrorResponse.Create(new UserChangedApiException("The user changed.") { ExpectedUserId = "9" }));
        var parent = new TestApiClient(handler);
        var child = new TestApiClient(parent, userIdPrecondition: 5);

        var ofTheChild = await Should.ThrowAsync<UserChangedApiException>(() => child.SendRequestAsync(child.CreateGetRequest("items")));
        var ofTheParent = await Should.ThrowAsync<UserChangedApiException>(() => parent.SendRequestAsync(parent.CreateGetRequest("items")));

        ofTheChild.ExpectedUserId.ShouldBe("5");
        ofTheParent.ExpectedUserId.ShouldBeNull();
    }

    private static async Task<List<TestItem>> ReadAllAsync(TestApiClient client)
    {
        var items = new List<TestItem>();

        await foreach (var item in client.SendStreamingRequestAsync<TestItem>(client.CreateGetRequest("items")))
            items.Add(item);

        return items;
    }
}
