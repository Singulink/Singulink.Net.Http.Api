using System.Text.Json;

namespace Singulink.Net.Http.Api.Client;

[PrefixTestClass]
public sealed class JsonSerializerOptionsExtensionsTests
{
    [TestMethod]
    public void GetTypeInfo_ReturnsTypeInfoOfOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = StrictJsonContext.Default }.ConfigureApiNullHandling();
        var typeInfo = options.GetTypeInfo<NullHandlingContract>();

        typeInfo.Options.ShouldBeSameAs(options);
        JsonSerializer.Deserialize("""{"name":"n","size":1,"code":"c"}""", typeInfo)
            .ShouldBe(new NullHandlingContract("n", null, null, 1) { Code = "c", Tag = null, Rank = null });
    }

    [TestMethod]
    public void GetTypeInfo_TypeNotProvided_Throws()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = OtherJsonContext.Default };

        Should.Throw<NotSupportedException>(() => options.GetTypeInfo<NullHandlingContract>());
    }
}
