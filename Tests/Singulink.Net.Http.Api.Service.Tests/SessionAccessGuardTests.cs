using Microsoft.Extensions.Primitives;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Origin (CSRF) and user ID precondition checks.
/// </summary>
[PrefixTestClass]
public sealed class SessionAccessGuardTests
{
    [TestMethod]
    [DataRow("https://example.com")]
    [DataRow("https://app.example.com")]
    [DataRow("https://EXAMPLE.com:8443")]
    public async Task Origin_Allowed_ReturnsToken(string origin)
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        var request = host.CreateRequest(session, origin: origin);

        (await request.GetTokenAsync()).ShouldBe(session.Token);
    }

    [TestMethod]
    [DataRow("https://evil.test")]
    [DataRow("https://example.com.evil.test")]
    [DataRow("null")]
    public async Task Origin_Disallowed_ThrowsForbidden(string origin)
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        var request = host.CreateRequest(session, origin: origin);

        var ex = await Should.ThrowAsync<ForbiddenApiException>(() => request.GetTokenAsync().AsTask());
        ex.ErrorCode.ShouldBe(ApiErrorCodes.OriginBlocked);
    }

    [TestMethod]
    public async Task Origin_Disallowed_AllowAllOriginsOption_ReturnsToken()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        var request = host.CreateRequest(session, origin: "https://evil.test");

        (await request.GetTokenAsync(SessionAccessOptions.AllowAllOrigins)).ShouldBe(session.Token);
    }

    [TestMethod]
    public async Task Origin_MultipleHeaders_ThrowsBadRequest()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        var request = host.CreateRequest(session);
        request.Context.Request.Headers.Origin = new StringValues(["https://example.com", "https://app.example.com"]);

        await Should.ThrowAsync<BadRequestApiException>(() => request.GetTokenAsync().AsTask());
    }

    [TestMethod]
    public async Task Origin_Disallowed_IsCheckedBeforeCookie()
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest(origin: "https://evil.test");

        await Should.ThrowAsync<ForbiddenApiException>(() => request.GetTokenAsync().AsTask());
    }

    [TestMethod]
    public async Task UserIdPrecondition_Missing_ThrowsUserRequired()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        var request = host.CreateRequest(session, includeUserIdPrecondition: false);

        await Should.ThrowAsync<UserRequiredApiException>(() => request.GetTokenAsync().AsTask());
    }

    [TestMethod]
    public async Task UserIdPrecondition_Missing_NoCookie_ReturnsNull()
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest(includeUserIdPrecondition: false);

        (await request.GetTokenAsync()).ShouldBeNull();
    }

    [TestMethod]
    public async Task UserIdPrecondition_Mismatch_ThrowsUserChangedWithThePrecondition()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(userId: 4321);
        var request = host.CreateRequest(session, userIdPreconditionValue: "2");

        var ex = await Should.ThrowAsync<UserChangedApiException>(() => request.GetTokenAsync().AsTask());

        ex.ExpectedUserId.ShouldBe("2");
        ex.Message.ShouldNotContain("4321");
        request.SessionCookies.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task UserIdPrecondition_Optional_MissingIsAllowed()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        var request = host.CreateRequest(session, includeUserIdPrecondition: false);

        (await request.GetTokenAsync(SessionAccessOptions.OptionalUserIdPrecondition)).ShouldBe(session.Token);
    }

    [TestMethod]
    public async Task UserIdPrecondition_Optional_MismatchStillThrows()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(userId: 1);
        var request = host.CreateRequest(session, userIdPreconditionValue: "2");

        await Should.ThrowAsync<UserChangedApiException>(() => request.GetTokenAsync(SessionAccessOptions.OptionalUserIdPrecondition).AsTask());
    }

    [TestMethod]
    public async Task UserIdPrecondition_Empty_ThrowsBadRequest()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        var request = host.CreateRequest(session, userIdPreconditionValue: string.Empty);

        await Should.ThrowAsync<BadRequestApiException>(() => request.GetTokenAsync().AsTask());
    }

    [TestMethod]
    public async Task UserIdPrecondition_MultipleValues_ThrowsBadRequest()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        var request = host.CreateRequest(session, includeUserIdPrecondition: false);
        request.Context.Request.QueryString = new QueryString("?if-userId=1&if-userId=1");

        await Should.ThrowAsync<BadRequestApiException>(() => request.GetTokenAsync().AsTask());
    }

    [TestMethod]
    public async Task UserIdPrecondition_CustomQueryName_IsUsed()
    {
        var host = new SessionTestHost(o => o.UserIdPreconditionQueryName = "uid");
        var session = host.CreateSession();

        (await host.CreateRequest(session).GetTokenAsync()).ShouldBe(session.Token);

        var defaultNameRequest = host.CreateRequest(session, includeUserIdPrecondition: false);
        defaultNameRequest.Context.Request.QueryString = new QueryString("?if-userId=1");

        await Should.ThrowAsync<UserRequiredApiException>(() => defaultNameRequest.GetTokenAsync().AsTask());
    }

    [TestMethod]
    public async Task ForcedAccessOptions_OptionalUserIdPrecondition_AppliesToEveryRetrieval()
    {
        var host = new SessionTestHost(o => o.ForcedAccessOptions = SessionAccessOptions.OptionalUserIdPrecondition);
        var session = host.CreateSession();
        var request = host.CreateRequest(session, includeUserIdPrecondition: false);

        (await request.GetTokenAsync()).ShouldBe(session.Token);
    }

    [TestMethod]
    public async Task UndefinedOptionFlags_ThrowArgumentException()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        var request = host.CreateRequest(session);

        await Should.ThrowAsync<ArgumentException>(() => request.GetTokenAsync((SessionAccessOptions)64).AsTask());
    }

    [TestMethod]
    public async Task CustomCookieName_IsUsed()
    {
        var host = new SessionTestHost(o => o.SessionCookieName = "sid");
        var session = host.CreateSession();

        (await host.CreateRequest(session).GetTokenAsync()).ShouldBe(session.Token);

        var defaultNameRequest = host.CreateRequest();
        defaultNameRequest.Context.Request.Headers.Cookie = $"{TestSessionCookies.CookieName}={host.Protect(session)}";

        (await defaultNameRequest.GetTokenAsync()).ShouldBeNull();
    }
}
