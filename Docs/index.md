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

- `Singulink.Net.Http.Api.Service` - the ASP.NET Core service library (sessions, exception handling, streaming responses, hub filter).
- `Singulink.Net.Http.Api.Client` - the API client base class for any .NET platform, including browser and mobile apps.
- `Singulink.Net.Http.Api.Client.SignalR` - SignalR hub connections for API clients.
- `Singulink.Net.Http.Api` - the shared contracts referenced by both sides (exceptions, streaming format, hub definitions). Installed automatically as a dependency of the packages above, and referenced directly by data contract assemblies that declare hub contracts.

**Supported Runtimes**: .NET 10.0+

## Information and Links

Here are some additional links to get you started:

- [Getting Started](articles/guides/getting-started.md) - Visit here first for a quick walkthrough of a service and a client.
- [Guides](articles/guides/toc.yml) - In-depth articles on sessions, exception handling, streaming, API clients and SignalR hubs.
- [Concepts](articles/concepts/toc.yml) - How the session token model, error format and streaming format work under the hood.
- [API Documentation](api/index.md) - Browse the fully documented API here.
- [Chat on Discord](https://discord.gg/EkQhJFsBu6) - Have questions or want to discuss the library? This is the place for all Singulink project discussions.
- [Github Repo](https://github.com/Singulink/Singulink.Net.Http.Api) - File issues, contribute pull requests or check out the code for yourself!

</div>
