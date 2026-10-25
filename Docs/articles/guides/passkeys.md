<div class="article">

# Passkeys

This guide covers passkey (WebAuthn) sign-in with the `Singulink.Net.Http.Api.Service.Passkeys` package: setting it up, storing passkeys, writing the ceremony endpoints, and the hand-off page that native apps use to run passkeys in the system browser.

### How passkeys work here

Every passkey operation is a **ceremony** of two requests. The begin request returns WebAuthn options as JSON. The client passes them to the browser, which asks the user to unlock a passkey and returns a credential as JSON. The finish request sends the credential back, and the service verifies it.

The package runs the ceremonies (verification is done by [Fido2NetLib](https://github.com/passwordless-lib/fido2-net-lib)) and keeps their state between the two requests in the short-lived store, so the finish request can reach any server. Your app keeps everything else: the endpoints and their data contracts, the passkey table, the wording users see and the rules about who may do what.

Clients run WebAuthn in one of two ways:

- **Browser apps** (including WebAssembly apps) call WebAuthn in their own page, using the shared `passkeys.js` module.
- **Native apps** open the **hand-off page** in the system browser. The page runs WebAuthn and returns the credential to the app, which posts it to your API itself.

## Setting Up

The package's types are in the `Singulink.Net.Http.Api.Service.Passkeys` namespace, and its registration and mapping methods (`AddPasskeys` and `MapPasskeyPage`) in `Singulink.Net.Http.Api.Service`, like the toolkit's other registration methods. Sign-in codes, which a passkey sign-in continues with, are in `Singulink.Net.Http.Api.Service.SignIn`.

Register <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyService> with your passkey store (see [Storing Passkeys](#storing-passkeys)) and the relying party settings:

```csharp
services.AddPasskeys<PasskeyStore>(options => {
    options.RelyingPartyId = "example.com";
    options.RelyingPartyName = "Example";
    options.AllowedOrigins.Add("https://app.example.com");
    options.AllowedOrigins.Add("https://api.example.com");
});
```

The options can also be bound from configuration:

```csharp
services.AddPasskeys<PasskeyStore>(options => builder.Configuration.GetSection("Passkeys").Bind(options));
```

The options are validated when the app starts. <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyOptions> has these settings:

| Option | Default | Purpose |
| --- | --- | --- |
| <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyOptions.RelyingPartyId> | required | The domain passkeys are bound to, such as `example.com`. |
| <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyOptions.RelyingPartyName> | required | The name browsers may show when a passkey is created. |
| <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyOptions.AllowedOrigins> | none | The exact origins of the pages that run ceremonies. |
| <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyOptions.AllowLocalhostOrigins> | `false` | Accepts pages on `http(s)://localhost` with any port. |
| <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyOptions.CeremonyTimeout> | 3 minutes | How long the browser waits for the user. |
| <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyOptions.CeremonyLifetime> | 5 minutes | How long a started ceremony can be finished. |
| <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyOptions.RequireCodeChallenge> | `true` | Requires every sign-in to be bound to a PKCE code challenge when it begins. |
| <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyOptions.RequireUserVerification> | `true` | Requires a biometric, PIN or screen lock in every ceremony. |
| <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyOptions.RequireDiscoverableCredentials> | `true` | Requires passkeys that can sign in without a user name. |

<xref:Singulink.Net.Http.Api.Service.PasskeyServiceCollectionExtensions.AddPasskeys*> also registers <xref:Singulink.Net.Http.Api.Service.SignIn.SignInCodes>, which a sign-in continues with, and the short-lived store that keeps ceremonies, as <xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddSignInCodes*> does. Call `AddSignInCodes` as well, before or after, to configure the sign-in codes (see [Sign-In Codes](sign-in-codes.md#options)):

```csharp
services.AddSignInCodes(options => options.Lifetime = TimeSpan.FromMinutes(5));
```

#### Relying party ID and origins

Passkeys belong to the relying party ID, so it must never change once users have passkeys. A ceremony can only run on a page whose host is the relying party ID or a subdomain of it: with `example.com`, pages on `example.com`, `app.example.com` and `api.example.com` all work.

<xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyOptions.AllowedOrigins?displayProperty=nameWithType> are the exact origins of those pages: your web app, and the API host if it serves the hand-off page. Android apps that use the platform passkey APIs report an `android:apk-key-hash:...` origin, which can be added as is. With the relying party ID `localhost`, <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyOptions.AllowLocalhostOrigins> accepts pages on `localhost` with any port, such as the changing ports of a development server, without listing them.

The options are checked when the app starts: an allowed origin whose host is neither the relying party ID nor a subdomain of it fails startup, and <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyOptions.AllowLocalhostOrigins> with a relying party ID other than `localhost` is logged as a warning, since browsers would not run ceremonies on those pages.

These origins are a different list from the allowed origins of session handling and CORS (<xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddAllowedOrigins*>):

| List | Entries | What it covers |
| --- | --- | --- |
| <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyOptions.AllowedOrigins?displayProperty=nameWithType> | Exact origins, such as `https://app.example.com` | The pages that run ceremonies, as browsers report them in credentials. |
| <xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddAllowedOrigins*> | Host patterns, such as `*.example.com` | The pages that can make requests with the session cookie: CORS, and the origin checks of session handling. |

## Storing Passkeys

The passkey table is yours. The library exchanges a passkey's values with it through <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyRecord>, so your entity does not need to implement anything. The entity below keeps the record's values together with values of the app's own, such as a name the user knows the passkey by and when it was created and last used:

```csharp
[Index(nameof(CredentialId), IsUnique = true)]
public class Passkey
{
    public int Id { get; set; }
    public int UserId { get; set; }

    [MaxLength(PasskeyRecord.MaxCredentialIdLength)]
    public byte[] CredentialId { get; set; } = [];
    public byte[] PublicKey { get; set; } = [];
    public long SignCount { get; set; }
    public List<string> Transports { get; set; } = [];
    public bool IsBackupEligible { get; set; }
    public bool IsBackedUp { get; set; }
    public Guid AaGuid { get; set; }

    public string Name { get; set; } = "";
    public string Device { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastUsedUtc { get; set; }

    public PasskeyRecord ToRecord() => new() {
        UserId = UserId.ToString(CultureInfo.InvariantCulture),
        CredentialId = CredentialId,
        PublicKey = PublicKey,
        SignCount = (uint)SignCount,
        Transports = Transports,
        IsBackupEligible = IsBackupEligible,
        IsBackedUp = IsBackedUp,
        AaGuid = AaGuid,
    };
}
```

The credential ID identifies a passkey across all users, which the unique index above enforces. The library looks passkeys up by credential ID, lists a user's passkeys, and saves what a passkey reports each time it is used, through an <xref:Singulink.Net.Http.Api.Service.Passkeys.IPasskeyStore> you implement. The store is resolved per request, so it can use your scoped database context:

```csharp
public sealed class PasskeyStore(AppDbContext db) : IPasskeyStore
{
    public async Task<PasskeyRecord?> FindAsync(byte[] credentialId, CancellationToken cancellationToken)
    {
        var passkey = await db.Passkeys.AsNoTracking().FirstOrDefaultAsync(p => p.CredentialId == credentialId, cancellationToken);
        return passkey?.ToRecord();
    }

    public async Task<IReadOnlyList<PasskeyRecord>> GetUserPasskeysAsync(string userId, CancellationToken cancellationToken)
    {
        int id = int.Parse(userId, CultureInfo.InvariantCulture);
        var passkeys = await db.Passkeys.AsNoTracking().Where(p => p.UserId == id).ToListAsync(cancellationToken);

        return passkeys.ConvertAll(p => p.ToRecord());
    }

    public Task UpdateAsync(PasskeyRecord passkey, DateTime usedUtc, CancellationToken cancellationToken)
    {
        return db.Passkeys
            .Where(p => p.CredentialId == passkey.CredentialId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.SignCount, passkey.SignCount)
                .SetProperty(p => p.IsBackedUp, passkey.IsBackedUp)
                .SetProperty(p => p.LastUsedUtc, usedUtc), cancellationToken);
    }
}
```

The library never adds or removes passkeys: registration returns the new passkey's values for you to store, and listing, renaming and removing passkeys are your own endpoints. <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyNames.GetProviderName*?displayProperty=nameWithType> names the provider a passkey is stored in (such as "iCloud Keychain" or "Google Password Manager") for passkey lists.

#### User IDs

User IDs are strings, so any key type works. The user ID becomes the passkey's WebAuthn user handle: authenticators store it with the passkey and return it when signing in. It must not contain personal information such as an email address or user name (the WebAuthn specification forbids it), must never change or be reused for another user, and must be 1 to 64 bytes in UTF-8 (<xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyUser.MaxIdLength>).

IDs are compared exactly, as strings and as UTF-8 bytes, so a non-string ID formatted another way, such as with another culture or another format than its passkeys were created with, is another user ID. The samples use `id.ToString(CultureInfo.InvariantCulture)`.

## Ceremony Endpoints

The endpoints are yours. Each begin endpoint returns the started ceremony, a <xref:Singulink.Net.Http.Api.DataContracts.PasskeyCeremony> with its <xref:Singulink.Net.Http.Api.DataContracts.PasskeyCeremony.Id>, its <xref:Singulink.Net.Http.Api.DataContracts.PasskeyCeremony.OptionsJson> and how long it can be finished (<xref:Singulink.Net.Http.Api.DataContracts.PasskeyCeremony.Lifetime>, which is <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyOptions.CeremonyLifetime>). It is a data contract in the shared `Singulink.Net.Http.Api` package that clients read with the same type, like the contracts of a sign-in: <xref:Singulink.Net.Http.Api.DataContracts.BeginPasskeySignInCommand>, <xref:Singulink.Net.Http.Api.DataContracts.FinishPasskeySignInCommand> and the <xref:Singulink.Net.Http.Api.DataContracts.SignInCode> that a sign-in continues with, and <xref:Singulink.Net.Http.Api.DataContracts.FinishPasskeyCeremonyCommand>, which finishes a registration or verification (see [Data contracts](sign-in-codes.md#data-contracts)). Each finish endpoint takes the ceremony ID and the credential JSON. The setup sketches below use contracts of the app's own like these, which carry the app's setup ticket too:

```csharp
public record BeginSetupPasskeyCommand(string SetupTicket, string CodeChallenge);
public record FinishSetupPasskeyCommand(string SetupTicket, string CeremonyId, string Credential);
```

### Signing in

A sign-in takes three requests: the client begins it with a PKCE code challenge, finishes it with the credential and the matching code verifier, and then exchanges the one-time sign-in code it receives for a session, with the same verifier. In between, the client can ask the user whether this is their own device and should stay signed in.

Without a user, the browser offers every passkey for the relying party it can find, so users sign in without typing anything. With a user, the sign-in is limited to their passkeys, and its options list their passkeys' credential IDs. A sign-in begun for a user therefore tells whoever begins it whether the user has passkeys, and with a user found by email address, whether the address has an account, while one begun without a user tells nothing about any account. Finishing verifies the credential, saves what the passkey reported, and returns the passkey, its user and the code challenge the sign-in began with, which the sign-in code is bound to:

```csharp
app.MapPost("/sessions/passkey/options", async (BeginPasskeySignInCommand command, PasskeyService passkeys, AppDbContext db) => {
    string? userId = command.UserName is null ? null : await FindUserIdAsync(db, command.UserName);
    return await passkeys.BeginSignInAsync(command.CodeChallenge, userId);
});

app.MapPost("/sessions/passkey", async (FinishPasskeySignInCommand command, PasskeyService passkeys, SignInCodes signInCodes) => {
    var verified = await passkeys.FinishSignInAsync(command.CeremonyId, command.Credential, command.CodeVerifier);
    return new SignInCode(await signInCodes.CreateAsync(verified));
});
```

<xref:Singulink.Net.Http.Api.Service.SignIn.PasskeySignInCodesExtensions.CreateAsync*> issues the code for the passkey's user, bound to the challenge the sign-in began with (<xref:Singulink.Net.Http.Api.Service.Passkeys.VerifiedPasskey.CodeChallenge>).

The client redeems the code with the same verifier, through the redeem endpoint described in [Sign-In Codes](sign-in-codes.md#sign-in-codes):

```csharp
var pkce = Pkce.Create();
var ceremony = await apiClient.BeginPasskeySignInAsync(new(pkce.Challenge));

var credential = await passkeys.GetCredentialAsync(ceremony.OptionsJson, cancellationToken);
var result = await apiClient.FinishPasskeySignInAsync(new(ceremony.Id, credential.Json, pkce.Verifier));

bool persistent = await AskWhetherToStaySignedInAsync(credential.Attachment);
await apiClient.RedeemSignInCodeAsync(new(result.Code, pkce.Verifier, persistent));
```

Here `passkeys` is the client's <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyAuthenticator> (see [Passkeys in Client Apps](passkeys-client.md)). A service with nothing to ask between the two steps can sign the user in with <xref:Singulink.Net.Http.Api.Service.SessionContext`1.SignInAsync*?displayProperty=nameWithType> in the finish endpoint instead.

<xref:Singulink.Net.Http.Api.Service.Passkeys.VerifiedPasskey.Attachment> on the service, and <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyCredential.Attachment?displayProperty=nameWithType> on the client, tell whether the passkey was on the device itself or on another one (a phone used through a QR code, or a security key). It is reported by the browser, so it is not proof of where the passkey is.

### Adding a passkey

Registration needs the signed-in user (or another proof of who the passkey is for, such as a setup link). Finishing checks that the ceremony was started for the same user and returns the new passkey's values, plus a suggested name such as "iCloud Keychain (iOS)":

```csharp
app.MapPost("/passkeys/options", async ([SessionAccess(SessionAccessOptions.ForceValidate)] SessionToken token, PasskeyService passkeys, AppDbContext db) => {
    var user = await db.Users.SingleAsync(u => u.Id == token.UserId);
    return await passkeys.BeginRegistrationAsync(new PasskeyUser(token, user.Email, user.FullName));
});

app.MapPost("/passkeys", async (
    FinishPasskeyCeremonyCommand command, SessionContext<SessionToken> sessionContext, PasskeyService passkeys, AppDbContext db) => {
    var token = await sessionContext.GetRequiredTokenAsync(SessionAccessOptions.ForceValidate);
    var result = await passkeys.FinishRegistrationAsync(token, command.CeremonyId, command.Credential, sessionContext.Device);

    db.Passkeys.Add(new Passkey {
        UserId = token.UserId,
        CredentialId = result.Passkey.CredentialId,
        PublicKey = result.Passkey.PublicKey,
        SignCount = result.Passkey.SignCount,
        Transports = result.Passkey.Transports.ToList(),
        IsBackupEligible = result.Passkey.IsBackupEligible,
        IsBackedUp = result.Passkey.IsBackedUp,
        AaGuid = result.Passkey.AaGuid,
        Name = result.SuggestedName,
        Device = sessionContext.Device,
        CreatedUtc = DateTime.UtcNow,
    });

    await db.SaveChangesAsync();
});
```

The methods that take a session token, such as <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyService.FinishRegistrationAsync*> and the <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyUser> constructor, use its <xref:Singulink.Net.Http.Api.Service.ISessionToken.UserId> as the user ID, and have overloads that take the user ID itself.

The options list the user's passkeys as credentials to exclude, which asks the authenticator not to create a second passkey for the same user, so a browser reports `InvalidStateError` when the device already has one. With `excludeExistingPasskeys: false`, <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyService.BeginRegistrationAsync*> leaves them out, and a device that has one of the user's passkeys creates a new one. Authenticators keep one discoverable passkey per user and relying party, so the new passkey replaces the one that the device, or its sync provider such as iCloud Keychain, held for the user, and the stored record of the replaced passkey no longer has a passkey behind it. A passkey that is already registered is rejected when the ceremony finishes.

#### Signing in after adding a passkey

A registration can continue with a one-time sign-in code, as a sign-in does, for example when a new account's first passkey is created through a setup link and the user is not signed in yet. The client creates a <xref:Singulink.Net.Http.Api.Client.Pkce> and sends its challenge with the begin request. <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyService.BeginRegistrationAsync*> binds the challenge to the ceremony, the registered passkey carries it as <xref:Singulink.Net.Http.Api.Service.Passkeys.RegisteredPasskey.CodeChallenge>, and once you have stored the passkey, <xref:Singulink.Net.Http.Api.Service.SignIn.PasskeySignInCodesExtensions.CreateAsync*> issues a sign-in code bound to it:

```csharp
app.MapPost("/setup/passkey/options", async (BeginSetupPasskeyCommand command, PasskeyService passkeys, SetupTickets tickets, AppDbContext db) => {
    // Reads the ticket without using it up, and throws if it is not valid.
    var user = await GetSetupUserAsync(db, tickets, command.SetupTicket);
    var passkeyUser = new PasskeyUser(user.Id.ToString(CultureInfo.InvariantCulture), user.Email, user.FullName);

    return await passkeys.BeginRegistrationAsync(passkeyUser, command.CodeChallenge);
});

app.MapPost("/setup/passkey", async (
    FinishSetupPasskeyCommand command, PasskeyService passkeys, SignInCodes signInCodes, SetupTickets tickets, AppDbContext db) => {
    var user = await GetSetupUserAsync(db, tickets, command.SetupTicket);
    var result = await passkeys.FinishRegistrationAsync(user.Id.ToString(CultureInfo.InvariantCulture), command.CeremonyId, command.Credential);

    await StorePasskeyAsync(db, user, result);   // as in the endpoint above
    await tickets.TakeAsync(command.SetupTicket);

    return new SignInCode(await signInCodes.CreateAsync(result));
});
```

The setup ticket is a state of the app's own in the short-lived store, such as the one in [The Short-Lived Store](sign-in-codes.md#the-short-lived-store). The finish endpoint takes it once the passkey is stored, so the ticket cannot add another passkey, while a registration that fails leaves it to try again. The client redeems the code with its verifier through the redeem endpoint described in [Sign-In Codes](sign-in-codes.md#sign-in-codes), so only the client that began the registration can sign in with it. <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyService.BeginRegistrationAsync*> throws a <xref:Singulink.Net.Http.Api.BadRequestApiException> (`code-challenge-invalid`) for a malformed challenge, and <xref:Singulink.Net.Http.Api.Service.SignIn.PasskeySignInCodesExtensions.CreateAsync*> throws an <xref:System.InvalidOperationException> for a registration that began without one.

### Confirming it is the user

A verification confirms that the signed-in user holds one of their passkeys. It begins like a sign-in, and finishes with the request of the action it confirms:

```csharp
var ceremony = await passkeys.BeginVerificationAsync(token);
// ...then, in the endpoint for the action:
await passkeys.FinishVerificationAsync(token, command.CeremonyId, command.Credential);
```

### Errors

Ceremony failures are thrown as <xref:Singulink.Net.Http.Api.ApiException> types, so the client receives them as typed exceptions. Each carries an error code from <xref:Singulink.Net.Http.Api.PasskeyErrorCodes>, or from <xref:Singulink.Net.Http.Api.SignInErrorCodes> for a code challenge, which lets you and your clients word them in your own way:

| Error code | Exception | Meaning |
| --- | --- | --- |
| `code-challenge-invalid` | Bad request | A sign-in began without a valid code challenge, or a registration with a malformed one (see [PKCE](#bind-sign-ins-to-the-app-that-started-them)). |
| `passkey-ceremony-expired` | Unauthorized or Validation | The ceremony is unknown, expired, already finished or for someone else. |
| `passkey-code-verifier-mismatch` | Unauthorized | The code verifier is missing or wrong (see [PKCE](#bind-sign-ins-to-the-app-that-started-them)). |
| `passkey-none` | Unauthorized or Conflict | The user has no passkeys. |
| `passkey-not-registered` | Unauthorized or Validation | The passkey is not registered. |
| `passkey-wrong-account` | Unauthorized or Validation | The passkey is registered to a different user than the one the sign-in or verification is for. |
| `passkey-verification-failed` | Unauthorized or Validation | The credential failed verification. |
| `passkey-already-registered` | Conflict | The new passkey is already registered. |

The status depends on the ceremony. A sign-in's passkey authenticates the request, so a sign-in that fails is refused with a <xref:Singulink.Net.Http.Api.UnauthorizedApiException> (`401`). Verifications and registrations run in a signed-in session, which authenticates their requests, so a passkey that fails there is reported with a <xref:Singulink.Net.Http.Api.ValidationApiException> (`422`) and the same error code, so that it is not taken for a session that ended (see [Catching Errors on the Client](exception-handling.md#catching-errors-on-the-client)). A verification for a user without passkeys and a passkey that is already registered conflict with the current state, and are reported with a <xref:Singulink.Net.Http.Api.ConflictApiException> (`409`).

Fido2NetLib's description of why a credential failed verification is logged as a warning, and is not sent to the client.

Malformed requests, such as a credential that is not valid JSON, throw <xref:Singulink.Net.Http.Api.BadRequestApiException>.

## The Hand-off Page

Native apps cannot all run passkeys themselves, so they open the hand-off page in the system browser (through the platform's web authentication broker) with the options of a ceremony. The page runs the ceremony and navigates back to the app's callback address with the credential. It never calls an API: the app gets the options from your API and posts the credential back to it, with its own session where it has one. The same page therefore works for any app.

Map it with <xref:Singulink.Net.Http.Api.Service.PasskeyPageEndpointRouteBuilderExtensions.MapPasskeyPage*>, listing the custom schemes of your apps' callback addresses:

```csharp
app.MapPasskeyPage("/passkey", options => options.RedirectSchemes.Add("myapp"));
```

The page must be served from an origin that is in <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyOptions.AllowedOrigins?displayProperty=nameWithType> and whose host is the relying party ID or a subdomain of it, for example an API at `https://api.example.com` for the relying party ID `example.com`.

<xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyPageOptions> has these settings:

- <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyPageOptions.RedirectSchemes> are the custom schemes callback addresses may use, such as `myapp` for `myapp://passkey`. Schemes that browsers handle themselves, such as `https` and `javascript`, are not allowed.
- <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyPageOptions.AllowLoopbackRedirects> allows `http://127.0.0.1:{port}/...` and `http://[::1]:{port}/...` callback addresses on any port, which desktop apps listen on. It is on by default.
- <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyPageOptions.AppName> is the name shown on the page. It defaults to the relying party name.

The page is sent with a strict content security policy (it loads only its own script and stylesheet and connects nowhere) and is never cached. It is excluded from API descriptions.

### Protocol

The app opens the page with the ceremony's options:

```
GET /passkey?op={get|create}&options={options}&redirect={callback}&state={state}
```

- `op` is `get` for a sign-in or verification and `create` for a registration.
- `options` is the ceremony's <xref:Singulink.Net.Http.Api.DataContracts.PasskeyCeremony.OptionsJson> in UTF-8, base64url encoded without padding.
- `redirect` is the app's callback address. Unless it is allowed, the page shows an error and never navigates away.
- `state` is an optional value of at most 256 characters, returned unchanged.

The page shows a Continue button (some browsers only run WebAuthn after a tap) and a Cancel button. It then navigates to the callback address with these query parameters added:

- on success, `state` and `credential`: the credential JSON in UTF-8, base64url encoded without padding;
- on failure or cancel, `state`, `error` and `message`, where `error` is the name of the browser's error: `NotAllowedError`, `AbortError` (also used when the user cancels), `InvalidStateError` (the device already has a passkey for the user), `NotSupportedError`, `SecurityError`, `ConstraintError` or `UnknownError`.

If the user closes the browser's passkey prompt, the page offers to try again instead of returning at once. An error that does not come from WebAuthn, such as options that cannot be read, returns to the app at once as `UnknownError`.

### Bind sign-ins to the app that started them

On phones, another app can register the same custom scheme and receive the callback instead. For registrations and verifications that does no harm, because finishing them needs the app's session, which never passes through the page. A sign-in credential, however, would let the other app sign in. Therefore every sign-in is bound to a PKCE code challenge when it **begins**:

> [!IMPORTANT]
> The app creates a random code verifier, passes its S256 code challenge to <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyService.BeginSignInAsync*>, and sends the verifier to <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyService.FinishSignInAsync*>. Only the app that holds the verifier can finish the sign-in. The challenge must come with the begin request: one that came with the credential could come from whoever intercepted it.

A wrong or missing verifier fails the sign-in and uses up the ceremony. A sign-in code issued for the verified passkey is bound to the same challenge (<xref:Singulink.Net.Http.Api.Service.Passkeys.VerifiedPasskey.CodeChallenge>), so the app redeems it with the same verifier.

The binding stops an app that only receives the callback. It does not stop an app that registers the same scheme and begins a sign-in of its own: that app can open the page with the options of its own ceremony, the user sees your page and their passkey prompt as usual, and the credential returns to that app, which holds the verifier. The page cannot tell which app opened it. [RFC 8252](https://www.rfc-editor.org/rfc/rfc8252#section-8.6) describes this limit of custom schemes for OAuth on native apps.

<xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyOptions.RequireCodeChallenge> is `true` by default, so <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyService.BeginSignInAsync*> rejects a sign-in without a valid challenge with a `400 Bad Request` (`code-challenge-invalid`), and a service cannot forget the binding. A service whose clients all run WebAuthn in their own web page, and never use the hand-off page, can set it to `false` and sign users in directly when the sign-in finishes.

## Browser Apps

Browser apps run WebAuthn in their own page with the shared `passkeys.js` module (an ES module, also served by the hand-off page at `{pattern}/passkeys.js`). It exports:

- `isSupported()`, which tells whether the browser supports passkeys;
- `getCredential(optionsJson)` and `createCredential(optionsJson)`, which take the ceremony's options JSON and resolve to the credential JSON. They reject with an `Error` whose message is `"{error name}: {message}"`, such as `"NotAllowedError: ..."`.

The `Singulink.Net.Http.Api.Client.WinUI` package includes the same module for Uno Platform WebAssembly apps and implements the client side of the hand-off page for native apps. See [Passkeys in Client Apps](passkeys-client.md).

## Testing

The `Singulink.Net.Http.Api.Service.Testing` package has <xref:Singulink.Net.Http.Api.Service.Testing.SoftwareAuthenticator>, which answers ceremonies as a browser with one passkey would: <xref:Singulink.Net.Http.Api.Service.Testing.SoftwareAuthenticator.Create*> takes the options JSON of a registration and returns the credential JSON, and <xref:Singulink.Net.Http.Api.Service.Testing.SoftwareAuthenticator.Get*> does the same for a sign-in or a verification:

```csharp
using var authenticator = new SoftwareAuthenticator("https://app.example.com");

var ceremony = await passkeys.BeginRegistrationAsync(new PasskeyUser("42", "alex@example.com", "Alex Smith"));
var registered = await passkeys.FinishRegistrationAsync("42", ceremony.Id, authenticator.Create(ceremony.OptionsJson));
```

Its properties set what the passkey reports, such as its signature counter, its backup flags and whether the user was verified. <xref:Singulink.Net.Http.Api.Service.Passkeys.VerifiedPasskey> and <xref:Singulink.Net.Http.Api.Service.Passkeys.RegisteredPasskey> have public constructors, so code that takes them can also be called without a ceremony.

## Operating Notes

These points concern deployment:

- **Ceremony state is kept in the short-lived store** between the begin and finish requests, which is in memory unless you register another store. Services that run on several servers register a shared store, so that a ceremony begun on one server can finish on another; see [Running Multiple Servers](multiple-servers.md). With the memory store, a restart only fails ceremonies in progress, which users start again.
- **Begin endpoints** of sign-ins need no session, and each started ceremony is kept in the short-lived store until it expires (see [Rate Limiting](rate-limiting.md)).
- **Signature counters** are checked and saved, which detects some cloned security keys. Most synced passkeys always report 0, which is normal.

## Next Steps

Continue with these related articles:

- [Sign-In Codes and Device Linking](sign-in-codes.md) - Redeeming the sign-in code for a session.
- [Session Handling](session-handling.md) - Signing users in once their passkey is verified.
- [Building API Clients](api-clients.md) - Calling the ceremony endpoints from clients.
- [Exception Handling](exception-handling.md) - How ceremony errors reach clients.

</div>
