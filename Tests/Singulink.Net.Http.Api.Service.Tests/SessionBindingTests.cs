using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
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
        var session = host.CreateSession();
        var request = host.CreateRequest(session);

        (await request.Context.BindSessionTokenAsync<TestSessionToken>(Param("required"))).ShouldBe(session.Token);
        host.Store.Calls.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task Bind_ForceValidateAttribute_ValidatesSession()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        var request = host.CreateRequest(session);

        (await request.Context.BindSessionTokenAsync<TestSessionToken>(Param("forced"))).ShouldBe(session.Token);
        host.Store.Calls.ShouldBe(["GetSession"]);
    }

    [TestMethod]
    public async Task Bind_OptionalPreconditionAttribute_AllowsMissingPrecondition()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession();
        var request = host.CreateRequest(session, includeUserIdPrecondition: false);

        (await request.Context.BindSessionTokenAsync<TestSessionToken>(Param("optionalPrecondition"))).ShouldBe(session.Token);
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
    }

    [TestMethod]
    public void GetSessionContext_DifferentRequests_GetDifferentInstances()
    {
        var host = new SessionTestHost();

        host.CreateRequest().Session.ShouldNotBeSameAs(host.CreateRequest().Session);
    }

    [TestMethod]
    public async Task GetSessionContext_ConcurrentFirstCalls_GetOneInstance()
    {
        // The HTTP context of a hub connection is shared by its hub method invocations, which can run concurrently.

        var host = new SessionTestHost();
        var factory = new GatedSessionContextFactory(host.Services.GetRequiredService<ISessionContextFactory<TestSessionToken>>());
        var services = new ServiceCollection().AddSingleton<ISessionContextFactory<TestSessionToken>>(factory).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };

        var first = Task.Run(() => context.GetSessionContext<TestSessionToken>());
        await factory.FirstCreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // While the first call creates the context, the second call gets time to create one of its own if it can.
        var second = Task.Run(() => context.GetSessionContext<TestSessionToken>());
        await Task.WhenAny(factory.SecondCreateStarted.Task, Task.Delay(500));
        factory.ReleaseFirstCreate();

        (await second).ShouldBeSameAs(await first);
        factory.CreateCount.ShouldBe(1);
    }

    [TestMethod]
    public void GetSessionContext_NoSessionHandlingRegistered_ReturnsNullAndRequiredThrows()
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };

        context.GetSessionContext<TestSessionToken>().ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => context.GetRequiredSessionContext<TestSessionToken>()).Message.ShouldContain("AddHttpSessionHandling");
    }

    [TestMethod]
    public async Task HttpContextExtensions_GetSessionToken_UsesSharedContext()
    {
        var host = new SessionTestHost();
        var session = host.CreateSession(age: TimeSpan.FromMinutes(11));
        var request = host.CreateRequest(session);

        var viaContext = await request.Context.GetSessionTokenAsync<TestSessionToken>();
        var viaRequired = await request.Context.GetRequiredSessionTokenAsync<TestSessionToken>();
        var viaSession = await request.GetTokenAsync();

        viaContext.ShouldBe(session.Token);
        viaRequired.ShouldBeSameAs(viaContext);
        viaSession.ShouldBeSameAs(viaContext);
        host.Store.Calls.ShouldBe(["GetSession"]);
    }

    [TestMethod]
    public async Task HttpContextExtensions_GetSessionToken_NoSessionHandlingRegistered_ReturnsNull()
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };

        (await context.GetSessionTokenAsync<TestSessionToken>()).ShouldBeNull();
    }

    [TestMethod]
    public void GetClientIpAddress_MapsIPv4MappedAddresses()
    {
        static IPAddress? Get(string? address)
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = address is null ? null : IPAddress.Parse(address);
            return context.GetClientIpAddress();
        }

        Get("::ffff:203.0.113.7").ShouldBe(IPAddress.Parse("203.0.113.7"));
        Get("203.0.113.7").ShouldBe(IPAddress.Parse("203.0.113.7"));
        Get("2001:db8::7").ShouldBe(IPAddress.Parse("2001:db8::7"));
        Get(null).ShouldBeNull();
    }

    [TestMethod]
    public async Task SessionContext_IpAddress_IsMappedToIPv4()
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest(ipAddress: IPAddress.Parse("::ffff:203.0.113.7"));
        SignInInfo? signInInfo = null;

        await request.Session.SignInAsync(persistent: false, info => {
            signInInfo = info;
            return Task.FromResult(new TestSessionToken(1, 1, 1));
        });

        request.Session.IpAddress.ShouldBe(IPAddress.Parse("203.0.113.7"));
        signInInfo.ShouldNotBeNull().IpAddress.ShouldBe(IPAddress.Parse("203.0.113.7"));
    }

    [TestMethod]
    public void GetClientDevice_IsTheSessionDevice()
    {
        var host = new SessionTestHost(o => o.MaxDeviceLength = 10);
        var request = host.CreateRequest();
        request.Context.Request.Headers.UserAgent = "UnknownClient/1.0 (with a long description)";

        request.Context.GetClientDevice().ShouldBe("UnknownCli");
        request.Session.Device.ShouldBe("UnknownCli");

        var withoutServices = new DefaultHttpContext();
        withoutServices.Request.Headers.UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0";

        withoutServices.GetClientDevice().ShouldStartWith("Windows (");
    }

    [TestMethod]
    public void GetClientDevice_IsReadOncePerRequest()
    {
        var host = new SessionTestHost();
        var request = host.CreateRequest();
        request.Context.Request.Headers.UserAgent = "FirstClient/1.0";

        request.Context.GetClientDevice().ShouldBe("FirstClient/1.0");

        request.Context.Request.Headers.UserAgent = "SecondClient/2.0";

        request.Context.GetClientDevice().ShouldBe("FirstClient/1.0");
        request.Session.Device.ShouldBe("FirstClient/1.0");
        host.CreateRequest().Context.GetClientDevice().ShouldNotBe("FirstClient/1.0");
    }

    [TestMethod]
    public void GetClientDevice_WithoutUserAgent_ThrowsUserAgentRequired()
    {
        var context = new DefaultHttpContext();

        Should.Throw<BadRequestApiException>(() => context.GetClientDevice()).ErrorCode.ShouldBe(ApiErrorCodes.UserAgentRequired);
    }

    [TestMethod]
    public async Task EndpointParameter_Required_WithoutSession_ReturnsSignInRequired()
    {
        await using var host = await StartEndpointHostAsync();

        var response = await SendAsync(host, HttpMethod.Get, "/required", withSession: false);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).ShouldStartWith($"[{ApiErrorCodes.SignInRequired}]");
    }

    [TestMethod]
    public async Task EndpointParameter_Nullable_WithoutSession_IsNull()
    {
        await using var host = await StartEndpointHostAsync();

        var get = await SendAsync(host, HttpMethod.Get, "/optional", withSession: false);
        var post = await SendAsync(host, HttpMethod.Post, "/optional", withSession: false);

        (await get.Content.ReadAsStringAsync()).ShouldBe("guest");
        (await post.Content.ReadAsStringAsync()).ShouldBe("guest");
    }

    [TestMethod]
    public async Task EndpointParameter_WithSession_IsTheSessionToken()
    {
        await using var host = await StartEndpointHostAsync();

        var get = await SendAsync(host, HttpMethod.Get, "/required", withSession: true);
        var post = await SendAsync(host, HttpMethod.Post, "/optional", withSession: true);

        (await get.Content.ReadAsStringAsync()).ShouldBe("user 1");
        (await post.Content.ReadAsStringAsync()).ShouldBe("user 1");
    }

    [TestMethod]
    public async Task EndpointParameter_SessionAccessAttribute_IsApplied()
    {
        await using var host = await StartEndpointHostAsync();
        var store = host.App.Services.GetRequiredService<InMemorySessionStore>();

        var forced = await SendAsync(host, HttpMethod.Get, "/forced", withSession: true);
        var withoutPrecondition = await SendAsync(host, HttpMethod.Get, "/required", withSession: true, userIdPrecondition: false);

        (await forced.Content.ReadAsStringAsync()).ShouldBe("user 1");
        store.Calls.ShouldBe(["GetSession"]);
        withoutPrecondition.StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired);
    }

    /// <summary>
    /// Starts a host whose endpoints take the session token as a parameter, bound through <see cref="ISessionToken{TSelf}"/>, with session 1 of user 1
    /// in the store.
    /// </summary>
    private static async Task<TestWebHost> StartEndpointHostAsync()
    {
        var store = new InMemorySessionStore();
        var utcNow = DateTime.UtcNow;

        store.UserStamps[1] = 1;
        store.Sessions[1] = new TestSessionData {
            Id = 1, UserId = 1, Device = "TestClient/1.0", RefreshedUtc = utcNow, ExpiresUtc = utcNow.AddDays(30), IsPersistent = true, CreatedUtc = utcNow,
        };

        return await TestWebHost.StartAsync(
            app => {
                app.MapGet("/required", (TestSessionToken token) => $"user {token.UserId}");
                app.MapGet("/optional", (TestSessionToken? token) => token is null ? "guest" : $"user {token.UserId}");
                app.MapPost("/optional", (TestSessionToken? token) => token is null ? "guest" : $"user {token.UserId}");
                app.MapGet("/forced", ([SessionAccess(SessionAccessOptions.ForceValidate)] TestSessionToken token) => $"user {token.UserId}");
            },
            configureServices: services => {
                services.AddSingleton<IOriginValidator>(new OriginValidator("localhost"));
                services.AddSingleton(store);
                services.AddHttpSessionHandling<TestSessionToken, ScopedInMemorySessionStore>();
            });
    }

    private static Task<HttpResponseMessage> SendAsync(TestWebHost host, HttpMethod method, string path, bool withSession, bool userIdPrecondition = true)
    {
        var request = new HttpRequestMessage(method, userIdPrecondition ? path + "?if-userId=1" : path);
        request.Headers.Add("User-Agent", "TestClient/1.0");

        if (withSession)
        {
            var cookie = new TestSessionCookie(new TestSessionToken(1, 1, 1), DateTime.UtcNow, TimeSpan.FromDays(30), 0, true);
            request.Headers.Add("Cookie", $"{TestSessionCookies.CookieName}={TestSessionCookies.Protect(host.App.Services, cookie)}");
        }

        return host.Client.SendAsync(request);
    }

    private static ParameterInfo Param(string name)
    {
        var method = typeof(SessionBindingTests).GetMethod(nameof(Endpoint), BindingFlags.NonPublic | BindingFlags.Static)!;
        return method.GetParameters().Single(p => p.Name == name);
    }

    /// <summary>
    /// Holds up the first context it creates until released.
    /// </summary>
    private sealed class GatedSessionContextFactory(ISessionContextFactory<TestSessionToken> inner) : ISessionContextFactory<TestSessionToken>
    {
        private readonly ManualResetEventSlim _release = new();
        private int _createCount;

        public TaskCompletionSource FirstCreateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondCreateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CreateCount => Volatile.Read(ref _createCount);

        public SessionContext<TestSessionToken> Create(HttpContext httpContext)
        {
            if (Interlocked.Increment(ref _createCount) is 1)
            {
                FirstCreateStarted.SetResult();
                _release.Wait(TimeSpan.FromSeconds(10));
            }
            else
            {
                SecondCreateStarted.TrySetResult();
            }

            return inner.Create(httpContext);
        }

        public void ReleaseFirstCreate() => _release.Set();
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
