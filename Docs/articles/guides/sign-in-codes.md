<div class="article">

# Sign-In Codes and Device Linking

This guide covers the building blocks for sign-in flows that span several requests or devices: the short-lived store, one-time sign-in codes bound to PKCE, device linking, and short codes that users type. The library provides the logic and the data contracts of the standard steps; your service keeps its own endpoints, its contracts for anything that carries its own data, its wording and its authorization rules.

### Registering the services

Register the parts you use:

```csharp
builder.Services.AddSignInCodes();    // sign-in codes and the short-lived store
builder.Services.AddDeviceLinks();    // device links, plus everything sign-in codes need
```

<xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddShortLivedStore*> registers just the short-lived store. Each method takes an optional callback to configure its options (see [Options](#options)). The registration methods, the short-lived store and its helpers are in the `Singulink.Net.Http.Api.Service` namespace, like the toolkit's other registration methods. Sign-in codes, device links and short codes are in `Singulink.Net.Http.Api.Service.SignIn`, and the data contracts that the endpoints take and return are in `Singulink.Net.Http.Api.DataContracts` (see [Data contracts](#data-contracts)).

### Data contracts

The shared `Singulink.Net.Http.Api` package has the data contracts of the standard steps, which endpoints take and return as they are and clients read and write with the same types:

| Contract | Step |
| --- | --- |
| <xref:Singulink.Net.Http.Api.DataContracts.SignInCode> | A one-time sign-in code that a verifying step returns. |
| <xref:Singulink.Net.Http.Api.DataContracts.RedeemSignInCodeCommand> | Redeeming a sign-in code for a session. |
| <xref:Singulink.Net.Http.Api.DataContracts.BeginPasskeySignInCommand> and <xref:Singulink.Net.Http.Api.DataContracts.FinishPasskeySignInCommand> | Beginning and finishing a passkey sign-in (see [Passkeys](passkeys.md#signing-in)). |
| <xref:Singulink.Net.Http.Api.DataContracts.FinishPasskeyCeremonyCommand> | Finishing a passkey registration or verification (see [Passkeys](passkeys.md#adding-a-passkey)). |
| <xref:Singulink.Net.Http.Api.DataContracts.StartDeviceLinkCommand> and <xref:Singulink.Net.Http.Api.DataContracts.DeviceLinkStart> | Starting a device link. |
| <xref:Singulink.Net.Http.Api.DataContracts.ApproveDeviceLinkCommand> and <xref:Singulink.Net.Http.Api.DataContracts.PendingDeviceLink> | Looking up the details of a device waiting to be linked by its code, and approving it. |
| <xref:Singulink.Net.Http.Api.DataContracts.CheckDeviceLinkCommand> and <xref:Singulink.Net.Http.Api.DataContracts.DeviceLinkStatus> | Checking the status of a device link. |

Like the app's own contracts, they are listed in the app's `JsonSerializerContext` on both sides: a client built with a type info resolver, and a trimmed or Native AOT service, has no reflection fallback and throws a <xref:System.NotSupportedException> for a type that is not listed.

## The Short-Lived Store

<xref:Singulink.Net.Http.Api.Service.IShortLivedStore> keeps short-lived state between requests: the toolkit keeps its sign-in codes, device links, passkey ceremonies and rate limit counts there, and your service can keep the state of its own flows, such as a setup ticket. Values are strings that expire after a lifetime.

<xref:Singulink.Net.Http.Api.Service.ShortLivedState`1> keeps states of one type as JSON under a key prefix of your own, each for the same lifetime, with the `JsonTypeInfo<T>` of a source-generated serializer context so that trimming and AOT keep working. The prefix keeps each kind of state apart, so an ID presented to the wrong step of a flow finds nothing. <xref:Singulink.Net.Http.Api.Service.ShortLivedState`1.AddAsync*> stores a state under a new random ID and returns the ID, for the client to present in the next step. The ID is 256 bits of randomness, so it also serves as the secret that protects the state. A class derived from it can be registered as a service:

```csharp
public sealed record SetupTicket(string UserId);

[JsonSerializable(typeof(SetupTicket))]
internal sealed partial class AppJsonContext : JsonSerializerContext;

public sealed class SetupTickets(IShortLivedStore store, ILogger<SetupTickets> logger)
    : ShortLivedState<SetupTicket>(store, "my-service:setup-ticket:", AppJsonContext.Default.SetupTicket, TimeSpan.FromMinutes(15), logger);

// Step 1: hand out a ticket.
string ticket = await setupTickets.AddAsync(new SetupTicket(userId));

// Step 2: use it up.
var setup = await setupTickets.TakeAsync(command.Ticket) ?? throw new UnauthorizedApiException("The setup expired. Please start again.");
```

<xref:Singulink.Net.Http.Api.Service.ShortLivedState`1.TakeAsync*> returns a state at most once, even when concurrent requests present the same ID, <xref:Singulink.Net.Http.Api.Service.ShortLivedState`1.GetAsync*> reads a state without using it up, and <xref:Singulink.Net.Http.Api.Service.ShortLivedState`1.RemoveAsync*> removes one without reading it. For an ID that does not have the form of one (see <xref:Singulink.Net.Http.Api.Service.ShortLivedId>), they return `null` or remove nothing, without a lookup. A stored value that cannot be read as the state, for example one that an earlier version of the service stored during a rolling deployment, counts as missing and is logged as a warning to the logger, if one is given.

<xref:Singulink.Net.Http.Api.Service.ShortLivedStoreExtensions> has the same operations with the prefix and type info passed to each call, and <xref:Singulink.Net.Http.Api.Service.ShortLivedStoreExtensions.TryAddAsync*>, which adds a state under an ID of your choosing, such as a user ID, only if it is free.

The store itself has five operations on whole keys (add if free, get, take, remove and count), which together cover one-time use, approve-once steps and attempt limits. The toolkit's keys start with `httpapi:`, so keys with a prefix of the app's own never clash with them.

#### Counting attempts

<xref:Singulink.Net.Http.Api.Service.ShortLivedStoreExtensions.AcquirePermitAsync*> counts attempts in fixed windows and returns a <xref:Singulink.Net.Http.Api.Service.FixedWindowPermit>: whether a permit was acquired and, if not, how long until the window ends. Each key within a prefix has a count of its own, kept in the store, so a limit holds across all the servers that share it. Windows are measured by the clock that is passed in, here the registered <xref:System.TimeProvider>:

```csharp
string email = command.Email.Trim().ToUpperInvariant();
var permit = await store.AcquirePermitAsync("my-service:setup-emails:", email, permitLimit: 3, window: TimeSpan.FromHours(1), timeProvider);

if (!permit.IsAcquired)
    throw new TooManyRequestsApiException("Too many emails were sent to this address. Please try again later.") { RetryAfter = permit.RetryAfter };
```

Windows start at fixed times on the clock (a one-hour window starts at every full hour), so every server counts the same window. Every attempt is counted, including the ones that acquire no permit. The store only receives a hash of the key, so a key such as an email address is not kept in it. Keys are compared exactly, so the sample upper-cases the address: `A@example.com` and `a@example.com` would otherwise be counted apart, each with a limit of its own.

The default store, <xref:Singulink.Net.Http.Api.Service.MemoryShortLivedStore>, keeps everything in the memory of its server, and a restart only interrupts the flows in progress. A store backed by shared storage, such as Redis or a database table, shares the state between the servers of a service; see [Running Multiple Servers](multiple-servers.md).

#### The memory store's size

The memory store holds about 256 MiB of values and counters, set with <xref:Singulink.Net.Http.Api.Service.MemoryShortLivedStoreOptions.MaxSize> in bytes:

```csharp
builder.Services.AddShortLivedStore(options => options.MaxSize = 64 * 1024 * 1024);
```

The size is an estimate: each entry counts 2 bytes per character of its key and value, plus about 120 bytes. When adding a value or starting a counter would go over it, expired entries are removed first. If that does not make enough room, the operation throws a <xref:Singulink.Net.Http.Api.ServiceUnavailableApiException> with the error code `short-lived-store-full` and a `Retry-After` of a minute, so the request receives a `503 Service Unavailable` error, and a warning is logged, at most once a minute. A rate limit that cannot count a request refuses it with the same error. Reading, taking and removing values, and changing counters that already exist, always work.

## Sign-In Codes

A sign-in code is a one-time code that a client exchanges for a session. Ending every way of signing in with a code has two benefits:

- The step that verifies the user (a passkey, a password, an approved device link) does not create the session itself, so the client can ask questions in between, such as whether this is the user's own device and should stay signed in.
- Each code is bound to a PKCE code challenge, so only the client that holds the matching code verifier can redeem it, wherever the code travels (see [PKCE](passkeys.md#bind-sign-ins-to-the-app-that-started-them)).

The client creates a <xref:Singulink.Net.Http.Api.Client.Pkce> for every sign-in and sends its challenge in the request that **starts** the sign-in, so that everything the sign-in hands back is bound to it. When verifying the user takes a single request, as with a password, that request carries the challenge:

```csharp
var pkce = Pkce.Create();
var result = await apiClient.SignInWithPasswordAsync(new(email, password, pkce.Challenge));

bool persistent = await AskWhetherToStaySignedInAsync();
await apiClient.RedeemSignInCodeAsync(new(result.Code, pkce.Verifier, persistent));
```

When it takes more than one, as with a passkey, the first request carries the challenge and the sign-in code is bound to the challenge that the flow already holds; see [Signing in with a passkey](passkeys.md#signing-in) and [Device Linking](#device-linking).

On the service, the verifying endpoint creates a code with <xref:Singulink.Net.Http.Api.Service.SignIn.SignInCodes.CreateAsync*> and returns it as a <xref:Singulink.Net.Http.Api.DataContracts.SignInCode>, and a redeem endpoint signs in with <xref:Singulink.Net.Http.Api.Service.SignIn.SignInCodes.SignInAsync*?displayProperty=nameWithType>, which takes the <xref:Singulink.Net.Http.Api.DataContracts.RedeemSignInCodeCommand>. The password sign-in command is the app's own:

```csharp
static async Task<SignInCode> SignInWithPasswordAsync(PasswordSignInCommand command, SignInCodes signInCodes, AppDbContext db)
{
    var user = await VerifyPasswordAsync(db, command.Email, command.Password);   // throws UnauthorizedApiException on failure
    return new SignInCode(await signInCodes.CreateAsync(user.Id.ToString(CultureInfo.InvariantCulture), command.CodeChallenge));
}

static async Task<CurrentSessionInfo> RedeemSignInCodeAsync(
    RedeemSignInCodeCommand command,
    SessionContext<SessionToken> sessionContext,
    SignInCodes signInCodes,
    AppDbContext db)
{
    var token = await signInCodes.SignInAsync(sessionContext, command, async (redeemed, info) => {
        var user = await db.Users.SingleAsync(u => u.Id == int.Parse(redeemed.UserId, CultureInfo.InvariantCulture));
        EnsureCanSignIn(user);   // throws if the account was disabled since the code was created

        return await CreateSessionAsync(db, user, info);   // adds the session record and returns its token, as in Session Handling
    });

    return new CurrentSessionInfo(token.UserId);
}
```

The rules are:

- <xref:Singulink.Net.Http.Api.Service.SignIn.SignInCodes.CreateAsync*> throws <xref:Singulink.Net.Http.Api.BadRequestApiException> for a malformed challenge. A valid challenge is 43 base64url characters: the SHA-256 hash of the verifier (the `S256` method). To check one up front with the same rules, for example when a sign-in begins, call <xref:Singulink.Net.Http.Api.Service.SignIn.PkceChallenge.EnsureValid*>, which throws the same exception, or <xref:Singulink.Net.Http.Api.Service.SignIn.PkceChallenge.IsValid*>.
- A code can be redeemed once, within <xref:Singulink.Net.Http.Api.Service.SignIn.SignInCodeOptions.Lifetime> (10 minutes by default).
- A wrong verifier uses the code up too, so a code that reached the wrong party can be tried only once.
- <xref:Singulink.Net.Http.Api.Service.SignIn.SignInCodes.RedeemAsync*> returns a <xref:Singulink.Net.Http.Api.Service.SignIn.RedeemedSignInCode> with the user ID, or `null` when the code is invalid. <xref:Singulink.Net.Http.Api.Service.SignIn.SignInCodes.SignInAsync*> throws <xref:Singulink.Net.Http.Api.UnauthorizedApiException> (`sign-in-code-invalid`) instead, and only calls your function for a valid code, with the redeemed code.
- <xref:Singulink.Net.Http.Api.Service.SignIn.SignInCodes.SignInAsync*> checks the request's origin before it redeems the code, as signing in does (see [Session Handling](session-handling.md#signing-in-and-out)), so a request from an origin that is not allowed gets a `403 Forbidden` error (`origin-blocked`) and leaves the code unused.

The exceptions carry error codes from <xref:Singulink.Net.Http.Api.SignInErrorCodes>, so clients can tell the failures apart:

| Error code | Exception | Meaning |
| --- | --- | --- |
| `sign-in-code-invalid` | Unauthorized | The sign-in code is unknown, expired or used up, or the verifier is wrong. |
| `code-challenge-invalid` | Bad request | The code challenge is missing or malformed. |
| `device-link-too-many-wrong-codes` | Too many requests | The user entered too many wrong device link codes (see [Device Linking](#device-linking)). |
| `device-link-expired` | Unauthorized | The device link is unknown or expired, or the poll secret is wrong (see [Device Linking](#device-linking)). |

The user was verified when the code was created, which can be up to the code lifetime earlier, so the session creation function above checks that the account can still sign in. A code created before a password reset or before the user ended their other sessions still signs in within its lifetime, unless the session creation function refuses it, for example by comparing a value that the code carries (see below) with the current one.

A code can also carry a value of the app's, which <xref:Singulink.Net.Http.Api.Service.SignIn.SignInCodes.CreateAsync*> takes as `appData` and redeeming returns as <xref:Singulink.Net.Http.Api.Service.SignIn.RedeemedSignInCode.AppData?displayProperty=nameWithType>, so the session creation function receives it with the user ID. The value is kept in the short-lived store with the code. The code that a device link issues carries the value passed to <xref:Singulink.Net.Http.Api.Service.SignIn.DeviceLinks.ApproveAsync*>, and the code for a passkey the value passed to <xref:Singulink.Net.Http.Api.Service.SignIn.PasskeySignInCodesExtensions.CreateAsync*>.

User IDs are strings, so that the library works with any ID type. The samples format them with the invariant culture.

## Device Linking

Device linking signs in a new device with the approval of a device where the user is already signed in. It covers devices that cannot use the user's other ways in, such as a computer without the user's passkey, or a command-line tool. The flow has four steps, each an endpoint of your service:

1. The new device creates a <xref:Singulink.Net.Http.Api.Client.Pkce>, starts a link with the challenge, and shows the returned code.
2. On the signed-in device, the user types the code and sees the new device's details: its name, IP address, approximate location and when the link was started.
3. The user checks that it is the device in front of them and approves it.
4. Meanwhile the new device polls the link's status. Once the link is approved, the status contains a sign-in code bound to the challenge the link was started with, which the new device redeems with its verifier like any other sign-in code.

The user starts the flow and types the code, so an attacker would have to talk them into typing the attacker's code. Showing the device details before approval is the defense against that.

The endpoints for the new device take no session, and take and return the standard [data contracts](#data-contracts):

```csharp
static Task<DeviceLinkStart> StartDeviceLinkAsync(StartDeviceLinkCommand command, DeviceLinks deviceLinks, HttpContext httpContext)
{
    // The location from Cloudflare's headers (see Cloudflare), or from another source, such as a geolocation database.
    string? location = httpContext.GetClientLocation()?.ToString();
    return deviceLinks.StartAsync(command.CodeChallenge, httpContext.GetClientDevice(), httpContext.GetClientIpAddress(), location);
}

static Task<DeviceLinkStatus> CheckDeviceLinkAsync(CheckDeviceLinkCommand command, DeviceLinks deviceLinks)
{
    return deviceLinks.CheckAsync(command.Id, command.PollSecret);
}
```

<xref:Singulink.Net.Http.Api.Service.HttpContextExtensions.GetClientDevice*> and <xref:Singulink.Net.Http.Api.Service.HttpContextExtensions.GetClientIpAddress*> get the device and the IP address that session handling records for a session, and <xref:Singulink.Net.Http.Api.Service.CloudflareHttpContextExtensions.GetClientLocation*> the location that Cloudflare sends (see [Cloudflare](cloudflare.md#visitor-location)).

The endpoints for the signed-in device take the code that the user typed in an <xref:Singulink.Net.Http.Api.DataContracts.ApproveDeviceLinkCommand>, validate the session, and apply any rules about who may add devices:

```csharp
static async Task<PendingDeviceLink> LookUpDeviceLinkAsync(
    ApproveDeviceLinkCommand command,
    [SessionAccess(SessionAccessOptions.ForceValidate)] SessionToken token,
    DeviceLinks deviceLinks)
{
    return await deviceLinks.FindAsync(command.Code, token) ??
        throw new NotFoundApiException("No device is waiting with this code. Check the code and try again.");
}

static async Task ApproveDeviceLinkAsync(
    ApproveDeviceLinkCommand command,
    [SessionAccess(SessionAccessOptions.ForceValidate)] SessionToken token,
    DeviceLinks deviceLinks,
    AccountEmails emails)
{
    var approval = await deviceLinks.ApproveAsync(command.Code, token);

    switch (approval.Outcome)
    {
        case DeviceLinkApprovalOutcome.NotFound:
            throw new NotFoundApiException("No device is waiting with this code. Check the code and try again.");
        case DeviceLinkApprovalOutcome.AlreadyApproved:
            throw new ConflictApiException("This device was already approved.");
    }

    await emails.SendDeviceLinkedAsync(token.UserId, approval.Link!);   // the details of the device that was approved
}
```

<xref:Singulink.Net.Http.Api.Service.SignIn.DeviceLinks.FindAsync*> and <xref:Singulink.Net.Http.Api.Service.SignIn.DeviceLinks.ApproveAsync*> take the session token of the signed-in user, whose <xref:Singulink.Net.Http.Api.Service.ISessionToken.UserId> identifies the user, or the user ID itself. The approval's <xref:Singulink.Net.Http.Api.Service.SignIn.DeviceLinkApprovalResult.Link> has the details of the device that was approved, which <xref:Singulink.Net.Http.Api.Service.SignIn.DeviceLinkApprovalResult.IsApproved> tells is not `null`.

On the new device, <xref:Singulink.Net.Http.Api.Client.DeviceLinkWaiter> in `Singulink.Net.Http.Api.Client` checks the link's status until the link is approved, and returns the sign-in code that the status then has:

```csharp
var pkce = Pkce.Create();
var waiter = new DeviceLinkWaiter(await apiClient.RequestDeviceLinkAsync(new(pkce.Challenge)));
ShowCode(waiter.Link.Code);

try
{
    string code = await waiter.WaitForApprovalAsync(apiClient.GetDeviceLinkStatusAsync, reachable => ShowOffline(!reachable), cancellationToken);
    await apiClient.RedeemSignInCodeAsync(new(code, pkce.Verifier, isPersistent: false));
}
catch (UnauthorizedApiException ex) when (ex.ErrorCode == SignInErrorCodes.DeviceLinkExpired)
{
    ShowMessage("The code has expired. Start again to get a new one.");
}
```

`RequestDeviceLinkAsync`, `GetDeviceLinkStatusAsync` and `RedeemSignInCodeAsync` stand for the client's methods for the endpoints above and the one that redeems sign-in codes (see [Signing In and Restoring a Session](api-clients.md#signing-in-and-restoring-a-session)). `GetDeviceLinkStatusAsync` takes a <xref:Singulink.Net.Http.Api.DataContracts.CheckDeviceLinkCommand> and a cancellation token, and returns the <xref:Singulink.Net.Http.Api.DataContracts.DeviceLinkStatus>, so the waiter can call it as it is. The waiter checks the status every <xref:Singulink.Net.Http.Api.Client.DeviceLinkWaiter.PollInterval> (2 seconds by default), and the service decides when the link has expired: the check of an expired link throws an <xref:Singulink.Net.Http.Api.UnauthorizedApiException> with the `device-link-expired` error code (<xref:Singulink.Net.Http.Api.SignInErrorCodes.DeviceLinkExpired>), which ends the wait. A check that fails for a reason that may not last (see <xref:Singulink.Net.Http.Api.Client.ApiFailures.IsTransient*?displayProperty=nameWithType>), such as while the service can't be reached, is made again, and reported to the optional callback, until the link's <xref:Singulink.Net.Http.Api.DataContracts.DeviceLinkStart.Lifetime> has passed, counted from when the waiter was created, which doesn't depend on the device's clock. The check's exception is then thrown. <xref:Singulink.Net.Http.Api.Client.DeviceLinkWaiter.RemainingLifetime> is the part of the lifetime that is left.

The rules are:

- A link waits for approval for <xref:Singulink.Net.Http.Api.Service.SignIn.DeviceLinkOptions.Lifetime> (10 minutes by default). <xref:Singulink.Net.Http.Api.Service.SignIn.DeviceLinks.StartAsync*> throws <xref:Singulink.Net.Http.Api.BadRequestApiException> for a malformed challenge.
- <xref:Singulink.Net.Http.Api.Service.SignIn.DeviceLinks.FindAsync*> and <xref:Singulink.Net.Http.Api.Service.SignIn.DeviceLinks.ApproveAsync*> accept the code as typed (see [Short Codes](#short-codes)).
- Wrong codes count against the user typing them. After 5 wrong codes, both methods throw <xref:Singulink.Net.Http.Api.TooManyRequestsApiException> for that user, so a signed-in account cannot be used to guess codes, not even with many requests at once. The count is forgotten once the link lifetime passes without a lookup by the user; lookups that are refused do not count, so the exception's <xref:Singulink.Net.Http.Api.ApiException.RetryAfter> is the link lifetime. Input that cannot be a code is not counted.
- A link can be approved once, even when several users approve it at the same moment on different servers.
- <xref:Singulink.Net.Http.Api.Service.SignIn.DeviceLinks.CheckAsync*> checks the poll secret in fixed time and throws <xref:Singulink.Net.Http.Api.UnauthorizedApiException> (`device-link-expired`) for an unknown or expired link or a wrong secret. Once the link is approved, every check gets the same sign-in code, so the new device can simply check again if a response does not reach it; the code can be redeemed once, and only with the new device's verifier. An approval made in the last seconds of the link can still be collected for a minute after the code expires.
- <xref:Singulink.Net.Http.Api.Service.SignIn.DeviceLinks.PollAsync*> does the same, and returns `null` instead of throwing. Its result also has the ID of the user who approved the link (<xref:Singulink.Net.Http.Api.Service.SignIn.DeviceLinkPollResult.ApproverUserId>).

Links, wrong code counts and the sign-in codes are kept in the registered <xref:Singulink.Net.Http.Api.Service.IShortLivedStore>, so each step can run on a different server when the store is shared.

## Short Codes

<xref:Singulink.Net.Http.Api.Service.SignIn.ShortCode> handles codes that users read on one screen and type on another. Device links use it, and so can the app's own codes, such as emailed setup codes. A code has 8 characters from Crockford's base 32 (about 10^12 codes), which avoids the easily confused letters I, L, O and U:

```csharp
string code = ShortCode.Generate();                  // "K7PXM3QR"
string display = ShortCode.Format(code);             // "K7PX-M3QR"
string? typed = ShortCode.Normalize(" k7px m3qr ");  // "K7PXM3QR", or null if the input cannot be a code
```

<xref:Singulink.Net.Http.Api.Service.SignIn.ShortCode.Normalize*> forgives typing: case does not matter, O is read as 0, I and L are read as 1, and spaces and dashes are ignored, so normalized codes compare equal however the code was typed.

A short code is far easier to guess than a random key: whatever it unlocks is exposed for as long as the code is valid, to as many wrong attempts as the app allows. There are only about 10^12 codes, so anyone with a copy of the database could find the code of a plain hash in minutes by hashing every code. A keyed hash, whose key is kept outside the database, such as in an environment variable or a key vault, tells them nothing without the key.

<xref:Singulink.Net.Http.Api.Service.SignIn.ShortCode.ComputeHash*> computes the keyed hash (HMAC-SHA256) of a code with a key of at least 32 bytes. It normalizes the code first, so the code as typed gives the same hash:

```csharp
// 32 random bytes, kept outside the database.
string hashKeyText = builder.Configuration["SetupCodes:HashKey"] ?? throw new InvalidOperationException("The SetupCodes:HashKey setting is missing.");
byte[] hashKey = Convert.FromBase64String(hashKeyText);

// Creating a code: store its hash and email the code.
string code = ShortCode.Generate();
string codeHash = ShortCode.ComputeHash(code, hashKey);

// Checking a typed code.
if (ShortCode.Normalize(command.Code) is not { } typed)
    throw new ValidationApiException("This is not a valid code.");

string typedHash = ShortCode.ComputeHash(typed, hashKey);
var setup = await db.Setups.SingleOrDefaultAsync(s => s.CodeHash == typedHash);
```

## Rate Limits

The per-user limit on wrong device link codes does not limit an attacker spread across many accounts, and the endpoints that start flows without a session use storage for each flow. <xref:Singulink.Net.Http.Api.Service.RateLimiterOptionsExtensions.AddClientFixedWindowLimiter*> limits endpoints per IP address, such as the ones that start device links, look up and approve codes, redeem sign-in codes and check the app's own codes. See [Rate Limiting](rate-limiting.md).

## Options

<xref:Singulink.Net.Http.Api.Service.SignIn.SignInCodeOptions>, <xref:Singulink.Net.Http.Api.Service.SignIn.DeviceLinkOptions> and <xref:Singulink.Net.Http.Api.Service.MemoryShortLivedStoreOptions> are configured through the registration methods, or bound from configuration like any options class:

```csharp
builder.Services.AddDeviceLinks(options => options.Lifetime = TimeSpan.FromMinutes(5));
builder.Services.Configure<SignInCodeOptions>(builder.Configuration.GetSection("SignInCodes"));
```

| Option | Default | Purpose |
| --- | --- | --- |
| <xref:Singulink.Net.Http.Api.Service.SignIn.SignInCodeOptions.Lifetime?displayProperty=nameWithType> | 10 minutes | How long a sign-in code can be redeemed. A device link's code is valid for at least as long as the link can be polled. |
| <xref:Singulink.Net.Http.Api.Service.SignIn.DeviceLinkOptions.Lifetime?displayProperty=nameWithType> | 10 minutes | How long a device link waits for approval, and how long wrong codes count against a user. |
| <xref:Singulink.Net.Http.Api.Service.MemoryShortLivedStoreOptions.MaxSize?displayProperty=nameWithType> | 256 MiB | How much the memory store holds, in bytes (see [The memory store's size](#the-memory-stores-size)). |

## Next Steps

Continue with these related articles:

- [Session Handling](session-handling.md) - Creating the session once the code is redeemed.
- [Passkeys](passkeys.md) - Signing in with a passkey, then with a sign-in code.
- [Running Multiple Servers](multiple-servers.md) - Sharing the short-lived store between servers.
- [Building API Clients](api-clients.md) - The client methods that call your sign-in endpoints.

</div>
