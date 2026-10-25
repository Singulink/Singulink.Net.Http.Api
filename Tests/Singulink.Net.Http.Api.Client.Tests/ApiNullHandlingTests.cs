using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using RuntimeNullables;

namespace Singulink.Net.Http.Api.Client;

#pragma warning disable SA1402 // File may only contain a single type

/// <summary>
/// Tests for the API null handling of JSON serializer options, with source-generated and reflection-based contracts. They run on the .NET and .NET Standard
/// builds.
/// </summary>
[PrefixTestClass]
public sealed class ApiNullHandlingTests
{
    private const string WithoutNullableMembers = """{"name":"n","size":1,"code":"c"}""";
    private const string WithNullMembers = """{"name":"n","note":null,"count":null,"size":1,"code":"c","tag":null,"rank":null}""";

    private static readonly NullHandlingContract Contract = new("n", null, null, 1) { Code = "c", Tag = null, Rank = null };

    /// <summary>
    /// Gets options with the null handling applied to source-generated contracts, and to reflection-based contracts where reflection is available.
    /// </summary>
    private static IEnumerable<(string Mode, JsonSerializerOptions Options)> CreateOptions()
    {
        List<(string, IJsonTypeInfoResolver)> resolvers = [("source-generated", NullHandlingJsonContext.Default)];

        if (JsonSerializer.IsReflectionEnabledByDefault)
            resolvers.Add(("reflection-based", new DefaultJsonTypeInfoResolver()));

        foreach (var (contracts, resolver) in resolvers)
            yield return (contracts, new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = resolver }.ConfigureApiNullHandling());
    }

    [TestMethod]
    [DataRow(WithoutNullableMembers)]
    [DataRow(WithNullMembers)]
    public void Read_NullableMembersMissingOrNull_AreNull(string json)
    {
        foreach (var (mode, options) in CreateOptions())
            JsonSerializer.Deserialize<NullHandlingContract>(json, options).ShouldBe(Contract, mode);
    }

    [TestMethod]
    public void Read_NullableMembersWithValues_AreRead()
    {
        foreach (var (mode, options) in CreateOptions())
        {
            JsonSerializer.Deserialize<NullHandlingContract>("""{"name":"n","note":"x","count":2,"size":1,"code":"c","tag":"t","rank":3}""", options)
                .ShouldBe(new NullHandlingContract("n", "x", 2, 1) { Code = "c", Tag = "t", Rank = 3 }, mode);
        }
    }

    [TestMethod]
    [DataRow("""{"size":1,"code":"c"}""", DisplayName = "Reference type constructor parameter missing")]
    [DataRow("""{"name":null,"size":1,"code":"c"}""", DisplayName = "Reference type constructor parameter null")]
    [DataRow("""{"name":"n","code":"c"}""", DisplayName = "Value type constructor parameter missing")]
    [DataRow("""{"name":"n","size":1}""", DisplayName = "Required property missing")]
    [DataRow("""{"name":"n","size":1,"code":null}""", DisplayName = "Required property null")]
    public void Read_NonNullableMembersMissingOrNull_Throws(string json)
    {
        foreach (var (mode, options) in CreateOptions())
            Should.Throw<JsonException>(() => JsonSerializer.Deserialize<NullHandlingContract>(json, options), mode);
    }

    [TestMethod]
    public void Read_JsonRequiredMembers_AreOptionalOnlyIfNullable()
    {
        foreach (var (mode, options) in CreateOptions())
        {
            var contract = JsonSerializer.Deserialize<JsonRequiredContract>("""{"id":"i"}""", options).ShouldNotBeNull(mode);
            contract.Id.ShouldBe("i", mode);
            contract.Label.ShouldBeNull(mode);
            contract.Level.ShouldBeNull(mode);

            Should.Throw<JsonException>(() => JsonSerializer.Deserialize<JsonRequiredContract>("""{"label":"l","level":1}""", options), mode);
        }
    }

    [TestMethod]
    public void Read_NestedTypes_AreCovered()
    {
        foreach (var (mode, options) in CreateOptions())
        {
            JsonSerializer.Deserialize<List<NullHandlingContract>>($"[{WithoutNullableMembers}]", options).ShouldBe([Contract], mode);
            Should.Throw<JsonException>(() => JsonSerializer.Deserialize<List<NullHandlingContract>>("""[{"size":1,"code":"c"}]""", options), mode);
        }
    }

    [TestMethod]
    public void Write_NullValues_AreWritten()
    {
        foreach (var (mode, options) in CreateOptions())
            JsonSerializer.Serialize(Contract, options).ShouldBe(WithNullMembers, mode);
    }

    [TestMethod]
    public void Write_NullForNonNullableMember_Throws()
    {
        foreach (var (mode, options) in CreateOptions())
        {
            Should.Throw<JsonException>(() => JsonSerializer.Serialize(new UncheckedContract(null!, null), options), mode);
            JsonSerializer.Serialize(new UncheckedContract("n", null), options).ShouldBe("""{"name":"n","note":null}""", mode);
        }
    }

    [TestMethod]
    public void RoundTrip_NullValues_AreReadBack()
    {
        foreach (var (mode, options) in CreateOptions())
        {
            JsonSerializer.Deserialize<NullHandlingContract>(JsonSerializer.Serialize(Contract, options), options).ShouldBe(Contract, mode);

            var contract = new JsonRequiredContract { Id = "i" };
            var read = JsonSerializer.Deserialize<JsonRequiredContract>(JsonSerializer.Serialize(contract, options), options).ShouldNotBeNull(mode);
            (read.Id, read.Label, read.Level).ShouldBe(("i", (string?)null, (int?)null), mode);
        }
    }

    [TestMethod]
    public void RoundTrip_NullValuesOfMembersWithDefaults_StayNull()
    {
        foreach (var (mode, options) in CreateOptions())
        {
            var contract = new DefaultsContract { Limit = null };
            var read = JsonSerializer.Deserialize<DefaultsContract>(JsonSerializer.Serialize(contract, options), options).ShouldNotBeNull(mode);

            read.Limit.ShouldBeNull(mode);
            read.Sort.ShouldBe("name", mode);

            JsonSerializer.Deserialize<DefaultsContract>("{}", options).ShouldNotBeNull(mode).Limit.ShouldBe(50, mode);
        }
    }

    [TestMethod]
    public void ConfigureApiNullHandling_SetsOptionsAndReturnsThem()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = NullHandlingJsonContext.Default };

        options.ConfigureApiNullHandling().ShouldBeSameAs(options);

        options.RespectNullableAnnotations.ShouldBeTrue();
        options.RespectRequiredConstructorParameters.ShouldBeTrue();
        options.DefaultIgnoreCondition.ShouldBe(JsonIgnoreCondition.Never);
        options.PropertyNamingPolicy.ShouldBe(JsonNamingPolicy.CamelCase);
    }

    [TestMethod]
    public void ConfigureApiNullHandling_KeepsDefaultIgnoreCondition()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) {
            TypeInfoResolver = NullHandlingJsonContext.Default,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        options.ConfigureApiNullHandling();

        options.DefaultIgnoreCondition.ShouldBe(JsonIgnoreCondition.WhenWritingNull);
        JsonSerializer.Serialize(Contract, options).ShouldBe(WithoutNullableMembers);
    }

    [TestMethod]
    public void ConfigureApiNullHandling_CalledAgain_KeepsResolver()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = NullHandlingJsonContext.Default }.ConfigureApiNullHandling();
        var resolver = options.TypeInfoResolver;

        options.ConfigureApiNullHandling();

        options.TypeInfoResolver.ShouldBeSameAs(resolver);
        options.TypeInfoResolverChain.ShouldHaveSingleItem().ShouldBeSameAs(resolver);
        JsonSerializer.Deserialize<NullHandlingContract>(WithoutNullableMembers, options).ShouldBe(Contract);
    }

    [TestMethod]
    public void ConfigureApiNullHandling_ResolverOfOtherOptionsWithNullHandling_IsCovered()
    {
        var other = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = NullHandlingJsonContext.Default }.ConfigureApiNullHandling();

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.TypeInfoResolverChain.Add(other.TypeInfoResolver!);
        options.TypeInfoResolverChain.Add(OtherJsonContext.Default);

        options.ConfigureApiNullHandling();

        JsonSerializer.Deserialize<NullHandlingContract>(WithoutNullableMembers, options).ShouldBe(Contract);
        JsonSerializer.Deserialize<OtherContract>("{}", options).ShouldBe(new OtherContract(null));
    }

    [TestMethod]
    public void ConfigureApiNullHandling_ResolversAddedAfterwards_AreNotCovered()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = NullHandlingJsonContext.Default }.ConfigureApiNullHandling();
        options.TypeInfoResolverChain.Add(OtherJsonContext.Default);

        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<OtherContract>("{}", options));
        JsonSerializer.Deserialize<NullHandlingContract>(WithoutNullableMembers, options).ShouldBe(Contract);
    }

    [TestMethod]
    public void ConfigureApiNullHandling_CalledAgain_CoversResolversAddedSince()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = NullHandlingJsonContext.Default }.ConfigureApiNullHandling();
        options.TypeInfoResolverChain.Insert(0, OtherJsonContext.Default);

        options.ConfigureApiNullHandling();

        JsonSerializer.Deserialize<OtherContract>("{}", options).ShouldBe(new OtherContract(null));
        JsonSerializer.Deserialize<NullHandlingContract>(WithoutNullableMembers, options).ShouldBe(Contract);
    }

    [TestMethod]
    public void ConfigureApiNullHandling_NoResolver_Throws()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        Should.Throw<InvalidOperationException>(() => options.ConfigureApiNullHandling()).Message.ShouldContain("Add your context/resolver first");

        options.RespectNullableAnnotations.ShouldBeFalse();
        options.RespectRequiredConstructorParameters.ShouldBeFalse();
        options.DefaultIgnoreCondition.ShouldBe(JsonIgnoreCondition.Never);
        options.TypeInfoResolver.ShouldBeNull();
    }

    [TestMethod]
    public void ConfigureApiNullHandling_ReadOnlyOptions_Throws()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = NullHandlingJsonContext.Default };
        options.MakeReadOnly();

        Should.Throw<InvalidOperationException>(() => options.ConfigureApiNullHandling()).Message.ShouldContain("read-only");
    }

    [TestMethod]
    public void ContextDefaultTypeInfo_IsNotAffected()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = StrictJsonContext.Default }.ConfigureApiNullHandling();

        JsonSerializer.Deserialize<NullHandlingContract>(WithoutNullableMembers, options).ShouldBe(Contract);
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize(WithoutNullableMembers, StrictJsonContext.Default.NullHandlingContract));
    }
}

public sealed record NullHandlingContract(string Name, string? Note, int? Count, int Size)
{
    public required string Code { get; init; }

    public required string? Tag { get; init; }

    public required int? Rank { get; init; }
}

public sealed class JsonRequiredContract
{
    [JsonRequired]
    public string Id { get; set; } = string.Empty;

    [JsonRequired]
    public string? Label { get; set; }

    [JsonRequired]
    public int? Level { get; set; }
}

public sealed record OtherContract(string? Note);

/// <summary>
/// A contract without runtime null checks, so that it can hold a <see langword="null"/> where its annotations don't allow one.
/// </summary>
[NullChecks(false)]
public sealed record UncheckedContract(string Name, string? Note);

public sealed class DefaultsContract
{
    public int? Limit { get; set; } = 50;

    public string? Sort { get; set; } = "name";
}

[JsonSerializable(typeof(NullHandlingContract))]
[JsonSerializable(typeof(List<NullHandlingContract>))]
[JsonSerializable(typeof(JsonRequiredContract))]
[JsonSerializable(typeof(DefaultsContract))]
[JsonSerializable(typeof(UncheckedContract))]
public sealed partial class NullHandlingJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(OtherContract))]
public sealed partial class OtherJsonContext : JsonSerializerContext;

/// <summary>
/// A context whose own options have the respect settings, so that its own type info rejects a missing nullable constructor parameter.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(NullHandlingContract))]
public sealed partial class StrictJsonContext : JsonSerializerContext;
