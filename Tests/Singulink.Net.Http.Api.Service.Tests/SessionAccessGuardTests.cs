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
        var token = host.CreateSession();
        var request = host.CreateRequest(token, origin: origin);

        (await request.GetTokenAsync()).ShouldBe(token);
    }

    [TestMethod]
    [DataRow("https://evil.test")]
    [DataRow("https://example.com.evil.test")]
    [DataRow("null")]
    public async Task Origin_Disallowed_ThrowsForbidden(string origin)
    {
        var host = new SessionTestHost();
        var token = host.CreateSession();
        var request = host.CreateRequest(token, origin: origin);

        await Should.ThrowAsync<ForbiddenApiException>(() => request.GetTokenAsync().AsTask());
    }

    [TestMethod]
    public async Task Origin_Disallowed_AllowAllOriginsOption_ReturnsToken()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession();
        var request = host.CreateRequest(token, origin: "https://evil.test");

        (await request.GetTokenAsync(SessionAccessOptions.AllowAllOrigins)).ShouldBe(token);
    }

    [TestMethod]
    public async Task Origin_MultipleHeaders_ThrowsBadRequest()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession();
        var request = host.CreateRequest(token);
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
        var token = host.CreateSession();
        var request = host.CreateRequest(token, includeUserIdPrecondition: false);

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
    public async Task UserIdPrecondition_Mismatch_ThrowsUserChanged()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession(userId: 1);
        var request = host.CreateRequest(token, userIdPreconditionValue: "2");

        await Should.ThrowAsync<UserChangedApiException>(() => request.GetTokenAsync().AsTask());
        request.SessionCookies.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task UserIdPrecondition_Optional_MissingIsAllowed()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession();
        var request = host.CreateRequest(token, includeUserIdPrecondition: false);

        (await request.GetTokenAsync(SessionAccessOptions.OptionalUserIdPrecondition)).ShouldBe(token);
    }

    [TestMethod]
    public async Task UserIdPrecondition_Optional_MismatchStillThrows()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession(userId: 1);
        var request = host.CreateRequest(token, userIdPreconditionValue: "2");

        await Should.ThrowAsync<UserChangedApiException>(() => request.GetTokenAsync(SessionAccessOptions.OptionalUserIdPrecondition).AsTask());
    }

    [TestMethod]
    public async Task UserIdPrecondition_Empty_ThrowsBadRequest()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession();
        var request = host.CreateRequest(token, userIdPreconditionValue: string.Empty);

        await Should.ThrowAsync<BadRequestApiException>(() => request.GetTokenAsync().AsTask());
    }

    [TestMethod]
    public async Task UserIdPrecondition_MultipleValues_ThrowsBadRequest()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession();
        var request = host.CreateRequest(token, includeUserIdPrecondition: false);
        request.Context.Request.QueryString = new QueryString("?if-userId=1&if-userId=1");

        await Should.ThrowAsync<BadRequestApiException>(() => request.GetTokenAsync().AsTask());
    }

    [TestMethod]
    public async Task UserIdPrecondition_CustomQueryName_IsUsed()
    {
        var host = new SessionTestHost(o => o.UserIdPreconditionQueryName = "uid");
        var token = host.CreateSession();

        (await host.CreateRequest(token).GetTokenAsync()).ShouldBe(token);

        var defaultNameRequest = host.CreateRequest(token, includeUserIdPrecondition: false);
        defaultNameRequest.Context.Request.QueryString = new QueryString("?if-userId=1");

        await Should.ThrowAsync<UserRequiredApiException>(() => defaultNameRequest.GetTokenAsync().AsTask());
    }

    [TestMethod]
    public async Task ForcedAccessOptions_OptionalUserIdPrecondition_AppliesToEveryRetrieval()
    {
        var host = new SessionTestHost(o => o.ForcedAccessOptions = SessionAccessOptions.OptionalUserIdPrecondition);
        var token = host.CreateSession();
        var request = host.CreateRequest(token, includeUserIdPrecondition: false);

        (await request.GetTokenAsync()).ShouldBe(token);
    }

    [TestMethod]
    public async Task UndefinedOptionFlags_ThrowArgumentException()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession();
        var request = host.CreateRequest(token);

        await Should.ThrowAsync<ArgumentException>(() => request.GetTokenAsync((SessionAccessOptions)64).AsTask());
    }

    [TestMethod]
    public async Task CustomCookieName_IsUsed()
    {
        var host = new SessionTestHost(o => o.SessionCookieName = "sid");
        var token = host.CreateSession();

        (await host.CreateRequest(token).GetTokenAsync()).ShouldBe(token);

        var defaultNameRequest = host.CreateRequest();
        defaultNameRequest.Context.Request.Headers.Cookie = $"session-token={host.Protect(token)}";

        (await defaultNameRequest.GetTokenAsync()).ShouldBeNull();
    }
}
