using System.Text.Json.Serialization;

namespace Singulink.Net.Http.Api.Service.Passkeys.Internal;

/// <summary>
/// Serialization metadata for the ceremony state kept in the <see cref="IShortLivedStore"/>, so that it is serialized without reflection. State with a
/// missing or unexpected null value fails to read (and is treated as expired) rather than being read with nulls.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(CeremonyState))]
internal sealed partial class CeremonyJsonContext : JsonSerializerContext
{
}
