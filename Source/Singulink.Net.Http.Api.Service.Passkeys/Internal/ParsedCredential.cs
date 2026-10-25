using Singulink.Net.Http.Api.DataContracts;

namespace Singulink.Net.Http.Api.Service.Passkeys.Internal;

/// <summary>
/// A credential read from its WebAuthn JSON, with the values that Fido2NetLib does not read itself.
/// </summary>
/// <typeparam name="T">The Fido2NetLib response type.</typeparam>
internal sealed record ParsedCredential<T>(T Response, PasskeyAttachment Attachment, string? Origin, List<string> Transports);
