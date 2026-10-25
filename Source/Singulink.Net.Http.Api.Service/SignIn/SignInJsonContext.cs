using System.Text.Json.Serialization;

namespace Singulink.Net.Http.Api.Service.SignIn;

/// <summary>
/// Serialization metadata for the sign-in state kept in the <see cref="IShortLivedStore"/>, so that it is serialized without reflection. State with a missing
/// or unexpected null value fails to read (and is treated as missing) rather than being read with nulls.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(SignInCodes.SignInCodeState))]
[JsonSerializable(typeof(DeviceLinks.LinkState))]
[JsonSerializable(typeof(DeviceLinks.CodeState))]
[JsonSerializable(typeof(DeviceLinks.ApprovalState))]
[JsonSerializable(typeof(DeviceLinks.IssuedState))]
internal sealed partial class SignInJsonContext : JsonSerializerContext
{
}
