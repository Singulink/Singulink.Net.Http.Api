using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Endpoint parameter binding and <see cref="HttpContextExtensions"/> behavior.
/// </summary>
[PrefixTestClass]
public sealed class SessionBindingTests
{
    [TestMethod]
    public async Task Bind_RequiredParameter_NoCookie_ThrowsUnauthorized()
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest();

        await Should.ThrowAsync<UnauthorizedApiException>(() => request.Context.BindSessionTokenAsync<TestSessionToken>(Param("required")).AsTask());
    }

    [TestMethod]
    public async Task Bind_NullableParameter_NoCookie_ReturnsNull()
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest();

        (await request.Context.BindSessionTokenAsync<TestSessionToken>(Param("optional"))).ShouldBeNull();
    }

    [TestMethod]
    public async Task Bind_RequiredParameter_WithCookie_ReturnsToken()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession();
        var request = host.CreateRequest(token);

        (await request.Context.BindSessionTokenAsync<TestSessionToken>(Param("required"))).ShouldBe(token);
        host.Store.Calls.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task Bind_ForceValidateAttribute_ValidatesSession()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession();
        var request = host.CreateRequest(token);

        (await request.Context.BindSessionTokenAsync<TestSessionToken>(Param("forced"))).ShouldBe(token);
        host.Store.Calls.ShouldBe(["GetSessionData", "IsTokenCurrent"]);
    }

    [TestMethod]
    public async Task Bind_OptionalPreconditionAttribute_AllowsMissingPrecondition()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession();
        var request = host.CreateRequest(token, includeUserIdPrecondition: false);

        (await request.Context.BindSessionTokenAsync<TestSessionToken>(Param("optionalPrecondition"))).ShouldBe(token);
        await Should.ThrowAsync<UserRequiredApiException>(() => request.Context.BindSessionTokenAsync<TestSessionToken>(Param("required")).AsTask());
    }

    [TestMethod]
    public void GetSessionContext_ReturnsSameInstanceForRequest()
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest();

        var first = request.Context.GetSessionContext<TestSessionToken>();
        var second = request.Context.GetRequiredSessionContext<TestSessionToken>();

        first.ShouldNotBeNull();
        second.ShouldBeSameAs(first);
        request.Session.ShouldBeSameAs(first);
        first.HttpContext.ShouldBeSameAs(request.Context);
    }

    [TestMethod]
    public void GetSessionContext_DifferentRequests_GetDifferentInstances()
    {
        var host = new SessionTestHost();

        host.CreateRequest().Session.ShouldNotBeSameAs(host.CreateRequest().Session);
    }

    [TestMethod]
    public void GetSessionContext_NoSessionHandlingRegistered_ReturnsNullAndRequiredThrows()
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };

        context.GetSessionContext<TestSessionToken>().ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => context.GetRequiredSessionContext<TestSessionToken>());
    }

    [TestMethod]
    public async Task HttpContextExtensions_GetSessionToken_UsesSharedContext()
    {
        var host = new SessionTestHost();
        var token = host.CreateSession(age: TimeSpan.FromMinutes(11));
        var request = host.CreateRequest(token);

        var viaContext = await request.Context.GetSessionTokenAsync<TestSessionToken>();
        var viaRequired = await request.Context.GetRequiredSessionTokenAsync<TestSessionToken>();
        var viaSession = await request.GetTokenAsync();

        viaContext.ShouldBe(token);
        viaRequired.ShouldBeSameAs(viaContext);
        viaSession.ShouldBeSameAs(viaContext);
        host.Store.Calls.ShouldBe(["GetSessionData", "IsTokenCurrent"]);
    }

    [TestMethod]
    public async Task HttpContextExtensions_GetSessionToken_NoSessionHandlingRegistered_ReturnsNull()
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };

        (await context.GetSessionTokenAsync<TestSessionToken>()).ShouldBeNull();
    }

    private static ParameterInfo Param(string name)
    {
        var method = typeof(SessionBindingTests).GetMethod(nameof(Endpoint), BindingFlags.NonPublic | BindingFlags.Static)!;
        return method.GetParameters().Single(p => p.Name == name);
    }

    // Reference endpoint signature used to obtain ParameterInfo values with the nullability and attributes that drive binding.
    private static void Endpoint(
        TestSessionToken required,
        TestSessionToken? optional,
        [SessionAccess(SessionAccessOptions.ForceValidate)] TestSessionToken forced,
        [SessionAccess(SessionAccessOptions.OptionalUserIdPrecondition)] TestSessionToken optionalPrecondition)
    {
    }
}
