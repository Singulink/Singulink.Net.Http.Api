using System.Text.Json.Serialization;
using Fido2NetLib;

namespace Singulink.Net.Http.Api.Service.Passkeys.Internal;

/// <summary>
/// Serialization metadata for the Fido2NetLib types that its own serializer context does not cover, so that credentials are read without reflection.
/// </summary>
[JsonSerializable(typeof(AuthenticatorAttestationRawResponse))]
internal sealed partial class PasskeyJsonContext : JsonSerializerContext
{
}
