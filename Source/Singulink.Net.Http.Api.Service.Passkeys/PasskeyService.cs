using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Exceptions;
using Fido2NetLib.Objects;
using Singulink.Net.Http.Api.DataContracts;
using Singulink.Net.Http.Api.Service.Passkeys.Internal;
using Singulink.Net.Http.Api.Service.SignIn;

namespace Singulink.Net.Http.Api.Service.Passkeys;

/// <summary>
/// Runs WebAuthn passkey ceremonies: signing in, verifying the signed-in user and registering new passkeys. Each ceremony is started by a begin method,
/// whose options the client passes to the browser, and finished by a finish method with the credential the browser returned.
/// </summary>
/// <remarks>
/// <para>
/// Passkeys are stored by the app and accessed through its <see cref="IPasskeyStore"/>. Ceremony state is kept in the registered
/// <see cref="IShortLivedStore"/> between the begin and finish requests, so a ceremony can be finished on any server that shares the store.
/// </para>
/// <para>
/// Ceremonies can be finished once. Failures are reported with <see cref="ApiException"/> types that carry one of the <see cref="PasskeyErrorCodes"/>,
/// or <see cref="SignInErrorCodes.CodeChallengeInvalid"/> for a sign-in or registration that begins without a valid code challenge.
/// </para>
/// <para>
/// The passkey of a sign-in authenticates the request, so a sign-in whose passkey check fails is refused with an <see cref="UnauthorizedApiException"/>
/// (401). Verifications and registrations run in a signed-in session that authenticates their requests, so a failed passkey check there is reported with
/// a <see cref="ValidationApiException"/> (422) and the same error codes. A verification of a user without passkeys and a registration of a passkey that
/// is already registered conflict with the current state and are reported with a <see cref="ConflictApiException"/> (409).
/// </para>
/// </remarks>
public sealed class PasskeyService
{
    private const string CeremonyKeyPrefix = "httpapi:passkey-ceremony:";

    private const string VerificationFailedMessage = "The passkey could not be verified.";

    private readonly PasskeyOptions _options;
    private readonly IShortLivedStore _ceremonies;
    private readonly IPasskeyStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    internal PasskeyService(
        PasskeyOptions options, IShortLivedStore ceremonies, IPasskeyStore store, TimeProvider timeProvider, ILogger<PasskeyService> logger)
    {
        _options = options;
        _ceremonies = ceremonies;
        _store = store;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Starts a sign-in.
    /// </summary>
    /// <param name="codeChallenge">The client's PKCE code challenge (S256, base64url) to bind the sign-in to, so that it can only be finished with the
    /// matching code verifier. Required unless <see cref="PasskeyOptions.RequireCodeChallenge"/> is <see langword="false"/>. The verified passkey carries it
    /// as <see cref="VerifiedPasskey.CodeChallenge"/>, so the sign-in can continue with a sign-in code bound to the same challenge.</param>
    /// <param name="userId">The user to sign in, if known (for example from an email address they entered), which limits the sign-in to their passkeys.
    /// If <see langword="null"/>, the browser offers all the passkeys for the relying party it can find (discoverable credentials).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <remarks>
    /// A sign-in begun for a user tells the caller whether the user has passkeys: the options list the credential IDs of the user's passkeys, and a user
    /// without passkeys is refused. With a user found by an identifier such as an email address, it also tells whether an account has that identifier. A
    /// sign-in begun without a user tells nothing about any account.
    /// </remarks>
    /// <exception cref="BadRequestApiException">The code challenge is malformed, or missing while it is required
    /// (<see cref="SignInErrorCodes.CodeChallengeInvalid"/>).</exception>
    /// <exception cref="UnauthorizedApiException">The user has no passkeys (<see cref="PasskeyErrorCodes.NoPasskeys"/>).</exception>
    public async Task<PasskeyCeremony> BeginSignInAsync(string? codeChallenge, string? userId = null, CancellationToken cancellationToken = default)
    {
        if (codeChallenge is not null)
            PkceChallenge.EnsureValid(codeChallenge);
        else if (_options.RequireCodeChallenge)
            throw new BadRequestApiException("A code challenge is required to sign in.") { ErrorCode = SignInErrorCodes.CodeChallengeInvalid };

        IReadOnlyList<PasskeyRecord> passkeys = [];

        if (userId is not null)
        {
            passkeys = await _store.GetUserPasskeysAsync(userId, cancellationToken);

            if (passkeys.Count is 0)
                throw new UnauthorizedApiException("No passkeys are registered for this account.") { ErrorCode = PasskeyErrorCodes.NoPasskeys };
        }

        return await BeginAssertionAsync(CeremonyKind.SignIn, userId, passkeys, codeChallenge, cancellationToken);
    }

    /// <summary>
    /// Finishes a sign-in: verifies the credential, saves the values the passkey reported through <see cref="IPasskeyStore.UpdateAsync"/>, and returns
    /// the passkey and its user. The app then signs the user in, or creates a sign-in code bound to <see cref="VerifiedPasskey.CodeChallenge"/> for the
    /// client to redeem.
    /// </summary>
    /// <param name="ceremonyId">The ID of the ceremony started with <see cref="BeginSignInAsync"/>.</param>
    /// <param name="credentialJson">The credential the browser returned, as WebAuthn JSON (the <c>PublicKeyCredential.toJSON()</c> form).</param>
    /// <param name="codeVerifier">The PKCE code verifier, required if the sign-in was started with a code challenge and not allowed otherwise.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="BadRequestApiException">The credential is malformed, or a code verifier was given for a sign-in that was started without a code
    /// challenge.</exception>
    /// <exception cref="UnauthorizedApiException">The sign-in failed. The error code is <see cref="PasskeyErrorCodes.CeremonyExpired"/>,
    /// <see cref="PasskeyErrorCodes.CodeVerifierMismatch"/>, <see cref="PasskeyErrorCodes.NotRegistered"/>, <see cref="PasskeyErrorCodes.WrongAccount"/>
    /// (for a sign-in started for a user) or <see cref="PasskeyErrorCodes.VerificationFailed"/>.</exception>
    public async Task<VerifiedPasskey> FinishSignInAsync(
        string ceremonyId, string credentialJson, string? codeVerifier = null, CancellationToken cancellationToken = default)
    {
        var ceremony = await TakeCeremonyAsync(ceremonyId, CeremonyKind.SignIn, userId: null, cancellationToken);

        // The ceremony is used up even when the verifier is wrong, so a credential that went to the wrong app cannot be tried again.

        if (ceremony.State.CodeChallenge is { } codeChallenge)
        {
            if (codeVerifier is null || !PkceChallenge.IsMatch(codeChallenge, codeVerifier))
            {
                throw new UnauthorizedApiException("The passkey sign-in could not be confirmed. Please try again.") {
                    ErrorCode = PasskeyErrorCodes.CodeVerifierMismatch,
                };
            }
        }
        else if (codeVerifier is not null)
        {
            // A client that sends a verifier expects the binding, so a server that did not bind the ceremony has a bug worth surfacing.
            throw new BadRequestApiException("The passkey sign-in was not started with a code challenge.");
        }

        return await VerifyAssertionAsync(ceremony, credentialJson, cancellationToken);
    }

    /// <summary>
    /// Starts a verification of the signed-in user with one of their passkeys, to confirm it is them before a sensitive action.
    /// </summary>
    /// <param name="userId">The signed-in user.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ConflictApiException">The user has no passkeys (<see cref="PasskeyErrorCodes.NoPasskeys"/>).</exception>
    public async Task<PasskeyCeremony> BeginVerificationAsync(string userId, CancellationToken cancellationToken = default)
    {
        var passkeys = await _store.GetUserPasskeysAsync(userId, cancellationToken);

        if (passkeys.Count is 0)
            throw new ConflictApiException("There are no passkeys on this account to confirm with.") { ErrorCode = PasskeyErrorCodes.NoPasskeys };

        return await BeginAssertionAsync(CeremonyKind.Verification, userId, passkeys, codeChallenge: null, cancellationToken);
    }

    /// <summary>
    /// Starts a verification of the signed-in user with one of their passkeys, to confirm it is them before a sensitive action.
    /// </summary>
    /// <param name="user">The session token of the signed-in user, whose <see cref="ISessionToken.UserId"/> identifies them.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ConflictApiException">The user has no passkeys (<see cref="PasskeyErrorCodes.NoPasskeys"/>).</exception>
    public Task<PasskeyCeremony> BeginVerificationAsync(ISessionToken user, CancellationToken cancellationToken = default)
    {
        return BeginVerificationAsync(user.UserId, cancellationToken);
    }

    /// <summary>
    /// Finishes a verification of the signed-in user: verifies that the credential is from one of their passkeys and saves the values the passkey reported
    /// through <see cref="IPasskeyStore.UpdateAsync"/>.
    /// </summary>
    /// <param name="userId">The signed-in user, who must be the user the verification was started for.</param>
    /// <param name="ceremonyId">The ID of the ceremony started with <see cref="BeginVerificationAsync(string, CancellationToken)"/>.</param>
    /// <param name="credentialJson">The credential the browser returned, as WebAuthn JSON (the <c>PublicKeyCredential.toJSON()</c> form).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="BadRequestApiException">The credential is malformed.</exception>
    /// <exception cref="ValidationApiException">The verification failed. The error code is <see cref="PasskeyErrorCodes.CeremonyExpired"/>,
    /// <see cref="PasskeyErrorCodes.NotRegistered"/>, <see cref="PasskeyErrorCodes.WrongAccount"/> or
    /// <see cref="PasskeyErrorCodes.VerificationFailed"/>.</exception>
    public async Task<VerifiedPasskey> FinishVerificationAsync(
        string userId, string ceremonyId, string credentialJson, CancellationToken cancellationToken = default)
    {
        var ceremony = await TakeCeremonyAsync(ceremonyId, CeremonyKind.Verification, userId, cancellationToken);
        return await VerifyAssertionAsync(ceremony, credentialJson, cancellationToken);
    }

    /// <summary>
    /// Finishes a verification of the signed-in user: verifies that the credential is from one of their passkeys and saves the values the passkey reported
    /// through <see cref="IPasskeyStore.UpdateAsync"/>.
    /// </summary>
    /// <param name="user">The session token of the signed-in user, who must be the user the verification was started for, and whose
    /// <see cref="ISessionToken.UserId"/> identifies them.</param>
    /// <param name="ceremonyId">The ID of the ceremony started with <see cref="BeginVerificationAsync(ISessionToken, CancellationToken)"/>.</param>
    /// <param name="credentialJson">The credential the browser returned, as WebAuthn JSON (the <c>PublicKeyCredential.toJSON()</c> form).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="BadRequestApiException">The credential is malformed.</exception>
    /// <exception cref="ValidationApiException">The verification failed. The error code is <see cref="PasskeyErrorCodes.CeremonyExpired"/>,
    /// <see cref="PasskeyErrorCodes.NotRegistered"/>, <see cref="PasskeyErrorCodes.WrongAccount"/> or
    /// <see cref="PasskeyErrorCodes.VerificationFailed"/>.</exception>
    public Task<VerifiedPasskey> FinishVerificationAsync(
        ISessionToken user, string ceremonyId, string credentialJson, CancellationToken cancellationToken = default)
    {
        return FinishVerificationAsync(user.UserId, ceremonyId, credentialJson, cancellationToken);
    }

    /// <summary>
    /// Starts creating a passkey for a user.
    /// </summary>
    /// <param name="user">The user to create the passkey for.</param>
    /// <param name="codeChallenge">The client's PKCE code challenge (S256, base64url) to bind the registration to, if any. The registered passkey carries it
    /// as <see cref="RegisteredPasskey.CodeChallenge"/>, so the registration can continue with a sign-in code bound to the same challenge.</param>
    /// <param name="excludeExistingPasskeys">Whether the options list the user's passkeys as credentials to exclude, which asks the authenticator not to
    /// create a passkey if it already has one of them. If <see langword="false"/>, the user's passkeys are not looked up, and an authenticator that has one
    /// of them creates a new one. Authenticators keep one discoverable passkey per user and relying party, so a new discoverable passkey replaces the one
    /// that the authenticator, or its sync provider, held for the user, and the record of the replaced passkey no longer has a passkey behind it. Default is
    /// <see langword="true"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="BadRequestApiException">The code challenge is malformed (<see cref="SignInErrorCodes.CodeChallengeInvalid"/>).</exception>
    public async Task<PasskeyCeremony> BeginRegistrationAsync(
        PasskeyUser user, string? codeChallenge = null, bool excludeExistingPasskeys = true, CancellationToken cancellationToken = default)
    {
        if (codeChallenge is not null)
            PkceChallenge.EnsureValid(codeChallenge);

        var existingPasskeys = excludeExistingPasskeys ? await _store.GetUserPasskeysAsync(user.Id, cancellationToken) : [];

        var creationOptions = CreateFido2(clientOrigin: null).RequestNewCredential(new RequestNewCredentialParams {
            User = new Fido2User { Id = PasskeyUser.GetUserHandle(user.Id), Name = user.Name, DisplayName = user.DisplayName },
            ExcludeCredentials = existingPasskeys.Select(ToDescriptor).ToList(),

            // The authenticator attachment is deliberately left open so that phones and security keys can hold passkeys too.
            AuthenticatorSelection = new AuthenticatorSelection {
                ResidentKey = _options.RequireDiscoverableCredentials ? ResidentKeyRequirement.Required : ResidentKeyRequirement.Preferred,
                UserVerification = GetUserVerification(),
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });

        string optionsJson = creationOptions.ToJson();
        var state = new CeremonyState(CeremonyKind.Registration, user.Id, codeChallenge, optionsJson);
        string id = await AddCeremonyAsync(state, cancellationToken);

        return new PasskeyCeremony(id, optionsJson, _options.CeremonyLifetime);
    }

    /// <summary>
    /// Finishes creating a passkey: verifies the new credential and returns its data, which the app then stores. The passkey is not stored by this
    /// method.
    /// </summary>
    /// <param name="userId">The user the passkey is being created for, who must be the user the registration was started for.</param>
    /// <param name="ceremonyId">The ID of the ceremony started with <see cref="BeginRegistrationAsync"/>.</param>
    /// <param name="credentialJson">The credential the browser returned, as WebAuthn JSON (the <c>PublicKeyCredential.toJSON()</c> form).</param>
    /// <param name="device">The device the passkey is being created from, in the format of <see cref="SessionContext{TSessionToken}.Device"/>, which is
    /// used in the suggested name. Optional.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="BadRequestApiException">The credential is malformed.</exception>
    /// <exception cref="ValidationApiException">The ceremony is unknown, expired or was started for another user
    /// (<see cref="PasskeyErrorCodes.CeremonyExpired"/>), or the credential failed verification (<see cref="PasskeyErrorCodes.VerificationFailed"/>).
    /// </exception>
    /// <exception cref="ConflictApiException">The credential is already registered (<see cref="PasskeyErrorCodes.AlreadyRegistered"/>).</exception>
    public async Task<RegisteredPasskey> FinishRegistrationAsync(
        string userId, string ceremonyId, string credentialJson, string? device = null, CancellationToken cancellationToken = default)
    {
        var ceremony = await TakeCeremonyAsync(ceremonyId, CeremonyKind.Registration, userId, cancellationToken);
        var parsed = CredentialParser.ParseAttestation(credentialJson);
        RegisteredPublicKeyCredential credential;

        try
        {
            credential = await CreateFido2(parsed.Origin).MakeNewCredentialAsync(
                new MakeNewCredentialParams {
                    AttestationResponse = parsed.Response,
                    OriginalOptions = ceremony.CreationOptions!,
                    IsCredentialIdUniqueToUserCallback = async (args, ct) => await _store.FindAsync(args.CredentialId, ct) is null,
                },
                cancellationToken);
        }
        catch (Fido2VerificationException ex) when (ex.Code is Fido2ErrorCode.NonUniqueCredentialId)
        {
            throw new ConflictApiException("This passkey is already registered.") { ErrorCode = PasskeyErrorCodes.AlreadyRegistered };
        }
        catch (Fido2VerificationException ex)
        {
            _logger.LogWarning(ex, "A new passkey failed verification.");
            throw new ValidationApiException(VerificationFailedMessage) { ErrorCode = PasskeyErrorCodes.VerificationFailed };
        }

        var passkey = new PasskeyRecord {
            UserId = userId,
            CredentialId = credential.Id,
            PublicKey = credential.PublicKey,
            SignCount = credential.SignCount,
            Transports = parsed.Transports,
            IsBackupEligible = credential.IsBackupEligible,
            IsBackedUp = credential.IsBackedUp,
            AaGuid = credential.AaGuid,
        };

        string suggestedName = PasskeyNames.GetDefaultName(passkey.AaGuid, passkey.Transports, device);
        return new RegisteredPasskey(passkey, suggestedName, parsed.Attachment, ceremony.State.CodeChallenge);
    }

    /// <summary>
    /// Finishes creating a passkey for the signed-in user: verifies the new credential and returns its data, which the app then stores. The passkey is not
    /// stored by this method.
    /// </summary>
    /// <param name="user">The session token of the signed-in user, who must be the user the registration was started for, and whose
    /// <see cref="ISessionToken.UserId"/> identifies them.</param>
    /// <param name="ceremonyId">The ID of the ceremony started with <see cref="BeginRegistrationAsync"/>.</param>
    /// <param name="credentialJson">The credential the browser returned, as WebAuthn JSON (the <c>PublicKeyCredential.toJSON()</c> form).</param>
    /// <param name="device">The device the passkey is being created from, in the format of <see cref="SessionContext{TSessionToken}.Device"/>, which is
    /// used in the suggested name. Optional.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="BadRequestApiException">The credential is malformed.</exception>
    /// <exception cref="ValidationApiException">The ceremony is unknown, expired or was started for another user
    /// (<see cref="PasskeyErrorCodes.CeremonyExpired"/>), or the credential failed verification (<see cref="PasskeyErrorCodes.VerificationFailed"/>).
    /// </exception>
    /// <exception cref="ConflictApiException">The credential is already registered (<see cref="PasskeyErrorCodes.AlreadyRegistered"/>).</exception>
    public Task<RegisteredPasskey> FinishRegistrationAsync(
        ISessionToken user, string ceremonyId, string credentialJson, string? device = null, CancellationToken cancellationToken = default)
    {
        return FinishRegistrationAsync(user.UserId, ceremonyId, credentialJson, device, cancellationToken);
    }

    private async Task<PasskeyCeremony> BeginAssertionAsync(
        CeremonyKind kind, string? userId, IReadOnlyList<PasskeyRecord> passkeys, string? codeChallenge, CancellationToken cancellationToken)
    {
        var assertionOptions = CreateFido2(clientOrigin: null).GetAssertionOptions(new GetAssertionOptionsParams {
            AllowedCredentials = passkeys.Select(ToDescriptor).ToList(),
            UserVerification = GetUserVerification(),
        });

        string optionsJson = assertionOptions.ToJson();
        string id = await AddCeremonyAsync(new CeremonyState(kind, userId, codeChallenge, optionsJson), cancellationToken);

        return new PasskeyCeremony(id, optionsJson, _options.CeremonyLifetime);
    }

    private async Task<VerifiedPasskey> VerifyAssertionAsync(Ceremony ceremony, string credentialJson, CancellationToken cancellationToken)
    {
        var parsed = CredentialParser.ParseAssertion(credentialJson);

        var passkey = await _store.FindAsync(parsed.Response.RawId, cancellationToken);

        var kind = ceremony.State.Kind;

        if (passkey is null)
            throw CreateFailure(kind, "This passkey is not registered. It may have been removed.", PasskeyErrorCodes.NotRegistered);

        if (ceremony.State.UserId is not null && passkey.UserId != ceremony.State.UserId)
            throw CreateFailure(kind, "This passkey belongs to a different account.", PasskeyErrorCodes.WrongAccount);

        byte[] userHandle = PasskeyUser.GetUserHandle(passkey.UserId);
        VerifyAssertionResult result;

        try
        {
            result = await CreateFido2(parsed.Origin).MakeAssertionAsync(
                new MakeAssertionParams {
                    AssertionResponse = parsed.Response,
                    OriginalOptions = ceremony.AssertionOptions!,
                    StoredPublicKey = passkey.PublicKey,
                    StoredSignatureCounter = passkey.SignCount,

                    // The passkey was found by its credential ID; the user handle the authenticator returns (if any) only has to agree with it.
                    IsUserHandleOwnerOfCredentialIdCallback = (args, _) => Task.FromResult(args.UserHandle.AsSpan().SequenceEqual(userHandle)),
                },
                cancellationToken);
        }
        catch (Fido2VerificationException ex)
        {
            _logger.LogWarning(ex, "A passkey failed verification in a {Ceremony}.", kind is CeremonyKind.SignIn ? "sign-in" : "verification");
            throw CreateFailure(kind, VerificationFailedMessage, PasskeyErrorCodes.VerificationFailed);
        }

        var updated = new PasskeyRecord {
            UserId = passkey.UserId,
            CredentialId = passkey.CredentialId,
            PublicKey = passkey.PublicKey,
            SignCount = result.SignCount,
            Transports = passkey.Transports,
            IsBackupEligible = passkey.IsBackupEligible,
            IsBackedUp = result.IsBackedUp,
            AaGuid = passkey.AaGuid,
        };

        await _store.UpdateAsync(updated, _timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        return new VerifiedPasskey(updated, parsed.Attachment, kind is CeremonyKind.SignIn ? ceremony.State.CodeChallenge : null);
    }

    private ValueTask<string> AddCeremonyAsync(CeremonyState state, CancellationToken cancellationToken)
    {
        return _ceremonies.AddAsync(CeremonyKeyPrefix, state, CeremonyJsonContext.Default.CeremonyState, _options.CeremonyLifetime, cancellationToken);
    }

    private async Task<Ceremony> TakeCeremonyAsync(string ceremonyId, CeremonyKind kind, string? userId, CancellationToken cancellationToken)
    {
        string? json = null;

        // Anything that cannot be a ceremony ID is not looked up.
        if (ShortLivedId.IsWellFormed(ceremonyId))
            json = await _ceremonies.TakeAsync(CeremonyKeyPrefix + ceremonyId, cancellationToken);

        var ceremony = json is null ? null : ReadCeremony(json);

        // Sign-ins are not started for a particular user, so only verifications and registrations are checked to be finished for the user they were
        // started for.
        if (ceremony is null || ceremony.State.Kind != kind || (kind is not CeremonyKind.SignIn && ceremony.State.UserId != userId))
            throw CreateFailure(kind, "The passkey request expired. Please try again.", PasskeyErrorCodes.CeremonyExpired);

        return ceremony;
    }

    /// <summary>
    /// Creates the exception for a failed passkey check: an <see cref="UnauthorizedApiException"/> for a sign-in, whose passkey authenticates the request,
    /// and a <see cref="ValidationApiException"/> for a verification or registration, whose request is authenticated by its session.
    /// </summary>
    private static ApiException CreateFailure(CeremonyKind kind, string message, string errorCode)
    {
        if (kind is CeremonyKind.SignIn)
            return new UnauthorizedApiException(message) { ErrorCode = errorCode };

        return new ValidationApiException(message) { ErrorCode = errorCode };
    }

    /// <summary>
    /// Reads a stored ceremony, or returns <see langword="null"/> if it cannot be read, for example because an earlier version of the service or of
    /// Fido2NetLib stored it during a rolling deployment. The user then sees an expired ceremony and starts again.
    /// </summary>
    private Ceremony? ReadCeremony(string json)
    {
        try
        {
            if (JsonSerializer.Deserialize(json, CeremonyJsonContext.Default.CeremonyState) is not { } state)
                return null;

            if (state.Kind is CeremonyKind.Registration)
                return CredentialCreateOptions.FromJson(state.OptionsJson) is { } creationOptions ? new Ceremony(state, null, creationOptions) : null;

            return AssertionOptions.FromJson(state.OptionsJson) is { } assertionOptions ? new Ceremony(state, assertionOptions, null) : null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "A stored passkey ceremony could not be read, so it was treated as expired.");
            return null;
        }
    }

    private UserVerificationRequirement GetUserVerification()
    {
        return _options.RequireUserVerification ? UserVerificationRequirement.Required : UserVerificationRequirement.Preferred;
    }

    private Fido2 CreateFido2(string? clientOrigin)
    {
        var origins = new HashSet<string>(_options.AllowedOrigins, StringComparer.OrdinalIgnoreCase);

        // Development pages are served from changing localhost ports, so the page's own origin is accepted when it is a localhost one.
        if (_options.AllowLocalhostOrigins && clientOrigin is not null && IsLocalhostOrigin(clientOrigin))
            origins.Add(clientOrigin);

        return new Fido2(
            new Fido2Configuration {
                RPID = _options.RelyingPartyId,
                RPName = _options.RelyingPartyName,
                Origins = origins,
                Timeout = (uint)_options.CeremonyTimeout.TotalMilliseconds,
                ChallengeSize = 32,
            },
            metadataService: null);
    }

    private static bool IsLocalhostOrigin(string origin)
    {
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" &&
            uri.Host is "localhost" &&
            uri.UserInfo.Length is 0 &&
            uri.PathAndQuery is "/";
    }

    private static PublicKeyCredentialDescriptor ToDescriptor(PasskeyRecord passkey)
    {
        return new PublicKeyCredentialDescriptor(PublicKeyCredentialType.PublicKey, passkey.CredentialId, Transports.ToKnown(passkey.Transports));
    }
}
