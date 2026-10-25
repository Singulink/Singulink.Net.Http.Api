using System.Text.Json;
using System.Text.Json.Serialization;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// The constructors that take a type info resolver apply the API null handling before the configuration callback, and the constructor that takes options
/// uses them as they are.
/// </summary>
[PrefixTestClass]
public sealed class SerializerOptionsTests
{
    private const string WithoutNullableMembers = """{"name":"n","size":1,"code":"c"}""";
    private const string WithNullMembers = """{"name":"n","note":null,"count":null,"size":1,"code":"c","tag":null,"rank":null}""";

    private static readonly NullHandlingContract Contract = new("n", null, null, 1) { Code = "c", Tag = null, Rank = null };

    [TestMethod]
    public void ResolverConstructor_AppliesNullHandling()
    {
        var client = new JsonTestClient(new FakeHttpMessageHandler());

        client.Options.RespectNullableAnnotations.ShouldBeTrue();
        client.Options.RespectRequiredConstructorParameters.ShouldBeTrue();
        client.Options.DefaultIgnoreCondition.ShouldBe(JsonIgnoreCondition.Never);
        client.Options.PropertyNamingPolicy.ShouldBe(JsonNamingPolicy.CamelCase);
    }

    [TestMethod]
    public async Task ResolverConstructor_RequestContent_WritesNulls()
    {
        var handler = new FakeHttpMessageHandler();
        var client = new JsonTestClient(handler);

        await client.PostContractAsync(Contract);

        handler.SingleRequest.Body.ShouldBe(WithNullMembers);
    }

    [TestMethod]
    public async Task ResolverConstructor_ResponseWithoutNullableMembers_IsRead()
    {
        var client = new JsonTestClient(new FakeHttpMessageHandler(_ => Responses.Json(WithoutNullableMembers)));

        (await client.GetContractAsync()).ShouldBe(Contract);
    }

    [TestMethod]
    [DataRow("""{"size":1,"code":"c"}""")]
    [DataRow("""{"name":null,"size":1,"code":"c"}""")]
    public async Task ResolverConstructor_ResponseWithoutNonNullableMember_Throws(string json)
    {
        var client = new JsonTestClient(new FakeHttpMessageHandler(_ => Responses.Json(json)));

        await Should.ThrowAsync<JsonException>(client.GetContractAsync);
    }

    [TestMethod]
    public async Task ConfigureOptions_RunsAfterNullHandling()
    {
        var handler = new FakeHttpMessageHandler();
        var client = new JsonTestClient(handler, options => {
            options.RespectNullableAnnotations = false;
            options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        await client.PostContractAsync(Contract);

        handler.SingleRequest.Body.ShouldBe(WithoutNullableMembers);
        client.Options.RespectNullableAnnotations.ShouldBeFalse();
        client.Options.RespectRequiredConstructorParameters.ShouldBeTrue();
    }

    [TestMethod]
    public async Task OptionsConstructor_UsesOptionsAsTheyAre()
    {
        var handler = new FakeHttpMessageHandler();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = NullHandlingJsonContext.Default };
        var client = new JsonTestClient(handler, options);

        await client.PostContractAsync(Contract);

        client.Options.ShouldBeSameAs(options);
        options.RespectNullableAnnotations.ShouldBeFalse();
        options.RespectRequiredConstructorParameters.ShouldBeFalse();
        handler.SingleRequest.Body.ShouldBe("""{"name":"n","note":null,"count":null,"size":1,"code":"c","tag":null,"rank":null}""");
    }

    private sealed class JsonTestClient : ApiClientBase
    {
        public JsonTestClient(FakeHttpMessageHandler handler, Action<JsonSerializerOptions>? configureOptions = null)
            : base(NullHandlingJsonContext.Default, configureOptions)
        {
            HttpHandler = handler;
        }

        public JsonTestClient(FakeHttpMessageHandler handler, JsonSerializerOptions options) : base(options)
        {
            HttpHandler = handler;
        }

        public JsonSerializerOptions Options => SerializerOptions;

        public Task PostContractAsync(NullHandlingContract contract) => PostAsync("contracts", contract);

        public Task<NullHandlingContract> GetContractAsync() => GetAsync<NullHandlingContract>("contracts/1");

        protected override Uri GetBaseAddress() => TestApiClient.DefaultBaseAddress;
    }
}
