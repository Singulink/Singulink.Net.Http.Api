namespace Singulink.Net.Http.Api.Service.Passkeys.Internal;

/// <summary>
/// The state of a passkey ceremony between its begin and finish requests, kept in the <see cref="IShortLivedStore"/>.
/// </summary>
/// <param name="Kind">The kind of ceremony.</param>
/// <param name="UserId">The user a verification or registration is for, or the user a sign-in is limited to (if any).</param>
/// <param name="CodeChallenge">The PKCE code challenge a sign-in or registration is bound to, if any.</param>
/// <param name="OptionsJson">The ceremony's options as Fido2NetLib serializes them: assertion options for sign-ins and verifications, or credential
/// creation options for registrations.</param>
internal sealed record CeremonyState(CeremonyKind Kind, string? UserId, string? CodeChallenge, string OptionsJson);
