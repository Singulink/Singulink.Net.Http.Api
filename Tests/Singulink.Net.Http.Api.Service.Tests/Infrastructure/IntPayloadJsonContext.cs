using System.Text.Json.Serialization;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Type info for hub payloads that are integers only, without type info for strings.
/// </summary>
[JsonSerializable(typeof(int))]
internal sealed partial class IntPayloadJsonContext : JsonSerializerContext;
