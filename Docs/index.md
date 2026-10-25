<div class="article">

# Singulink HTTP API Toolkit

**HTTP API Toolkit** is a set of client and service libraries for building and consuming HTTP APIs in .NET based on a consistent set of opinionated conventions. A service built with the toolkit and a client built with the toolkit agree on how sessions, errors, streamed results and SignalR hubs work, so application code deals with typed exceptions, typed hub contracts and plain `IAsyncEnumerable<T>` results instead of HTTP plumbing.

The main conventions are:

- **Sessions** live in an encrypted, self-contained cookie that the service validates without a store lookup on most requests, refreshes periodically, and rotates to detect theft.
- **Errors** are thrown as `ApiException` types on the server and rethrown as the same types on the client, with optional machine-readable error codes.
- **Streamed results** are written as newline-delimited JSON records with keep-alive pings and in-band error propagation, and consumed on the client as an `IAsyncEnumerable<T>`.
- **SignalR hubs** are described by shared contract definitions that give both sides compile-time checked message names and argument types, with hub errors surfaced as the same typed exceptions.

**HTTP API Toolkit** is part of the **Singulink Libraries** collection. Visit https://github.com/Singulink/ to see our full list of publicly available libraries and other open-source projects.

### Installation

The libraries are available on NuGet. Install the packages that match your needs:

- `Singulink.Net.Http.Api.Service` - the ASP.NET Core service library (sessions, exception handling, streaming responses, hub filters, per-client rate limiting, sign-in codes, device links and the short-lived store).
- `Singulink.Net.Http.Api.Service.Cloudflare` - trusts the client address and scheme forwarded by Cloudflare, for services behind Cloudflare.
- `Singulink.Net.Http.Api.Service.Passkeys` - passkey (WebAuthn) sign-in, verification and registration ceremonies, and the passkey page for native apps.
- `Singulink.Net.Http.Api.Service.Testing` - test helpers for services: a session context for testing code without a request (`TestSessionContext<TSessionToken>`) and a software passkey authenticator (`SoftwareAuthenticator`).
- `Singulink.Net.Http.Api.Client` - the API client base class for any .NET platform, including browser, mobile and .NET Framework apps.
- `Singulink.Net.Http.Api.Client.SignalR` - SignalR hub connections for API clients.
- `Singulink.Net.Http.Api.Client.WinUI` - passkeys and session token storage in WinUI 3 and Uno Platform apps.
- `Singulink.Net.Http.Api` - the shared contracts referenced by both sides (exceptions, error codes, streaming format, hub definitions, JSON null handling, and the data contracts of the standard sign-in steps). Installed automatically as a dependency of the packages above, and referenced directly by data contract assemblies that declare hub contracts or use its data contracts.

**Supported Runtimes**: The shared library and the API client library support .NET 10.0+ and .NET Framework 4.6.2+; browser (WebAssembly) apps require .NET 10.0+. Other runtimes that can load .NET Standard 2.0 libraries, such as .NET 8 and 9, get a build warning: they are not supported or tested. All other packages require .NET 10.0+.

## Information and Links

Here are some additional links to get you started:

<!-- Keep the API link as api/index.html, even though docfx warns "Invalid file link:(~/api/index.html)": it opens the API landing page with all the
     namespaces listed and collapsed, so that the Client and Service namespaces are as easy to find as the core one. Never link the API index as
     api/index.md (in toc.yml it drops the landing page's API sidebar, a known docfx issue) or as api/ (docfx leaves that as a bare folder link). Link to a
     page of the guides or concepts, never to a toc.yml, which builds to a bare, unstyled toc.html. -->

- [Getting Started](articles/guides/getting-started.md) - Visit here first for a quick walkthrough of a service and a client. It opens the guides: in-depth articles on sessions, exception handling, streaming, API clients, SignalR hubs, sign-in codes and passkeys, listed in the sidebar.
- [Concepts](articles/concepts/architecture.md) - How the session token model, error format and streaming format work under the hood, starting with the architecture and conventions.
- [API Documentation](api/index.html) - Browse the fully documented API here.
- [Chat on Discord](https://discord.gg/EkQhJFsBu6) - Have questions or want to discuss the library? This is the place for all Singulink project discussions.
- [Github Repo](https://github.com/Singulink/Singulink.Net.Http.Api) - File issues, contribute pull requests or check out the code for yourself!

</div>
