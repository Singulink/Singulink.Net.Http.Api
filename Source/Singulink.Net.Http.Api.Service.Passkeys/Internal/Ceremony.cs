using Fido2NetLib;

namespace Singulink.Net.Http.Api.Service.Passkeys.Internal;

/// <summary>
/// A ceremony taken from the store to be finished, with its options read back into Fido2NetLib's types.
/// </summary>
/// <param name="State">The stored state.</param>
/// <param name="AssertionOptions">The options of a sign-in or verification.</param>
/// <param name="CreationOptions">The options of a registration.</param>
internal sealed record Ceremony(CeremonyState State, AssertionOptions? AssertionOptions, CredentialCreateOptions? CreationOptions);
