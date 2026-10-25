using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// <see cref="WebApplicationExtensions.UseApiResponseHandling"/> converts the streaming endpoints of the application wherever it is called in the pipeline:
/// before or after the endpoints are mapped, before or after an explicit <c>UseRouting</c>, and for endpoints mapped in route groups. The application's
/// other middleware, such as authentication and authorization, still runs.
/// </summary>
[PrefixTestClass]
public sealed class EndpointMappingOrderTests
{
    public enum Ordering
    {
        CallBeforeMapping,
        CallAfterMapping,
        CallBetweenMappings,
        RoutingThenCallThenMapping,
        CallThenRoutingThenMapping,
        MappingThenRoutingThenCall,
        MappingThenCallThenRouting,
    }

    [TestMethod]
    [DataRow(Ordering.CallBeforeMapping)]
    [DataRow(Ordering.CallAfterMapping)]
    [DataRow(Ordering.CallBetweenMappings)]
    [DataRow(Ordering.RoutingThenCallThenMapping)]
    [DataRow(Ordering.CallThenRoutingThenMapping)]
    [DataRow(Ordering.MappingThenRoutingThenCall)]
    [DataRow(Ordering.MappingThenCallThenRouting)]
    public async Task Ordering_ConvertsEndpointsAndRunsAuthentication(Ordering ordering)
    {
        // The app's authentication middleware is added automatically. With an explicit UseRouting, the authorization middleware must come after it.

        await using var app = CreateApp(authentication: true);
        Configure(app, ordering);

        await app.StartAsync();

        await ShouldStreamAsync(app, "/items");
        await ShouldStreamAsync(app, "/group/items");
        await ShouldStreamAsync(app, "/group/nested/items");

        using var client = app.GetTestClient();
        (await client.GetStringAsync("/me")).ShouldBe("authenticated");

        using var secure = await client.GetAsync("/secure");
        secure.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TestMethod]
    [DataRow(Ordering.CallBeforeMapping)]
    [DataRow(Ordering.CallAfterMapping)]
    [DataRow(Ordering.CallBetweenMappings)]
    [DataRow(Ordering.RoutingThenCallThenMapping)]
    [DataRow(Ordering.CallThenRoutingThenMapping)]
    [DataRow(Ordering.MappingThenRoutingThenCall)]
    [DataRow(Ordering.MappingThenCallThenRouting)]
    public async Task Ordering_AppEndpointsHaveStreamingMetadataOnce(Ordering ordering)
    {
        // The endpoints of the app's endpoint data source service are the ones that API descriptions and link generation use.

        await using var app = CreateApp(authentication: true);
        Configure(app, ordering);

        await app.StartAsync();

        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        endpoints.Select(e => e.RoutePattern.RawText).ShouldBe(["/group/items", "/group/nested/items", "/items", "/me", "/secure"], ignoreOrder: true);

        foreach (var endpoint in endpoints.Where(e => e.RoutePattern.RawText!.EndsWith("/items", StringComparison.Ordinal)))
        {
            var produces = endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>().Where(m => m.StatusCode == 200).ShouldHaveSingleItem();
            produces.ContentTypes.ShouldBe([StreamingResponse.MediaType]);
        }
    }

    [TestMethod]
    public async Task GroupMappedBefore_EndpointsAddedToGroupAfter_AreConverted()
    {
        await using var app = CreateApp();
        var group = app.MapGroup("/group");
        app.UseApiResponseHandling();
        group.MapGet("/items", () => ItemsAsync());

        await app.StartAsync();

        await ShouldStreamAsync(app, "/group/items");
    }

    [TestMethod]
    public async Task CalledTwice_ConvertsEndpointsOnce()
    {
        (await GetItemsAsync(calls: 2)).ShouldBe(await GetItemsAsync(calls: 1));

        static async Task<string> GetItemsAsync(int calls)
        {
            await using var app = CreateApp();

            for (int i = 0; i < calls; i++)
                app.UseApiResponseHandling();

            app.MapGet("/items", () => ItemsAsync());
            await app.StartAsync();

            using var response = await app.GetTestClient().GetAsync("/items");
            response.Content.Headers.ContentType?.MediaType.ShouldBe(StreamingResponse.MediaType);

            return await response.Content.ReadAsStringAsync();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ControllerRoutesMappedAroundCall_DoNotDuplicateEndpoints(bool callFirst)
    {
        // A conventional route mapped after the call must reuse the controllers' data source: a second one would match every attribute-routed action
        // twice, which fails each request with an ambiguous match.

        await using var app = CreateApp(controllers: true);

        if (callFirst)
            app.UseApiResponseHandling();

        app.MapControllers();

        if (!callFirst)
            app.UseApiResponseHandling();

        app.MapControllerRoute("default", "{controller}/{action=Index}");
        app.MapGet("/items", () => ItemsAsync());

        await app.StartAsync();

        using var client = app.GetTestClient();
        (await client.GetStringAsync("/attributed")).ShouldBe("attributed");
        (await client.GetStringAsync("/Ordering/Conventional")).ShouldBe("conventional");
        await ShouldStreamAsync(app, "/items");
    }

    [TestMethod]
    public async Task CalledFirst_HandlesExceptionsOfLaterMiddleware()
    {
        await using var app = CreateApp();
        app.UseApiResponseHandling();
        app.Use((context, next) => context.Request.Path == "/blocked" ? throw new ForbiddenApiException("Blocked.") : next(context));
        app.MapGet("/items", () => ItemsAsync());

        await app.StartAsync();

        using var response = await app.GetTestClient().GetAsync("/blocked");
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Blocked.");

        await ShouldStreamAsync(app, "/items");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EndpointsMappedInUseEndpoints_FailsAtStartup(bool callFirst)
    {
        // UseEndpoints registers the endpoints with the app's endpoint data source service before they are converted, which would list them twice.

        await using var app = CreateApp();

        if (callFirst)
            app.UseApiResponseHandling();

        app.UseRouting();
#pragma warning disable ASP0014 // Suggest using top level route registrations
        app.UseEndpoints(endpoints => endpoints.MapGet("/items", () => ItemsAsync()));
#pragma warning restore ASP0014

        if (!callFirst)
            app.UseApiResponseHandling();

        (await Should.ThrowAsync<InvalidOperationException>(() => app.StartAsync())).Message.ShouldContain("UseEndpoints");
    }

    [TestMethod]
    public async Task UseEndpointsAfterMappingOnApp_FailsAtStartup()
    {
        // UseEndpoints registers the app's endpoints with the service even if its callback maps none.

        await using var app = CreateApp();
        app.MapGet("/items", () => ItemsAsync());
        app.UseRouting();
#pragma warning disable ASP0014 // Suggest using top level route registrations
        app.UseEndpoints(_ => { });
#pragma warning restore ASP0014
        app.UseApiResponseHandling();

        (await Should.ThrowAsync<InvalidOperationException>(() => app.StartAsync())).Message.ShouldContain("Remove the UseEndpoints call");
    }

    [TestMethod]
    public async Task UseApiResponseHandlingNotCalled_FailsAtStartup()
    {
        // Without the exception middleware, every API exception would become a 500.

        await using var app = CreateApp();
        app.MapGet("/items", () => ItemsAsync());

        (await Should.ThrowAsync<InvalidOperationException>(() => app.StartAsync())).Message.ShouldContain("UseApiResponseHandling is not called");
    }

    [TestMethod]
    public async Task ServicesNotRegistered_Throws()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        await using var app = builder.Build();

        Should.Throw<InvalidOperationException>(() => app.UseApiResponseHandling()).Message.ShouldContain("AddApiResponseHandling");
    }

    [TestMethod]
    public async Task AppDisposed_DisposesDecoratedDataSources()
    {
        var dataSource = new DisposableDataSource();

        var app = CreateApp();
        ((IEndpointRouteBuilder)app).DataSources.Add(dataSource);
        app.UseApiResponseHandling();

        await app.StartAsync();
        app.Services.GetRequiredService<EndpointDataSource>().Endpoints.ShouldBeEmpty();

        await app.StopAsync();
        await app.DisposeAsync();

        dataSource.IsDisposed.ShouldBeTrue();
    }

    private static void Configure(WebApplication app, Ordering ordering)
    {
        switch (ordering)
        {
            case Ordering.CallBeforeMapping:
                app.UseApiResponseHandling();
                MapAll(app);
                break;
            case Ordering.CallAfterMapping:
                MapAll(app);
                app.UseApiResponseHandling();
                break;
            case Ordering.CallBetweenMappings:
                MapFirstHalf(app);
                app.UseApiResponseHandling();
                MapSecondHalf(app);
                break;
            case Ordering.RoutingThenCallThenMapping:
                app.UseRouting();
                app.UseAuthorization();
                app.UseApiResponseHandling();
                MapAll(app);
                break;
            case Ordering.CallThenRoutingThenMapping:
                app.UseApiResponseHandling();
                app.UseRouting();
                app.UseAuthorization();
                MapAll(app);
                break;
            case Ordering.MappingThenRoutingThenCall:
                MapAll(app);
                app.UseRouting();
                app.UseAuthorization();
                app.UseApiResponseHandling();
                break;
            case Ordering.MappingThenCallThenRouting:
                MapAll(app);
                app.UseApiResponseHandling();
                app.UseRouting();
                app.UseAuthorization();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(ordering));
        }
    }

    private static void MapAll(WebApplication app)
    {
        MapFirstHalf(app);
        MapSecondHalf(app);
    }

    private static void MapFirstHalf(WebApplication app)
    {
        app.MapGet("/items", () => ItemsAsync());
        app.MapGet("/me", (HttpContext context) => context.User.Identity?.IsAuthenticated is true ? "authenticated" : "anonymous");
    }

    private static void MapSecondHalf(WebApplication app)
    {
        app.MapGet("/secure", () => "secure").RequireAuthorization();

        var group = app.MapGroup("/group");
        group.MapGet("/items", () => ItemsAsync());
        group.MapGroup("/nested").MapGet("/items", () => ItemsAsync());
    }

    private static async Task ShouldStreamAsync(WebApplication app, string path)
    {
        using var response = await app.GetTestClient().GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe(StreamingResponse.MediaType);
    }

    private static WebApplication CreateApp(bool authentication = false, bool controllers = false)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddApiResponseHandling();

        if (authentication)
        {
            builder.Services.AddAuthentication(TestAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, null);

            builder.Services.AddAuthorization();
        }

        if (controllers)
        {
            builder.Services.AddControllers().ConfigureApplicationPartManager(manager => {
                manager.ApplicationParts.Clear();
                manager.ApplicationParts.Add(new AssemblyPart(typeof(OrderingController).Assembly));
            });
        }

        return builder.Build();
    }

    private static async IAsyncEnumerable<string> ItemsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield return "item";
    }

    private sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Test";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "user")], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }

    private sealed class DisposableDataSource : EndpointDataSource, IDisposable
    {
        public bool IsDisposed { get; private set; }

        public override IReadOnlyList<Endpoint> Endpoints => [];

        public override IChangeToken GetChangeToken() => new CancellationChangeToken(CancellationToken.None);

        public void Dispose() => IsDisposed = true;
    }
}

/// <summary>
/// A controller with an attribute-routed action and a conventionally routed one, for <see cref="EndpointMappingOrderTests"/>.
/// </summary>
public sealed class OrderingController : Controller
{
    [HttpGet("/attributed")]
    public string Attributed() => "attributed";

    public string Conventional() => "conventional";
}
