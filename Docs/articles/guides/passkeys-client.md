<div class="article">

# Passkeys in Client Apps

<xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyAuthenticator> runs passkey (WebAuthn) operations in WinUI 3 and Uno Platform apps: signing in, confirming the signed-in user and creating passkeys. This guide covers how it works on each platform, setting it up, the three operations, error handling and the setup each platform needs.

### Packages

The client's passkey support is in three packages:

- `Singulink.Net.Http.Api.Client.WinUI`, a library for Windows App SDK (WinUI 3) apps and for Uno Platform apps on WebAssembly, iOS, Android and desktop, has the authenticator, <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyAuthenticator>, and the loopback web authentication broker for Uno Platform desktop apps.
- `Singulink.Net.Http.Api.Client`, which has no UI framework dependency, has the interface that the authenticator implements, <xref:Singulink.Net.Http.Api.Client.Passkeys.IPasskeyAuthenticator>, and the types it uses: <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyCredential>, <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyErrorReason> and <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyAuthenticatorException>. It also has <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyCeremonyPrefetch`1> and the <xref:Singulink.Net.Http.Api.Client.Pkce> helper used for sign-in.
- The shared `Singulink.Net.Http.Api` package has <xref:Singulink.Net.Http.Api.DataContracts.PasskeyAttachment> and <xref:Singulink.Net.Http.Api.DataContracts.PasskeyCeremony>.

The passkey types are in the `Singulink.Net.Http.Api.Client.Passkeys` namespace, and the loopback web authentication broker and <xref:Singulink.Net.Http.Api.Client.Pkce> are in `Singulink.Net.Http.Api.Client`. The service side, which runs the ceremonies and serves the passkey page, is in `Singulink.Net.Http.Api.Service.Passkeys`; see [Passkeys](passkeys.md).

## How It Works

The authenticator knows nothing about the app's API. The app gets WebAuthn options from its API, passes them to the authenticator as JSON, and posts the credential JSON it returns (<xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyCredential.Json?displayProperty=nameWithType>) back to its API, with its own session where it has one.

Browsers run WebAuthn themselves, so where the operation runs depends on the platform:

| Platform | Where the operation runs | How the result returns |
| --- | --- | --- |
| WebAssembly | In the app's own page. | Directly. |
| iOS | On the passkey page, in an `ASWebAuthenticationSession`. | Through the app's custom scheme. |
| Android | On the passkey page, in a Custom Tab. | Through the app's custom scheme. |
| Desktop (Uno Platform on Windows, macOS and Linux, and Windows App SDK) | On the passkey page, in the default browser. | Through a loopback address (`http://127.0.0.1:{port}/callback`) chosen automatically. |

<xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyAuthenticator.UsesPasskeyPage> tells which applies. The passkey page is a small page on the service that runs WebAuthn with the options it is given and redirects back to the app with the credential or an error. Passkeys are bound to the relying party ID, so both the passkey page and a WebAssembly app must be served from the relying party's domain or a subdomain of it, from an origin the service allows.

## Setting Up the Authenticator

Create one authenticator for the app with the address of the passkey page and, for iOS and Android, the callback address:

```csharp
var passkeys = new PasskeyAuthenticator(new PasskeyAuthenticatorOptions
{
    PageUri = new Uri("https://service.example.com/passkey"),
    CallbackUri = new Uri("myapp://passkey"),
});
```

- <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyAuthenticatorOptions.PageUri> is required everywhere except WebAssembly. It must use HTTPS, except for loopback hosts, where HTTP is accepted.
- <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyAuthenticatorOptions.CallbackUri> is used on iOS and Android. If it isn't set, the web authentication broker's default callback address is used. Desktop apps always use a loopback address.
- <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyAuthenticatorOptions.PrefersEphemeralWebBrowserSession> applies to iOS; see [iOS](#ios).
- <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyAuthenticatorOptions.CompletedMessage>, <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyAuthenticatorOptions.Timeout> and <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyAuthenticatorOptions.StartTimeout> apply to desktop; see [Desktop](#desktop).

A WebAssembly-only app can use the parameterless constructor.

<xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyAuthenticator.IsSupported> is false in browsers without WebAuthn support.

<xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyAuthenticator.PrepareAsync*> loads what operations need ahead of time: on WebAssembly, the library's script, which the first operation loads otherwise. It does nothing on other platforms. Browsers such as Safari only allow a WebAuthn operation shortly after a user action such as a click, and loading the script in between can take longer than that:

```csharp
try
{
    await passkeys.PrepareAsync();
}
catch (PasskeyAuthenticatorException ex)
{
    // The script could not be loaded. The first operation loads it again.
    Log(ex);
}
```

The request that begins a ceremony on the service also sits between the action and the prompt in the samples below. <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyCeremonyPrefetch`1> begins it ahead of the action (see [Beginning Ceremonies Ahead of Time](#beginning-ceremonies-ahead-of-time)).

## Signing In

A sign-in ceremony is bound to a PKCE code challenge (see [Bind sign-ins to the app that started them](passkeys.md#bind-sign-ins-to-the-app-that-started-them)): the app sends the challenge when it begins the ceremony and the verifier when it finishes. The service then returns a one-time sign-in code bound to the same challenge, which the app redeems with the same verifier:

```csharp
var pkce = Pkce.Create();
var ceremony = await apiClient.BeginPasskeySignInAsync(new(pkce.Challenge));

var credential = await passkeys.GetCredentialAsync(ceremony.OptionsJson, cancellationToken);
var result = await apiClient.FinishPasskeySignInAsync(new(ceremony.Id, credential.Json, pkce.Verifier));

bool persistent = await AskWhetherToStaySignedInAsync(credential.Attachment);
await apiClient.RedeemSignInCodeAsync(new(result.Code, pkce.Verifier, persistent));
```

`BeginPasskeySignInAsync`, `FinishPasskeySignInAsync` and `RedeemSignInCodeAsync` stand for the client's methods for the app's endpoints, which run the service's sign-in ceremony and redeem the sign-in code (see [Signing in](passkeys.md#signing-in)). They send the standard data contracts, <xref:Singulink.Net.Http.Api.DataContracts.BeginPasskeySignInCommand>, <xref:Singulink.Net.Http.Api.DataContracts.FinishPasskeySignInCommand> and <xref:Singulink.Net.Http.Api.DataContracts.RedeemSignInCodeCommand>, and the finish request returns a <xref:Singulink.Net.Http.Api.DataContracts.SignInCode>:

```csharp
public Task<PasskeyCeremony> BeginPasskeySignInAsync(BeginPasskeySignInCommand command, CancellationToken cancellationToken = default) =>
    PostAsync<PasskeyCeremony>("sessions/passkey/options", command, cancellationToken);

public Task<SignInCode> FinishPasskeySignInAsync(FinishPasskeySignInCommand command, CancellationToken cancellationToken = default) =>
    PostAsync<SignInCode>("sessions/passkey", command, cancellationToken);
```

`RedeemSignInCodeAsync` also creates the client's child client for the signed-in user from the response (see [Signing In and Restoring a Session](api-clients.md#signing-in-and-restoring-a-session)). The code is the same on every platform, WebAssembly included. The ceremony is a <xref:Singulink.Net.Http.Api.DataContracts.PasskeyCeremony>, which can be finished for its <xref:Singulink.Net.Http.Api.DataContracts.PasskeyCeremony.Lifetime> after the service started it. The time the person spends in the browser's prompt counts toward it, and a ceremony that is finished later fails with <xref:Singulink.Net.Http.Api.PasskeyErrorCodes.CeremonyExpired?displayProperty=nameWithType>.

### Passkeys that were removed

A passkey can stay in the user's password manager after it was removed from the account, and the browser keeps offering it. The service then rejects the sign-in or verification with <xref:Singulink.Net.Http.Api.PasskeyErrorCodes.NotRegistered?displayProperty=nameWithType>, as a `401` in a sign-in and a `422` in a verification. <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyAuthenticator.SignalUnknownCredentialAsync*> tells the browser that the relying party doesn't know the passkey, so that the password manager can stop offering it:

```csharp
catch (ApiException ex) when (ex.ErrorCode == PasskeyErrorCodes.NotRegistered)
{
    await passkeys.SignalUnknownCredentialAsync(credential, ceremony.OptionsJson);
    ShowMessage("This passkey was removed from your account.");
}
```

On WebAssembly it uses the WebAuthn Signal API (`PublicKeyCredential.signalUnknownCredential`) in browsers that have it. It does nothing in other browsers and on the platforms that use the passkey page, and ignores failures.

Only `NotRegistered` means that the relying party doesn't know the passkey. A passkey that is registered to another user, offered in a verification of the signed-in user or a sign-in started for a user, is rejected with <xref:Singulink.Net.Http.Api.PasskeyErrorCodes.WrongAccount?displayProperty=nameWithType> instead, and the relying party does know it.

### Attachment

<xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyCredential.Attachment> tells where the passkey was, as reported by the browser: <xref:Singulink.Net.Http.Api.DataContracts.PasskeyAttachment.Platform> for a passkey on the device itself (including one synced to it), <xref:Singulink.Net.Http.Api.DataContracts.PasskeyAttachment.CrossPlatform> for one on another device, such as a phone used through a QR code or a security key, and <xref:Singulink.Net.Http.Api.DataContracts.PasskeyAttachment.Unknown> when the browser didn't say. It is the same on WebAssembly and with the passkey page.

<xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyCredential.FromJson*?displayProperty=nameWithType> creates a credential from its WebAuthn JSON with the attachment that the JSON reports, as the authenticator does.

## Confirming the Signed-In User

Confirming the user before a sensitive action works the same way with the service's verification ceremony. No code challenge is needed, because finishing the ceremony requires the app's session, which never passes through the passkey page:

```csharp
var ceremony = await sessionClient.BeginPasskeyVerificationAsync();
var credential = await passkeys.GetCredentialAsync(ceremony.OptionsJson, cancellationToken);

await sessionClient.DeleteAccountAsync(ceremony.Id, credential.Json);
```

## Creating a Passkey

<xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyAuthenticator.CreateCredentialAsync*> takes the creation options from the service's registration ceremony and returns the new credential for the app to post back:

```csharp
var ceremony = await sessionClient.BeginPasskeyRegistrationAsync();
var credential = await passkeys.CreateCredentialAsync(ceremony.OptionsJson, cancellationToken);

await sessionClient.FinishPasskeyRegistrationAsync(new(ceremony.Id, credential.Json));
```

`FinishPasskeyRegistrationAsync` stands for the client's method for the app's endpoint that finishes the registration, which sends the standard <xref:Singulink.Net.Http.Api.DataContracts.FinishPasskeyCeremonyCommand> (see [Adding a passkey](passkeys.md#adding-a-passkey)).

The creation options list the user's existing passkeys, unless the service leaves them out (see [Passkeys](passkeys.md)), so an authenticator that already has one fails with <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyErrorReason.AlreadyOnAuthenticator?displayProperty=nameWithType>. A new credential whose passkey the service already has registered is rejected by the service instead, with <xref:Singulink.Net.Http.Api.PasskeyErrorCodes.AlreadyRegistered?displayProperty=nameWithType> (a `409`).

## Beginning Ceremonies Ahead of Time

<xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyCeremonyPrefetch`1> begins a ceremony on the service before the person's action that shows its prompt, so that the prompt can follow the action right away. It takes a delegate that begins the ceremony and one that gets the <xref:Singulink.Net.Http.Api.DataContracts.PasskeyCeremony> from what the first returns, which can be a type of the app's own that also holds what the app needs to finish the ceremony, such as the PKCE pair of a sign-in:

```csharp
private sealed record StartedSignIn(PasskeyCeremony Ceremony, Pkce Pkce);

private readonly PasskeyCeremonyPrefetch<StartedSignIn> _signIn;

public SignInPageModel()
{
    _signIn = new(BeginSignInAsync, signIn => signIn.Ceremony);
}

// When the page opens:
public void OnShown() => _signIn.Prefetch();

// When the person taps "Sign In With a Passkey":
public async Task SignInWithPasskeyAsync()
{
    var result = await _signIn.RunAsync(passkeys.GetCredentialAsync, (signIn, credential, cancellationToken) =>
        apiClient.FinishPasskeySignInAsync(new(signIn.Ceremony.Id, credential.Json, signIn.Pkce.Verifier), cancellationToken));

    // ...
}

private async Task<StartedSignIn> BeginSignInAsync(CancellationToken cancellationToken)
{
    var pkce = Pkce.Create();
    var ceremony = await apiClient.BeginPasskeySignInAsync(new(pkce.Challenge), cancellationToken);

    return new StartedSignIn(ceremony, pkce);
}
```

<xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyCeremonyPrefetch`1.Prefetch*> begins a ceremony in the background, unless one that can still be used was begun. A failure isn't reported: <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyCeremonyPrefetch`1.RunAsync*> then begins the ceremony itself and throws its failure. <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyCeremonyPrefetch`1.RunAsync*> shows the prompt with the begun ceremony and sends the credential with the delegate that finishes the ceremony:

- A ceremony whose prompt fails or is cancelled is kept for the next try.
- A ceremony whose credential was sent is not used again, since sending it ends the ceremony on the service. When sending fails, a new ceremony is begun in the background for the next try.
- A ceremony is begun again once it is in the last fifth of its <xref:Singulink.Net.Http.Api.DataContracts.PasskeyCeremony.Lifetime>, counted from when it was received, so that the credential reaches the service before the ceremony expires there.

When sending the credential fails for a reason that may not last (see <xref:Singulink.Net.Http.Api.Client.ApiFailures.IsTransient*?displayProperty=nameWithType>) or is cancelled, the next try sends the same credential again instead of showing the prompt, while its ceremony can still be used. For a passkey creation, that keeps a retry from creating a second passkey, which would leave the first one in the password manager without an account if its credential never arrived. <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyCeremonyPrefetch`1.DiscardUnsentCredential*> discards the credential, so that the next try shows the prompt again, such as for a sign-in that may use another passkey. The service may have received the credential although its answer was lost, in which case it ended the ceremony, and the credential that is sent again fails with <xref:Singulink.Net.Http.Api.PasskeyErrorCodes.CeremonyExpired?displayProperty=nameWithType>. <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyCeremonyPrefetch`1.ResentCredential> tells whether the last try sent a credential again.

## Handling Errors

Operations throw <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyAuthenticatorException> when they fail or the person cancels them. <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyAuthenticatorException.Reason> says why:

| Reason | Browser error | Typical cause |
| --- | --- | --- |
| <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyErrorReason.Cancelled> | `AbortError` | The person closed the passkey page or the browser. |
| <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyErrorReason.NotAllowed> | `NotAllowedError` | The person dismissed the passkey prompt, it timed out, or the browser refused. |
| <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyErrorReason.AlreadyOnAuthenticator> | `InvalidStateError` | The authenticator already has a passkey for the account. |
| <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyErrorReason.NotSupported> | `NotSupportedError` | The browser or device doesn't support passkeys. |
| <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyErrorReason.RequirementsNotMet> | `ConstraintError` | The authenticator can't meet the options' requirements, such as user verification. |
| <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyErrorReason.Security> | `SecurityError` | The relying party ID doesn't match the origin. This is a configuration error. |
| <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyErrorReason.PageNotOpened> | None | The passkey page couldn't be opened, for example because the browser didn't start. |
| <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyErrorReason.TimedOut> | None | On desktop, the browser didn't return from the passkey page within <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyAuthenticatorOptions.Timeout>. |
| <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyErrorReason.Unknown> | Anything else | Includes a response that doesn't belong to the request. |

Browsers report a dismissed or timed out prompt as `NotAllowedError` rather than `AbortError`, the same as an operation they refuse. <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyAuthenticatorException.IsCancellation?displayProperty=nameWithType> is true for both <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyErrorReason.Cancelled> and <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyErrorReason.NotAllowed>:

```csharp
try
{
    var credential = await passkeys.GetCredentialAsync(ceremony.OptionsJson, cancellationToken);
    // ...
}
catch (PasskeyAuthenticatorException ex) when (ex.IsCancellation)
{
    // Nothing to report.
}
catch (PasskeyAuthenticatorException ex) when (ex.Reason is PasskeyErrorReason.AlreadyOnAuthenticator)
{
    ShowMessage("This device already has a passkey for your account.");
}
```

The exception message comes from the browser where it supplied one. It isn't localized or written for the person using the app. Cancelling the operation's token throws <xref:System.OperationCanceledException> instead. On desktop the authenticator stops waiting for the browser right away, though the passkey page stays open in it; on WebAssembly it stops waiting but can't close a prompt the browser is already showing. A missing <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyAuthenticatorOptions.PageUri> throws <xref:System.InvalidOperationException>.

## Platform Setup

### WebAssembly

Nothing needs to be set up in the app. The library's script is deployed with the app's other assets and loaded the first time an operation runs. The app must be served from the relying party's domain or a subdomain of it, and its origin must be one the service allows.

### iOS

Register the callback scheme in `Info.plist`:

```xml
<key>CFBundleURLTypes</key>
<array>
  <dict>
    <key>CFBundleURLName</key>
    <string>com.example.myapp</string>
    <key>CFBundleURLSchemes</key>
    <array>
      <string>myapp</string>
    </array>
  </dict>
</array>
```

By default the passkey page opens in an ephemeral browser session, which shares no cookies or website data with Safari (the page needs none) and skips the system alert asking whether the app may use the website to sign in. Set <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyAuthenticatorOptions.PrefersEphemeralWebBrowserSession> to false to share Safari's website data instead.

### Android

Add an activity that receives the callback. It derives from Uno's `WebAuthenticationBrokerActivityBase` and has an intent filter for the callback address's scheme and host:

```csharp
using Android.App;
using Android.Content;
using Android.Content.PM;
using Uno.AuthenticationBroker;

[Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = "myapp",
    DataHost = "passkey")]
public class WebAuthenticationCallbackActivity : WebAuthenticationBrokerActivityBase
{
}
```

### Desktop

Nothing needs to be set up, in Uno Platform desktop apps or in Windows App SDK apps. The authenticator opens a short address on its loopback listener in the default browser (`http://127.0.0.1:{port}/start`), which redirects to the passkey page, and receives the result on a loopback address, which the passkey page must allow. Once the passkey page returns, the browser shows <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyAuthenticatorOptions.CompletedMessage>. If the browser doesn't open the start address within <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyAuthenticatorOptions.StartTimeout> (30 seconds by default), the operation fails with <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyErrorReason.PageNotOpened>. If the browser doesn't return within <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyAuthenticatorOptions.Timeout> (by default 5 minutes, or Uno's `WinRTFeatureConfiguration.WebAuthenticationBroker.AuthenticationTimeout` in Uno Platform apps), it fails with <xref:Singulink.Net.Http.Api.Client.Passkeys.PasskeyErrorReason.TimedOut>:

```csharp
var passkeys = new PasskeyAuthenticator(new PasskeyAuthenticatorOptions
{
    PageUri = new Uri("https://service.example.com/passkey"),
    CompletedMessage = "Done. You can close this tab and return to Example.",
    Timeout = TimeSpan.FromMinutes(10),
});
```

Uno has no web authentication broker for desktop apps. To use the same loopback broker for other `WebAuthenticationBroker` flows, such as OAuth sign-in, register <xref:Singulink.Net.Http.Api.Client.LoopbackWebAuthenticationBrokerProvider> in `Program.Main`, before the host is built:

```csharp
LoopbackWebAuthenticationBrokerProvider.Register(provider => provider.CompletedMessage = "You can close this tab.");
```

The provider's <xref:Singulink.Net.Http.Api.Client.LoopbackWebAuthenticationBrokerProvider.CompletedMessage>, <xref:Singulink.Net.Http.Api.Client.LoopbackWebAuthenticationBrokerProvider.StartTimeout> and <xref:Singulink.Net.Http.Api.Client.LoopbackWebAuthenticationBrokerProvider.Timeout> apply to `WebAuthenticationBroker` flows.

The authenticator doesn't go through the provider. It runs the same flow itself, with the settings of its options, whether or not the provider is registered, so passkey operations behave the same in Uno Platform desktop apps and Windows App SDK apps.

A web authentication broker provider of another kind that an Uno Platform app registers runs the authenticator's operations instead, and the options' hand-off settings don't apply to it.

### The passkey page

The passkey page only redirects to addresses it allows: the app's custom scheme for iOS and Android, such as `myapp:`, and loopback addresses for desktop. Configure both when mapping the page on the service; see [Passkeys](passkeys.md).

## Next Steps

Continue with these related articles:

- [Passkeys](passkeys.md) - The service side: ceremonies, passkey storage and the passkey page.
- [Building API Clients](api-clients.md) - The API client that posts the credentials.

</div>
