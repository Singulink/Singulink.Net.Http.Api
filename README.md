# Singulink HTTP API Toolkit

[![Chat on Discord](https://img.shields.io/discord/906246067773923490)](https://discord.gg/EkQhJFsBu6)
[![Build and Test](https://github.com/Singulink/Singulink.Net.Http.Api/workflows/build%20and%20test/badge.svg)](https://github.com/Singulink/Singulink.Net.Http.Api/actions?query=workflow%3A%22build+and+test%22)

**HTTP API Toolkit** is a set of client and service libraries for building and consuming HTTP APIs in .NET based on a consistent set of opinionated conventions: cookie-based sessions that need no server-side lookup on most requests, errors that travel as typed exceptions, streamed results with keep-alive and error propagation, and strongly typed SignalR hub contracts shared between server and client.

Details of each component are provided below:

| Library | Status | Package |
| --- | --- | --- |
| **Singulink.Net.Http.Api** | Public | [![View nuget package](https://img.shields.io/nuget/v/Singulink.Net.Http.Api.svg)](https://www.nuget.org/packages/Singulink.Net.Http.Api/) |
| **Singulink.Net.Http.Api.Client** | Public | [![View nuget package](https://img.shields.io/nuget/v/Singulink.Net.Http.Api.Client.svg)](https://www.nuget.org/packages/Singulink.Net.Http.Api.Client/) |
| **Singulink.Net.Http.Api.Client.SignalR** | Public | [![View nuget package](https://img.shields.io/nuget/v/Singulink.Net.Http.Api.Client.SignalR.svg)](https://www.nuget.org/packages/Singulink.Net.Http.Api.Client.SignalR/) |
| **Singulink.Net.Http.Api.Service** | Public | [![View nuget package](https://img.shields.io/nuget/v/Singulink.Net.Http.Api.Service.svg)](https://www.nuget.org/packages/Singulink.Net.Http.Api.Service/) |

**Supported Runtimes**: .NET 10.0+

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

Shared library referenced by both clients and services. It contains the parts of the conventions that both sides must agree on:

✔️ The `ApiException` hierarchy (`BadRequestApiException`, `UnauthorizedApiException`, `NotFoundApiException`, `ValidationApiException`, `ServerErrorApiException` and more), thrown on the server and rethrown as the same type on the client  
✔️ The `StreamingResponse` format and reader for streamed results  
✔️ `HubMessage`, `HubMethod` and `HubStream` definitions for strongly typed SignalR hub contracts  

### Singulink.Net.Http.Api.Service

ASP.NET Core library for building services:

✔️ **Cookie-based sessions** backed by an encrypted, self-contained session token, so most requests need no session store lookup  
✔️ Sliding session expiry with periodic refresh, token rotation for theft detection, and a grace window for concurrent requests  
✔️ Forced validation for security-sensitive operations, so they always act on current data  
✔️ Cross-origin request blocking (CSRF protection) and a user ID precondition that catches stale clients after account switches  
✔️ Exceptions mapped to error responses through a single `IApiExceptionHandler`, with reference IDs for unexpected failures  
✔️ Endpoints returning `IAsyncEnumerable<T>` automatically converted to flushed, keep-alive capable streams that propagate errors  
✔️ SignalR hub filter that reports hub exceptions to clients with the same typed errors as HTTP requests  

### Singulink.Net.Http.Api.Client

Base class for API clients on any .NET platform, including browser (WebAssembly) and mobile:

✔️ Session cookie handling with persistence callbacks, so a signed-in session survives app restarts  
✔️ Automatic user ID preconditions on requests  
✔️ Error responses rethrown as typed `ApiException` instances  
✔️ Streamed results consumed as `IAsyncEnumerable<T>`  
✔️ Trimming and AOT friendly when used with a source-generated JSON serializer context  

### Singulink.Net.Http.Api.Client.SignalR

Adds SignalR support to API clients:

✔️ Hub connections that share the client's session and always present the current session token, including on reconnect  
✔️ `ApiHubConnection` with a strongly typed message, method and stream API based on shared contract definitions  
✔️ Hub errors surfaced as the same typed `ApiException` instances as HTTP requests  

## Further Reading

You can view the full documentation, including guides and API documentation, on the [project documentation site](https://www.singulink.com/Docs/Singulink.Net.Http.Api/index.html).
