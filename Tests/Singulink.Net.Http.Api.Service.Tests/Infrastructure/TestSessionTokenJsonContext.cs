using System.Text.Json.Serialization;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Source-generated metadata for <see cref="TestSessionToken"/> with default settings.
/// </summary>
[JsonSerializable(typeof(TestSessionToken))]
internal sealed partial class TestSessionTokenJsonContext : JsonSerializerContext;

/// <summary>
/// Source-generated metadata for <see cref="TestSessionToken"/> with camel case property names, which differ from the reflection-based registration's.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TestSessionToken))]
internal sealed partial class CamelCaseTestSessionTokenJsonContext : JsonSerializerContext;
