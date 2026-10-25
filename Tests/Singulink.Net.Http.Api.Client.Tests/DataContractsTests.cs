using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Singulink.Net.Http.Api.DataContracts;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// The types in <c>Singulink.Net.Http.Api.DataContracts</c> go into apps' own contracts and serializer contexts as-is, so every public type in the
/// namespace must round-trip through a source-generated context and through reflection-based serialization, with the API null handling too. Their JSON is
/// public API, which deployed clients read, so each sample is also checked against the JSON it must have.
/// </summary>
[PrefixTestClass]
public sealed partial class DataContractsTests
{
    private static readonly DateTime SampleUtc = new(2026, 10, 22, 12, 30, 0, DateTimeKind.Utc);

    // Samples of every public type in the namespace, with every property set, and with each nullable property null, and the JSON of each.
    private static readonly (object Sample, string Json)[] GoldenSamples = [
        (new PasskeyCeremony("ceremony-id", """{"challenge":"abc"}""", TimeSpan.FromMinutes(5)),
            """{"id":"ceremony-id","optionsJson":"{\u0022challenge\u0022:\u0022abc\u0022}","lifetime":"00:05:00"}"""),
        (new DeviceLinkStart("link-id", "K7PXM3QR", "poll-secret", TimeSpan.FromMinutes(10)),
            """{"id":"link-id","code":"K7PXM3QR","pollSecret":"poll-secret","lifetime":"00:10:00"}"""),
        (new PendingDeviceLink("Windows (Edge 140)", "203.0.113.10", "Toronto, Canada", SampleUtc),
            """{"device":"Windows (Edge 140)","ipAddress":"203.0.113.10","location":"Toronto, Canada","createdUtc":"2026-10-22T12:30:00Z"}"""),
        (new PendingDeviceLink("Windows (Edge 140)", null, null, SampleUtc),
            """{"device":"Windows (Edge 140)","ipAddress":null,"location":null,"createdUtc":"2026-10-22T12:30:00Z"}"""),
        (new BeginPasskeySignInCommand("code-challenge", "user@example.com"), """{"codeChallenge":"code-challenge","userName":"user@example.com"}"""),
        (new BeginPasskeySignInCommand("code-challenge"), """{"codeChallenge":"code-challenge","userName":null}"""),
        (new FinishPasskeySignInCommand("ceremony-id", """{"id":"abc"}""", "code-verifier"),
            """{"ceremonyId":"ceremony-id","credential":"{\u0022id\u0022:\u0022abc\u0022}","codeVerifier":"code-verifier"}"""),
        (new FinishPasskeyCeremonyCommand("ceremony-id", "credential-json"), """{"ceremonyId":"ceremony-id","credential":"credential-json"}"""),
        (new SignInCode("sign-in-code"), """{"code":"sign-in-code"}"""),
        (new RedeemSignInCodeCommand("sign-in-code", "code-verifier", true), """{"code":"sign-in-code","codeVerifier":"code-verifier","isPersistent":true}"""),
        (new StartDeviceLinkCommand("code-challenge"), """{"codeChallenge":"code-challenge"}"""),
        (new CheckDeviceLinkCommand("link-id", "poll-secret"), """{"id":"link-id","pollSecret":"poll-secret"}"""),
        (new DeviceLinkStatus("sign-in-code"), """{"signInCode":"sign-in-code"}"""),
        (new DeviceLinkStatus(null), """{"signInCode":null}"""),
        (new ApproveDeviceLinkCommand("K7PX-M3QR"), """{"code":"K7PX-M3QR"}"""),
        (PasskeyAttachment.Platform, "\"Platform\""),
        (PasskeyAttachment.CrossPlatform, "\"CrossPlatform\""),
        (PasskeyAttachment.Unknown, "\"Unknown\""),
        (new CreateUploadCommand("video.mp4", 12_345_678, "video/mp4", 8_388_608, JsonDocument.Parse("""{"folderId":5}""").RootElement, "upload-key"),
            """{"fileName":"video.mp4","length":12345678,"contentType":"video/mp4","chunkSize":8388608,"command":{"folderId":5},""" +
            "\"idempotencyKey\":\"upload-key\"}"),
        (new CreateUploadCommand("notes.txt", 0),
            """{"fileName":"notes.txt","length":0,"contentType":null,"chunkSize":null,"command":null,"idempotencyKey":null}"""),
        (new UploadSession("upload-id", 5_242_880, SampleUtc), """{"id":"upload-id","chunkSize":5242880,"expiresUtc":"2026-10-22T12:30:00Z"}"""),
        (new UploadStatus(UploadState.Receiving, 12_345_678, 5_242_880, [new ChunkRange(0, 1), new ChunkRange(2, 2)], SampleUtc),
            """{"state":"Receiving","length":12345678,"chunkSize":5242880,"missingChunks":[{"first":0,"last":1},{"first":2,"last":2}],""" +
            "\"expiresUtc\":\"2026-10-22T12:30:00Z\"}"),
        (new UploadStatus(UploadState.Completed, 0, 5_242_880, [], SampleUtc),
            """{"state":"Completed","length":0,"chunkSize":5242880,"missingChunks":[],"expiresUtc":"2026-10-22T12:30:00Z"}"""),
        (new ChunkRange(3, 7), """{"first":3,"last":7}"""),
        (UploadState.Receiving, "\"Receiving\""),
        (UploadState.Processing, "\"Processing\""),
        (UploadState.Completed, "\"Completed\""),
    ];

    private static IEnumerable<object> Samples => GoldenSamples.Select(s => s.Sample);

    private static string DataContractsNamespace => typeof(PasskeyCeremony).Namespace!;

    [TestMethod]
    public void EveryPublicType_HasSamples()
    {
        var types = typeof(PasskeyCeremony).Assembly.GetExportedTypes().Where(t => t.Namespace == DataContractsNamespace).Select(t => t.FullName);
        var sampledTypes = Samples.Select(s => s.GetType()).Distinct().Select(t => t.FullName);

        types.OrderBy(n => n, StringComparer.Ordinal).ShouldBe(sampledTypes.OrderBy(n => n, StringComparer.Ordinal));
    }

    [TestMethod]
    public void Samples_RoundTrip_SourceGenerated()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = DataContractsJsonContext.Default };
        RoundTripSamples(options);
    }

    [TestMethod]
    public void Samples_RoundTrip_SourceGenerated_ApiNullHandling()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = DataContractsJsonContext.Default }.ConfigureApiNullHandling();
        RoundTripSamples(options);
    }

    [TestMethod]
    public void Samples_RoundTrip_Reflection()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        RoundTripSamples(options);
    }

    [TestMethod]
    public void Samples_RoundTrip_Reflection_ApiNullHandling()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() }.ConfigureApiNullHandling();
        RoundTripSamples(options);
    }

    [TestMethod]
    public void Objects_HaveAPropertyForEachConstructorParameter()
    {
        foreach (object sample in Samples.Where(s => !s.GetType().IsEnum))
        {
            var type = sample.GetType();
            var constructor = type.GetConstructors().ShouldHaveSingleItem();
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            constructor.GetParameters().Select(p => p.Name!.ToUpperInvariant()).OrderBy(n => n, StringComparer.Ordinal)
                .ShouldBe(properties.Select(p => p.Name.ToUpperInvariant()).OrderBy(n => n, StringComparer.Ordinal), type.Name);
        }
    }

    [TestMethod]
    public void BeginPasskeySignInCommand_WithoutUserName_IsRead()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = DataContractsJsonContext.Default }.ConfigureApiNullHandling();

        var command = JsonSerializer.Deserialize<BeginPasskeySignInCommand>("""{"codeChallenge":"code-challenge"}""", options).ShouldNotBeNull();

        command.CodeChallenge.ShouldBe("code-challenge");
        command.UserName.ShouldBeNull();
    }

    [TestMethod]
    public void Enums_AreWrittenAsNames()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = DataContractsJsonContext.Default };

        JsonSerializer.Serialize(PasskeyAttachment.CrossPlatform, options).ShouldBe("\"CrossPlatform\"");
        JsonSerializer.Deserialize<PasskeyAttachment>("\"Platform\"", options).ShouldBe(PasskeyAttachment.Platform);
    }

    private static void RoundTripSamples(JsonSerializerOptions options)
    {
        foreach (var (sample, goldenJson) in GoldenSamples)
        {
            var type = sample.GetType();
            var typeInfo = options.TypeInfoResolver!.GetTypeInfo(type, options).ShouldNotBeNull($"{type.Name} is missing from the serializer context.");

            string json = JsonSerializer.Serialize(sample, typeInfo);
            json.ShouldBe(goldenJson, $"The JSON of {type.Name} changed.");

            object? result = JsonSerializer.Deserialize(json, typeInfo);

            if (type.IsEnum)
            {
                result.ShouldBe(sample);
                continue;
            }

            result.ShouldNotBeNull().ShouldBeOfType(type);

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                object? actualValue = property.GetValue(result);
                object? sampleValue = property.GetValue(sample);
                string message = $"{type.Name}.{property.Name} in {json}";

                // JSON elements and lists have no value equality.
                if (sampleValue is JsonElement element)
                    actualValue.ShouldBeOfType<JsonElement>(message).GetRawText().ShouldBe(element.GetRawText(), message);
                else if (sampleValue is System.Collections.IEnumerable items and not string)
                    actualValue.ShouldBeAssignableTo<System.Collections.IEnumerable>(message)!.Cast<object>().ShouldBe(items.Cast<object>(), message);
                else
                    actualValue.ShouldBe(sampleValue, message);
            }

            // Every property is part of the JSON (a null one at most left out).
            var jsonObject = JsonNode.Parse(json).ShouldBeOfType<JsonObject>();
            var written = jsonObject.Select(p => p.Key.ToUpperInvariant()).ToList();
            var expected = type.GetProperties().Where(p => p.GetValue(sample) is not null).Select(p => p.Name.ToUpperInvariant());

            written.ShouldBeSubsetOf(type.GetProperties().Select(p => p.Name.ToUpperInvariant()), json);
            expected.ShouldBeSubsetOf(written, json);
        }
    }

    [JsonSerializable(typeof(PasskeyCeremony))]
    [JsonSerializable(typeof(DeviceLinkStart))]
    [JsonSerializable(typeof(PendingDeviceLink))]
    [JsonSerializable(typeof(PasskeyAttachment))]
    [JsonSerializable(typeof(BeginPasskeySignInCommand))]
    [JsonSerializable(typeof(FinishPasskeySignInCommand))]
    [JsonSerializable(typeof(FinishPasskeyCeremonyCommand))]
    [JsonSerializable(typeof(SignInCode))]
    [JsonSerializable(typeof(RedeemSignInCodeCommand))]
    [JsonSerializable(typeof(StartDeviceLinkCommand))]
    [JsonSerializable(typeof(CheckDeviceLinkCommand))]
    [JsonSerializable(typeof(DeviceLinkStatus))]
    [JsonSerializable(typeof(ApproveDeviceLinkCommand))]
    [JsonSerializable(typeof(CreateUploadCommand))]
    [JsonSerializable(typeof(UploadSession))]
    [JsonSerializable(typeof(UploadStatus))]
    [JsonSerializable(typeof(ChunkRange))]
    [JsonSerializable(typeof(UploadState))]
    private sealed partial class DataContractsJsonContext : JsonSerializerContext;
}
