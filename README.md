# Singulink HTTP API Toolkit

[![Chat on Discord](https://img.shields.io/discord/906246067773923490)](https://discord.gg/EkQhJFsBu6)
[![Build and Test](https://github.com/Singulink/Singulink.Net.Http.Api/workflows/build%20and%20test/badge.svg)](https://github.com/Singulink/Singulink.Net.Http.Api/actions?query=workflow%3A%22build+and+test%22)

**HTTP API Toolkit** is a set of client and service libraries for building and consuming HTTP APIs in .NET based on a consistent set of opinionated conventions. A service and a client built with the toolkit agree on how sessions, errors, streamed results and SignalR hubs work, so application code deals with typed exceptions, typed hub contracts and plain `IAsyncEnumerable<T>` results instead of HTTP plumbing.

✔️ Cookie sessions validated without a store lookup  
✔️ Session token rotation and theft detection  
✔️ Errors travel as typed exceptions  
✔️ Streamed results with keep-alive and error propagation  
✔️ Strongly typed SignalR hub contracts  
✔️ Passkey sign-in with app-owned storage  
✔️ Runs on several servers with one pluggable store  
✔️ Runs on desktop, mobile and browser clients  
✔️ Trimming and Native AOT compatible with source-generated JSON  

Details of each component are provided below:

| Library | Status | Package |
| --- | --- | --- |
| **Singulink.Net.Http.Api** | Public | [![View nuget package](https://img.shields.io/nuget/v/Singulink.Net.Http.Api.svg)](https://www.nuget.org/packages/Singulink.Net.Http.Api/) |
| **Singulink.Net.Http.Api.Client** | Public | [![View nuget package](https://img.shields.io/nuget/v/Singulink.Net.Http.Api.Client.svg)](https://www.nuget.org/packages/Singulink.Net.Http.Api.Client/) |
| **Singulink.Net.Http.Api.Client.SignalR** | Public | [![View nuget package](https://img.shields.io/nuget/v/Singulink.Net.Http.Api.Client.SignalR.svg)](https://www.nuget.org/packages/Singulink.Net.Http.Api.Client.SignalR/) |
| **Singulink.Net.Http.Api.Client.WinUI** | Public | [![View nuget package](https://img.shields.io/nuget/v/Singulink.Net.Http.Api.Client.WinUI.svg)](https://www.nuget.org/packages/Singulink.Net.Http.Api.Client.WinUI/) |
| **Singulink.Net.Http.Api.Service** | Public | [![View nuget package](https://img.shields.io/nuget/v/Singulink.Net.Http.Api.Service.svg)](https://www.nuget.org/packages/Singulink.Net.Http.Api.Service/) |
| **Singulink.Net.Http.Api.Service.Cloudflare** | Public | [![View nuget package](https://img.shields.io/nuget/v/Singulink.Net.Http.Api.Service.Cloudflare.svg)](https://www.nuget.org/packages/Singulink.Net.Http.Api.Service.Cloudflare/) |
| **Singulink.Net.Http.Api.Service.Passkeys** | Public | [![View nuget package](https://img.shields.io/nuget/v/Singulink.Net.Http.Api.Service.Passkeys.svg)](https://www.nuget.org/packages/Singulink.Net.Http.Api.Service.Passkeys/) |
| **Singulink.Net.Http.Api.Service.Testing** | Public | [![View nuget package](https://img.shields.io/nuget/v/Singulink.Net.Http.Api.Service.Testing.svg)](https://www.nuget.org/packages/Singulink.Net.Http.Api.Service.Testing/) |

**Supported Runtimes**:
- **Singulink.Net.Http.Api** and **Singulink.Net.Http.Api.Client**: .NET 10.0+ and .NET Framework 4.6.2+. Browser (WebAssembly) apps require .NET 10.0+. Other runtimes that can load .NET Standard 2.0 libraries, such as .NET 8 and 9, get a build warning: they are not supported or tested.
- All other packages: .NET 10.0+

Libraries may be in the following states:
- Internal: Source code (and possibly a nuget package) is available but the library is intended for internal use at this time.
- Preview: Library is available for public preview but the APIs may not be fully documented and the API surface is subject to change without notice.
- Public: Library is intended for public use with a fully documented and stable API surface.

You are welcome to use any libraries or code in this repository that you find useful and feedback/contributions are appreciated regardless of library state.

### About Singulink

We are a small team of engineers and designers dedicated to building beautiful, functional and well-engineered software solutions. We offer very competitive rates as well as fixed-price contracts and welcome inquiries to discuss any custom development / project support needs you may have.

These packages are part of our **Singulink Libraries** collection. Visit https://github.com/Singulink to see our full list of publicly available libraries and other open-source projects.

## Components

### Singulink.Net.Http.Api

Shared library referenced by both clients and services. It contains the parts of the conventions that both sides must agree on, and has no ASP.NET Core dependency so it can also be referenced by data contract projects:

- The `ApiException` hierarchy (`BadRequestApiException`, `UnauthorizedApiException`, `NotFoundApiException`, `ValidationApiException`, `ServerErrorApiException` and more), thrown on the server and rethrown as the same type on the client.
- The error codes of the errors the toolkit reports itself (`ApiErrorCodes`, `SignInErrorCodes` and `PasskeyErrorCodes`).
- The data contracts of the standard sign-in steps, which services and clients use as they are: passkey sign-in (`BeginPasskeySignInCommand`, `PasskeyCeremony`, `FinishPasskeySignInCommand`), passkey registration and verification (`FinishPasskeyCeremonyCommand`), sign-in codes (`SignInCode`, `RedeemSignInCodeCommand`) and device links (`StartDeviceLinkCommand`, `DeviceLinkStart`, `CheckDeviceLinkCommand`, `DeviceLinkStatus`, `ApproveDeviceLinkCommand`, `PendingDeviceLink`), and `PasskeyAttachment`.
- The JSON null handling that services and clients apply to the app's serializer options (`ConfigureApiNullHandling`).
- The `StreamingResponse` format and reader for streamed results.
- `HubMessage`, `HubMethod` and `HubStream` definitions for strongly typed SignalR hub contracts.

### Singulink.Net.Http.Api.Service

ASP.NET Core library for building services. Sessions are cookie-based and backed by an encrypted, self-contained session token, so most requests need no session store lookup. Sessions use a sliding expiry with periodic refresh, token rotation for theft detection and a grace window for concurrent requests, and security-sensitive operations can force validation so they always act on current data.

- Cross-origin request blocking (CSRF protection) and a user ID precondition that catches stale clients after account switches.
- Exceptions mapped to error responses through a single `IApiExceptionHandler`, with reference IDs logged and reported for unexpected failures, and requests that cannot be read (such as a malformed body) reported in the same format instead of as empty responses.
- Minimal API route handlers returning `IAsyncEnumerable<T>` converted to flushed, keep-alive capable streams that propagate errors.
- SignalR hub filters that report hub exceptions to clients with the same typed errors as HTTP requests, and recheck the sessions of open connections, closing those whose sessions have ended.
- Per-client rate limiting on top of ASP.NET Core rate limiting, with exemptions by address, counts that hold across servers and rejections reported as typed errors.
- Sign-in building blocks: one-time sign-in codes bound to PKCE, device linking (approving a new device from a signed-in one) and short codes for users to type.
- A short-lived store for the state of sign-ins, rate limits and the app's own flows, in memory by default or in shared storage such as Redis or a database table, so that any server can finish what another started.
- Trimming and Native AOT compatible when used with source-generated JSON, including for the session token and SignalR hubs (see [Trimming and Native AOT](https://www.singulink.com/Docs/Singulink.Net.Http.Api/articles/guides/trimming-and-aot.html)).

### Singulink.Net.Http.Api.Service.Cloudflare

Adds Cloudflare support to services, for services that are reached through Cloudflare at some times and directly at others:

- The client's address and scheme forwarded by Cloudflare are applied only to connections from Cloudflare's IP ranges and from the proxies the app trusts, such as a Cloudflare Tunnel connector, so per-client features such as rate limiting see the real client.
- Cloudflare headers (such as `CF-IPCountry`) are removed from direct connections, so they can be trusted whenever they are present.
- The visitor location that Cloudflare sends (country, region, city and more), only for requests that came through Cloudflare.
- Cloudflare's IP ranges are downloaded at startup, saved for when they cannot be downloaded (a copy can be seeded for new servers and containers), and refreshed periodically, or downloads can be turned off to read a provided copy, picking up changes to it. Until the ranges are available, requests are refused with a typed `503` error rather than misattributed to Cloudflare's servers.

### Singulink.Net.Http.Api.Service.Passkeys

Passkey (WebAuthn) sign-in for services, built on Fido2NetLib. The app keeps its own endpoints and passkey table, which can use the standard data contracts; the library runs the ceremonies:

- Sign-in (with discoverable passkeys or a user's passkeys), confirming the signed-in user before sensitive actions, and adding passkeys.
- Sign-ins bound to the app with PKCE from the start, which can continue with a one-time sign-in code bound to the same challenge.
- Storage through a small interface over the app's own table, with sign counters, backup state and last use saved automatically.
- Ceremony state kept in the short-lived store, so a ceremony can finish on any server.
- A hand-off page served by the API that native apps open in the system browser to run passkeys. It makes no API calls of its own.

### Singulink.Net.Http.Api.Service.Testing

Test helpers for services, which production apps don't reference:

- `TestSessionContext<TSessionToken>`, a session context for testing code that takes one, without a request or a session store.
- `SoftwareAuthenticator`, a software passkey authenticator that answers WebAuthn ceremonies as a browser would.

### Singulink.Net.Http.Api.Client

Base class for API clients on any .NET platform, including browser (WebAssembly), mobile and .NET Framework apps:

- One-line request methods with interpolated paths whose values are escaped and formatted with the invariant culture.
- Session cookie handling with persistence callbacks, so a persistent session survives app restarts.
- Child clients that share the session and add a base path and a user ID precondition to their requests.
- Error responses rethrown as typed `ApiException` instances.
- Streamed results consumed as `IAsyncEnumerable<T>`.
- Trimming and AOT friendly when used with a source-generated JSON serializer context.
- `ApiFailures.IsTransient`, which tells the failures that may not happen again when a request is made again, such as a service that can't be reached.
- `Pkce` for the code verifier and challenge that bind sign-in codes to the client, and `DeviceLinkWaiter`, which waits on a new device for the approval of a device link.
- The passkey authenticator interface (`IPasskeyAuthenticator`) and its credential and error types, without a UI framework dependency, and `PasskeyCeremonyPrefetch`, which begins passkey ceremonies ahead of the action that shows the prompt.

### Singulink.Net.Http.Api.Client.SignalR

Adds SignalR support to API clients:

- Hub connections that share the client's session and always present the current session token, including on reconnect.
- `ApiHubConnection` with a strongly typed message, method and stream API based on shared contract definitions.
- Hub errors surfaced as the same typed `ApiException` instances as HTTP requests.
- An opt-in that keeps a connection trying to connect, and handlers that run on a synchronization context, such as the UI thread's.

### Singulink.Net.Http.Api.Client.WinUI

Passkey (WebAuthn) support and session token storage for WinUI 3 (Windows App SDK) apps, and Uno Platform apps on WebAssembly, iOS, Android and desktop:

- `PasskeyAuthenticator` signs in, confirms the signed-in user and creates passkeys with options from the app's API, returning the credential as WebAuthn JSON for the app to post back, and whether the passkey was on this device.
- WebAssembly apps run WebAuthn in their own page; native apps hand off to the service's passkey page in the system browser, which returns the result to the app. On desktop, a browser that doesn't open the page is reported after a start timeout, 30 seconds by default, rather than after the full timeout.
- Browser errors surfaced as a `PasskeyAuthenticatorException` with a reason (cancelled, not allowed, already on the authenticator, not supported, requirements not met, security, page not opened, timed out).
- Passkeys the service doesn't know reported to the browser's password manager, in browsers with the WebAuthn Signal API.
- A loopback web authentication broker for Uno Platform desktop apps, which Uno doesn't provide.
- `SessionTokenStore`, which keeps the token of a persistent session between launches: in the password vault on Android, in a keychain item on iOS that only the device can read, in the local settings on Uno Platform desktop, encrypted with DPAPI on Windows, and in a DPAPI-encrypted file in Windows App SDK apps.

## Upgrading from 3.x

Version 4.0 moves the session's refresh info out of the session token and into the session cookie, replaces the session store context with a scoped session store, and changes the cookie format, so users sign in once after upgrading. API clients escape the values in interpolated paths, apply the toolkit's JSON null handling and don't follow redirects, and errors that the toolkit reports itself carry error codes. See [Upgrading from 3.x](https://www.singulink.com/Docs/Singulink.Net.Http.Api/articles/guides/upgrading.html) for the full list of changes.

## Further Reading

You can view the full documentation, including guides and API documentation, on the [project documentation site](https://www.singulink.com/Docs/Singulink.Net.Http.Api/index.html).
