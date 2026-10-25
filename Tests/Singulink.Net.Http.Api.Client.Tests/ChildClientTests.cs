namespace Singulink.Net.Http.Api.Client;

[PrefixTestClass]
public sealed class ChildClientTests
{
    [TestMethod]
    public void BaseAddress_IsInherited()
    {
        var parent = new TestApiClient(new FakeHttpMessageHandler());
        var child = new TestApiClient(parent);
        var grandchild = new TestApiClient(child);

        child.GetUrl("documents").AbsoluteUri.ShouldBe("https://api.example.com/v1/documents");
        grandchild.GetUrl("documents").AbsoluteUri.ShouldBe("https://api.example.com/v1/documents");
    }

    [TestMethod]
    [DataRow("https://a.example.com/api", "https://a.example.com/api/items/1")]
    [DataRow("https://a.example.com/api/v1?key=1", "https://a.example.com/api/v1/items/1")]
    [DataRow("https://a.example.com/a%2Fb", "https://a.example.com/a%2Fb/items/1")]
    [DataRow("https://a.example.com", "https://a.example.com/items/1")]
    public void BaseAddressWithoutTrailingSlash_GetsOne(string baseAddress, string url)
    {
        var parent = new TestApiClient(new FakeHttpMessageHandler()) { BaseAddress = new Uri(baseAddress) };
        var child = new TestApiClient(parent, "items/");

        parent.GetUrl("items/1").AbsoluteUri.ShouldBe(url);
        child.GetUrl("1").AbsoluteUri.ShouldBe(url);
    }

    [TestMethod]
    public void BaseAddressWithoutTrailingSlash_SiblingPathThrows()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler()) { BaseAddress = new Uri("https://a.example.com/api") };

        Should.Throw<ArgumentException>(() => client.GetUrl("../api2/x"));
    }

    [TestMethod]
    public void BaseAddress_FollowsParentChanges()
    {
        var parent = new TestApiClient(new FakeHttpMessageHandler());
        var child = new TestApiClient(parent, "repos/");

        parent.BaseAddress = new Uri("https://eu.example.com/v2/");

        child.GetUrl("issues").AbsoluteUri.ShouldBe("https://eu.example.com/v2/repos/issues");
    }

    [TestMethod]
    public void BasePath_IsAddedToParentAddressWithEscapedValues()
    {
        var parent = new TestApiClient(new FakeHttpMessageHandler());
        string repoId = "a/b c";
        var child = new TestApiClient(parent, $"repos/{repoId}/");

        child.GetUrl("issues", [("page", 2)]).AbsoluteUri.ShouldBe("https://api.example.com/v1/repos/a%2Fb%20c/issues?page=2");
    }

    [TestMethod]
    public void BasePath_WithoutTrailingSlash_GetsOne()
    {
        var parent = new TestApiClient(new FakeHttpMessageHandler());
        int repoId = 42;
        var child = new TestApiClient(parent, $"repos/{repoId}");

        child.GetUrl("issues").AbsoluteUri.ShouldBe("https://api.example.com/v1/repos/42/issues");
    }

    [TestMethod]
    public void BasePaths_Combine()
    {
        var parent = new TestApiClient(new FakeHttpMessageHandler());
        var child = new TestApiClient(parent, "repos/42/");
        var grandchild = new TestApiClient(child, "issues/7/");

        grandchild.GetUrl("comments").AbsoluteUri.ShouldBe("https://api.example.com/v1/repos/42/issues/7/comments");
    }

    [TestMethod]
    [DataRow("../")]
    [DataRow("/repos/")]
    [DataRow("//other.example.com/")]
    [DataRow("\\/other.example.com/")]
    [DataRow("repos/../../")]
    [DataRow("https://other.example.com/")]
    public void BasePath_OutsideParentAddress_Throws(string basePath)
    {
        var parent = new TestApiClient(new FakeHttpMessageHandler());
        var child = new TestApiClient(parent, basePath);

        Should.Throw<InvalidOperationException>(() => child.GetUrl("issues")).Message.ShouldContain("leads outside its parent's base address");
    }

    [TestMethod]
    public void BasePath_InvalidUri_Throws()
    {
        var parent = new TestApiClient(new FakeHttpMessageHandler());
        var child = new TestApiClient(parent, "a:b/");

        Should.Throw<InvalidOperationException>(() => child.GetUrl("issues")).Message.ShouldContain("is not a valid relative path or absolute URI");
    }

    [TestMethod]
    public void BasePath_AbsoluteUriOfOtherScheme_Throws()
    {
        var parent = new TestApiClient(new FakeHttpMessageHandler());
        var child = new TestApiClient(parent, "repos:1/");

        Should.Throw<InvalidOperationException>(() => child.GetUrl("issues")).Message.ShouldContain("is an absolute URI outside its parent's base address");
    }

    [TestMethod]
    public void BasePath_DotSegmentValue_Throws()
    {
        var parent = new TestApiClient(new FakeHttpMessageHandler());
        string repoId = "..";

        Should.Throw<ArgumentException>(() => new TestApiClient(parent, $"repos/{repoId}/"));
    }

    [TestMethod]
    public void BasePath_WithDotSegmentsInside_IsResolved()
    {
        var parent = new TestApiClient(new FakeHttpMessageHandler());
        var child = new TestApiClient(parent, "repos/../issues/");

        child.GetUrl("7").AbsoluteUri.ShouldBe("https://api.example.com/v1/issues/7");
    }

    [TestMethod]
    public void RootClientWithoutBaseAddress_Throws()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler()) { BaseAddress = null };

        var ex = Should.Throw<InvalidOperationException>(() => client.GetUrl("documents"));

        ex.Message.ShouldContain("GetBaseAddress");
    }

    [TestMethod]
    public void UserIdPrecondition_IsInheritedAndCanBeReplaced()
    {
        var parent = new TestApiClient(new FakeHttpMessageHandler());
        var child = new TestApiClient(parent, userIdPrecondition: "5");
        var grandchild = new TestApiClient(child, "repos/42/");
        var otherUser = new TestApiClient(child, userIdPrecondition: "7");

        grandchild.GetUrl("issues").Query.ShouldBe("?if-userId=5");
        otherUser.GetUrl("issues").Query.ShouldBe("?if-userId=7");
        parent.GetUrl("issues").Query.ShouldBeEmpty();
    }

    [TestMethod]
    public void UserIdPreconditionQueryName_IsInherited()
    {
        var parent = new TestApiClient(new FakeHttpMessageHandler()) { PreconditionName = "uid" };
        var child = new TestApiClient(parent, userIdPrecondition: "5");
        var grandchild = new TestApiClient(child);

        parent.CurrentUserIdPreconditionQueryName.ShouldBe("uid");
        grandchild.CurrentUserIdPreconditionQueryName.ShouldBe("uid");
        grandchild.GetUrl("documents").Query.ShouldBe("?uid=5");
    }

    [TestMethod]
    public void Names_HaveDefaults()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler());

        client.CurrentUserIdPreconditionQueryName.ShouldBe("if-userId");
        client.CurrentSessionCookieName.ShouldBe("__Host-session-token");
    }

    [TestMethod]
    public async Task SessionCookieName_IsInherited()
    {
        var handler = new FakeHttpMessageHandler(_ => Responses.NoContent().WithCookie("sid=refreshed; path=/; secure; httponly"));
        var parent = new TestApiClient(handler, "saved-token") { CookieName = "sid" };
        var child = new TestApiClient(parent);

        child.CurrentSessionCookieName.ShouldBe("sid");

        await child.SendRequestAsync(child.CreateGetRequest("documents"));
        await child.SendRequestAsync(child.CreateGetRequest("documents"));

        handler.Requests.Select(r => r.Cookie).ShouldBe(["sid=saved-token", "sid=refreshed"]);
        parent.SessionToken.ShouldBe("refreshed");
    }
}
