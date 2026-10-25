using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service;

#pragma warning disable SA1402 // File may only contain a single type

/// <summary>
/// <see cref="ServiceCollectionExtensions.AddApiJson"/> configures the minimal API and SignalR payload options with the app's resolver and the API null
/// handling, whether SignalR is registered before or after it or not at all, and leaves the toolkit's own JSON as it is.
/// </summary>
[PrefixTestClass]
public sealed class ApiJsonTests
{
    private const string WithoutNullableMembers = """{"name":"n","size":1,"code":"c"}""";
    private const string WithNullMembers = """{"name":"n","note":null,"count":null,"size":1,"code":"c"}""";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public enum SignalRRegistration
    {
        BeforeApiJson,
        AfterApiJson,
    }

    public sealed class ContractHub : Hub
    {
        public string Describe(ApiJsonContract contract) => $"{contract.Name}: {contract.Note ?? "no note"}";

        public ApiJsonContract Get() => ApiJsonContract.Sample;
    }

    private static Task<TestWebHost> StartAsync(Action<IServiceCollection>? configureServices = null)
    {
        return TestWebHost.StartAsync(
            app => {
                app.MapPost("/contracts", (ApiJsonContract contract) => contract);
                app.MapGet("/painted", () => new PaintedContract("n", Color.Green));
            },
            configureServices: services => {
                services.AddApiExceptionHandler();
                (configureServices ?? (s => s.AddApiJson(ApiJsonTestContext.Default)))(services);
            });
    }

    private static Task<HttpResponseMessage> PostJsonAsync(TestWebHost host, string json)
    {
        return host.Client.PostAsync("/contracts", new StringContent(json, Encoding.UTF8, "application/json"));
    }

    [TestMethod]
    [DataRow(WithoutNullableMembers)]
    [DataRow(WithNullMembers)]
    public async Task HttpBody_NullableConstructorParametersMissingOrNull_AreAccepted(string json)
    {
        await using var host = await StartAsync();

        var response = await PostJsonAsync(host, json);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldBe(WithNullMembers);
    }

    [TestMethod]
    [DataRow("""{"size":1,"code":"c"}""", "The request is not valid. Path: $ | ")]
    [DataRow("""{"name":null,"size":1,"code":"c"}""", "The request is not valid. Path: $.name | ")]
    [DataRow("""{"name":"n","code":"c"}""", "The request is not valid. Path: $ | ")]
    [DataRow("""{"name":"n","size":1}""", "The request is not valid. Path: $ | ")]
    public async Task HttpBody_NonNullableOrRequiredMembersMissingOrNull_AreRejected(string json, string message)
    {
        await using var host = await StartAsync();

        var response = await PostJsonAsync(host, json);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldStartWith(message);
    }

    [TestMethod]
    public async Task Resolver_IsInsertedFirst_AndConfigureRunsBeforeNullHandling()
    {
        var resolver = new RecordingResolver(ApiJsonTestContext.Default);

        await using var host = await StartAsync(services => services.AddApiJson(resolver, options => {
            options.Converters.Add(new JsonStringEnumConverter());
            options.RespectNullableAnnotations = false;
            options.TypeInfoResolverChain.Add(OtherApiJsonContext.Default);
        }));

        (await host.Client.GetStringAsync("/painted")).ShouldBe("""{"name":"n","color":"Green"}""");
        (await (await PostJsonAsync(host, WithoutNullableMembers)).Content.ReadAsStringAsync()).ShouldBe(WithNullMembers);
        resolver.Types.ShouldContain(typeof(PaintedContract));
        resolver.Types.ShouldContain(typeof(ApiJsonContract));

        var options = host.App.Services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        options.RespectNullableAnnotations.ShouldBeTrue();
        JsonSerializer.Deserialize<OtherApiJsonContract>("{}", options).ShouldBe(new OtherApiJsonContract(null));
    }

    [TestMethod]
    [DataRow(SignalRRegistration.BeforeApiJson)]
    [DataRow(SignalRRegistration.AfterApiJson)]
    public void HubPayloadOptions_HaveResolverConfigurationAndNullHandling(SignalRRegistration registration)
    {
        var resolver = new RecordingResolver(ApiJsonTestContext.Default);
        var services = new ServiceCollection();

        if (registration is SignalRRegistration.BeforeApiJson)
            services.AddSignalR();

        services.AddApiJson(resolver, options => options.Converters.Add(new JsonStringEnumConverter()));

        if (registration is SignalRRegistration.AfterApiJson)
            services.AddSignalR();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value.PayloadSerializerOptions;

        options.RespectNullableAnnotations.ShouldBeTrue();
        options.RespectRequiredConstructorParameters.ShouldBeTrue();
        options.DefaultIgnoreCondition.ShouldBe(JsonIgnoreCondition.Never);

        JsonSerializer.Serialize(new PaintedContract("n", Color.Green), options).ShouldBe("""{"name":"n","color":"Green"}""");
        JsonSerializer.Deserialize<ApiJsonContract>(WithoutNullableMembers, options).ShouldBe(ApiJsonContract.Sample);
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<ApiJsonContract>("""{"size":1,"code":"c"}""", options));
        resolver.Types.ShouldContain(typeof(ApiJsonContract));
    }

    [TestMethod]
    [DataRow(SignalRRegistration.BeforeApiJson)]
    [DataRow(SignalRRegistration.AfterApiJson)]
    public async Task HubPayloads_HaveNullHandling(SignalRRegistration registration)
    {
        await using var host = await TestWebHost.StartAsync(
            app => app.MapHub<ContractHub>("/hub"),
            configureServices: services => {
                if (registration is SignalRRegistration.BeforeApiJson)
                    services.AddSignalR();

                services.AddApiJson(ApiJsonTestContext.Default);

                if (registration is SignalRRegistration.AfterApiJson)
                    services.AddSignalR();
            });

        var server = host.App.GetTestServer();

        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "hub"), options => {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        await connection.StartAsync().WaitAsync(Timeout);

        (await connection.InvokeAsync<string>("Describe", Parse(WithoutNullableMembers)).WaitAsync(Timeout)).ShouldBe("n: no note");
        await Should.ThrowAsync<HubException>(() => connection.InvokeAsync<string>("Describe", Parse("""{"size":1,"code":"c"}""")).WaitAsync(Timeout));

        var contract = await connection.InvokeAsync<JsonElement>("Get").WaitAsync(Timeout);
        contract.GetRawText().ShouldBe(WithNullMembers);

        static JsonElement Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
    }

    [TestMethod]
    public async Task ToolkitJson_IsNotAffected()
    {
        // The app's options name properties in upper snake case and write nulls. The session cookie and the records of streaming responses keep the
        // toolkit's format, and only the streamed items use the app's options.

        var store = new InMemorySessionStore();

        await using var host = await TestWebHost.StartAsync(
            app => app.MapGet("/items", async (HttpContext context) => {
                await context.GetRequiredSessionTokenAsync<TestSessionToken>(SessionAccessOptions.OptionalUserIdPrecondition);
                return ItemsAsync();
            }),
            configureServices: services => {
                services.AddSingleton<IOriginValidator>(new OriginValidator("localhost"));
                services.AddSingleton(store);
                services.AddHttpSessionHandling<TestSessionToken, ScopedInMemorySessionStore>();
                services.AddApiJson(ApiJsonTestContext.Default, options => options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseUpper);
            });

        var utcNow = DateTime.UtcNow;
        store.UserStamps[1] = 1;
        store.Sessions[1] = new TestSessionData {
            Id = 1, UserId = 1, Device = "TestClient/1.0", RefreshedUtc = utcNow.AddMinutes(-11), ExpiresUtc = utcNow.AddDays(30), IsPersistent = true,
            CreatedUtc = utcNow.AddMinutes(-11),
        };
        var cookie = new TestSessionCookie(new TestSessionToken(1, 1, 1), utcNow.AddMinutes(-11), TimeSpan.FromDays(30), 0, true);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/items");
        request.Headers.Add("Cookie", $"{TestSessionCookies.CookieName}={TestSessionCookies.Protect(host.App.Services, cookie)}");
        request.Headers.Add("User-Agent", "TestClient/1.0");

        var response = await host.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        (await response.Content.ReadAsStringAsync()).ShouldBe("""{"item":{"NAME":"a","NOTE":null}}""" + "\n" + """{"end":true}""" + "\n");

        response.Headers.TryGetValues("Set-Cookie", out var cookies).ShouldBeTrue();
        string refreshed = cookies!.ShouldHaveSingleItem().Split(';')[0][(TestSessionCookies.CookieName.Length + 1)..];
        string cookieJson = host.App.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(TestSessionCookies.Purpose).Unprotect(refreshed);

        TestSessionCookies.FromJson(cookieJson).Token.ShouldBe(cookie.Token);
        cookieJson.ShouldContain("\"SessionId\":1");

        static async IAsyncEnumerable<StreamedItem> ItemsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new StreamedItem("a", null);
        }
    }

    /// <summary>
    /// A resolver that records the types it is asked for.
    /// </summary>
    private sealed class RecordingResolver(IJsonTypeInfoResolver inner) : IJsonTypeInfoResolver
    {
        private readonly ConcurrentQueue<Type> _types = [];

        public IReadOnlyCollection<Type> Types => _types;

        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            _types.Enqueue(type);
            return inner.GetTypeInfo(type, options);
        }
    }
}

public enum Color
{
    Red,
    Green,
}

public sealed record ApiJsonContract(string Name, string? Note, int? Count, int Size)
{
    public static ApiJsonContract Sample { get; } = new("n", null, null, 1) { Code = "c" };

    public required string Code { get; init; }
}

public sealed record PaintedContract(string Name, Color Color);

public sealed record StreamedItem(string Name, string? Note);

public sealed record OtherApiJsonContract(string? Note);

[JsonSerializable(typeof(ApiJsonContract))]
[JsonSerializable(typeof(PaintedContract))]
[JsonSerializable(typeof(StreamedItem))]
[JsonSerializable(typeof(IAsyncEnumerable<StreamedItem>))]
[JsonSerializable(typeof(string))]
internal sealed partial class ApiJsonTestContext : JsonSerializerContext;

[JsonSerializable(typeof(OtherApiJsonContract))]
internal sealed partial class OtherApiJsonContext : JsonSerializerContext;
